using System.Buffers;
using System.Reflection;
using Mpgsql.Internal;
using Mpgsql.Protocol;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class AdoCursorAdministrativeConcurrencyTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public async Task FirstCloseConfirmationReleasesTheCursorGateBeforeAcquiringTheStatementGate()
    {
        await using var wire = new ScriptedSession();
        await using var source = new MpgsqlDataSource(_ => ValueTask.FromResult(wire.Session),
            (_, _) => ValueTask.CompletedTask);
        await using var connection = await source.OpenConnectionAsync(Token);
        await using var batch = connection.CreateBatch();
        batch.BatchCommands.Add(new MpgsqlBatchCommand("select first prepared"));
        batch.BatchCommands.Add(new MpgsqlBatchCommand("select second prepared"));
        var preparing = batch.PrepareAsync(Token);
        var expectedPrepare = new ArrayBufferWriter<byte>();
        FrontendMessage.Parse("select first prepared", "mpgsql_ps_1").Write(expectedPrepare);
        FrontendMessage.Parse("select second prepared", "mpgsql_ps_2").Write(expectedPrepare);
        FrontendMessage.Sync().Write(expectedPrepare);
        Assert.Equal(expectedPrepare.WrittenSpan.ToArray(), await Sync(wire));
        await Task.WhenAll(preparing, wire.WriteAsync(Join(Packet('1'), Packet('1'), Ready()), 1))
            .WaitAsync(TestTimeout, Token);
        var firstStatement = batch.BatchCommands[0].Statement!;
        var secondStatement = batch.BatchCommands[1].Statement!;

        var closing = batch.UnprepareAsync(Token).AsTask();
        var expectedClose = new ArrayBufferWriter<byte>();
        FrontendMessage.Close(StatementOrPortal.Statement, firstStatement.Name).Write(expectedClose);
        FrontendMessage.Close(StatementOrPortal.Statement, secondStatement.Name).Write(expectedClose);
        FrontendMessage.Sync().Write(expectedClose);
        Assert.Equal(expectedClose.WrittenSpan.ToArray(), await Sync(wire));
        var cursor = (AdoCursor)typeof(MpgsqlMessageSession).GetField("_adoCursor", PrivateInstance)!
            .GetValue(wire.Session)!;
        var sessionGate = (Lock)typeof(MpgsqlMessageSession).GetField("_gate", PrivateInstance)!
            .GetValue(wire.Session)!;
        var statementGate = (Lock)typeof(MpgsqlPreparedStatement).GetField("_gate", PrivateInstance)!
            .GetValue(firstStatement)!;
        var responses = (Queue<PendingResponse>)typeof(AdoCursor).GetField("_responses", PrivateInstance)!
            .GetValue(cursor)!;
        Assert.Equal(2, responses.Count);
        var firstClosed = typeof(MpgsqlPreparedStatement).GetField("_closed", PrivateInstance)!;
        var draining = typeof(MpgsqlMessageSession).GetField("_adoDraining", PrivateInstance)!;
        var pendingInput = typeof(MpgsqlMessageSession).GetField("_adoHasPendingInput", PrivateInstance)!;
        var outstandingInput = typeof(MpgsqlMessageSession).GetField("_adoReadOutstanding", PrivateInstance)!;
        // A processed control frame and its consumed input buffer prove that the
        // drain has already passed the next idle-owner gate before the test holds
        // that gate. A merely-started drain would not provide this IO barrier.
        await wire.WriteAsync(Packet('S', "cursor_close_wait\0posted\0"u8.ToArray()), 1);
        await WaitUntil(() => wire.Session.TryGetParameter("cursor_close_wait", out var value) && value == "posted"
            && (bool)draining.GetValue(wire.Session)!
            && !(bool)pendingInput.GetValue(wire.Session)! && !(bool)outstandingInput.GetValue(wire.Session)!);

        Task<bool>? inspection = null;
        var overlap = Task.Run(() =>
        {
            // Lock is thread-affine: this scope and all bounded waits stay on one
            // worker. The finally-generated scope release also unblocks a broken
            // implementation, so a failed assertion cannot deadlock the runner.
            using (sessionGate.EnterScope())
            using (statementGate.EnterScope())
            {
                wire.WriteAsync(Packet('3')).GetAwaiter().GetResult();
                Assert.True(SpinWait.SpinUntil(() =>
                {
                    // One input owner dequeues this first response before calling
                    // ConfirmClosed. The held statement lock then keeps that owner
                    // from touching the FIFO again until this worker releases it.
                    Thread.MemoryBarrier();
                    return responses.Count == 1;
                }, TestTimeout), "CloseComplete must dequeue its response before the lock-order probe.");
                Assert.False((bool)firstClosed.GetValue(firstStatement)!);
                inspection = Task.Run(() => cursor.ProtocolCompleted, Token);
                Assert.True(inspection.Wait(TestTimeout),
                    "ConfirmClosed must release the cursor gate before acquiring the statement gate.");
                Assert.False(inspection.Result);
            }
        }, Token);
        try { await overlap.WaitAsync(TestTimeout + TestTimeout + TestTimeout, Token); }
        finally
        {
            // Observe the probe even when its bounded assertion fails. The worker
            // releases the session gate before either task is observed here.
            if (overlap.IsCompleted && inspection is not null)
                await inspection.WaitAsync(TestTimeout, Token);
        }
        Assert.False(closing.IsCompleted);
        await WaitUntil(() => (bool)firstClosed.GetValue(firstStatement)!);
        Assert.True((bool)firstClosed.GetValue(firstStatement)!);
        Assert.False((bool)firstClosed.GetValue(secondStatement)!);
        var finishing = wire.WriteAsync(Join(Packet('3'), Ready()), 1);
        await closing.WaitAsync(TestTimeout, Token);
        await finishing.WaitAsync(TestTimeout, Token);
        Assert.Null(batch.BatchCommands[0].Statement);
        Assert.Null(batch.BatchCommands[1].Statement);
        Assert.True(wire.Session.IsIdleAndHealthy);
        batch.BatchCommands[0].CommandText = "mutable after both closes";
    }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TestTimeout);
        while (!predicate())
            await Task.Delay(1, deadline.Token);
    }

    private static async Task<byte[]> Sync(ScriptedSession wire)
    {
        var bytes = new List<byte>();
        do { bytes.AddRange(await wire.ReadOutputAsync()); }
        while (!Tags([.. bytes]).Contains('S'));
        return [.. bytes];
    }
}
