namespace Mpgsql.Internal;

// One FIFO write operation. Cancellation cannot release borrowed parameters while encoding.
internal sealed class OutboundWork
{
    private enum State { Queued, Encoding, Published, Finished }

    private volatile State _state;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal MpgsqlQueryBatch Batch { get; }
    internal bool IsSync { get; }
    internal string? Sql { get; private set; }
    internal ReadOnlyMemory<MpgsqlParameter> Parameters { get; private set; }
    internal int Size { get; }
    internal Task Completion => _completion.Task;

    internal OutboundWork(
        MpgsqlQueryBatch batch,
        string? sql = null,
        ReadOnlyMemory<MpgsqlParameter> parameters = default,
        int size = 0)
        => (Batch, IsSync, Sql, Parameters, Size) = (batch, sql is null, sql, parameters, size);

    internal bool TryStart() => Interlocked.CompareExchange(ref _state, State.Encoding, State.Queued) == State.Queued;
    internal void Published()
    {
        Parameters = default; Sql = null;
        _state = State.Published;
        if (!IsSync && Batch.RequestToken.IsCancellationRequested) Cancel();
    }

    internal void Cancel()
    {
        if (IsSync) return;
        State state = _state;
        if (state == State.Queued && Interlocked.CompareExchange(ref _state, State.Finished, State.Queued) == State.Queued)
        {
            Parameters = default; Sql = null;
            _completion.TrySetCanceled(Batch.RequestToken);
        }
        else if (state == State.Published) _completion.TrySetCanceled(Batch.RequestToken);
    }

    internal void Complete(Exception? error = null)
    {
        Parameters = default; Sql = null;
        _state = State.Finished;
        if (error is OperationCanceledException && !IsSync && Batch.RequestToken.IsCancellationRequested)
            _completion.TrySetCanceled(Batch.RequestToken);
        else if (error is not null) _completion.TrySetException(error);
        else _completion.TrySetResult();
    }

    internal void FailQueued(Exception error)
    {
        State state = _state;
        if (state == State.Queued && Interlocked.CompareExchange(ref _state, State.Finished, State.Queued) == State.Queued)
        {
            Parameters = default; Sql = null;
            _completion.TrySetException(error);
        }
        else if (state == State.Published) _completion.TrySetException(error);
    }
}
