using System.Buffers;
using Mpgsql.Internal;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class IdleWriterLifecycleTests
{
    [Theory, InlineData(false), InlineData(true)]
    public async Task FailurePublishesWhileAnEncoderStillOwnsCallerInput(bool abort)
    {
        var token = TestContext.Current.CancellationToken;
        using var input = new BlockingBytes();
        await using var wire = new ScriptedSession();
        await using var batch = wire.Session.CreateBatch(token);
        var parameters = new[] {MpgsqlParameterValue.Bytea(input.Memory)};
        var sending = Task.Run(() => batch.SendExecution(new QueryDefinition("select $1::bytea", parameters), null), token);
        try
        {
            await input.Entered.WaitAsync(TestTimeout, token);
            var encoding = await sending.WaitAsync(TestTimeout, token);
            if (abort)
            {
                wire.Session.Abort(new IOException("encoder transport failure"));
            }
            else
            {
                await wire.Incoming.Writer.CompleteAsync();
            }
            await Assert.ThrowsAnyAsync<IOException>(() => wire.Session.Completion.WaitAsync(TestTimeout, token));
            Assert.False(encoding.Completion.IsCompleted);
            Assert.False(wire.Session.IsHealthy);
        }
        finally { input.Resume(); }
        var work = await sending.WaitAsync(TestTimeout, token);
        await Assert.ThrowsAnyAsync<IOException>(() => work.Completion.WaitAsync(TestTimeout, token));
        await Assert.ThrowsAnyAsync<IOException>(() => batch.ObserveCompletionAsync().AsTask());
        Assert.Equal(0, wire.Session.BufferedRowBytes);
    }

    [Fact]
    public async Task CallerParameterContainerKeepsItsEncoderOffTheStartingThread()
    {
        var token = TestContext.Current.CancellationToken;
        using var input = new BlockingParameters();
        await using var wire = new ScriptedSession();
        await using var source = new MpgsqlMultiplexingDataSource(_ => ValueTask.FromResult(wire.Session), new MpgsqlMultiplexingOptions {MaxConnections = 1, MaxInFlightPerConnection = 1});
        // Sizing can read once. A subsequent container access belongs to the asynchronous encoder.
        var opening = source.ExecuteReaderAsync("select $1::bigint", input.Memory, token).AsTask();
        try
        {
            await input.Entered.WaitAsync(TestTimeout, token);
            await wire.Incoming.Writer.CompleteAsync();
            await Assert.ThrowsAnyAsync<IOException>(() => wire.Session.Completion.WaitAsync(TestTimeout, token));
            Assert.False(opening.IsCompleted);
        }
        finally { input.Resume(); }
        await Assert.ThrowsAnyAsync<IOException>(() => opening.WaitAsync(TestTimeout, token));
        Assert.Equal(2, input.Reads);
        Assert.Equal(0, wire.Session.BufferedRowBytes);
    }

    [Fact]
    public async Task DisposalFinishesAPendingFlushAndFollowingQueuedBoundary()
    {
        var token = TestContext.Current.CancellationToken;
        await using var wire = new ScriptedSession(true);
        await using var first = wire.Session.CreateBatch(token);
        var one = first.SendExecution(new QueryDefinition("select $1::bigint", new[] {MpgsqlParameterValue.Int64(11)}), null);
        await using var following = wire.Session.CreateBatch(token);
        var two = following.SendExecution(new QueryDefinition("select $1::bigint", new[] {MpgsqlParameterValue.Int64(22)}), null);
        Assert.False(one.Delivery.IsCompleted);
        Assert.False(two.Delivery.IsCompleted);
        await wire.Session.DisposeAsync().AsTask().WaitAsync(TestTimeout, token);
        foreach (var work in new[] {one, two})
        {
            var error = await Record.ExceptionAsync(() => work.Delivery.WaitAsync(TestTimeout, token));
            Assert.True(error is ObjectDisposedException or OperationCanceledException);
            await Record.ExceptionAsync(() => work.Completion.WaitAsync(TestTimeout, token));
        }
        await Record.ExceptionAsync(() => first.ObserveCompletionAsync().AsTask());
        await Record.ExceptionAsync(() => following.ObserveCompletionAsync().AsTask());
        Assert.False(wire.Session.IsHealthy);
        Assert.Equal(0, wire.Session.BufferedRowBytes);
    }

    private sealed class BlockingBytes : MemoryManager<byte>
    {
        private readonly byte[] _bytes = new byte[16];
        private readonly TaskCompletionSource _entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _resume = new ManualResetEventSlim();
        public override Memory<byte> Memory => CreateMemory(_bytes.Length);
        internal Task Entered => _entered.Task;
        internal void Resume()
        {
            _resume.Set();
        }
        public override Span<byte> GetSpan()
        {
            _entered.TrySetResult();
            if (!_resume.Wait(TestTimeout))
            {
                throw new TimeoutException("encoder input was not released");
            }
            return _bytes;
        }
        public override MemoryHandle Pin(int elementIndex = 0)
        {
            throw new NotSupportedException();
        }
        public override void Unpin() { }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _resume.Dispose();
            }
        }
    }

    private sealed class BlockingParameters : MemoryManager<MpgsqlParameterValue>
    {
        private readonly TaskCompletionSource _entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _resume = new ManualResetEventSlim();
        private readonly MpgsqlParameterValue[] _values = [MpgsqlParameterValue.Int64(17)];
        private int _reads;
        internal int Reads => Volatile.Read(ref _reads);
        internal Task Entered => _entered.Task;
        public override Memory<MpgsqlParameterValue> Memory => CreateMemory(_values.Length);
        internal void Resume()
        {
            _resume.Set();
        }
        public override Span<MpgsqlParameterValue> GetSpan()
        {
            if (Interlocked.Increment(ref _reads) > 1)
            {
                _entered.TrySetResult();
                if (!_resume.Wait(TestTimeout))
                {
                    throw new TimeoutException("parameter container was not released");
                }
            }
            return _values;
        }
        public override MemoryHandle Pin(int elementIndex = 0)
        {
            throw new NotSupportedException();
        }
        public override void Unpin() { }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _resume.Dispose();
            }
        }
    }
}