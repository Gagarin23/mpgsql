using System.Text;
using Mpgsql.Protocol;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class TerminalDiagnosticsTests
{
    private const string OriginalMessage = "Завершение backend: original reason";

    private static byte[] Diagnostic(string severity = "FATAL", bool invariant = true)
    {
        return Packet
        (
            'E', Encoding.UTF8.GetBytes
            (
                "S" + (invariant ? "локализовано" : severity) + "\0"
                + (invariant ? "V" + severity + "\0" : "")
                + "C57P01\0M" + OriginalMessage + "\0Doriginal detail\0Horiginal hint\0zextension field\0\0"
            )
        );
    }

    private static void CheckDiagnostics(
        MpgsqlServerException error, string severity = "FATAL",
        bool invariant = true
    )
    {
        Assert.Equal("57P01", error.SqlState);
        Assert.Equal(OriginalMessage, error.Message);
        Assert.Equal(OriginalMessage, error.Diagnostics.Message);
        Assert.Equal(invariant ? "локализовано" : severity, error.Diagnostics.Severity);
        Assert.Equal(invariant ? severity : null, error.Diagnostics.InvariantSeverity);
        Assert.Equal("original detail", error.Diagnostics.GetField((byte)'D'));
        Assert.Equal("original hint", error.Diagnostics.GetField((byte)'H'));
        Assert.Equal("extension field", error.Diagnostics.GetField((byte)'z'));
        Assert.Null(error.TransactionStatus);
    }

    private static async Task PublishDiagnostic(
        ScriptedSession wire, byte[] bytes,
        bool eof = true
    )
    {
        try { await wire.WriteAsync(bytes, 3); }
        catch (MpgsqlServerException)
        {
            /* FATAL may complete the reader before the test writer's flush. */
        }
        if (eof)
        {
            await wire.Incoming.Writer.CompleteAsync();
        }
    }

    private static async Task DisposeBatch(MpgsqlQueryBatch batch)
    {
        try { await batch.DisposeAsync(); }
        catch (MpgsqlServerException)
        {
            /* retained error may first be observed by disposal */
        }
    }

    [Theory, InlineData("FATAL", true), InlineData("PANIC", true), InlineData("FATAL", false)]
    public async Task FatalThenEofPreservesOwnedDiagnosticsAndUnknownStatus(string severity, bool invariant)
    {
        await using var wire = new ScriptedSession();
        var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await batch.SendQueryAsync("select fatal");
        await batch.SendSyncAsync();
        var opening = batch
            .ReadResultsAsync()
            .AsTask();
        await PublishDiagnostic(wire, Diagnostic(severity, invariant));

        var sessionError = await Assert.ThrowsAsync<MpgsqlServerException>
        (() =>
            wire.Session.Completion.WaitAsync(TestTimeout, TestContext.Current.CancellationToken)
        );
        CheckDiagnostics(sessionError, severity, invariant);
        Assert.Null(sessionError.QueryIndex);
        var groupError = await Assert.ThrowsAsync<MpgsqlServerException>
        (() =>
            batch.Completion.WaitAsync(TestTimeout, TestContext.Current.CancellationToken)
        );
        CheckDiagnostics(groupError, severity, invariant);
        Assert.Equal(0, groupError.QueryIndex);
        Assert.Null(batch.TransactionStatus);
        CheckDiagnostics(await Assert.ThrowsAsync<MpgsqlServerException>(() => opening), severity, invariant);
        Assert.False(wire.Session.IsHealthy);
        await DisposeBatch(batch);
    }

    [Theory, InlineData("FATAL"), InlineData("PANIC")]
    public async Task IdleFatalPreservesDiagnosticsWithoutWaitingForEof(string severity)
    {
        await using var wire = new ScriptedSession();
        await PublishDiagnostic(wire, Diagnostic(severity), false);
        var error = await Assert.ThrowsAsync<MpgsqlServerException>
        (() => wire.Session.Completion.WaitAsync
            (
                TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken
            )
        );
        CheckDiagnostics(error, severity);
        Assert.Null(error.QueryIndex);
        Assert.False(wire.Session.IsHealthy);
        Assert.Same
        (
            error, Assert.Throws<MpgsqlServerException>
            (() =>
                wire.Session.CreateBatch(TestContext.Current.CancellationToken)
            )
        );
    }

    [Fact]
    public async Task FatalCompletesEveryOutstandingGroupAndPreparation()
    {
        await using var wire = new ScriptedSession();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var active = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await active.SendQueryAsync("select active");
        await active.SendSyncAsync();
        var neighbour = wire.Session.CreateBatch(cancellation.Token);
        await neighbour.SendQueryAsync("select neighbour");
        await neighbour.SendSyncAsync();
        cancellation.Cancel();
        var preparing = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        var statement = wire.Session.CreatePreparedStatement
        (
            "select $1", new uint[]
            {
                20
            }
        );
        await preparing.SendPrepareAsync(statement); // collecting, without Sync
        var unpublished = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await PublishDiagnostic(wire, Diagnostic());

        foreach (var batch in new[]
                 {
                     active,
                     neighbour,
                     preparing,
                     unpublished
                 })
        {
            var error = await Assert.ThrowsAsync<MpgsqlServerException>
            (() =>
                batch.Completion.WaitAsync(TestTimeout, TestContext.Current.CancellationToken)
            );
            CheckDiagnostics(error);
            Assert.Equal(batch == active ? 0 : null, error.QueryIndex);
            Assert.Null(batch.TransactionStatus);
            await DisposeBatch(batch);
        }
        CheckDiagnostics(await Assert.ThrowsAsync<MpgsqlServerException>(() => statement.Prepared));
        await Assert.ThrowsAsync<MpgsqlServerException>(() => unpublished.Sealed);
        await Assert.ThrowsAsync<MpgsqlServerException>(() => preparing.Sealed);
        CheckDiagnostics(await Assert.ThrowsAsync<MpgsqlServerException>(() => wire.Session.Completion));
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task UnrecoveredErrorRetainsDiagnosticsWhenInputFails(bool ioFailure)
    {
        await using var wire = new ScriptedSession();
        var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await batch.SendQueryAsync("select bad");
        await batch.SendSyncAsync();
        // V=ERROR overrides the localized S field. Acknowledgement proves the error was accepted.
        await wire.WriteAsync
        (
            Join
            (
                Diagnostic("ERROR"),
                Packet('S', Encoding.UTF8.GetBytes("r04_received\0yes\0"))
            )
        );
        await WaitForAcknowledgement(wire);
        Assert.False(batch.Completion.IsCompleted);
        Assert.True(wire.Session.IsHealthy);
        var cause = ioFailure ? new IOException("original input failure") : null;
        await wire.Incoming.Writer.CompleteAsync(cause);

        var error = await Assert.ThrowsAsync<MpgsqlServerException>
        (() =>
            batch.Completion.WaitAsync(TestTimeout, TestContext.Current.CancellationToken)
        );
        CheckDiagnostics(error, "ERROR");
        Assert.Equal(0, error.QueryIndex);
        if (ioFailure)
        {
            Assert.Same(cause, error.InnerException);
        }
        else
        {
            Assert.IsType<EndOfStreamException>(error.InnerException);
        }
        var sessionError = await Assert.ThrowsAsync<MpgsqlServerException>(() => wire.Session.Completion);
        CheckDiagnostics(sessionError, "ERROR");
        Assert.Same(error.InnerException, sessionError.InnerException);
        Assert.Null(batch.TransactionStatus);
        await DisposeBatch(batch);
    }

    private static async Task WaitForAcknowledgement(ScriptedSession wire)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TestTimeout);
        while (!wire.Session.TryGetParameter("r04_received", out _))
        {
            await Task.Delay(1, timeout.Token);
        }
    }

    [Fact]
    public async Task OutputFailureAndSendTasksRetainTheReceivedError()
    {
        await using var wire = new ScriptedSession();
        var first = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await first.SendQueryAsync("select bad");
        await first.SendSyncAsync();
        await wire.WriteAsync
        (
            Join
            (
                Diagnostic("ERROR"),
                Packet('S', Encoding.UTF8.GetBytes("r04_received\0yes\0"))
            )
        );
        await WaitForAcknowledgement(wire);
        var cause = new IOException("original output failure");
        await wire.Outgoing.Reader.CompleteAsync(cause);
        var next = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        var send = next
            .SendQueryAsync("select next")
            .AsTask();
        CheckDiagnostics
        (
            await Assert.ThrowsAsync<MpgsqlServerException>
            (() =>
                send.WaitAsync(TestTimeout, TestContext.Current.CancellationToken)
            ), "ERROR"
        );
        foreach (var batch in new[]
                 {
                     first,
                     next
                 })
        {
            var error = await Assert.ThrowsAsync<MpgsqlServerException>(() => batch.Completion);
            CheckDiagnostics(error, "ERROR");
            Assert.Same(cause, error.InnerException);
            Assert.Null(batch.TransactionStatus);
            await DisposeBatch(batch);
        }
        CheckDiagnostics(await Assert.ThrowsAsync<MpgsqlServerException>(() => wire.Session.Completion), "ERROR");
    }

    [Fact]
    public async Task FatalDuringErrorRecoveryRetainsTheTerminalReason()
    {
        await using var wire = new ScriptedSession();
        var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await batch.SendQueryAsync("select bad");
        await batch.SendSyncAsync();
        await wire.WriteAsync
        (
            Join
            (
                Error(),
                Packet('S', Encoding.UTF8.GetBytes("r04_received\0yes\0"))
            )
        );
        await WaitForAcknowledgement(wire);
        Assert.False(batch.Completion.IsCompleted);
        await PublishDiagnostic(wire, Diagnostic());
        CheckDiagnostics
        (
            await Assert.ThrowsAsync<MpgsqlServerException>
            (() =>
                batch.Completion.WaitAsync(TestTimeout, TestContext.Current.CancellationToken)
            )
        );
        CheckDiagnostics(await Assert.ThrowsAsync<MpgsqlServerException>(() => wire.Session.Completion));
        Assert.Null(batch.TransactionStatus);
        await DisposeBatch(batch);
    }

    [Fact]
    public async Task RecoveredSqlErrorIsNotReusedForALaterEof()
    {
        await using var wire = new ScriptedSession();
        var recovered = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await recovered.SendQueryAsync("select bad");
        await recovered.SendSyncAsync();
        await wire.WriteAsync(Join(Diagnostic("ERROR"), Ready('E')));
        var sqlError = await Assert.ThrowsAsync<MpgsqlServerException>
        (() =>
            recovered.Completion.WaitAsync(TestTimeout, TestContext.Current.CancellationToken)
        );
        Assert.Equal(TransactionStatus.FailedTransaction, sqlError.TransactionStatus);
        Assert.Null(sqlError.InnerException);
        await DisposeBatch(recovered);
        var next = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await next.SendQueryAsync("rollback");
        await next.SendSyncAsync();
        await wire.Incoming.Writer.CompleteAsync();
        await Assert.ThrowsAsync<EndOfStreamException>
        (() => next.Completion.WaitAsync
            (
                TestTimeout,
                TestContext.Current.CancellationToken
            )
        );
        await Assert.ThrowsAsync<EndOfStreamException>(() => wire.Session.Completion);
        Assert.Null(next.TransactionStatus);
    }

    [Fact]
    public async Task FatalAfterACompletedGroupDoesNotRewriteItsStatus()
    {
        await using var wire = new ScriptedSession();
        var done = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await done.SendQueryAsync("select 1");
        await done.SendSyncAsync();
        await wire.WriteAsync(Join(Query(1), Ready()));
        await done.Completion.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        await PublishDiagnostic(wire, Diagnostic());
        CheckDiagnostics(await Assert.ThrowsAsync<MpgsqlServerException>(() => wire.Session.Completion));
        Assert.True(done.Completion.IsCompletedSuccessfully);
        Assert.Equal(TransactionStatus.Idle, done.TransactionStatus);
        await done.DisposeAsync();
    }

    [Fact]
    public async Task FatalWakesBlockedAndQueuedSendsWithoutReadingReleasedInputs()
    {
        using var input = new BlockingInputMemory();
        await using var wire = new ScriptedSession(true);
        var first = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        var published = first
            .SendQueryAsync("select first")
            .AsTask();
        var firstSync = first
            .SendSyncAsync()
            .AsTask();
        var held = await wire.Outgoing.Reader.ReadAsync(TestContext.Current.CancellationToken);
        var queued = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        var send = queued
            .SendQueryAsync
            (
                "select $1", new[]
                {
                    MpgsqlParameterValue.Int64Array(input.Memory)
                }
            )
            .AsTask();
        var sync = queued
            .SendSyncAsync()
            .AsTask();
        await PublishDiagnostic(wire, Diagnostic());
        foreach (var task in new[]
                 {
                     published,
                     firstSync,
                     send,
                     sync,
                     first.Completion,
                     queued.Completion
                 })
        {
            CheckDiagnostics
            (
                await Assert.ThrowsAsync<MpgsqlServerException>
                (() =>
                    task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken)
                )
            );
        }
        input.Revoke();
        Assert.Equal(0, input.Reads);
        Assert.Null(first.TransactionStatus);
        Assert.Null(queued.TransactionStatus);
        wire.Outgoing.Reader.AdvanceTo(held.Buffer.End);
        await DisposeBatch(first);
        await DisposeBatch(queued);
    }
}