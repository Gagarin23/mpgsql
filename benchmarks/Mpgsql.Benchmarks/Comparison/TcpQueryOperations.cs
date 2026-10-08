using System.Buffers;
using System.Diagnostics;
using Mpgsql.Benchmarks.Queries;
using Npgsql;

namespace Mpgsql.Benchmarks.Comparison;

internal static class TcpQueryOperations
{
    internal readonly record struct Timing(long Checksum, long LatencyTicks, long FirstRowTicks);
    internal static async Task<long> ConsumeAsync(MpgsqlResultReader reader, QueryScenario scenario, byte[] buffer)
    {
        long sum = 0;
        do { while (await reader.ReadAsync().ConfigureAwait(false)) sum += Row(reader, scenario, buffer); }
        while (await reader.NextResultAsync().ConfigureAwait(false));
        return sum;
    }
    internal static async Task<long> ConsumeAsync(NpgsqlDataReader reader, QueryScenario scenario, byte[] buffer)
    {
        long sum = 0;
        do { while (await reader.ReadAsync().ConfigureAwait(false)) sum += Row(reader, scenario, buffer); }
        while (await reader.NextResultAsync().ConfigureAwait(false));
        return sum;
    }
    internal static long Row(MpgsqlResultReader reader, QueryScenario scenario, byte[] buffer)
    {
        long sum = 0;
        for (int column = 0; column < (scenario.ByteaBytes == 0 ? scenario.Columns : 1); column++) sum += reader.GetInt64(column) ?? 0;
        if (scenario.ByteaBytes != 0)
        {
            var blob = reader.GetRawValue(1)!.Value;
            blob.CopyTo(buffer);
            sum += blob.Length + buffer[0] + buffer[scenario.ByteaBytes - 1];
        }
        return sum;
    }
    internal static long Row(NpgsqlDataReader reader, QueryScenario scenario, byte[] buffer)
    {
        long sum = 0;
        for (int column = 0; column < (scenario.ByteaBytes == 0 ? scenario.Columns : 1); column++)
            sum += reader.IsDBNull(column) ? 0 : reader.GetInt64(column);
        if (scenario.ByteaBytes != 0)
        {
            long length = reader.GetBytes(1, 0, buffer, 0, scenario.ByteaBytes);
            sum += length + buffer[0] + buffer[scenario.ByteaBytes - 1];
        }
        return sum;
    }
    internal static async Task<long> RawAsync(MpgsqlTcpTransport transport, QueryCatalog catalog, QueryScenario scenario, byte[] buffer)
    {
        await using var batch = transport.Session.CreateBatch();
        var send = batch.SendQueryAsync(scenario.Sql, catalog.Inputs[catalog.Index(scenario)][0]);
        var sync = batch.SendSyncAsync();
        await Task.WhenAll(send.AsTask(), sync.AsTask()).ConfigureAwait(false);
        await using var reader = await batch.ReadResultsAsync().ConfigureAwait(false);
        long sum = await ConsumeAsync(reader, scenario, buffer).ConfigureAwait(false);
        await batch.Completion.ConfigureAwait(false);
        return sum;
    }
    internal static async Task<long> MpgsqlAsync(TcpMpgsqlFixture fixture, QueryScenario scenario, int worker = 0, bool slow = false)
    {
        await using var reader = await fixture.Source.ExecuteReaderAsync(scenario.Sql, fixture.Catalog.Inputs[fixture.Catalog.Index(scenario)][worker]).ConfigureAwait(false);
        long sum = 0; int rows = 0;
        do
        {
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                sum += Row(reader, scenario, fixture.Buffers[worker]);
                if (slow && ++rows % 8 == 0) await Task.Delay(1).ConfigureAwait(false);
            }
        } while (await reader.NextResultAsync().ConfigureAwait(false));
        return sum;
    }
    internal static async Task<long> NpgsqlAsync(TcpNpgsqlFixture fixture, QueryCatalog catalog, QueryScenario scenario, int worker = 0, bool slow = false)
    {
        await using var reader = await fixture.Commands[catalog.Index(scenario)][worker].ExecuteReaderAsync().ConfigureAwait(false);
        long sum = 0; int rows = 0;
        do
        {
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                sum += Row(reader, scenario, fixture.Buffers[worker]);
                if (slow && ++rows % 8 == 0) await Task.Delay(1).ConfigureAwait(false);
            }
        } while (await reader.NextResultAsync().ConfigureAwait(false));
        return sum;
    }
    internal static async Task<Timing> TimedAsync(TcpMpgsqlFixture fixture, QueryScenario scenario, int worker, bool slow)
    {
        long start = Stopwatch.GetTimestamp(), first = 0, sum = 0; int rows = 0;
        var reader = await fixture.Source.ExecuteReaderAsync(scenario.Sql, fixture.Catalog.Inputs[fixture.Catalog.Index(scenario)][worker]).ConfigureAwait(false);
        try
        {
            do
            {
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    if (first == 0) first = Stopwatch.GetTimestamp() - start;
                    sum += Row(reader, scenario, fixture.Buffers[worker]); fixture.Observe();
                    if (slow && ++rows % 8 == 0) await Task.Delay(1).ConfigureAwait(false);
                }
            } while (await reader.NextResultAsync().ConfigureAwait(false));
        }
        finally { await reader.DisposeAsync().ConfigureAwait(false); }
        return new(sum, Stopwatch.GetTimestamp() - start, first);
    }
    internal static async Task<Timing> TimedAsync(TcpNpgsqlFixture fixture, QueryCatalog catalog, QueryScenario scenario, int worker, bool slow)
    {
        long start = Stopwatch.GetTimestamp(), first = 0, sum = 0; int rows = 0;
        var reader = await fixture.Commands[catalog.Index(scenario)][worker].ExecuteReaderAsync().ConfigureAwait(false);
        try
        {
            do
            {
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    if (first == 0) first = Stopwatch.GetTimestamp() - start;
                    sum += Row(reader, scenario, fixture.Buffers[worker]);
                    if (slow && ++rows % 8 == 0) await Task.Delay(1).ConfigureAwait(false);
                }
            } while (await reader.NextResultAsync().ConfigureAwait(false));
        }
        finally { await reader.DisposeAsync().ConfigureAwait(false); }
        return new(sum, Stopwatch.GetTimestamp() - start, first);
    }
}
