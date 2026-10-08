using Mpgsql.Benchmarks.Queries;
using Mpgsql.Multiplexing.Internal;

namespace Mpgsql.Benchmarks.Comparison;

internal sealed class TcpMpgsqlFixture : IAsyncDisposable
{
    private readonly object _gate = new object();
    private readonly List<MpgsqlTcpTransport> _transports = [];
    private long _observed;
    private TcpMpgsqlFixture(
        QueryCatalog catalog, int connections,
        int inFlight, long budget,
        int syncGroupSize, int syncTimeoutMs
    )
    {
        Catalog = catalog;
        Peer = new TcpQueryPeer(new TcpQueryCatalog(catalog));
        Buffers =
        [
            .. catalog
                .Inputs[0]
                .Select(_ => new byte[catalog.Scenarios.Max(x => x.ByteaBytes)])
        ];
        Source = new MpgsqlMultiplexingDataSource
        (
            async token =>
            {
                var transport = await MpgsqlTcpTransport
                    .OpenAsync(Peer.Port, token)
                    .ConfigureAwait(false);
                lock (_gate)
                {
                    _transports.Add(transport);
                }
                return transport.Session;
            }, new MpgsqlMultiplexingOptions
            {
                MaxConnections = connections,
                MaxInFlightPerConnection = inFlight,
                MaxBufferedRowBytesPerConnection = budget,
                SyncGroupSize = syncGroupSize,
                SyncGroupTimeout = TimeSpan.FromMilliseconds(syncTimeoutMs)
            }
        );
    }
    internal QueryCatalog Catalog { get; }
    internal TcpQueryPeer Peer { get; }
    internal MpgsqlMultiplexingDataSource Source { get; }
    internal MpgsqlDataSource? ClientSource { get; private set; }
    internal MpgsqlTcpTransport[] Transports { get; private set; } = [];
    internal byte[][] Buffers { get; }
    internal long MaxObservedBuffered => Interlocked.Read(ref _observed);
    public async ValueTask DisposeAsync()
    {
        if (ClientSource is not null)
        {
            await ClientSource.DisposeAsync();
        }
        await Source
            .DisposeAsync()
            .ConfigureAwait(false);
        foreach (var transport in _transports)
        {
            await transport
                .DisposeAsync()
                .ConfigureAwait(false);
        }
        await Peer
            .DisposeAsync()
            .ConfigureAwait(false);
    }
    internal async ValueTask<MpgsqlConnection> OpenClientConnectionAsync()
    {
        ClientSource ??= new MpgsqlDataSource
        (
            async token =>
            {
                var transport = await MpgsqlTcpTransport.OpenAsync(Peer.Port, token);
                lock (_gate)
                {
                    _transports.Add(transport);
                    Transports = [.. _transports];
                }
                return transport.Session;
            }, (_, _) => ValueTask.CompletedTask
        );
        return await ClientSource.OpenConnectionAsync();
    }
    internal static async Task<TcpMpgsqlFixture> CreateAsync(
        QueryCatalog catalog, int connections = 1,
        int inFlight = 1, long budget = 8 * 1024 * 1024,
        int syncGroupSize = 1, int syncTimeoutMs = 1,
        bool multiplexing = true
    )
    {
        var fixture = new TcpMpgsqlFixture(catalog, connections, inFlight, budget, syncGroupSize, syncTimeoutMs);
        var leases = new PooledSession[multiplexing ? connections * inFlight : 0];
        try
        {
            for (var i = 0;
                 i < leases.Length;
                 i++)
            {
                leases[i] = await fixture
                    .Source.AcquireAsync(default)
                    .ConfigureAwait(false);
                await using var group = leases[i]
                    .Session.CreateBatch();
                await group.SendQueryAsync
                (
                    catalog.Scenarios[0].Sql, catalog
                        .Inputs[0][0]
                );
                await group.SendSyncAsync();
                await using var reader = await group.ReadResultsAsync();
                await TcpQueryOperations.ConsumeAsync(reader, catalog.Scenarios[0], fixture.Buffers[0]);

            }
        }
        catch
        {
            await fixture
                .DisposeAsync()
                .ConfigureAwait(false);
            throw;
        }
        finally
        {
            foreach (var lease in leases)
            {
                if (lease is not null)
                {
                    await fixture
                        .Source.ReleaseRequestAsync(lease)
                        .ConfigureAwait(false);
                }
            }
        }
        lock (fixture._gate)
        {
            fixture.Transports = [.. fixture._transports];
        }
        fixture.CheckIdle();
        return fixture;
    }
    internal void Observe()
    {
        long current = 0;
        foreach (var transport in Transports)
        {
            current = Math.Max(current, transport.Session.BufferedRowBytes);
        }
        long previous;
        do
        {
            previous = Interlocked.Read(ref _observed);
            if (previous >= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _observed, current, previous) != previous);
    }
    internal void ResetObservation()
    {
        Interlocked.Exchange(ref _observed, 0);
    }
    internal void CheckIdle()
    {
        Peer.CheckHealthy();
        lock (_gate)
        {
            if (_transports.Count != Transports.Length)
            {
                throw new InvalidOperationException("A prewarmed Mpgsql.Protocol transport was replaced.");
            }
        }
        foreach (var transport in Transports)
        {
            if (!transport.Session.IsHealthy || transport.Session.BufferedRowBytes != 0)
            {
                throw new InvalidOperationException("Mpgsql.Protocol TCP transport retains rows or is unhealthy.");
            }
        }
    }
}