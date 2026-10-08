using System.Diagnostics;

namespace Mpgsql.IntegrationTests;

internal static partial class UpperApiChecks
{
    internal static async Task SharedSyncAsync(
        string host, int port,
        string user, string password,
        string database, CancellationToken token
    )
    {
        await using var fixture = new UpperApiTestSource
        (
            host, port, user, password, database,
            1, syncGroupSize: 3, syncGroupTimeout: TimeSpan.FromMilliseconds(50)
        );
        var source = fixture.Source;
        // Establish the physical session before concurrent admission, using an independent command boundary.
        Console.WriteLine("Shared Sync live PostgreSQL " + (await source.ExecuteScalarAsync<string>("SELECT current_setting('server_version')", cancellationToken: token)).Value);
        await source.ExecuteNonQueryAsync("CREATE TEMP TABLE mpgsql_shared_sync_test(value bigint NOT NULL)", cancellationToken: token);

        var inserted = source
            .ExecuteNonQueryAsync("INSERT INTO mpgsql_shared_sync_test VALUES (1)", cancellationToken: token)
            .AsTask();
        var failed = source
            .ExecuteScalarAsync<long>("SELECT 1/0::bigint", cancellationToken: token)
            .AsTask();
        var skipped = source
            .ExecuteNonQueryAsync("INSERT INTO mpgsql_shared_sync_test VALUES (2)", cancellationToken: token)
            .AsTask();
        MpgsqlSyncGroupException previous;
        try
        {
            await inserted;
            throw new InvalidDataException("The earlier command hid a shared-boundary error.");
        }
        catch (MpgsqlSyncGroupException error) { previous = error; }
        Check(!previous.WasSkipped && previous.FailedRequestIndex == 1, "shared error attributes the earlier completed command");
        try
        {
            await failed;
            throw new InvalidDataException("The failing request hid its SQL error.");
        }
        catch (MpgsqlServerException error)
        {
            Check
            (
                error.SqlState == "22012" && error.QueryIndex == 0 && ReferenceEquals(error, previous.Cause),
                "shared failing request keeps its original SQL diagnostic"
            );
        }
        try
        {
            await skipped;
            throw new InvalidDataException("The skipped request reported success.");
        }
        catch (MpgsqlSyncGroupException error) { Check(error.WasSkipped, "shared request explicitly reports it was skipped"); }
        Check
        (
            (await source.ExecuteScalarAsync<long>("SELECT count(*) FROM mpgsql_shared_sync_test", cancellationToken: token)).Value == 0,
            "shared implicit transaction rolls back the completed insert; skipped insert is not retried"
        );

        var values = await Task.WhenAll
        (
            Enumerable
                .Range(0, 64)
                .Select
                (async i =>
                    (await source.ExecuteScalarAsync<long>
                    (
                        "SELECT $1::bigint", new[]
                        {
                            MpgsqlParameterValue.Int64(i)
                        }, token
                    )).Value
                )
        );
        Check
        (
            values.SequenceEqual
            (
                Enumerable
                    .Range(0, 64)
                    .Select(i => (long)i)
            ), "shared groups retain caller identity"
        );

        var nullResult = source
            .ExecuteScalarAsync<long>("SELECT NULL::bigint", cancellationToken: token)
            .AsTask();
        var emptyResult = source
            .ExecuteScalarAsync<string>("SELECT ''::text", cancellationToken: token)
            .AsTask();
        var loneValue = source
            .ExecuteScalarAsync<long>("SELECT 7::bigint", cancellationToken: token)
            .AsTask();
        Check
        (
            (await nullResult).IsNull && (await emptyResult).Value == "" && (await loneValue).Value == 7,
            "shared NULL, empty and scalar values"
        );

        var opening = source
            .ExecuteReaderAsync
            (
                "SELECT 11::bigint, repeat('x',8192)::bytea FROM generate_series(1,128)", cancellationToken: token
            )
            .AsTask();
        var neighbour = source
            .ExecuteScalarAsync<long>("SELECT 12::bigint", cancellationToken: token)
            .AsTask();
        var last = source
            .ExecuteScalarAsync<long>("SELECT 13::bigint", cancellationToken: token)
            .AsTask();
        var reader = await opening;
        Check
        (
            reader.QueryIndex == 0 && await reader.ReadAsync() && reader.GetInt64(0) == 11
            && reader.GetRawValue(1)
                ?.Length == 8192, "shared large result first row"
        );
        await reader.DisposeAsync(); // Drains this query while preserving the neighbouring readers.
        Check((await neighbour).Value == 12 && (await last).Value == 13, "shared early disposal preserves neighbours");

        await using var observer = new UpperApiTestSource(host, port, user, password, database, 1);
        // Authenticate the observing transport before starting the sleeping request.
        Check
        (
            (await observer.Source.ExecuteScalarAsync<long>("SELECT 1::bigint", cancellationToken: token)).Value == 1,
            "shared cancellation observer is ready"
        );
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
        var marker = "mpgsql_shared_cancel_" + Guid
            .NewGuid()
            .ToString("N");
        var cancelled = source
            .ExecuteScalarAsync<long>
            (
                "SELECT 21::bigint FROM pg_sleep(1) /*" + marker + "*/",
                cancellationToken: request.Token
            )
            .AsTask();
        var surviving = source
            .ExecuteScalarAsync<long>("SELECT 22::bigint", cancellationToken: token)
            .AsTask();
        var survivingLast = source
            .ExecuteScalarAsync<long>("SELECT 23::bigint", cancellationToken: token)
            .AsTask();
        var deadline = Stopwatch.StartNew();
        while (!(await observer.Source.ExecuteScalarAsync<bool>
               (
                   "select exists(select 1 from pg_stat_activity where state='active' and query like $1 and pid<>pg_backend_pid())",
                   new[]
                   {
                       MpgsqlParameterValue.Text("%" + marker + "%")
                   }, token
               )).Value)
        {
            if (cancelled.IsCompleted)
            {
                await cancelled; // Preserve a driver/server failure instead of masking it as an observation timeout.
                throw new TimeoutException("The shared cancellation query finished before it could be observed.");
            }
            if (deadline.Elapsed > TimeSpan.FromSeconds(5))
            {
                throw new TimeoutException("The shared cancellation query did not become active.");
            }
            await Task.Delay(10, token);
        }
        var elapsed = Stopwatch.StartNew();
        request.Cancel();
        try
        {
            await cancelled;
            throw new InvalidDataException("Shared logical cancellation did not cancel consumption.");
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        Check
        (
            elapsed.Elapsed < TimeSpan.FromMilliseconds(750) && fixture.CancelRequests == 0,
            "shared logical cancellation releases the caller without a server CancelRequest"
        );
        Check((await surviving).Value == 22 && (await survivingLast).Value == 23, "shared SQL continues after logical cancellation");

        // This lone request cannot reach the count threshold. Only the independent timer can finish it.
        Check
        (
            (await source.ExecuteScalarAsync<long>("SELECT 99::bigint", cancellationToken: token)).Value == 99,
            "shared timer completes a lone request"
        );
        Check
        (
            fixture.FactoryCalls == 1 && fixture.Sessions.Single()
                                          .IsIdleAndHealthy
                                      && fixture.Sessions.Single()
                                          .BufferedRowBytes == 0, "shared groups recover the original healthy session and row budget"
        );
        Console.WriteLine("PASS shared DataSource Sync: count/timer, caller identity, NULL/empty, implicit rollback/skipped requests, early drain, logical cancellation, following request");
    }
}