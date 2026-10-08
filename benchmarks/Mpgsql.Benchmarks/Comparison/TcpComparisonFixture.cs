using Mpgsql.Benchmarks.Queries;

namespace Mpgsql.Benchmarks.Comparison;

internal sealed class TcpComparisonFixture : IAsyncDisposable
{
    private TcpComparisonFixture(ComparisonDriver driver, QueryCatalog catalog,
        TcpMpgsqlFixture? m, TcpNpgsqlFixture? n)
    {
        (Driver, Catalog, Mpgsql, Npgsql) = (driver, catalog, m, n);
    }
    internal ComparisonDriver Driver { get; }
    internal QueryCatalog Catalog { get; }
    internal TcpMpgsqlFixture? Mpgsql { get; }
    internal TcpNpgsqlFixture? Npgsql { get; }
    internal TcpQueryPeer Peer => Mpgsql?.Peer ?? Npgsql!.Peer;
    public async ValueTask DisposeAsync()
    {
        if (Mpgsql is { } m)
        {
            await m.DisposeAsync();
        }
        else
        {
            await Npgsql!.DisposeAsync();
        }
    }
    internal static async Task<TcpComparisonFixture> CreateAsync(ComparisonDriver driver, QueryLoadProfile profile,
        int syncGroupSize = 1, int syncTimeoutMs = 1)
    {
        var catalog = profile.CreateCatalog();
        return driver == ComparisonDriver.Mpgsql
            ? new TcpComparisonFixture(driver, catalog, await TcpMpgsqlFixture.CreateAsync(catalog, profile.Connections, profile.InFlight, profile.RowBudget,
                syncGroupSize, syncTimeoutMs), null)
            : new TcpComparisonFixture(driver, catalog, null, await TcpNpgsqlFixture.CreateAsync(catalog, profile.Connections, driver == ComparisonDriver.NpgsqlMultiplexed));
    }
    internal Task<long> ReadAsync(int worker, bool slow)
    {
        return Mpgsql is { } m
            ? TcpQueryOperations.MpgsqlAsync(m, slow ? QueryScenario.Slow : QueryScenario.One, worker, slow)
            : TcpQueryOperations.NpgsqlAsync(Npgsql!, Catalog, slow ? QueryScenario.Slow : QueryScenario.One, worker, slow);
    }
    internal Task<TcpQueryOperations.Timing> TimedAsync(int worker, bool slow)
    {
        return Mpgsql is { } m
            ? TcpQueryOperations.TimedAsync(m, slow ? QueryScenario.Slow : QueryScenario.One, worker, slow)
            : TcpQueryOperations.TimedAsync(Npgsql!, Catalog, slow ? QueryScenario.Slow : QueryScenario.One, worker, slow);
    }
    internal void CheckIdle()
    {
        if (Mpgsql is { } m)
        {
            m.CheckIdle();
        }
        else
        {
            Npgsql!.CheckIdle();
        }
    }
}