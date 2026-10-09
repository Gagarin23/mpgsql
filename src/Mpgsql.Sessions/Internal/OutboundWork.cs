namespace Mpgsql.Internal;

// One FIFO write operation and its multi-observer acknowledgement. Keeping the promise on the
// work removes a separate TaskCompletionSource object, with the same asynchronous continuations.
// Cancellation cannot release borrowed parameters while encoding.
internal sealed class OutboundWork : TaskCompletionSource
{
    // These private markers never escape as an incomplete awaitable or receive a
    // work-specific result. They preserve the field's existing layout and leave
    // general work's null/shared acknowledgement representation unchanged.
    private static readonly TaskCompletionSource PendingAdoDelivery = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly TaskCompletionSource SuccessfulAdoDelivery = CreateSuccessfulDelivery();
    private readonly int _queryCount;
    private TaskCompletionSource? _delivery;
    private int _nextQuery;
    private QueryDefinition[]? _queries;

    private volatile State _state;

    internal OutboundWork(
        IQueryGroup batch, QueryDefinition[] queries,
        bool sync = false
    )
        : base(TaskCreationOptions.RunContinuationsAsynchronously)
    {
        Group = ResponseGroup = batch;
        Kind = MessageOperationKind.Query;
        IsQueryGroup = true;
        HasSync = sync;
        if (sync && batch.IsAdoSession)
        {
            _delivery = PendingAdoDelivery;
        }
        else if (sync && batch.RequestToken.CanBeCanceled)
        {
            _delivery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        _queries = queries;
        _queryCount = queries.Length;
    }

    internal OutboundWork(IQueryGroup batch, QueryDefinition query)
        : base(TaskCreationOptions.RunContinuationsAsynchronously)
    {
        Group = ResponseGroup = batch;
        Kind = MessageOperationKind.Query;
        IsQueryGroup = HasSync = true;
        if (batch.IsAdoSession)
        {
            _delivery = PendingAdoDelivery;
        }
        else if (batch.RequestToken.CanBeCanceled)
        {
            _delivery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        _queryCount = 1;
        (Sql, Parameters, Size, Statement) = (query.Sql, query.Parameters, query.EncodedSize, query.PreparedStatement);
    }

    internal OutboundWork(
        IQueryGroup batch, IQueryGroup responseBatch,
        QueryDefinition query
    )
        : base(TaskCreationOptions.RunContinuationsAsynchronously)
    {
        Group = batch;
        ResponseGroup = responseBatch;
        Kind = MessageOperationKind.Query;
        (Sql, Parameters, Size) = (query.Sql, query.Parameters, query.EncodedSize);
    }

    internal OutboundWork(
        IQueryGroup batch,
        string? sql = null,
        ReadOnlyMemory<MpgsqlParameterValue> parameters = default,
        int size = 0
    )
        : base(TaskCreationOptions.RunContinuationsAsynchronously)
    {
        ResponseGroup = batch;
        (Group, Kind, Sql, Parameters, Size) = (batch,
            sql is null ? MessageOperationKind.Sync : MessageOperationKind.Query, sql, parameters, size);
    }

    internal OutboundWork(
        IQueryGroup batch,
        MessageOperationKind kind,
        MpgsqlPreparedStatement statement,
        int size,
        ReadOnlyMemory<MpgsqlParameterValue> parameters = default
    )
        : base(TaskCreationOptions.RunContinuationsAsynchronously)
    {
        ResponseGroup = batch;
        (Group, Kind, Statement, Size, Parameters) = (batch, kind, statement, size, parameters);
    }
    internal IQueryGroup Group { get; }
    internal MpgsqlQueryBatch Batch => (MpgsqlQueryBatch)Group;
    internal IQueryGroup ResponseGroup { get; }
    internal MpgsqlQueryBatch ResponseBatch => (MpgsqlQueryBatch)ResponseGroup;
    internal MessageOperationKind Kind { get; }
    internal bool IsSync => Kind == MessageOperationKind.Sync;
    internal bool IsQueryGroup { get; }
    internal bool HasSync { get; }
    // Snapshot taken by the successful Encoding claim. Cancellation may have
    // cleared inputs even when a manual Cancel has no cancelled request token.
    internal bool EncodingWasCancelled { get; private set; }
    internal bool CancellationRequested => EncodingWasCancelled || Group.CancellationRequested;
    // Session admission owns this marker until the writer's last output access.
    // A general work's late cleanup must not enter a newly handed-off ADO counter.
    internal bool AdoWriterTracked { get; set; }
    internal MpgsqlPreparedStatement? Statement { get; private set; }
    internal string? Sql { get; private set; }
    internal ReadOnlyMemory<MpgsqlParameterValue> Parameters { get; private set; }
    internal int Size { get; }
    internal Task Completion => Task;
    // Logical cancellation can release borrowed inputs before Sync delivery. ADO
    // always separates those barriers, including Cancel()/timeouts without a token,
    // but an individual delivery promise is needed only while a caller awaits it.
    internal Task Delivery
    {
        get
        {
            var delivery = Volatile.Read(ref _delivery);
            if (!ReferenceEquals(delivery, PendingAdoDelivery))
                return delivery?.Task ?? Task;
            lock (Group.DeliveryGate)
            {
                delivery = _delivery;
                if (ReferenceEquals(delivery, PendingAdoDelivery))
                {
                    delivery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    Volatile.Write(ref _delivery, delivery);
                }
                return delivery!.Task;
            }
        }
    }

    private static TaskCompletionSource CreateSuccessfulDelivery()
    {
        var delivery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        delivery.SetResult();
        return delivery;
    }

    internal void PrepareInputFlush()
    {
        // Only the writer creates a general work's split barrier, while it still
        // owns Encoding and before PauseQueryGroup. Cancel/FailQueued cannot finish
        // that encoding claim. ADO already has a non-null admission marker.
        if (Volatile.Read(ref _delivery) is null)
            Volatile.Write(ref _delivery, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }
    internal void InputsFlushed()
    {
        Signal(cancelled: CancellationRequested);
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
        if (CancellationRequested)
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
            TrySetCanceled(Group.RequestToken);
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
                EncodingWasCancelled = state == State.Cancelled;
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
        if (!IsSync && CancellationRequested)
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
            FailUnpublishedPreparation(new OperationCanceledException(Group.RequestToken));
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
        CompleteDelivery(error);
        if (error is OperationCanceledException && !IsSync && CancellationRequested)
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
            CompleteDelivery(error);
        }
        else if (state == State.Published)
        {
            Signal(error);
            CompleteDelivery(error);
        }
    }

    private void CompleteDelivery(Exception? error)
    {
        var delivery = Volatile.Read(ref _delivery);
        if (ReferenceEquals(delivery, PendingAdoDelivery))
        {
            // A getter may have observed Pending before this writer's flush
            // completed. Serialize both transitions on the existing batch gate.
            lock (Group.DeliveryGate)
            {
                delivery = _delivery;
                if (ReferenceEquals(delivery, PendingAdoDelivery))
                {
                    if (error is null)
                        delivery = SuccessfulAdoDelivery;
                    else
                    {
                        delivery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        delivery.SetException(error);
                    }
                    Volatile.Write(ref _delivery, delivery);
                    return;
                }
            }
        }
        if (ReferenceEquals(delivery, SuccessfulAdoDelivery))
            return;
        if (error is null)
            delivery?.TrySetResult();
        else
            delivery?.TrySetException(error);
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
