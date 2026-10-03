using System.Diagnostics;

namespace Mpgsql.IntegrationTests;

internal static partial class UpperApiChecks
{
    internal static async Task RunAsync(string host, int port, string user, string password, string database, bool transactionPool)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = lifetime.Token;
        await using (var fixture = new UpperApiTestSource(host, port, user, password, database))
        {
            var values = await Task.WhenAll(Enumerable.Range(0, 32).Select(async i =>
                (await fixture.Source.ExecuteScalarAsync<long>("select $1", new[] {MpgsqlParameter.Int64(i)}, token)).Value));
            Check(values.SequenceEqual(Enumerable.Range(0, 32).Select(i => (long)i)), "multiplexed scalar results");
            await TypedValuesAsync(fixture.Source, token);
            await ErrorAndDrainAsync(fixture.Source, token);
            await CancelAsync(fixture, token);
        }
        await SessionStateAsync(host, port, user, password, database, transactionPool, token);
        if (transactionPool) await BackendSwapAsync(host, port, user, password, database, token);
        Console.WriteLine($"PASS upper API: multiplexing, all 46 OIDs, error/drain recovery, server cancellation, explicit cleanup, prepared statements; mode={(transactionPool ? "transaction" : "session/direct")}");
    }

    private static async Task ErrorAndDrainAsync(MpgsqlDataSource source, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using (var batch = connection.CreateBatch())
        {
            batch.Commands.Add(batch.CreateCommand("select 7::bigint"));
            batch.Commands.Add(batch.CreateCommand("select 1/0"));
            try { await batch.ExecuteScalarAsync<long>(token); throw new InvalidDataException("Scalar hid a later SQL error."); }
            catch (MpgsqlServerException ex) { Check(ex.SqlState == "22012", "scalar terminal error"); }
        }
        await using (var batch = connection.CreateBatch())
        {
            batch.Commands.Add(batch.CreateCommand("select repeat('x',8192) from generate_series(1,32)"));
            var second = batch.CreateCommand("select $1");
            second.Parameters.Add(MpgsqlParameter.Text(new string('y', 1024 * 1024)));
            batch.Commands.Add(second); batch.Commands.Add(batch.CreateCommand("select 1/0"));
            var reader = await batch.ExecuteReaderAsync(token);
            Check(await reader.ReadAsync(), "large first row");
            try { await reader.DisposeAsync(); throw new InvalidDataException("Early disposal skipped later commands."); }
            catch (MpgsqlServerException ex) { Check(ex.SqlState == "22012", "early disposal observes accepted trailing command"); }
        }
        Check(await Scalar<long>(connection, "select 8::bigint", token) == 8, "connection recovery after draining");
    }

    private static async Task CancelAsync(UpperApiTestSource fixture, CancellationToken token)
    {
        await using var connection = await fixture.Source.OpenConnectionAsync(token);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
        string marker = "mpgsql_cancel_" + Guid.NewGuid().ToString("N");
        await using var command = connection.CreateCommand("select pg_sleep(10) /*" + marker + "*/");
        var pending = command.ExecuteReaderAsync(request.Token).AsTask();
        // Establish that SQL is active before cancellation; merely timing the client is insufficient.
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            bool active = (await fixture.Source.ExecuteScalarAsync<bool>(
                "select exists(select 1 from pg_stat_activity where state='active' and query like $1 and pid<>pg_backend_pid())",
                new[] {MpgsqlParameter.Text("%" + marker + "%")}, token)).Value;
            if (active) break;
            if (deadline.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("pg_sleep did not become active.");
            await Task.Delay(20, token);
        }
        var elapsed = Stopwatch.StartNew(); request.Cancel();
        try { await pending; throw new InvalidDataException("Explicit SQL cancellation did not cancel consumption."); }
        catch (OperationCanceledException) { }
        Check(fixture.CancelRequests == 1 && elapsed.Elapsed < TimeSpan.FromSeconds(5), "server CancelRequest and closed cancel channel");
        Check(await Scalar<long>(connection, "select 9::bigint", token) == 9, "reuse after cancelled SQL");
    }

    private static async Task SessionStateAsync(string host, int port, string user, string password, string database,
        bool transactionPool, CancellationToken token)
    {
        await using var fixture = new UpperApiTestSource(host, port, user, password, database, maxConnections: 1, prepare: true);
        var connection = await fixture.Source.OpenConnectionAsync(token);
        var session = fixture.Sessions.Single();
        string baseline = await Scalar<string>(connection, "select current_setting('application_name')", token);
        if (transactionPool) await NonQuery(connection, "BEGIN", token);
        await NonQuery(connection, "SET application_name='mpgsql_state'", token);
        await NonQuery(connection, "LISTEN mpgsql_state", token);
        await NonQuery(connection, "CREATE TEMP TABLE mpgsql_state(value bigint)" + (transactionPool ? " ON COMMIT DROP" : ""), token);
        long lockKey = Random.Shared.NextInt64(1, long.MaxValue);
        Check(await Scalar<bool>(connection, "select pg_try_advisory_lock(" + lockKey + ")", token), "session lock acquired");
        if (!transactionPool) await NonQuery(connection, "BEGIN", token);
        await NonQuery(connection, "DECLARE mpgsql_cursor CURSOR " + (transactionPool ? "" : "WITH HOLD ") + "FOR SELECT 1", token);
        if (!transactionPool)
        {
            await NonQuery(connection, "COMMIT", token);
            await connection.DisposeAsync();
            connection = await fixture.Source.OpenConnectionAsync(token);
            Check(fixture.FactoryCalls == 1, "logical lease reuse without reset");
            Check(await Scalar<string>(connection, "select current_setting('application_name')", token) == "mpgsql_state", "setting survives lease return");
            Check(await Scalar<long>(connection, "select count(*) from pg_listening_channels()", token) == 1, "LISTEN survives lease return");
        }
        Check(await Scalar<long>(connection, "select count(*) from pg_cursors where name='mpgsql_cursor'", token) == 1, "cursor before cleanup");
        await connection.ClearSessionStateAsync(MpgsqlSessionCleanup.Settings, token);
        Check(await Scalar<string>(connection, "select current_setting('application_name')", token) == baseline, "selective settings reset");
        Check(await Scalar<bool>(connection, "select to_regclass('pg_temp.mpgsql_state') is not null", token), "settings reset retains temp table");
        await connection.ClearSessionStateAsync(MpgsqlSessionCleanup.ListenSubscriptions | MpgsqlSessionCleanup.AdvisoryLocks
            | MpgsqlSessionCleanup.Cursors | MpgsqlSessionCleanup.TemporaryObjects, token);
        Check(await Scalar<long>(connection, "select count(*) from pg_cursors where name='mpgsql_cursor'", token) == 0, "cursors closed");
        Check(await Scalar<bool>(connection, "select to_regclass('pg_temp.mpgsql_state') is null", token), "temporary objects dropped");
        Check(await Scalar<long>(connection, "select count(*) from pg_locks where pid=pg_backend_pid() and locktype='advisory'", token) == 0, "session locks released");
        if (transactionPool) await NonQuery(connection, "COMMIT", token);
        Check(await Scalar<long>(connection, "select count(*) from pg_listening_channels()", token) == 0, "LISTEN subscriptions cleared");
        Check(await PreparedValue(session, fixture.Statement(session), 41, token) == 42, "named prepared expression survives cleanup");
        await connection.DisposeAsync();
    }

    private static async Task BackendSwapAsync(string host, int port, string user, string password, string database, CancellationToken token)
    {
        // The test pooler configurations have exactly two backend slots and no reserve slots.
        await using var fixture = new UpperApiTestSource(host, port, user, password, database, maxConnections: 3, prepare: true);
        await using var a = await fixture.Source.OpenConnectionAsync(token);
        var aSession = fixture.Sessions.Single();
        await using var b = await fixture.Source.OpenConnectionAsync(token);
        await using var c = await fixture.Source.OpenConnectionAsync(token);
        await NonQuery(a, "BEGIN", token); int first = await Scalar<int>(a, "select pg_backend_pid()", token);
        await NonQuery(b, "BEGIN", token); int second = await Scalar<int>(b, "select pg_backend_pid()", token);
        Check(first != second, "two independently pinned backends");
        await NonQuery(a, "COMMIT", token);
        await NonQuery(c, "BEGIN", token); Check(await Scalar<int>(c, "select pg_backend_pid()", token) == first, "third client occupies released backend");
        await NonQuery(b, "COMMIT", token);
        await NonQuery(a, "BEGIN", token); int changed = await Scalar<int>(a, "select pg_backend_pid()", token);
        Check(changed == second && changed != first, "backend changes on the same client transport");
        await a.ClearSessionStateAsync(MpgsqlSessionCleanup.Settings | MpgsqlSessionCleanup.TemporaryObjects, token);
        Check(await PreparedValue(aSession, fixture.Statement(aSession), 42, token) == 43, "prepared Bind after backend reassignment");
        await NonQuery(a, "COMMIT", token); await NonQuery(c, "COMMIT", token);
        Console.WriteLine("PASS transaction pooler backend reassignment and named prepared Bind");
    }

    private static async Task<long> PreparedValue(MpgsqlMessageSession session, MpgsqlPreparedStatement statement, long input, CancellationToken token)
    {
        await using var group = session.CreateBatch(token);
        await group.SendQueryAsync(statement, new[] {MpgsqlParameter.Int64(input)}); await group.SendSyncAsync();
        await using var reader = await group.ReadResultsAsync();
        Check(await reader.ReadAsync(), "prepared row"); long result = reader.GetFieldValue<long>(0);
        Check(!await reader.NextResultAsync(), "prepared result boundary"); return result;
    }

    private static async Task<T> Scalar<T>(MpgsqlConnection connection, string sql, CancellationToken token)
    { await using var command = connection.CreateCommand(sql); return (await command.ExecuteScalarAsync<T>(token)).Value; }
    private static async Task NonQuery(MpgsqlConnection connection, string sql, CancellationToken token)
    { await using var command = connection.CreateCommand(sql); await command.ExecuteNonQueryAsync(token); }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidDataException("Upper API check failed: " + message); }
}
