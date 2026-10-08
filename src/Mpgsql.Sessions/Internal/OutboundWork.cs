namespace Mpgsql.Internal;

// One FIFO write operation and its multi-observer acknowledgement. Keeping the promise on the
// work removes a separate TaskCompletionSource object, with the same asynchronous continuations.
// Cancellation cannot release borrowed parameters while encoding.
internal sealed class OutboundWork : TaskCompletionSource
{
    private readonly int _queryCount;
    private TaskCompletionSource? _delivery;
    private int _nextQuery;
    private QueryDefinition[]? _queries;

    private volatile State _state;

    internal OutboundWork(
        MpgsqlQueryBatch batch, QueryDefinition[] queries,
        bool sync = false
    )
        : base(TaskCreationOptions.RunContinuationsAsynchronously)
    {
        Batch = ResponseBatch = batch;
        Kind = MessageOperationKind.Query;
        IsQueryGroup = true;
        HasSync = sync;
        if (sync && batch.RequestToken.CanBeCanceled)
        {
            _delivery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        _queries = queries;
        _queryCount = queries.Length;
    }

    internal OutboundWork(MpgsqlQueryBatch batch, QueryDefinition query)
        : base(TaskCreationOptions.RunContinuationsAsynchronously)
    {
        Batch = ResponseBatch = batch;
        Kind = MessageOperationKind.Query;
        IsQueryGroup = HasSync = true;
        if (batch.RequestToken.CanBeCanceled)
        {
            _delivery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        _queryCount = 1;
        (Sql, Parameters, Size, Statement) = (query.Sql, query.Parameters, query.EncodedSize, query.PreparedStatement);
    }

    internal OutboundWork(
        MpgsqlQueryBatch batch, MpgsqlQueryBatch responseBatch,
        QueryDefinition query
    )
        : base(TaskCreationOptions.RunContinuationsAsynchronously)
    {
        Batch = batch;
        ResponseBatch = responseBatch;
        Kind = MessageOperationKind.Query;
        (Sql, Parameters, Size) = (query.Sql, query.Parameters, query.EncodedSize);
    }

    internal OutboundWork(
        MpgsqlQueryBatch batch,
        string? sql = null,
        ReadOnlyMemory<MpgsqlParameterValue> parameters = default,
        int size = 0
    )
        : base(TaskCreationOptions.RunContinuationsAsynchronously)
    {
        ResponseBatch = batch;
        (Batch, Kind, Sql, Parameters, Size) = (batch,
            sql is null ? MessageOperationKind.Sync : MessageOperationKind.Query, sql, parameters, size);
    }

    internal OutboundWork(
        MpgsqlQueryBatch batch,
        MessageOperationKind kind,
        MpgsqlPreparedStatement statement,
        int size,
        ReadOnlyMemory<MpgsqlParameterValue> parameters = default
    )
        : base(TaskCreationOptions.RunContinuationsAsynchronously)
    {
        ResponseBatch = batch;
        (Batch, Kind, Statement, Size, Parameters) = (batch, kind, statement, size, parameters);
    }
    internal MpgsqlQueryBatch Batch { get; }
    internal MpgsqlQueryBatch ResponseBatch { get; }
    internal MessageOperationKind Kind { get; }
    internal bool IsSync => Kind == MessageOperationKind.Sync;
    internal bool IsQueryGroup { get; }
    internal bool HasSync { get; }
    internal MpgsqlPreparedStatement? Statement { get; private set; }
    internal string? Sql { get; private set; }
    internal ReadOnlyMemory<MpgsqlParameterValue> Parameters { get; private set; }
    internal int Size { get; }
    internal Task Completion => Task;
    // Logical cancellation can release borrowed inputs before Sync delivery. Only cancellable
    // upper executions need a second promise; normal execution shares the flush acknowledgement.
    internal Task Delivery => _delivery?.Task ?? Task;
    internal void PrepareInputFlush()
    {
        _delivery ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    internal void InputsFlushed()
    {
        Signal(cancelled: Batch.RequestToken.IsCancellationRequested);
    }

    internal bool TryGetQuery(out QueryDefinition query)
    {
        if (_nextQuery == _queryCount)
        {
            query = default;
            return false;
        }
        query = _queries is { } queries ? queries[_nextQuery] : new QueryDefinition(Sql!, Parameters, Size, Statement);
        return true;
    }

    internal void QueryPublished()
    {
        if (_queries is { } queries)
        {
            queries[_nextQuery] = default;
        }
        else
        {
            Sql = null;
            Parameters = default;
            Statement = null;
        }
        _nextQuery++;
    }

    // A large group resumes before later FIFO items, after transport backpressure. No encoder
    // uses its inputs while queued; cancellation can release its remaining borrowed buffers.
    internal void PauseQueryGroup()
    {
        Interlocked.Exchange(ref _state, State.Queued);
        if (Batch.RequestToken.IsCancellationRequested)
        {
            Cancel();
        }
    }

    private void ReleaseInputs()
    {
        Parameters = default;
        Sql = null;
        _queries = null;
    }

    private void Signal(Exception? error = null, bool cancelled = false)
    {
        if (cancelled)
        {
            TrySetCanceled(Batch.RequestToken);
        }
        else if (error is not null)
        {
            TrySetException(error);
        }
        else
        {
            TrySetResult();
        }
    }

    private void FailUnpublishedPreparation(Exception error)
    {
        if (Kind == MessageOperationKind.Prepare)
        {
            Statement!.FailPreparation(error);
        }
    }

    internal void Write(Span<byte> destination)
    {
        switch (Kind)
        {
            case MessageOperationKind.Query:
                QueryPacket.WriteMeasured(Sql!, Parameters.Span, destination, Size);
                break;
            case MessageOperationKind.Prepare:
                Statement!.ParseMessage.Write(destination);
                break;
            case MessageOperationKind.PreparedQuery:
                QueryPacket.WritePreparedMeasured(Statement!.Name, Parameters.Span, destination, Size);
                break;
            case MessageOperationKind.Close:
                Statement!.CloseMessage.Write(destination);
                break;
            default:
                throw new InvalidOperationException("Sync is written separately.");
        }
    }

    internal bool TryStart()
    {
        var state = _state;
        while (state == State.Queued || HasSync && state == State.Cancelled)
        {
            var observed = Interlocked.CompareExchange(ref _state, State.Encoding, state);
            if (observed == state)
            {
                return true;
            }
            state = observed;
        }
        return false;
    }
    internal void Published()
    {
        ReleaseInputs();
        Statement = null; // The response FIFO now owns any published statement.
        // Publish before rechecking cancellation so its callback cannot miss the input barrier.
        Interlocked.Exchange(ref _state, State.Published);
        if (!IsSync && Batch.RequestToken.IsCancellationRequested)
        {
            Cancel();
        }
    }

    internal void Cancel()
    {
        if (IsSync)
        {
            return;
        }
        var state = _state;
        if (state == State.Queued && Interlocked.CompareExchange
            (
                ref _state,
                HasSync ? State.Cancelled : State.Finished,
                State.Queued
            ) == State.Queued)
        {
            ReleaseInputs();
            FailUnpublishedPreparation(new OperationCanceledException(Batch.RequestToken));
            Statement = null;
            Signal(cancelled: true);
        }
        else if (state == State.Published)
        {
            Signal(cancelled: true);
        }
    }

    internal void Complete(Exception? error = null)
    {
        if (error is not null && _state == State.Encoding)
        {
            FailUnpublishedPreparation(error);
        }
        ReleaseInputs();
        Statement = null;
        _state = State.Finished;
        if (error is null)
        {
            _delivery?.TrySetResult();
        }
        else
        {
            _delivery?.TrySetException(error);
        }
        if (error is OperationCanceledException && !IsSync && Batch.RequestToken.IsCancellationRequested)
        {
            Signal(error, true);
        }
        else if (error is not null)
        {
            Signal(error);
        }
        else
        {
            Signal();
        }
    }

    internal void FailQueued(Exception error)
    {
        var state = _state;
        if ((state == State.Queued || state == State.Cancelled) && Interlocked.CompareExchange
            (
                ref _state,
                State.Finished,
                state
            ) == state)
        {
            ReleaseInputs();
            FailUnpublishedPreparation(error);
            Statement = null;
            Signal(error);
            _delivery?.TrySetException(error);
        }
        else if (state == State.Published)
        {
            Signal(error);
            _delivery?.TrySetException(error);
        }
    }

    private enum State
    {
        Queued,
        Cancelled,
        Encoding,
        Published,
        Finished
    }
}