using System.Threading.Tasks.Sources;

namespace Mpgsql.Internal;

// One pending admission; the data source gate protects queue membership and slot handoff.
internal sealed class SessionWaiter(MpgsqlDataSource source, bool exclusive, CancellationToken token)
    : IValueTaskSource<PooledSession>
{
    // Exactly one internal admission consumer. Embed its source instead of allocating a TCS/Task
    // pair. A waiter is not reused: cancellation and factory completion can still reference it.
    private ManualResetValueTaskSourceCore<PooledSession> _completion = new() { RunContinuationsAsynchronously = true };
    private int _completed;
    internal MpgsqlDataSource Source { get; } = source;
    internal bool Exclusive { get; } = exclusive;
    internal CancellationToken Token { get; } = token;
    internal LinkedListNode<SessionWaiter>? Node;

    internal ValueTask<PooledSession> WaitAsync() => new(this, _completion.Version);
    internal bool TrySetResult(PooledSession session)
    {
        if (Interlocked.CompareExchange(ref _completed, 1, 0) != 0) return false;
        _completion.SetResult(session);
        return true;
    }
    internal bool TrySetException(Exception error)
    {
        if (Interlocked.CompareExchange(ref _completed, 1, 0) != 0) return false;
        _completion.SetException(error);
        return true;
    }
    internal bool TrySetCanceled()
    {
        if (Interlocked.CompareExchange(ref _completed, 1, 0) != 0) return false;
        _completion.SetException(new OperationCanceledException(Token));
        return true;
    }

    PooledSession IValueTaskSource<PooledSession>.GetResult(short token) => _completion.GetResult(token);
    ValueTaskSourceStatus IValueTaskSource<PooledSession>.GetStatus(short token) => _completion.GetStatus(token);
    void IValueTaskSource<PooledSession>.OnCompleted(Action<object?> continuation, object? state, short token,
        ValueTaskSourceOnCompletedFlags flags) => _completion.OnCompleted(continuation, state, token, flags);
}
