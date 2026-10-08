using Mpgsql.Multiplexing.Internal;
using Mpgsql.Protocol;

namespace Mpgsql.Benchmarks.Queries;

internal sealed class QuerySourceFixture : IAsyncDisposable
{
    private readonly object _gate = new object();
    private readonly List<QueryPeer> _peers = [];
    private long _observed;

    private QuerySourceFixture(QueryCatalog catalog, int connections,
        int inFlight, long rowBytes,
        int chunk)
    {
        Catalog = catalog;
        Source = new MpgsqlMultiplexingDataSource(_ =>
        {
            var peer = new QueryPeer(catalog, chunk);
            lock (_gate)
            {
                _peers.Add(peer);
            }
            return ValueTask.FromResult(peer.Session);
        }, new MpgsqlMultiplexingOptions
        {
            MaxConnections = connections, MaxInFlightPerConnection = inFlight,
            MaxBufferedRowBytesPerConnection = rowBytes
        });
    }
    internal MpgsqlMultiplexingDataSource Source { get; }
    internal QueryCatalog Catalog { get; }
    internal QueryPeer[] Peers { get; private set; } = [];
    internal long MaxObservedBufferedRowBytes => Interlocked.Read(ref _observed);

    public async ValueTask DisposeAsync()
    {
        await Source.DisposeAsync().ConfigureAwait(false);
        QueryPeer[] peers;
        lock (_gate)
        {
            peers = [.. _peers];
        }
        foreach (var peer in peers) await peer.DisposeAsync().ConfigureAwait(false);
    }

    internal static async Task<QuerySourceFixture> CreateAsync(QueryCatalog catalog, int connections = 1,
        int inFlight = 1, long rowBytes = 8 * 1024 * 1024,
        int chunk = 0)
    {
        var fixture = new QuerySourceFixture(catalog, connections, inFlight, rowBytes, chunk);
        try
        {
            // Hold all in-flight slots simultaneously so the factory creates the entire pool.
            var leases = new PooledSession[connections * inFlight];
            try
            {
                for (var i = 0; i < leases.Length; i++)
                {
                    leases[i] = await fixture.Source.AcquireAsync(default).ConfigureAwait(false);
                }
            }
            finally
            {
                foreach (var lease in leases)
                {
                    if (lease is not null)
                    {
                        await fixture.Source.ReleaseRequestAsync(lease).ConfigureAwait(false);
                    }
                }
            }
            lock (fixture._gate)
            {
                fixture.Peers = [.. fixture._peers];
            }
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal void ObserveBuffers()
    {
        long current = 0;
        foreach (var peer in Peers) current = Math.Max(current, peer.Session.BufferedRowBytes);
        long previous;
        do
        {
            previous = Interlocked.Read(ref _observed);
            if (previous >= current)
            {
                return;
            }
        } while (Interlocked.CompareExchange(ref _observed, current, previous) != previous);
    }

    internal void ResetObservation()
    {
        Interlocked.Exchange(ref _observed, 0);
    }
    internal void CheckIdle()
    {
        lock (_gate)
        {
            if (_peers.Count != Peers.Length)
            {
                throw new InvalidOperationException("A prewarmed transport was replaced.");
            }
        }
        foreach (var peer in Peers)
        {
            peer.ThrowIfFailed();
            if (!peer.Session.IsHealthy || peer.Session.BufferedRowBytes != 0
                                        || peer.Session.LastTransactionStatus != TransactionStatus.Idle)
            {
                throw new InvalidOperationException("Session is unhealthy or retains row reservations.");
            }
        }
    }
}