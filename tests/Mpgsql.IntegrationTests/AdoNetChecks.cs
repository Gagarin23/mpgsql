using System.Data;
using System.Data.Common;

namespace Mpgsql.IntegrationTests;

internal static class AdoNetChecks
{
    internal static async Task RunAsync(
        string host, int port,
        string user, string password,
        string database, bool singleSessionCancellation = false
    )
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = deadline.Token;
        var settings = new MpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port,
            Username = user,
            Password = password,
            Database = database,
            SslMode = MpgsqlSslMode.Disable
        };
        await using DbDataSource source = new MpgsqlDataSource(settings.ConnectionString);
        await using var connection = await source.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "select $1::bigint";
        var value = new MpgsqlParameter<long>(TypeOid.Int64, 7);
        command.Parameters.Add(value);
        for (var i = 0;
             i < 8;
             i++)
        {
            value.TypedValue = i;
            Check(Equals(await command.ExecuteScalarAsync(token), (long)i), "reused DbCommand");
        }
        command.Parameters.Clear();
        command.CommandText = "select null::bigint";
        Check(ReferenceEquals(await command.ExecuteScalarAsync(token), DBNull.Value), "SQL NULL");
        command.CommandText = "select 1::bigint where false";
        Check(await command.ExecuteScalarAsync(token) is null, "empty scalar");
        command.CommandText = "select 1/0";
        try
        {
            await command.ExecuteScalarAsync(token);
            throw new InvalidDataException("SQL error hidden.");
        }
        catch (MpgsqlPostgresException error) { Check(error.SqlState == "22012", "SQLSTATE"); }

        await using (var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token))
        {
            command.Transaction = transaction;
            command.CommandText = "create temporary table mpgsql_ado_test(value bigint) on commit drop";
            await command.ExecuteNonQueryAsync(token);
            await using var batch = connection.CreateBatch();
            batch.Transaction = transaction;
            var insert = batch.CreateBatchCommand();
            insert.CommandText = "insert into mpgsql_ado_test values ($1)";
            insert.Parameters.Add(new MpgsqlParameter<long>(TypeOid.Int64, 42));
            batch.BatchCommands.Add(insert);
            var select = batch.CreateBatchCommand();
            select.CommandText = "select value from mpgsql_ado_test";
            batch.BatchCommands.Add(select);
            await batch.PrepareAsync(token);
            for (var i = 0;
                 i < 2;
                 i++)
            {
                await using var reader = await batch.ExecuteReaderAsync(token);
                Check(!reader.HasRows && await reader.NextResultAsync(token) && reader.HasRows, "DbBatch result metadata");
                Check(await reader.ReadAsync(token) && reader.GetInt64(0) == 42, "prepared batch typed row");
                while (await reader.ReadAsync(token)) { }
                Check(!await reader.NextResultAsync(token) && reader.RecordsAffected == 1 && insert.RecordsAffected == 1, "affected rows");
            }
            await ((MpgsqlBatch)batch).UnprepareAsync(token);
            command.CommandText = "select $1::bigint";
            command.Parameters.Add(new MpgsqlParameter<long>(TypeOid.Int64, 9));
            await command.PrepareAsync(token);
            Check(Equals(await command.ExecuteScalarAsync(token), 9L), "prepared command");
            await ((MpgsqlCommand)command).UnprepareAsync(token);
            await transaction.RollbackAsync(token);
        }
        command.Transaction = null;
        command.Parameters.Clear();
        command.CommandText = "select pg_sleep(10)";
        using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            var pending = command.ExecuteReaderAsync(cancel.Token);
            await Task.Delay(100, token);
            cancel.Cancel();
            try
            {
                await pending;
                throw new InvalidDataException("Cancellation hidden.");
            }
            catch (OperationCanceledException) { }
        }
        command.CommandText = "select 99::bigint";
        Check(Equals(await command.ExecuteScalarAsync(token), 99L), "post-cancellation recovery");
        command.CommandText = "select pg_sleep(10)";
        var manual = command.ExecuteReaderAsync(token);
        await Task.Delay(100, token);
        command.Cancel();
        try
        {
            await manual;
            throw new InvalidDataException("Cancel() hidden.");
        }
        catch (OperationCanceledException) { }
        if (singleSessionCancellation)
        {
            // pg_doorman 3.10.6 drops the main client's cancel-map entry when the
            // first cancel-mode Client is dropped. Session mode does not reclaim
            // that entry between queries. A second cancel therefore cannot reach
            // PostgreSQL; our bounded recovery must retire the physical session.
            // Reopen explicitly in this fixture, never retry a query in the driver.
            Check(connection.State == ConnectionState.Broken, "unacknowledged repeated pooler cancellation retires the session");
            await connection.CloseAsync();
            await connection.OpenAsync(token);
            Console.WriteLine("KNOWN pg_doorman 3.10.6 session limitation: second CancelRequest loses its target mapping; bounded recovery closes the session, explicit reopen succeeds.");
        }
        command.CommandTimeout = 1;
        try
        {
            await command.ExecuteReaderAsync(token);
            throw new InvalidDataException("Command timeout hidden.");
        }
        catch (MpgsqlException error) when (error.InnerException is TimeoutException) { }
        command.CommandTimeout = 0;
        command.CommandText = "select 98::bigint";
        Check(Equals(await command.ExecuteScalarAsync(token), 98L), "manual/timeout cancellation cannot affect next execution");
        command.CommandText = "select i::bigint from generate_series(1,4096) as s(i)";
        var active = await command.ExecuteReaderAsync(token);
        while (await active.ReadAsync(token))
        {
            var v = active.GetInt64(0);
        }
        Check(active.HasRows, "active reader");
        await active.DisposeAsync();
        command.CommandText = "select 100::bigint";
        Check(Equals(await command.ExecuteScalarAsync(token), 100L), "async reader drain");
        var closeReader = await command.ExecuteReaderAsync(CommandBehavior.CloseConnection, token);
        await closeReader.DisposeAsync();
        Check(connection.State == ConnectionState.Closed, "CloseConnection");
        await connection.OpenAsync(token);
        command.CommandText = "select 101::bigint";
        Check(Equals(await command.ExecuteScalarAsync(token), 101L), "reopened connection");
        Console.WriteLine
        (
            "PASS native ADO.NET: startup/authentication, Db* reuse, NULL, batch, Prepare/Unprepare, local transaction, cancellation recovery, reader drain, CloseConnection, reopen."
            + (singleSessionCancellation ? " Repeated same-session CancelRequest has the explicitly checked pooler limitation above." : "")
        );
    }
    private static void Check(bool condition, string label)
    {
        if (!condition)
        {
            throw new InvalidDataException(label);
        }
    }
}