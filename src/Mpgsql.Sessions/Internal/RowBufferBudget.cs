using System.Threading.Tasks.Sources;

namespace Mpgsql.Internal;

// A single receive loop reserves bytes; consumers release them when owned rows die.
internal sealed class RowBufferBudget(long limit) : IValueTaskSource<bool>
{
    private readonly Lock _gate = new Lock();
    private ManualResetValueTaskSourceCore<bool> _changed = new ManualResetValueTaskSourceCore<bool> {RunContinuationsAsynchronously = true};
    private bool _pending;
    private CancellationToken _token;
    private long _used;
    private int _waiting;
    internal long Used => Volatile.Read(ref _used);

    bool IValueTaskSource<bool>.GetResult(short token)
    {
        lock (_gate)
        {
            if (_changed.GetStatus(token) == ValueTaskSourceStatus.Pending)
            {
                throw new InvalidOperationException("The capacity notification has not completed.");
            }
            try { return _changed.GetResult(token); }
            finally { _pending = false; }
        }
    }
    ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token)
    {
        lock (_gate)
        {
            return _changed.GetStatus(token);
        }
    }
    void IValueTaskSource<bool>.OnCompleted(Action<object?> continuation, object? state,
        short token,
        ValueTaskSourceOnCompletedFlags flags)
    {
        lock (_gate)
        {
            _changed.OnCompleted(continuation, state, token, flags);
        }
    }

    // Only the receiver reserves; consumers can only decrease usage. A false result
    // cannot turn into a capacity wait before that receiver's following reservation.
    internal bool WouldBlock(long bytes)
    {
        var used = Used;
        return used != 0 && bytes > limit - used;
    }

    internal async ValueTask<bool> ReserveAsync(long bytes, MpgsqlQueryBatch batch,
        CancellationToken token)
    {
        while (true)
        {
            if (batch.DiscardsRows)
            {
                return false;
            }
            if (TryReserve(bytes))
            {
                return true;
            }
            ValueTask<bool> wait;
            bool reserved;
            lock (_gate)
            {
                if (batch.DiscardsRows)
                {
                    return false;
                }
                if (_pending)
                {
                    throw new InvalidOperationException("Only the receive loop may await row capacity.");
                }
                _changed.Reset();
                _pending = true;
                _token = token;
                // Publish with a full fence before the space recheck: a volatile store alone
                // can leave the waiter invisible while that recheck still sees a full budget.
                Interlocked.Exchange(ref _waiting, 1);
                wait = new ValueTask<bool>(this, _changed.Version);
                // Register before rechecking space. A release between the first failed attempt
                // and registration either wakes this signal or is observed by this attempt.
                reserved = TryReserve(bytes);
                if (reserved)
                {
                    PulseCore();
                }
            }
            if (reserved)
            {
                wait.GetAwaiter().GetResult();
                return true;
            }
            // Queued rows must be consumable before the receiver waits for their reservations.
            // Do this outside the budget gate: cancellation drains rows under the batch gate.
            batch.NotifyEvents();
            // There is one receive-loop waiter. Dispose the registration before reusing its
            // source, so an old cancellation callback cannot complete the following wait.
            using var registration = token.UnsafeRegister(static state => ((RowBufferBudget)state!).CancelWait(), this);
            _ = await wait.ConfigureAwait(false);
        }
    }

    internal void Release(long bytes)
    {
        Interlocked.Add(ref _used, -bytes);
        if (Volatile.Read(ref _waiting) != 0)
        {
            Pulse();
        }
    }

    private bool TryReserve(long bytes)
    {
        while (true)
        {
            var used = Volatile.Read(ref _used);
            if (used != 0 && bytes > limit - used)
            {
                return false;
            }
            if (Interlocked.CompareExchange(ref _used, used + bytes, used) == used)
            {
                return true;
            }
        }
    }

    internal void Pulse()
    {
        lock (_gate)
        {
            PulseCore();
        }
    }
    private void PulseCore()
    {
        if (Interlocked.Exchange(ref _waiting, 0) != 0)
        {
            _changed.SetResult(true);
        }
    }

    private void CancelWait()
    {
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _waiting, 0) != 0)
            {
                _changed.SetException(new OperationCanceledException(_token));
            }
        }
    }
}