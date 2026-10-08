using System.Buffers;
using System.Threading.Tasks.Sources;

namespace Mpgsql.Internal;

// One reader can await availability. Cancellation/disposal can drain concurrently with it.
// Rows are owned by the buffer until TryRead transfers ownership or Drain releases them.
internal sealed class ResultEventBuffer : IValueTaskSource<bool>
{
    private readonly Lock _gate = new();
    private ManualResetValueTaskSourceCore<bool> _available = new() { RunContinuationsAsynchronously = true };
    private ResultEvent[]? _items;
    private int _head;
    private int _count;
    private bool _completed;
    private Exception? _error;
    private bool _pending;
    private bool _waiting;

    internal bool TryWrite(ResultEvent item, bool notify = true)
    {
        lock (_gate)
        {
            if (_completed) return false;
            if (_items is null) _items = ArrayPool<ResultEvent>.Shared.Rent(8);
            else if (_count == _items.Length) Grow();
            int tail = _head + _count;
            if (tail >= _items.Length) tail -= _items.Length;
            _items[tail] = item;
            _count++;
            if (notify) Signal(true);
            return true;
        }
    }

    // The receiver can publish a whole available transport burst before waking its consumer.
    // Items remain readable immediately; only an already waiting consumer is deferred.
    internal void NotifyAvailable()
    {
        lock (_gate)
            if (_count != 0 || _completed) Signal(_count != 0, _count == 0 ? _error : null);
    }

    internal bool TryRead(out ResultEvent item)
    {
        lock (_gate)
        {
            if (_count == 0) { item = default; return false; }
            item = _items![_head];
            _items[_head] = default;
            if (++_head == _items.Length) _head = 0;
            if (--_count == 0 && _completed) ReturnBuffer();
            return true;
        }
    }

    internal ValueTask<bool> WaitToReadAsync()
    {
        lock (_gate)
        {
            if (_count != 0) return new(true);
            if (_completed) return _error is null ? new(false) : ValueTask.FromException<bool>(_error);
            if (_pending) throw new InvalidOperationException("Only one result reader may await availability.");
            _available.Reset();
            _pending = _waiting = true;
            return new(this, _available.Version);
        }
    }

    internal void Complete(Exception? error = null, bool notify = true)
    {
        lock (_gate)
        {
            if (_completed)
            {
                if (notify) Signal(_count != 0, _count == 0 ? _error : null);
                return;
            }
            _completed = true;
            _error = error;
            if (_count == 0) ReturnBuffer();
            // Completion must not hide items whose notification was deferred by the receiver.
            if (notify) Signal(_count != 0, _count == 0 ? error : null);
        }
    }

    internal void Drain()
    {
        lock (_gate)
        {
            while (_count != 0)
            {
                var item = _items![_head];
                _items[_head] = default;
                if (++_head == _items.Length) _head = 0;
                _count--;
                item.Row?.Dispose();
            }
            if (_completed) ReturnBuffer();
        }
    }

    private void Signal(bool available, Exception? error = null)
    {
        if (!_waiting) return;
        _waiting = false;
        if (error is null) _available.SetResult(available);
        else _available.SetException(error);
    }

    private void Grow()
    {
        var old = _items!;
        var next = ArrayPool<ResultEvent>.Shared.Rent(checked(old.Length * 2));
        int first = Math.Min(_count, old.Length - _head);
        old.AsSpan(_head, first).CopyTo(next);
        old.AsSpan(0, _count - first).CopyTo(next.AsSpan(first));
        Array.Clear(old, _head, first);
        Array.Clear(old, 0, _count - first);
        ArrayPool<ResultEvent>.Shared.Return(old);
        _items = next;
        _head = 0;
    }

    private void ReturnBuffer()
    {
        if (_items is not { } items) return;
        // Every occupied slot was cleared by TryRead or Drain before reaching an empty buffer.
        _items = null;
        _head = 0;
        ArrayPool<ResultEvent>.Shared.Return(items);
    }

    bool IValueTaskSource<bool>.GetResult(short token)
    {
        lock (_gate)
        {
            if (_available.GetStatus(token) == ValueTaskSourceStatus.Pending)
                throw new InvalidOperationException("The result notification has not completed.");
            try { return _available.GetResult(token); }
            finally { _pending = false; }
        }
    }

    ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token)
    {
        lock (_gate) return _available.GetStatus(token);
    }

    void IValueTaskSource<bool>.OnCompleted(Action<object?> continuation, object? state, short token,
        ValueTaskSourceOnCompletedFlags flags)
    {
        lock (_gate) _available.OnCompleted(continuation, state, token, flags);
    }
}
