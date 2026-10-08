using System.Diagnostics;

namespace Mpgsql.Benchmarks.Queries;

internal static class QueryOperations
{
    internal static async Task<long> RawAsync(QueryPeer peer, QueryCatalog catalog,
        QueryScenario scenario, int worker = 0)
    {
        await using var batch = peer.Session.CreateBatch();
        var send = batch.SendQueryAsync(scenario.Sql, catalog.Inputs[catalog.Index(scenario)][worker]);
        var sync = batch.SendSyncAsync(); // queue the boundary before awaiting publication
        await Task.WhenAll(send.AsTask(), sync.AsTask()).ConfigureAwait(false);
        await using var reader = await batch.ReadResultsAsync().ConfigureAwait(false);
        var checksum = await ConsumeAsync(reader, scenario).ConfigureAwait(false);
        await batch.Completion.ConfigureAwait(false);
        return checksum;
    }

    internal static async Task<long> DataSourceAsync(QuerySourceFixture fixture,
        QueryScenario scenario, int worker = 0)
    {
        await using var reader = await fixture.Source.ExecuteReaderAsync(scenario.Sql,
            fixture.Catalog.Inputs[fixture.Catalog.Index(scenario)][worker]).ConfigureAwait(false);
        return await ConsumeAsync(reader, scenario).ConfigureAwait(false);
    }

    internal static async Task<long> ConsumeAsync(MpgsqlResultReader reader, QueryScenario scenario)
    {
        long checksum = 0;
        do
        {
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                checksum += Row(reader, scenario);
            }
        } while (await reader.NextResultAsync().ConfigureAwait(false));
        return checksum;
    }

    internal static async Task<long> SlowAsync(QuerySourceFixture fixture, int worker)
    {
        await using var reader = await fixture.Source.ExecuteReaderAsync(QueryScenario.Slow.Sql,
            fixture.Catalog.Inputs[1][worker]).ConfigureAwait(false);
        long checksum = 0;
        var rows = 0;
        do
        {
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                checksum += Row(reader, QueryScenario.Slow);
                if (++rows % 8 == 0)
                {
                    await Task.Delay(1).ConfigureAwait(false);
                }
            }
        } while (await reader.NextResultAsync().ConfigureAwait(false));
        return checksum;
    }

    internal static long Row(MpgsqlResultReader reader, QueryScenario scenario)
    {
        long checksum = 0;
        var integers = scenario.ByteaBytes == 0 ? scenario.Columns : 1;
        for (var column = 0; column < integers; column++)
        {
            checksum += reader.GetInt64(column) ?? 0;
        }
        if (scenario.ByteaBytes != 0)
        {
            var blob = reader.GetRawValue(1)!.Value;
            checksum += blob.Length + blob.FirstSpan[0] + blob.Slice(blob.Length - 1).FirstSpan[0];
        }
        return checksum;
    }

    internal static async Task<long> BatchAsync(QueryPeer peer, QueryCatalog catalog)
    {
        await using var batch = peer.Session.CreateBatch();
        var pending = new Task[17];
        for (var i = 0; i < 16; i++)
        {
            pending[i] = batch.SendQueryAsync(QueryScenario.One.Sql, catalog.Inputs[0][i]).AsTask();
        }
        pending[16] = batch.SendSyncAsync().AsTask();
        await Task.WhenAll(pending).ConfigureAwait(false);
        await using var reader = await batch.ReadResultsAsync().ConfigureAwait(false);
        var checksum = await ConsumeAsync(reader, QueryScenario.One).ConfigureAwait(false);
        await batch.Completion.ConfigureAwait(false);
        return checksum;
    }

    internal static async Task<Timing> TimedAsync(QuerySourceFixture fixture, QueryScenario scenario,
        int scenarioIndex, int worker,
        bool slow)
    {
        var start = Stopwatch.GetTimestamp();
        long first = 0, checksum = 0;
        var rows = 0;
        var reader = await fixture.Source.ExecuteReaderAsync(scenario.Sql,
            fixture.Catalog.Inputs[scenarioIndex][worker]).ConfigureAwait(false);
        try
        {
            do
            {
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    if (first == 0)
                    {
                        first = Stopwatch.GetTimestamp() - start;
                    }
                    checksum += Row(reader, scenario);
                    fixture.ObserveBuffers();
                    if (slow && ++rows % 8 == 0)
                    {
                        await Task.Delay(1).ConfigureAwait(false);
                    }
                }
            } while (await reader.NextResultAsync().ConfigureAwait(false));
        }
        finally { await reader.DisposeAsync().ConfigureAwait(false); }
        return new Timing(checksum, Stopwatch.GetTimestamp() - start, first);
    }

    internal readonly record struct Timing(long Checksum, long LatencyTicks, long FirstRowTicks);
}