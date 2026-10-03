namespace Mpgsql.Internal;

// A single receive loop reserves bytes; consumers release them when owned rows die.
internal sealed class RowBufferBudget(long limit)
{
    private readonly Lock _gate = new();
    private TaskCompletionSource? _changed;
    private long _used;
    internal long Used { get { lock (_gate) return _used; } }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal async ValueTask<bool> ReserveAsync(long bytes, MpgsqlQueryBatch batch, CancellationToken token)
    {
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                if (batch.DiscardsRows) return false;
                if (_used == 0 || bytes <= limit - _used)
                {
                    _used += bytes;
                    return true;
                }
                wait = (_changed ??= NewSignal()).Task;
            }
            await wait.WaitAsync(token).ConfigureAwait(false);
        }
    }

    internal void Release(long bytes)
    {
        lock (_gate)
        {
            _used -= bytes;
            PulseCore();
        }
    }

    internal void Pulse() { lock (_gate) PulseCore(); }
    private void PulseCore()
    {
        var previous = _changed;
        _changed = null;
        previous?.TrySetResult();
    }
}
