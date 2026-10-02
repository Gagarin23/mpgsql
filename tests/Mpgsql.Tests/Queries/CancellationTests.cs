using Mpgsql.Protocol;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class CancellationTests
{
    [Fact]
    public async Task CancellationDropsQueuedSendButPreservesExplicitSync()
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var wire = new ScriptedSession(blockWrites: true);
        var batch = wire.Session.CreateBatch(request.Token);
        var reading = batch.ReadResultsAsync().AsTask();
        var published = batch.SendQueryAsync("select $1",
            new[]
            {
                MpgsqlParameter.Int64(11)
            }).AsTask();
        var held = await wire.Outgoing.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.False(published.IsCompleted); // output backpressure, parameters already encoded
        long[] array = [22, 33];
        var queued = batch.SendQueryAsync("select $1",
            new[]
            {
                MpgsqlParameter.Int64Array(array)
            }).AsTask();
        var sync = batch.SendSyncAsync().AsTask();
        request.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(TestTimeout,
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => published.WaitAsync(TestTimeout,
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(TestTimeout,
            TestContext.Current.CancellationToken));
        array[0] = 99; // cancelled send has released borrowed memory
        Assert.False(batch.Completion.IsCompleted);
        Assert.False(batch.Sealed.IsCompleted);
        wire.Outgoing.Reader.AdvanceTo(held.Buffer.End);
        Assert.Equal(new[]
            {
                'S'
            },
            Tags(await wire.ReadOutputAsync()));
        await sync.WaitAsync(TestTimeout,
            TestContext.Current.CancellationToken);
        await wire.WriteAsync(Join(Query(11),
            Ready()));
        await batch.Completion.WaitAsync(TestTimeout,
            TestContext.Current.CancellationToken);
        Assert.Equal(TransactionStatus.Idle,
            batch.TransactionStatus);
        Assert.False(wire.HasOutput());
    }

    [Fact]
    public async Task CancelledFragmentedRowIsDiscardedWithoutCopiesOrValueDecoding()
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var wire = new ScriptedSession();
        var cancelled = wire.Session.CreateBatch(request.Token);
        await cancelled.SendQueryAsync("select large_array");
        await cancelled.SendSyncAsync();
        var survivor = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await survivor.SendQueryAsync("select 7::bigint");
        await survivor.SendSyncAsync();
        request.Cancel();
        await cancelled.DisposeAsync(); // does not wait for server
        await wire.WriteAsync(Begin(1016),
            fragment: 1);
        long before = wire.Session.CopiedRowBytes;
        // Deliberately not a valid bigint[] payload: cancellation must not run its converter.
        await wire.WriteAsync(Row(new byte[128 * 1024]),
            fragment: 29);
        await wire.WriteAsync(Join(Command(),
                Ready()),
            fragment: 3);
        await cancelled.Completion.WaitAsync(TestTimeout,
            TestContext.Current.CancellationToken);
        Assert.Equal(before,
            wire.Session.CopiedRowBytes);
        await wire.WriteAsync(Join(Query(7),
                Ready()),
            fragment: 3);
        await using var reader = await survivor.ReadResultsAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(7,
            reader.GetInt64(0));
        Assert.False(await reader.NextResultAsync());
        await survivor.DisposeAsync();
    }

    [Fact]
    public async Task BulkCancellationLeavesUnrelatedGroupReadable()
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var wire = new ScriptedSession();
        var cancelled = new MpgsqlQueryBatch[32];
        for (int i = 0; i < cancelled.Length; i++)
        {
            cancelled[i] = wire.Session.CreateBatch(request.Token);
            await cancelled[i].SendQueryAsync("select 1::bigint");
            await cancelled[i].SendSyncAsync();
        }
        var survivor = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await survivor.SendQueryAsync("select 123::bigint");
        await survivor.SendSyncAsync();
        request.Cancel();
        foreach (var batch in cancelled) await batch.DisposeAsync();
        long before = wire.Session.CopiedRowBytes;
        for (int i = 0; i < cancelled.Length; i++)
            await wire.WriteAsync(Join(Query(i),
                    Ready()),
                fragment: 1);
        await Task.WhenAll(cancelled.Select(batch => batch.Completion)).WaitAsync(TestTimeout,
            TestContext.Current.CancellationToken);
        Assert.Equal(before,
            wire.Session.CopiedRowBytes);
        await wire.WriteAsync(Join(Query(123),
            Ready()));
        await using var reader = await survivor.ReadResultsAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(123,
            reader.GetInt64(0));
        Assert.False(await reader.NextResultAsync());
        await survivor.DisposeAsync();
    }

    [Fact]
    public async Task CancellationRetainsServerErrorOnCompletion()
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var wire = new ScriptedSession();
        var batch = wire.Session.CreateBatch(request.Token);
        await batch.SendQueryAsync("bad query");
        request.Cancel();
        await batch.DisposeAsync();
        await batch.SendSyncAsync();
        await wire.WriteAsync(Join(Error(),
            Ready('E')));
        var error = await Assert.ThrowsAsync<MpgsqlServerException>(() => batch.Completion.WaitAsync(TestTimeout,
            TestContext.Current.CancellationToken));
        Assert.Equal(0,
            error.QueryIndex);
        Assert.Equal(TransactionStatus.FailedTransaction,
            error.TransactionStatus);
        Assert.False(wire.Session.Completion.IsCompleted);
    }

    [Fact]
    public async Task LifetimeCancellationStopsAllGroupsAndBlockedOutput()
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var wire = new ScriptedSession(blockWrites: true,
            lifetime: lifetime.Token);
        var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        var send = batch.SendQueryAsync("select 1::bigint").AsTask();
        var sync = batch.SendSyncAsync().AsTask();
        var read = batch.ReadResultsAsync().AsTask();
        lifetime.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wire.Session.Completion.WaitAsync(TestTimeout,
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send.WaitAsync(TestTimeout,
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sync.WaitAsync(TestTimeout,
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TestTimeout,
            TestContext.Current.CancellationToken));
    }
}