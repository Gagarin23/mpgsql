using System.IO.Pipelines;
using Mpgsql.Protocol;

namespace Mpgsql.IntegrationTests;

internal static class MessageSessionChecks
{
    internal static async Task RunAsync(TestConnection connection)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var session = new MpgsqlMessageSession
        (
            PipeReader.Create
            (
                connection.CopyStream,
                new StreamPipeReaderOptions(leaveOpen: true)
            ),
            PipeWriter.Create
            (
                connection.CopyStream,
                new StreamPipeWriterOptions(leaveOpen: true)
            ),
            lifetime.Token
        );
        await ConcurrentPipeline(session);
        await TypedParameters(session);
        await PreparedStatementChecks.RunAsync(session);
        await LargeRows(session);
        await ErrorRecovery(session);
        await DeferredError(session);
        await Cancellation
        (
            session,
            lifetime.Token
        );
    }

    private static async Task ConcurrentPipeline(MpgsqlMessageSession session)
    {
        await using var batch = session.CreateBatch();
        var consumer = Task.Run
        (async () =>
            {
                await using var reader = await batch.ReadResultsAsync();
                for (var i = 0;
                     i < 128;
                     i++)
                {
                    Check
                    (
                        reader.QueryIndex == i,
                        "Pipeline result index"
                    );
                    Check
                    (
                        await reader.ReadAsync() && reader.GetInt64(0) == i,
                        "Pipeline bigint value"
                    );
                    Check
                    (
                        !await reader.ReadAsync(),
                        "Pipeline row count"
                    );
                    Check
                    (
                        await reader.NextResultAsync() == i < 127,
                        "Pipeline result count"
                    );
                }
            }
        );
        var producer = Task.Run
        (async () =>
            {
                Task[] pending =
                [
                    .. Enumerable
                        .Range
                        (
                            0,
                            128
                        )
                        .Select
                        (i =>
                            batch
                                .SendQueryAsync
                                (
                                    "select $1",
                                    new[]
                                    {
                                        MpgsqlParameterValue.Int64(i)
                                    }
                                )
                                .AsTask()
                        )
                ];
                await batch.SendSyncAsync();
                await Task.WhenAll(pending);
            }
        );
        await Task.WhenAll
        (
            producer,
            consumer
        );
        Check
        (
            batch.TransactionStatus == TransactionStatus.Idle,
            "Pipeline Sync status"
        );
        Console.WriteLine("PASS helper concurrent send/read, 128 unawaited sends and explicit Sync");
    }

    private static async Task TypedParameters(MpgsqlMessageSession session)
    {
        await using var batch = session.CreateBatch();
        long?[] nullable = [long.MinValue, null, long.MaxValue];
        await batch.SendQueryAsync
        (
            "select $1, $2, $3, $4, $5",
            new[]
            {
                MpgsqlParameterValue.Int64(null),
                MpgsqlParameterValue.Int64Array(null),
                MpgsqlParameterValue.Int64Array(ReadOnlyMemory<long>.Empty),
                MpgsqlParameterValue.NullableInt64Array(nullable),
                MpgsqlParameterValue.Int64(42)
            }
        );
        await batch.SendQueryAsync(" ");
        await batch.SendSyncAsync();
        await using var reader = await batch.ReadResultsAsync();
        Check
        (
            await reader.ReadAsync(),
            "Typed parameter row"
        );
        Check
        (
            reader.GetInt64(0) is null && reader.GetInt64Array(1) is null,
            "Scalar/array SQL NULL"
        );
        Check
        (
            reader.GetInt64Array(2) is
            {
                Length: 0
            },
            "Empty array"
        );
        Check
        (
            reader.GetNullableInt64Array(3)!.Value.Span.SequenceEqual(nullable),
            "Nullable array"
        );
        Check
        (
            reader.GetInt64(4) == 42,
            "Scalar parameter"
        );
        Check
        (
            await reader.NextResultAsync() && reader.Columns.IsEmpty,
            "Empty query NoData"
        );
        Check
        (
            !await reader.ReadAsync() && reader.CommandTag is null,
            "EmptyQueryResponse"
        );
        Check
        (
            !await reader.NextResultAsync(),
            "Typed results boundary"
        );
        Console.WriteLine("PASS helper binary parameters, NULL/empty/nullable arrays and empty query");
    }

    private static async Task LargeRows(MpgsqlMessageSession session)
    {
        await using var batch = session.CreateBatch();
        await batch.SendQueryAsync
        (
            "select i::bigint, array_fill($1, ARRAY[8192]) from generate_series(1, 4) i",
            new[]
            {
                MpgsqlParameterValue.Int64(77)
            }
        );
        await batch.SendSyncAsync();
        await using var reader = await batch.ReadResultsAsync();
        var rows = 0;
        while (await reader.ReadAsync())
        {
            Check
            (
                reader.GetInt64(0) == ++rows,
                "Large row scalar"
            );
            var array = reader.GetInt64Array(1)!.Value;
            Check
            (
                array.Length == 8192 && array.Span.IndexOfAnyExcept(77L) < 0,
                "Large row array"
            );
        }
        Check
        (
            rows == 4 && !await reader.NextResultAsync(),
            "Large row count"
        );
        Console.WriteLine("PASS helper large DataRows across stream buffer boundaries");
    }

    private static async Task ErrorRecovery(MpgsqlMessageSession session)
    {
        foreach (var parse in new[]
                 {
                     true,
                     false
                 })
        {
            await using var failed = session.CreateBatch();
            await failed.SendQueryAsync
            (
                parse
                    ? "select from"
                    : "select 1::bigint / $1",
                parse
                    ? default
                    : new[]
                    {
                        MpgsqlParameterValue.Int64(0)
                    }
            );
            await failed.SendQueryAsync("select 999::bigint");
            await failed.SendSyncAsync();
            await using var recovered = session.CreateBatch();
            await recovered.SendQueryAsync("select 9::bigint");
            await recovered.SendSyncAsync();
            try
            {
                await using var reader = await failed.ReadResultsAsync();
                while (await reader.NextResultAsync()) { }
                throw new InvalidOperationException("Expected a group error.");
            }
            catch (MpgsqlServerException error)
            {
                Check
                (
                    error.QueryIndex == 0
                    && error.SqlState
                    == (parse
                        ? "42601"
                        : "22012"),
                    "Error index/SQLSTATE"
                );
                Check
                (
                    error.TransactionStatus == TransactionStatus.Idle,
                    "Error recovery boundary"
                );
            }
            await using var good = await recovered.ReadResultsAsync();
            Check
            (
                await good.ReadAsync() && good.GetInt64(0) == 9 && !await good.NextResultAsync(),
                "Next group recovery"
            );
        }
        Console.WriteLine("PASS helper Parse/Execute errors, skipped query and following Sync group");
    }

    private static async Task DeferredError(MpgsqlMessageSession session)
    {
        await NoRows
        (
            session,
            "create temp table mpgsql_helper_unique (id bigint unique deferrable initially deferred)"
        );
        await using var failed = session.CreateBatch();
        await failed.SendQueryAsync("insert into mpgsql_helper_unique values (1), (1)");
        await failed.SendSyncAsync();
        try
        {
            await failed.Completion;
            throw new InvalidOperationException("Expected a Sync error.");
        }
        catch (MpgsqlServerException error)
        {
            Check
            (
                error.QueryIndex is null && error.SqlState == "23505",
                "Deferred error belongs to Sync"
            );
        }
        // Dispose observes the error if no reader did; observe it once before the using scope ends.
        try { await failed.DisposeAsync(); }
        catch (MpgsqlServerException) { }
        await NoRows
        (
            session,
            "drop table mpgsql_helper_unique"
        );
        Console.WriteLine("PASS helper deferred constraint error at Sync and recovery");
    }

    private static async Task Cancellation(
        MpgsqlMessageSession session,
        CancellationToken lifetime
    )
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        var cancelled = session.CreateBatch(request.Token);
        await cancelled.SendQueryAsync("select 1::bigint from pg_sleep(0.2)");
        await cancelled.SendSyncAsync();
        Task opening = cancelled
            .ReadResultsAsync()
            .AsTask();
        request.Cancel();
        try
        {
            await opening;
            throw new InvalidOperationException("Expected logical cancellation.");
        }
        catch (OperationCanceledException) { }
        await cancelled.DisposeAsync();
        await using var survivor = session.CreateBatch();
        await survivor.SendQueryAsync("select 123::bigint");
        await survivor.SendSyncAsync();
        await cancelled.Completion; // SQL still runs; recovery belongs to the physical owner
        await using var reader = await survivor.ReadResultsAsync();
        Check
        (
            await reader.ReadAsync() && reader.GetInt64(0) == 123 && !await reader.NextResultAsync(),
            "Cancellation survivor"
        );
        Console.WriteLine("PASS helper logical cancellation preserves transport and later groups");
    }

    private static async Task NoRows(
        MpgsqlMessageSession session,
        string sql
    )
    {
        await using var batch = session.CreateBatch();
        await batch.SendQueryAsync(sql);
        await batch.SendSyncAsync();
        await using var reader = await batch.ReadResultsAsync();
        Check
        (
            reader.Columns.IsEmpty && !await reader.ReadAsync() && !await reader.NextResultAsync(),
            "NoData command"
        );
    }

    private static void Check(
        bool condition,
        string name
    )
    {
        if (!condition)
        {
            throw new InvalidOperationException(name);
        }
    }
}