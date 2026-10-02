using System.Buffers;
using System.Threading.Channels;
using Mpgsql.Internal;
using Mpgsql.Protocol;

namespace Mpgsql;

/// <summary>One ordered group of queries with a common Sync, transaction and error boundary.</summary>
/// <remarks>Cancellation stops admission and consumption, not SQL or the shared transport.</remarks>
public sealed class MpgsqlQueryBatch : IAsyncDisposable
{
    private enum Phase
    {
        Parse,
        Bind,
        Describe,
        Rows,
        Recovery
    }

    private readonly object _gate = new();

    private readonly Channel<ResultEvent> _events = Channel.CreateUnbounded<ResultEvent>(new()
        {AllowSynchronousContinuations = false});

    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _sealed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenRegistration _registration;
    private readonly MpgsqlMessageSession _session;
    private readonly HashSet<OutboundWork> _pendingWrites = [];
    private int _queryCount;
    private int _responseQuery;
    private int _columnCount;
    private bool _hasRows;
    private bool _isSealed;
    private Phase _phase;
    private DiagnosticMessage? _error;
    private int? _errorIndex;
    private int _discard;
    private int _syncQueued;
    private int _readStarted;
    private int _disposed;
    private int _errorObserved;
    private MpgsqlResultReader? _reader;

    internal MpgsqlQueryBatch(MpgsqlMessageSession session,
        CancellationToken token)
    {
        _session = session;
        RequestToken = token;
        _registration = token.UnsafeRegister(static state =>
            {
                var batch = (MpgsqlQueryBatch)state!;
                Volatile.Write(ref batch._discard,
                    1);
                batch._session.ScheduleDiscard(batch);
            },
            this);
    }

    internal CancellationToken RequestToken { get; }
    internal bool DiscardsRows => Volatile.Read(ref _discard) != 0;

    internal int QueryCount
    {
        get
        {
            lock (_gate) return _queryCount;
        }
    }

    internal bool ConsumerDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Completes when the explicit Sync is published, independently of request cancellation.</summary>
    public Task Sealed => _sealed.Task;
    /// <summary>Completes at ReadyForQuery; SQL/transport errors fault this task even when consumption was cancelled.</summary>
    public Task Completion => _completion.Task;
    public TransactionStatus? TransactionStatus { get; private set; }

    public ValueTask SendQueryAsync(string sql,
        ReadOnlyMemory<MpgsqlParameter> parameters = default)
    {
        ThrowForSend();
        return _session.SendQueryAsync(this,
            sql,
            parameters);
    }

    /// <summary>Queues exactly one Sync after all previously submitted sends, including sends not yet awaited.</summary>
    /// <remarks>Call from the sending flow. Request cancellation never suppresses this recovery boundary.</remarks>
    public ValueTask SendSyncAsync() => _session.SendSyncAsync(this);

    public async ValueTask<MpgsqlResultReader> ReadResultsAsync()
    {
        if (Interlocked.CompareExchange(ref _readStarted,
                1,
                0) != 0)
        {
            throw new InvalidOperationException("ReadResultsAsync can be called only once per group.");
        }
        if (ConsumerDisposed)
        {
            throw new ObjectDisposedException(nameof(MpgsqlQueryBatch));
        }
        try
        {
            RequestToken.ThrowIfCancellationRequested();
            _reader = new(this);
            await _reader.InitializeAsync().ConfigureAwait(false);
            return _reader;
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal void ThrowForSend()
    {
        RequestToken.ThrowIfCancellationRequested();
        if (ConsumerDisposed)
        {
            throw new ObjectDisposedException(nameof(MpgsqlQueryBatch));
        }
        if (Volatile.Read(ref _syncQueued) != 0)
        {
            throw new InvalidOperationException("The query group is already closing.");
        }
    }

    internal void RegisterQuery()
    {
        lock (_gate)
        {
            _queryCount++;
        }
    }

    internal void SealPublished()
    {
        lock (_gate)
        {
            _isSealed = true;
            _sealed.TrySetResult();
        }
    }

    internal void SyncQueued()
    {
        if (Interlocked.CompareExchange(ref _syncQueued,
                1,
                0) != 0)
        {
            throw new InvalidOperationException("SendSyncAsync can be called only once per group.");
        }
    }

    internal void AddWrite(OutboundWork work)
    {
        lock (_gate)
        {
            _pendingWrites.Add(work);
            if (RequestToken.IsCancellationRequested)
            {
                work.Cancel();
            }
        }
    }

    internal void RemoveWrite(OutboundWork work)
    {
        lock (_gate) _pendingWrites.Remove(work);
    }
    internal void ReleaseRegistration() => _registration.Unregister();

    internal void Accept(BackendMessage message,
        ref IMemoryOwner<byte>? owner)
    {
        lock (_gate)
        {
            if (message.Kind == BackendMessageKind.ErrorResponse)
            {
                if (_phase == Phase.Recovery)
                {
                    Unexpected(message.Kind);
                }
                _error = message.GetDiagnostics();
                _errorIndex = _responseQuery == _queryCount ? null : _responseQuery;
                _phase = Phase.Recovery;
                DrainEvents();
                return;
            }
            if (message.Kind == BackendMessageKind.ReadyForQuery)
            {
                if (!_isSealed || (_phase != Phase.Recovery &&
                                   (_phase != Phase.Parse || _responseQuery != _queryCount)))
                {
                    Unexpected(message.Kind);
                }
                TransactionStatus = message.GetTransactionStatus();
                Complete(_error is { } error ? new MpgsqlServerException(error,
                    _errorIndex,
                    TransactionStatus.Value) : null);
                return;
            }
            if (_responseQuery >= _queryCount || _phase == Phase.Recovery)
            {
                Unexpected(message.Kind);
            }
            switch (_phase, message.Kind)
            {
                case (Phase.Parse, BackendMessageKind.ParseComplete): _phase = Phase.Bind; break;
                case (Phase.Bind, BackendMessageKind.BindComplete): _phase = Phase.Describe; break;
                case (Phase.Describe, BackendMessageKind.RowDescription):
                    if (DiscardsRows)
                    {
                        var description = new WireReader(message.Payload);
                        _columnCount = description.Count();
                        _hasRows = true;
                        _phase = Phase.Rows;
                        break;
                    }
                    var columns = message.GetRowDescription();
                    foreach (var column in columns.Span)
                        if (column.Format != FormatCode.Binary)
                        {
                            throw new InvalidDataException("A binary result has a text column.");
                        }
                    _columnCount = columns.Length;
                    _hasRows = true;
                    _phase = Phase.Rows;
                    Publish(new(_responseQuery,
                        columns));
                    break;
                case (Phase.Describe, BackendMessageKind.NoData):
                    _columnCount = 0;
                    _hasRows = false;
                    _phase = Phase.Rows;
                    Publish(new(_responseQuery,
                        default));
                    break;
                case (Phase.Rows, BackendMessageKind.DataRow):
                    ValidateRow(message.GetDataRow().Count);
                    if (!DiscardsRows)
                    {
                        var row = new OwnedRow(message,
                            owner);
                        if (owner is null)
                        {
                            _session.RecordRowCopy(message.Payload.Length);
                        }
                        owner = null;
                        Publish(new(_responseQuery,
                            default,
                            row));
                    }
                    break;
                case (Phase.Rows, BackendMessageKind.CommandComplete):
                case (Phase.Rows, BackendMessageKind.EmptyQueryResponse):
                    Publish(new(_responseQuery,
                        default,
                        CommandTag:
                        !DiscardsRows && message.Kind == BackendMessageKind.CommandComplete
                            ? message.GetCommandTag()
                            : null,
                        IsEnd: true));
                    _responseQuery++;
                    _phase = Phase.Parse;
                    break;
                default: Unexpected(message.Kind); break;
            }
        }
    }

    internal void AcceptSkippedRow(int columns)
    {
        lock (_gate) ValidateRow(columns);
    }

    private void ValidateRow(int columns)
    {
        if (_phase != Phase.Rows || !_hasRows || _columnCount != columns)
        {
            throw new InvalidDataException("DataRow does not match the active portal description.");
        }
    }

    private void Publish(ResultEvent result)
    {
        if (DiscardsRows || !_events.Writer.TryWrite(result))
        {
            result.Row?.Dispose();
        }
    }

    private void Complete(Exception? error)
    {
        if (error is null)
        {
            _completion.TrySetResult();
        }
        else
        {
            _completion.TrySetException(error);
        }
        _events.Writer.TryComplete(error);
    }

    internal void Fail(Exception error)
    {
        lock (_gate)
        {
            Volatile.Write(ref _discard,
                1);
            foreach (var work in _pendingWrites) work.FailQueued(error);
            DrainEvents();
            _sealed.TrySetException(error);
            Complete(error);
        }
    }

    internal void ProcessCancellation()
    {
        lock (_gate)
        {
            foreach (var work in _pendingWrites) work.Cancel();
            DrainEvents();
            _events.Writer.TryComplete(new OperationCanceledException(RequestToken));
        }
    }

    private void DrainEvents()
    {
        while (_events.Reader.TryRead(out var result)) result.Row?.Dispose();
    }

    internal async ValueTask<ResultEvent?> ReadEventAsync()
    {
        RequestToken.ThrowIfCancellationRequested();
        try { return await _events.Reader.ReadAsync().ConfigureAwait(false); }
        catch (ChannelClosedException)
        {
            RequestToken.ThrowIfCancellationRequested();
            await ObserveCompletionAsync().ConfigureAwait(false);
            return null;
        }
    }

    internal async ValueTask ObserveCompletionAsync()
    {
        try { await Completion.ConfigureAwait(false); }
        catch
        {
            if (Interlocked.Exchange(ref _errorObserved,
                    1) == 0)
            {
                throw;
            }
        }
    }

    internal async ValueTask DiscardResultsAsync()
    {
        Volatile.Write(ref _discard,
            1);
        lock (_gate)
        {
            DrainEvents();
            _events.Writer.TryComplete();
        }
        _reader?.ReleaseCurrent();
        if (Volatile.Read(ref _syncQueued) != 0 && !RequestToken.IsCancellationRequested)
        {
            await ObserveCompletionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Discards consumption. Never sends Sync; the sending flow must still call SendSyncAsync.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed,
                1) != 0)
        {
            return;
        }
        try { await DiscardResultsAsync().ConfigureAwait(false); }
        finally { _session.ReleaseBatch(this); }
    }

    private void Unexpected(BackendMessageKind kind)
        => throw new InvalidDataException($"Unexpected {kind} in query {_responseQuery}, phase {_phase}.");
}