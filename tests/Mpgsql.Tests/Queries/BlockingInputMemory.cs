using System.Buffers;

namespace Mpgsql.Tests.Queries;

// bigint[] sizing uses Length; only the encoder obtains this span.
internal sealed class BlockingInputMemory(bool blockEncoding = false) : MemoryManager<long>
{
    private readonly TaskCompletionSource _entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly long[] _values = [11, 22];
    private int _reads;
    private bool _revoked;

    public override Memory<long> Memory => CreateMemory(_values.Length);
    internal Task Entered => _entered.Task;
    internal int Reads => Volatile.Read(ref _reads);
    internal void Resume()
    {
        _resume.TrySetResult();
    }
    internal void Revoke()
    {
        Volatile.Write(ref _revoked, true);
    }

    public override Span<long> GetSpan()
    {
        Interlocked.Increment(ref _reads);
        _entered.TrySetResult();
        if (blockEncoding && !_resume.Task.Wait(ScriptedSession.TestTimeout))
        {
            throw new TimeoutException("The test did not release the encoder.");
        }
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _revoked), this);
        return _values;
    }

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        throw new NotSupportedException();
    }
    public override void Unpin() { }
    protected override void Dispose(bool disposing)
    {
        Resume();
        Revoke();
    }
}