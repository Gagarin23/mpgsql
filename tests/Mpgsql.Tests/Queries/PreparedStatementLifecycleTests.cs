using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class PreparedStatementLifecycleTests
{
    private static Task Wait(Task task) => task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

    [Fact]
    public async Task AbandonedLocalHandlesSqlAndOwnedTypesAreCollectedWhileSessionRemainsAlive()
    {
        await using var wire = new ScriptedSession();
        var abandoned = CreateAbandonedHandles(wire.Session);
        Assert.Equal(0, wire.Session.TrackedStatementCount);
        Collect();
        foreach (var (handle, sql, types) in abandoned)
        {
            Assert.False(IsAlive(handle));
            Assert.False(IsAlive(sql));
            Assert.False(IsAlive(types));
        }
        Assert.False(wire.HasOutput());
        Assert.True(wire.Session.IsHealthy);
        Assert.Equal("mpgsql_ps_1001", wire.Session.CreatePreparedStatement("select 1").Name);
    }

    [Fact]
    public async Task LocalDisposeIsIdempotentFaultsPreparedAndSendsNothing()
    {
        await using var wire = new ScriptedSession();
        var statement = wire.Session.CreatePreparedStatement("select 1");
        Task prepared = statement.Prepared;
        statement.Dispose();
        statement.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Wait(prepared));
        Assert.Equal(0, wire.Session.TrackedStatementCount);
        await using var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        Assert.Throws<ObjectDisposedException>(() => batch.SendPrepareAsync(statement));
        Assert.Throws<ObjectDisposedException>(() => batch.SendQueryAsync(statement));
        Assert.Throws<ObjectDisposedException>(() => batch.SendCloseAsync(statement));
        Assert.False(wire.HasOutput());
        Assert.True(wire.Session.IsHealthy);
    }

    [Fact]
    public async Task ValidationFailuresLeaveTheHandleLocalAndAllowLaterPreparation()
    {
        await using var wire = new ScriptedSession();
        await using var foreign = new ScriptedSession();
        Assert.Throws<ArgumentException>(() => wire.Session.CreatePreparedStatement("select $1", new uint[] { 0 }));
        Assert.Throws<ArgumentNullException>(() => wire.Session.CreatePreparedStatement(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => wire.Session.CreatePreparedStatement("select 1", new uint[65536]));
        var statement = wire.Session.CreatePreparedStatement("select $1", new uint[] { 20 });
        await using var wrongOwner = foreign.Session.CreateBatch(TestContext.Current.CancellationToken);
        Assert.Throws<ArgumentException>(() => wrongOwner.SendPrepareAsync(statement));
        Assert.Equal(0, wire.Session.TrackedStatementCount);
        Assert.Equal(0, foreign.Session.TrackedStatementCount);
        Assert.False(wire.HasOutput());
        Assert.False(foreign.HasOutput());

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await using var rejected = wire.Session.CreateBatch(canceled.Token);
        Assert.ThrowsAny<OperationCanceledException>(() => rejected.SendPrepareAsync(statement));
        Assert.Equal(0, wire.Session.TrackedStatementCount);
        Assert.False(statement.Prepared.IsCompleted);
        Assert.False(wire.HasOutput());

        await using var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await batch.SendPrepareAsync(statement);
        Assert.Equal(1, wire.Session.TrackedStatementCount);
        Assert.Throws<ArgumentException>(() => batch.SendQueryAsync(statement));
        Assert.Throws<ArgumentException>(() => batch.SendQueryAsync(statement, new[] { MpgsqlParameter.Int64Array(null) }));
        Assert.Equal(1, wire.Session.TrackedStatementCount);
        await batch.SendCloseAsync(statement);
        await batch.SendSyncAsync();
        Assert.Equal("PCS", new string(Tags(await wire.ReadOutputAsync())));
        await wire.WriteAsync(Join(Packet('1'), Packet('3'), Ready()));
        await Wait(batch.Completion);
        await Wait(statement.Prepared);
        Assert.Equal(0, wire.Session.TrackedStatementCount);
        statement.Dispose();
    }

    [Fact]
    public async Task QueuedCancellationReleasesAbandonedHandleBeforeBlockedFlushResumes()
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var wire = new ScriptedSession(blockWrites: true);
        await using var batch = wire.Session.CreateBatch(request.Token);
        Task published = batch.SendQueryAsync("select 11::bigint").AsTask();
        var held = await wire.Outgoing.Reader.ReadAsync(TestContext.Current.CancellationToken);
        var (handle, send, prepared) = QueueAbandonedPreparation(wire.Session, batch);
        Task sync = batch.SendSyncAsync().AsTask();
        Assert.Equal(1, wire.Session.TrackedStatementCount);
        request.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Wait(published));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Wait(send));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Wait(prepared));
        Assert.Equal(0, wire.Session.TrackedStatementCount);
        // The canceled work is still in the writer's FIFO, behind the blocked shared flush.
        Collect();
        Assert.False(IsAlive(handle));
        wire.Outgoing.Reader.AdvanceTo(held.Buffer.End);
        Assert.Equal("S", new string(Tags(await wire.ReadOutputAsync())));
        await Wait(sync);
        await wire.WriteAsync(Join(Query(11), Ready()));
        await Wait(batch.Completion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedAndSkippedParseReleaseAbandonedHandleAtReadyForQuery(bool skipped)
    {
        await using var wire = new ScriptedSession();
        var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        if (skipped) await batch.SendQueryAsync("earlier bad sql");
        var (handle, send, prepared) = QueueAbandonedPreparation(wire.Session, batch);
        await Wait(send);
        await batch.SendSyncAsync();
        await wire.ReadOutputAsync();
        await wire.WriteAsync(Error("42601"));
        Assert.False(prepared.IsCompleted);
        Assert.Equal(1, wire.Session.TrackedStatementCount);
        Collect();
        Assert.True(IsAlive(handle));
        await wire.WriteAsync(Ready());
        var error = await Assert.ThrowsAsync<MpgsqlServerException>(() => Wait(batch.Completion));
        Assert.Equal(skipped ? 0 : (int?)null, error.QueryIndex);
        Assert.Same(error, await Assert.ThrowsAsync<MpgsqlServerException>(() => Wait(prepared)));
        Assert.Equal(0, wire.Session.TrackedStatementCount);
        // Keep the failed batch and both completion tasks alive while checking handle ownership.
        Collect();
        Assert.False(IsAlive(handle));
        await Assert.ThrowsAsync<MpgsqlServerException>(() => batch.DisposeAsync().AsTask());
        Assert.False(wire.HasOutput());
        Assert.True(wire.Session.IsHealthy);
    }

    [Fact]
    public async Task QueuedCloseCancellationKeepsConfirmedStatementForExplicitRetry()
    {
        await using var wire = new ScriptedSession(blockWrites: true);
        var statement = wire.Session.CreatePreparedStatement("select 1");
        await using var preparing = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        Task prepare = preparing.SendPrepareAsync(statement).AsTask();
        Assert.Equal("P", new string(Tags(await wire.ReadOutputAsync())));
        await Wait(prepare);
        Task prepareSync = preparing.SendSyncAsync().AsTask();
        Assert.Equal("S", new string(Tags(await wire.ReadOutputAsync())));
        await Wait(prepareSync);
        await wire.WriteAsync(Join(Packet('1'), Ready()));
        await Wait(preparing.Completion);

        using var request = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var canceled = wire.Session.CreateBatch(request.Token);
        Task published = canceled.SendQueryAsync("select 11::bigint").AsTask();
        var held = await wire.Outgoing.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Task close = canceled.SendCloseAsync(statement).AsTask();
        Task sync = canceled.SendSyncAsync().AsTask();
        request.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Wait(published));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Wait(close));
        Assert.True(statement.Prepared.IsCompletedSuccessfully);
        Assert.Equal(1, wire.Session.TrackedStatementCount);
        Assert.Throws<InvalidOperationException>(statement.Dispose);
        wire.Outgoing.Reader.AdvanceTo(held.Buffer.End);
        Assert.Equal("S", new string(Tags(await wire.ReadOutputAsync())));
        await Wait(sync);
        await wire.WriteAsync(Join(Query(11), Ready()));
        await Wait(canceled.Completion);
        Assert.Equal(1, wire.Session.TrackedStatementCount);

        await using var retry = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        Task retryClose = retry.SendCloseAsync(statement).AsTask();
        Assert.Equal("C", new string(Tags(await wire.ReadOutputAsync())));
        await Wait(retryClose);
        Task retrySync = retry.SendSyncAsync().AsTask();
        Assert.Equal("S", new string(Tags(await wire.ReadOutputAsync())));
        await Wait(retrySync);
        await wire.WriteAsync(Join(Packet('3'), Ready()));
        await Wait(retry.Completion);
        Assert.Equal(0, wire.Session.TrackedStatementCount);
        statement.Dispose();
    }

    [Fact]
    public async Task ConfirmedStatementStaysAliveUntilServerCloseIsAcknowledged()
    {
        await using var wire = new ScriptedSession();
        await using var preparing = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        var (handle, send, prepared) = QueueAbandonedPreparation(wire.Session, preparing);
        await Wait(send);
        await preparing.SendSyncAsync();
        await wire.ReadOutputAsync();
        await wire.WriteAsync(Join(Packet('1'), Ready()));
        await Wait(prepared);
        await Wait(preparing.Completion);
        Assert.Equal(1, wire.Session.TrackedStatementCount);
        Collect();
        Assert.True(IsAlive(handle));

        await using var closing = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await Wait(QueueClose(handle, closing));
        await closing.SendSyncAsync();
        Assert.Equal("CS", new string(Tags(await wire.ReadOutputAsync())));
        Assert.Equal(1, wire.Session.TrackedStatementCount);
        Collect();
        Assert.True(IsAlive(handle));
        await wire.WriteAsync(Join(Packet('3'), Ready()));
        await Wait(closing.Completion);
        Assert.Equal(0, wire.Session.TrackedStatementCount);
        Collect();
        Assert.False(IsAlive(handle));
    }

    [Fact]
    public async Task PendingAndConfirmedPreparationRejectDisposeUntilConfirmedClose()
    {
        await using var wire = new ScriptedSession();
        var statement = wire.Session.CreatePreparedStatement("select 1");
        await using var preparing = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await preparing.SendPrepareAsync(statement);
        Assert.Throws<InvalidOperationException>(statement.Dispose);
        Assert.False(statement.Prepared.IsCompleted);
        await preparing.SendSyncAsync();
        await wire.ReadOutputAsync();
        await wire.WriteAsync(Join(Packet('1'), Ready()));
        await Wait(preparing.Completion);
        Assert.Throws<InvalidOperationException>(statement.Dispose);
        Assert.True(statement.Prepared.IsCompletedSuccessfully);
        Assert.False(wire.HasOutput());
        await using var closing = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await closing.SendCloseAsync(statement);
        Assert.Throws<InvalidOperationException>(statement.Dispose);
        await closing.SendSyncAsync();
        await wire.ReadOutputAsync();
        await wire.WriteAsync(Join(Packet('3'), Ready()));
        await Wait(closing.Completion);
        statement.Dispose();
        statement.Dispose();
        Assert.True(statement.Prepared.IsCompletedSuccessfully);
        Assert.Equal(0, wire.Session.TrackedStatementCount);
        Assert.False(wire.HasOutput());
    }

    [Fact]
    public async Task SessionFailureFaultsLiveLocalAndPendingTasksAndAllowsConfirmedHandleDisposal()
    {
        await using var wire = new ScriptedSession();
        var confirmed = wire.Session.CreatePreparedStatement("select 1");
        await using var preparing = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await preparing.SendPrepareAsync(confirmed);
        await preparing.SendSyncAsync();
        await wire.ReadOutputAsync();
        await wire.WriteAsync(Join(Packet('1'), Ready()));
        await Wait(preparing.Completion);

        var local = wire.Session.CreatePreparedStatement("select 2");
        var pending = wire.Session.CreatePreparedStatement("select 3");
        var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await batch.SendPrepareAsync(pending);
        Assert.Equal(2, wire.Session.TrackedStatementCount);
        await wire.Incoming.Writer.CompleteAsync();
        var error = await Assert.ThrowsAsync<EndOfStreamException>(() => Wait(wire.Session.Completion));
        Assert.Same(error, await Assert.ThrowsAsync<EndOfStreamException>(() => Wait(local.Prepared)));
        Assert.Same(error, await Assert.ThrowsAsync<EndOfStreamException>(() => Wait(pending.Prepared)));
        Assert.True(confirmed.Prepared.IsCompletedSuccessfully);
        Assert.Equal(0, wire.Session.TrackedStatementCount);
        local.Dispose();
        pending.Dispose();
        confirmed.Dispose();
        Assert.Same(error, await Assert.ThrowsAsync<EndOfStreamException>(() => Wait(batch.Completion)));
        await batch.DisposeAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference<MpgsqlPreparedStatement>, WeakReference<string>, WeakReference<uint[]>)[]
        CreateAbandonedHandles(MpgsqlMessageSession session)
    {
        var references = new (WeakReference<MpgsqlPreparedStatement>, WeakReference<string>, WeakReference<uint[]>)[1000];
        for (int i = 0; i < references.Length; i++)
        {
            var statement = session.CreatePreparedStatement($"select $1 /* {i} {new string('x', 1024)} */", new uint[] { 20 });
            Assert.True(MemoryMarshal.TryGetArray(statement.ParameterTypes, out var ownedTypes));
            references[i] = (new(statement), new(statement.Sql), new(ownedTypes.Array!));
        }
        return references;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference<MpgsqlPreparedStatement> Handle, Task Send, Task Prepared)
        QueueAbandonedPreparation(MpgsqlMessageSession session, MpgsqlQueryBatch batch)
    {
        var statement = session.CreatePreparedStatement("select 1");
        return (new(statement), batch.SendPrepareAsync(statement).AsTask(), statement.Prepared);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task QueueClose(WeakReference<MpgsqlPreparedStatement> reference, MpgsqlQueryBatch batch)
    {
        Assert.True(reference.TryGetTarget(out var statement));
        Assert.Throws<InvalidOperationException>(statement.Dispose);
        return batch.SendCloseAsync(statement).AsTask();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive<T>(WeakReference<T> reference) where T : class => reference.TryGetTarget(out _);

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
