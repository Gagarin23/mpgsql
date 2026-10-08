namespace Mpgsql.IntegrationTests;

internal static partial class UpperApiChecks
{
    private static void CheckTerminal(MpgsqlServerException error, string sqlState = "57P01",
        string? originalMessage = null)
    {
        Check(error.SqlState == sqlState, "terminal SQLSTATE retained");
        Check((error.Diagnostics.InvariantSeverity ?? error.Diagnostics.Severity) == "FATAL", "terminal severity retained");
        Check(error.Message == error.Diagnostics.Message && (originalMessage is null
                ? error.Message.Contains("terminating connection")
                : error.Message == originalMessage),
            "original received termination message retained");
        Check(error.TransactionStatus is null, "no inferred transaction status without ReadyForQuery");
    }

    private static async Task TerminalDiagnosticsAsync(string host, int port,
        string user, string password,
        string database, bool transactionPool,
        bool activeEofOnly, CancellationToken token)
    {
        await using var fixture = new UpperApiTestSource(host, port, user, password, database, 1);
        // Warm the single shared transport, then terminate only this test's own backend.
        Check((await fixture.Source.ExecuteScalarAsync<long>("select 1::bigint", cancellationToken: token)).Value == 1,
            "terminal diagnostics transport opened");
        var terminated = fixture.Sessions.Single();
        try
        {
            await fixture.Source.ExecuteScalarAsync<bool>("select pg_terminate_backend(pg_backend_pid())",
                cancellationToken: token);
            throw new InvalidDataException("Self-termination unexpectedly returned a result.");
        }
        catch (MpgsqlServerException error)
        {
            Check(!activeEofOnly, "pooler expected to forward no diagnostics in this fixture version");
            CheckTerminal(error);
            Check(error.QueryIndex == 0, "terminal active query index retained");
        }
        catch (EndOfStreamException) when (activeEofOnly)
        {
            // pg_doorman 3.10.6 sends EOF without an ErrorResponse (verified by raw socket probe).
        }
        try
        {
            await terminated.Completion.WaitAsync(TimeSpan.FromSeconds(5), token);
            throw new InvalidDataException("Terminated session completed successfully.");
        }
        catch (MpgsqlServerException error)
        {
            Check(!activeEofOnly, "session received diagnostics on the EOF-only fixture");
            CheckTerminal(error);
            Check(error.QueryIndex is null, "session failure does not attribute all groups to one query");
        }
        catch (EndOfStreamException) when (activeEofOnly) { }
        Check((await fixture.Source.ExecuteScalarAsync<long>("select 9::bigint", cancellationToken: token)).Value == 9,
            "DataSource replaces terminated transport");
        Check(fixture.FactoryCalls == 2 && !terminated.IsHealthy, "terminated transport retired once");

        if (transactionPool)
        {
            return; // idle transaction-pool clients do not pin a backend
        }
        // Independent sources now have independent session pools. Release the multiplexer's
        // pinned backend before the idle ADO.NET connection and observer use the two slots.
        await fixture.Source.DisposeAsync();
        await using var connection = await fixture.ClientSource.OpenConnectionAsync(token);
        var idle = connection.Session;
        var pid = await Scalar<int>(connection, "select pg_backend_pid()", token);
        await using var observer = new UpperApiTestSource(host, port, user, password, database, 1);
        Check((await observer.Source.ExecuteScalarAsync<bool>("select pg_terminate_backend($1)",
            new[] {MpgsqlParameterValue.Int32(pid)}, token)).Value, "observer terminates this test's idle backend");
        try
        {
            await idle.Completion.WaitAsync(TimeSpan.FromSeconds(5), token);
            throw new InvalidDataException("Idle terminated session completed successfully.");
        }
        catch (MpgsqlServerException error)
        {
            // The same pg_doorman version does send its own FATAL diagnostic for an idle backend.
            if (activeEofOnly)
            {
                CheckTerminal(error, "08006",
                    "server closed the connection unexpectedly while client was idle in transaction");
            }
            else
            {
                CheckTerminal(error);
            }
            Check(error.QueryIndex is null, "idle session has no query index");
        }
        Check(!idle.IsHealthy, "idle terminal failure retires transport");
    }
}