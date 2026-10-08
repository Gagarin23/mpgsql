using Mpgsql.Benchmarks.Queries;

namespace Mpgsql.Benchmarks.Comparison;

internal sealed class TcpMpgsqlFixture : IAsyncDisposable
{
    private readonly List<MpgsqlTcpTransport> _transports = [];
    private readonly object _gate = new();
    internal QueryCatalog Catalog { get; }
    internal TcpQueryPeer Peer { get; }
    internal MpgsqlDataSource Source { get; }
    internal MpgsqlTcpTransport[] Transports { get; private set; } = [];
    internal byte[][] Buffers { get; }
    private long _observed;
    internal long MaxObservedBuffered => Interlocked.Read(ref _observed);
    private TcpMpgsqlFixture(QueryCatalog catalog, int connections, int inFlight, long budget, int syncGroupSize, int syncTimeoutMs)
    {
        Catalog = catalog; Peer = new(new(catalog));
        Buffers = [.. catalog.Inputs[0].Select(_ => new byte[catalog.Scenarios.Max(x => x.ByteaBytes)])];
        Source = new(async token =>
        {
            var transport = await MpgsqlTcpTransport.OpenAsync(Peer.Port, token).ConfigureAwait(false);
            lock (_gate) _transports.Add(transport);
            return transport.Session;
        }, (_, _) => throw new InvalidOperationException("Cancellation channel is outside this baseline."), new()
        {
            MaxConnections = connections, MaxInFlightPerConnection = inFlight, MaxBufferedRowBytesPerConnection = budget,
            SyncGroupSize = syncGroupSize, SyncGroupTimeout = TimeSpan.FromMilliseconds(syncTimeoutMs)
        });
    }
    internal static async Task<TcpMpgsqlFixture> CreateAsync(QueryCatalog catalog, int connections = 1,
        int inFlight = 1, long budget = 8 * 1024 * 1024, int syncGroupSize = 1, int syncTimeoutMs = 1)
    {
        var fixture = new TcpMpgsqlFixture(catalog, connections, inFlight, budget, syncGroupSize, syncTimeoutMs);
        var leases = new MpgsqlConnection[connections];
        try
        {
            for (int i = 0; i < leases.Length; i++)
            {
                leases[i] = await fixture.Source.OpenConnectionAsync().ConfigureAwait(false);
                var command = leases[i].CreateCommand(catalog.Scenarios[0].Sql);
                foreach (var parameter in catalog.Inputs[0][0]) command.Parameters.Add(parameter);
                await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
                if (await TcpQueryOperations.ConsumeAsync(reader, catalog.Scenarios[0], fixture.Buffers[0]).ConfigureAwait(false)
                    != catalog.Scenarios[0].Expected(0)) throw new InvalidOperationException("Mpgsql physical warmup checksum.");
            }
        }
        catch { await fixture.DisposeAsync().ConfigureAwait(false); throw; }
        finally { foreach (var lease in leases) if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false); }
        lock (fixture._gate) fixture.Transports = [.. fixture._transports];
        fixture.CheckIdle();
        return fixture;
    }
    internal void Observe()
    {
        long current = 0;
        foreach (var transport in Transports) current = Math.Max(current, transport.Session.BufferedRowBytes);
        long previous;
        do { previous = Interlocked.Read(ref _observed); if (previous >= current) return; }
        while (Interlocked.CompareExchange(ref _observed, current, previous) != previous);
    }
    internal void ResetObservation() => Interlocked.Exchange(ref _observed, 0);
    internal void CheckIdle()
    {
        Peer.CheckHealthy();
        lock (_gate) if (_transports.Count != Transports.Length) throw new InvalidOperationException("A prewarmed Mpgsql transport was replaced.");
        foreach (var transport in Transports)
            if (!transport.Session.IsHealthy || transport.Session.BufferedRowBytes != 0)
                throw new InvalidOperationException("Mpgsql TCP transport retains rows or is unhealthy.");
    }
    public async ValueTask DisposeAsync()
    {
        await Source.DisposeAsync().ConfigureAwait(false);
        foreach (var transport in _transports) await transport.DisposeAsync().ConfigureAwait(false);
        await Peer.DisposeAsync().ConfigureAwait(false);
    }
}
