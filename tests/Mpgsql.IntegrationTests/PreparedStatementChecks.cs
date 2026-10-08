using Mpgsql.Protocol;

namespace Mpgsql.IntegrationTests;

internal static class PreparedStatementChecks
{
    internal static async Task RunAsync(MpgsqlMessageSession session)
    {
        var baseline = session.TrackedStatementCount;
        var local = session.CreatePreparedStatement("select 1::bigint");
        Check(session.TrackedStatementCount == baseline, "Local handle is not strongly registered");
        local.Dispose();
        local.Dispose();
        try
        {
            await local.Prepared;
            throw new InvalidOperationException("Expected local disposal to fault unqueued preparation.");
        }
        catch (ObjectDisposedException) { }
        Check(await Count(session, local) == 0, "Local disposal creates no server statement");
        var scalar = session.CreatePreparedStatement
        (
            "select $1", new uint[]
            {
                20
            }
        );
        var arrays = session.CreatePreparedStatement
        (
            "select $1, $2, $3", new uint[]
            {
                20,
                1016,
                1016
            }
        );
        await using (var batch = session.CreateBatch())
        {
            var consumer = Task.Run
            (async () =>
                {
                    await using var reader = await batch.ReadResultsAsync();
                    for (var i = 0;
                         i < 65;
                         i++)
                    {
                        Check(reader.QueryIndex == i, "Prepared pipeline query index");
                        Check
                        (
                            await reader.ReadAsync() && reader.GetInt64(0) == (i == 64 ? 999 : i),
                            "Prepared pipeline value"
                        );
                        Check
                        (
                            !await reader.ReadAsync() && await reader.NextResultAsync() == i < 64,
                            "Prepared pipeline result count"
                        );
                    }
                }
            );
            var producer = Task.Run
            (async () =>
                {
                    Task[] sends =
                    [
                        batch
                            .SendPrepareAsync(scalar)
                            .AsTask(),
                        batch
                            .SendPrepareAsync(arrays)
                            .AsTask(),
                        .. Enumerable
                            .Range(0, 64)
                            .Select
                            (i =>
                                batch
                                    .SendQueryAsync
                                    (
                                        scalar, new[]
                                        {
                                            MpgsqlParameterValue.Int64(i)
                                        }
                                    )
                                    .AsTask()
                            ),
                        batch
                            .SendQueryAsync("select 999::bigint")
                            .AsTask()
                    ];
                    await batch.SendSyncAsync();
                    await Task.WhenAll(sends);
                }
            );
            await Task.WhenAll(producer, consumer);
            await Task.WhenAll(scalar.Prepared, arrays.Prepared);
        }
        Check(session.TrackedStatementCount == baseline + 2, "Confirmed statements remain strongly registered");
        CheckRejectedDispose(scalar);
        Check(await Count(session, scalar) == 1, "Named statement exists in its server session");
        await using (var first = session.CreateBatch())
        await using (var second = session.CreateBatch())
        {
            await first.SendQueryAsync
            (
                scalar, new[]
                {
                    MpgsqlParameterValue.Int64(101)
                }
            );
            await first.SendSyncAsync();
            await second.SendQueryAsync
            (
                scalar, new[]
                {
                    MpgsqlParameterValue.Int64(202)
                }
            );
            await second.SendSyncAsync();
            await ReadOne(second, 202);
            await ReadOne(first, 101);
        }
        await using (var batch = session.CreateBatch())
        {
            long?[] values = [long.MinValue, null, long.MaxValue];
            await batch.SendQueryAsync
            (
                arrays, new[]
                {
                    MpgsqlParameterValue.Int64(null),
                    MpgsqlParameterValue.Int64Array(ReadOnlyMemory<long>.Empty),
                    MpgsqlParameterValue.NullableInt64Array(values)
                }
            );
            await batch.SendSyncAsync();
            await using var reader = await batch.ReadResultsAsync();
            Check
            (
                await reader.ReadAsync() && reader.GetInt64(0) is null && reader.GetInt64Array(1)!.Value.IsEmpty,
                "Prepared scalar NULL and empty array"
            );
            Check
            (
                reader.GetNullableInt64Array(2)!.Value.Span.SequenceEqual(values) && !await reader.NextResultAsync(),
                "Prepared nullable array"
            );
        }
        var empty = session.CreatePreparedStatement(" ");
        await using (var batch = session.CreateBatch())
        {
            await batch.SendPrepareAsync(empty);
            await batch.SendQueryAsync(empty);
            await batch.SendCloseAsync(empty);
            await batch.SendSyncAsync();
            await using var reader = await batch.ReadResultsAsync();
            Check
            (
                reader.QueryIndex == 0 && reader.Columns.IsEmpty && !await reader.ReadAsync()
                && reader.CommandTag is null && !await reader.NextResultAsync(), "Prepared empty SQL"
            );
        }
        await Close(session, scalar, arrays);
        Check
        (
            await Count(session, scalar) == 0 && await Count(session, arrays) == 0 && await Count(session, empty) == 0,
            "Close removes named statements"
        );
        Check(session.TrackedStatementCount == baseline, "Confirmed Close releases statement registry");
        scalar.Dispose();
        arrays.Dispose();
        empty.Dispose();
        Console.WriteLine("PASS prepared named Parse, binary reuse, mixed pipeline, multiple groups, NULL/arrays and Close");
        await Errors(session);
    }

    private static async Task Errors(MpgsqlMessageSession session)
    {
        var baseline = session.TrackedStatementCount;
        var bad = session.CreatePreparedStatement("select from");
        await using (var batch = session.CreateBatch())
        {
            await batch.SendPrepareAsync(bad);
            await batch.SendQueryAsync(bad);
            await batch.SendSyncAsync();
            var error = await ExpectError(batch, "42601", null);
            await ExpectPreparationFailure(bad, error);
        }
        Check
        (
            session.TrackedStatementCount == baseline && await Count(session, bad) == 0,
            "Failed Parse releases registry and creates no server statement"
        );
        var skipped = session.CreatePreparedStatement
        (
            "select $1", new uint[]
            {
                20
            }
        );
        await using (var batch = session.CreateBatch())
        {
            await batch.SendQueryAsync("select 1::bigint / 0");
            await batch.SendPrepareAsync(skipped);
            await batch.SendQueryAsync
            (
                skipped, new[]
                {
                    MpgsqlParameterValue.Int64(1)
                }
            );
            await batch.SendSyncAsync();
            var error = await ExpectError(batch, "22012", 0);
            await ExpectPreparationFailure(skipped, error);
        }
        Check(await Count(session, skipped) == 0, "Skipped Parse creates no server statement");
        Check(session.TrackedStatementCount == baseline, "Skipped Parse releases registry");
        var divide = session.CreatePreparedStatement
        (
            "select 100::bigint / $1", new uint[]
            {
                20
            }
        );
        await using (var failed = session.CreateBatch())
        {
            await failed.SendPrepareAsync(divide);
            await failed.SendQueryAsync
            (
                divide, new[]
                {
                    MpgsqlParameterValue.Int64(0)
                }
            );
            await failed.SendSyncAsync();
            await ExpectError(failed, "22012", 0);
            await divide.Prepared;
        }
        Check(session.TrackedStatementCount == baseline + 1, "Execution error preserves confirmed preparation ownership");
        CheckRejectedDispose(divide);
        await using (var recovered = session.CreateBatch())
        {
            await recovered.SendQueryAsync
            (
                divide, new[]
                {
                    MpgsqlParameterValue.Int64(4)
                }
            );
            await recovered.SendSyncAsync();
            await ReadOne(recovered, 25);
        }
        await using (var failedClose = session.CreateBatch())
        {
            await failedClose.SendQueryAsync("select 1::bigint / 0");
            await failedClose.SendCloseAsync(divide);
            await failedClose.SendSyncAsync();
            await ExpectError(failedClose, "22012", 0);
        }
        Check(await Count(session, divide) == 1, "Skipped Close preserves the server statement");
        Check(session.TrackedStatementCount == baseline + 1, "Skipped Close preserves registry for explicit retry");
        await Close(session, bad, skipped, divide);
        Check(await Count(session, divide) == 0, "Close retry after recovery removes the statement");
        Check(session.TrackedStatementCount == baseline, "Failed preparations need no Close and confirmed Close releases ownership");
        bad.Dispose();
        skipped.Dispose();
        divide.Dispose();
        Console.WriteLine("PASS prepared Parse/Execute errors, skipped Parse/Close, recovery, survival after rollback and Close retry");
    }

    private static void CheckRejectedDispose(MpgsqlPreparedStatement statement)
    {
        var rejected = false;
        try { statement.Dispose(); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Confirmed statement requires explicit server Close before local disposal");
    }

    private static async Task ReadOne(MpgsqlQueryBatch batch, long expected)
    {
        await using var reader = await batch.ReadResultsAsync();
        Check
        (
            reader.QueryIndex == 0 && await reader.ReadAsync() && reader.GetInt64(0) == expected
            && !await reader.NextResultAsync(), "Reused prepared statement value"
        );
    }

    private static async Task<long> Count(MpgsqlMessageSession session, MpgsqlPreparedStatement statement)
    {
        await using var batch = session.CreateBatch();
        // The name is generated by Mpgsql and contains only ASCII letters, digits and underscores.
        await batch.SendQueryAsync($"select count(*) from pg_prepared_statements where name = '{statement.Name}'");
        await batch.SendSyncAsync();
        await using var reader = await batch.ReadResultsAsync();
        Check(await reader.ReadAsync(), "Prepared statement catalog row");
        var count = reader.GetInt64(0)!.Value;
        Check(!await reader.NextResultAsync(), "Prepared statement catalog result count");
        return count;
    }

    private static async Task Close(MpgsqlMessageSession session, params MpgsqlPreparedStatement[] statements)
    {
        await using var batch = session.CreateBatch();
        foreach (var statement in statements)
        {
            await batch.SendCloseAsync(statement);
        }
        await batch.SendSyncAsync();
        await batch.Completion;
    }

    private static async Task<MpgsqlServerException> ExpectError(
        MpgsqlQueryBatch batch, string sqlState,
        int? queryIndex
    )
    {
        try
        {
            await batch.Completion;
            throw new InvalidOperationException("Expected a prepared statement group error.");
        }
        catch (MpgsqlServerException error)
        {
            Check
            (
                error.SqlState == sqlState && error.QueryIndex == queryIndex
                                           && error.TransactionStatus == TransactionStatus.Idle, "Prepared error diagnostics"
            );
            return error;
        }
        finally
        {
            try { await batch.DisposeAsync(); }
            catch (MpgsqlServerException) { }
        }
    }

    private static async Task ExpectPreparationFailure(MpgsqlPreparedStatement statement, MpgsqlServerException expected)
    {
        try
        {
            await statement.Prepared;
            throw new InvalidOperationException("Expected failed or skipped preparation.");
        }
        catch (MpgsqlServerException error)
        {
            Check(ReferenceEquals(error, expected), "Preparation reports its recovered group error");
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException(name);
        }
    }
}