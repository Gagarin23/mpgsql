using System.Diagnostics;

namespace Mpgsql.IntegrationTests;

internal static partial class UpperApiChecks
{
    private static async Task LogicalCancellationAsync(string host, int port,
        string user,
        string password, string database,
        CancellationToken token)
    {
        await using var fixture = new UpperApiTestSource(host, port, user, password, database, 1);
        // Observe the active backend through another transport; the shared one is executing pg_sleep.
        await using var observer = new UpperApiTestSource(host, port, user, password, database, 1);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
        var marker = "mpgsql_logical_cancel_" + Guid.NewGuid().ToString("N");
        var pending = fixture.Source.ExecuteScalarAsync<long>(
            "select 11::bigint from pg_sleep(1) /*" + marker + "*/", cancellationToken: request.Token).AsTask();
        var deadline = Stopwatch.StartNew();
        while (!(await observer.Source.ExecuteScalarAsync<bool>(
                   "select exists(select 1 from pg_stat_activity where state='active' and query like $1 and pid<>pg_backend_pid())",
                   new[] {MpgsqlParameterValue.Text("%" + marker + "%")}, token)).Value)
        {
            if (pending.IsCompleted || deadline.Elapsed > TimeSpan.FromSeconds(5))
            {
                throw new TimeoutException("The logical-cancellation query did not become active.");
            }
            await Task.Delay(20, token);
        }

        var neighbour = fixture.Source.ExecuteScalarAsync<long>("select 8::bigint", cancellationToken: token).AsTask();
        var elapsed = Stopwatch.StartNew();
        request.Cancel();
        try
        {
            await pending.WaitAsync(TimeSpan.FromSeconds(5), token);
            throw new InvalidDataException("Logical cancellation did not cancel consumption.");
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        Check(elapsed.Elapsed < TimeSpan.FromMilliseconds(750), "logical cancellation returns before sleeping SQL finishes");
        Check(fixture.CancelRequests == 0, "logical cancellation does not send CancelRequest");
        Check((await neighbour.WaitAsync(TimeSpan.FromSeconds(5), token)).Value == 8, "shared neighbour survives logical cancellation");
        Check((await fixture.Source.ExecuteScalarAsync<long>("select 9::bigint", cancellationToken: token)).Value == 9,
            "shared transport reuse after background ReadyForQuery recovery");
        Check(fixture.FactoryCalls == 1 && fixture.Sessions.Single().IsHealthy,
            "logical cancellation preserves the original shared transport");
    }
}