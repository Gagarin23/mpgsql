using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using Mpgsql.Internal;
using Mpgsql.Protocol;

namespace Mpgsql;

public sealed partial class MpgsqlMessageSession
{
    private ReadOnlySequence<byte> _adoInput;
    private ReadOnlyMemory<byte> _adoSegment;
    private int _adoSegmentConsumed;
    private bool _adoReadOutstanding;
    private bool _adoReadCompleted;
    private IMemoryOwner<byte>? _adoFrameOwner;
    private BorrowedRow? _adoRow;
    private bool _adoDraining;
    private TaskCompletionSource? _adoDrainIdle;
    private CancellationTokenRegistration _adoLifetimeWake;
    private ValueTask<ReadResult> _adoPendingInput;
    private bool _adoHasPendingInput;

    internal bool IsAdoSession => _adoSession;
    internal BorrowedRow AdoRow => _adoRow!;

    // Public factories may already have started their idle network reader. Stop it at a
    // PipeReader boundary before handing its unread bytes to the exclusive ADO consumer.
    internal async ValueTask ClaimForAdoDataSourceAsync(long rowBytes, CancellationToken cancellationToken)
    {
        ClaimForDataSource(rowBytes);
        if (_adoSession)
            return;
        try
        {
            _adoReceiveHandoff = true;
            _input.CancelPendingRead();
            await _receiveTask.ConfigureAwait(false);
            await WaitForBackgroundOutputIdleAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                ThrowIfStopped();
                _adoRow = new BorrowedRow();
                _adoSession = true;
                _adoIdlePartialFrame = _frames.HasPartialFrame;
                InstallAdoLifetimeWake();
                InstallAdoIdleMonitor();
            }
        }
        catch (Exception error)
        {
            Abort(error);
            // Ownership has transferred, so finish disposal here rather than letting a
            // pool mistake this claimed session for another owner's live connection.
            _adoSession = true;
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal void WakeAdoReader()
    {
        if (!_adoSession)
            return;
        try { _input.CancelPendingRead(); }
        catch (ObjectDisposedException) { }
    }

    // One session-owned internal wake replaces a lifetime-token registration on
    // each PipeReader read. Request cancellation retains its separate ownership wake.
    private void InstallAdoLifetimeWake()
    {
        _adoLifetimeWake = _lifetime.Token.UnsafeRegister(
            static state => ((MpgsqlMessageSession)state!).WakeAdoReader(), this);
    }

    // Called within writer admission, before any inline packet can publish. Post
    // input IO now; parsing and the single await still belong to reader movement.
    private void ArmAdoInput()
    {
        if (_adoIdleOwner is not null || _adoHasPendingInput || _adoReadOutstanding || !_adoInput.IsEmpty)
            return;
        _adoHasPendingInput = true;
        try { _adoPendingInput = _input.ReadAsync(); }
        catch (Exception error)
        {
            // Admission already owns the writer item. Its normal writer must still
            // run/release it; the input owner observes this failure through one await.
            _adoPendingInput = ValueTask.FromException<ReadResult>(error);
        }
    }

    // Disposal has stopped admission and waited for movement, drain and both
    // writers. Only it can own a read posted before raw-reader registration now.
    private async ValueTask ObservePendingAdoInputAsync()
    {
        if (!_adoHasPendingInput)
            return;
        var pending = _adoPendingInput;
        _adoPendingInput = default;
        _adoHasPendingInput = false;
        try { await pending.ConfigureAwait(false); }
        catch { } // Preserve the session's original failure while observing the read.
    }

    internal void ReleaseAdoRow()
    {
        _adoRow?.Reset();
        _adoFrameOwner?.Dispose();
        _adoFrameOwner = null;
    }

    // The movement owner retains the outstanding PipeReader buffer while a row is
    // visible. Full frames are borrowed; only fragmented frames need assembly storage.
    internal bool TryReadAdoEvent(MpgsqlQueryBatch batch, out ResultEvent result, bool draining = false)
    {
        if (!draining && batch.AdoConsumptionStopped)
        {
            result = default;
            return false;
        }
        if (batch.TryTakeAdoEvent(out result))
            return true;
        while (!_adoInput.IsEmpty)
        {
            if (!draining && !batch.DiscardsRows && TryReadBufferedAdoRow(batch))
            {
                result = new ResultEvent(batch.AdoRowIndex, default, IsBorrowedRow: true);
                return true;
            }
            CommitAdoSegment();
            if (!_frames.TryRead(ref _adoInput, !_adoIdlePartialFrame && (draining || batch.DiscardsRows),
                    out var message, out var owner, out var skippedColumns))
                break;
            try
            {
                if (_adoIdlePartialFrame)
                {
                    _adoIdlePartialFrame = false;
                    AcceptAdoIdleMessage(message);
                    continue;
                }
                if (skippedColumns >= 0)
                {
                    batch.AcceptSkippedRow(skippedColumns);
                    continue;
                }
                if (message.IsAsynchronous)
                {
                    RouteAsynchronous(message);
                    continue;
                }
                if (message.Kind == BackendMessageKind.ErrorResponse)
                {
                    DiagnosticMessage diagnostics;
                    lock (_gate) { _unrecoveredError = diagnostics = message.GetDiagnostics(); }
                    var terminal = (diagnostics.InvariantSeverity ?? diagnostics.Severity) is "FATAL" or "PANIC";
                    batch.AcceptError(diagnostics, terminal);
                    if (terminal)
                        throw new MpgsqlServerException(diagnostics, null, null);
                    continue;
                }
                if (message.Kind is BackendMessageKind.CopyInResponse or BackendMessageKind.CopyOutResponse or BackendMessageKind.CopyBothResponse)
                    throw new NotSupportedException("COPY requires the separate COPY API and an exclusively owned connection.");
                if (message.Kind == BackendMessageKind.DataRow)
                {
                    var index = batch.AcceptBorrowedRow(message.GetDataRow().Count);
                    if (draining || batch.DiscardsRows)
                    {
                        OwnedRow.ValidateValues(message);
                        continue;
                    }
                    _adoRow!.Initialize(message);
                    _adoFrameOwner = owner;
                    owner = null;
                    result = new ResultEvent(index, default, IsBorrowedRow: true);
                    return true;
                }
                RowBufferBudget? reservation = null;
                if (message.Kind == BackendMessageKind.ReadyForQuery)
                {
                    lock (_gate) { _transactionStatus = message.GetTransactionStatus(); }
                }
                batch.Accept(message, ref owner, ref reservation);
                if (message.Kind == BackendMessageKind.ReadyForQuery)
                {
                    lock (_gate)
                    {
                        _unrecoveredError = null;
                        if (!_responses.TryPeek(out var head) || head != batch)
                            throw new InvalidDataException("ADO response group does not own ReadyForQuery.");
                        _responses.Dequeue();
                    }
                    ReleaseBatch(batch);
                    break;
                }
                if (batch.TryTakeAdoEvent(out result))
                    return true;
            }
            catch (Exception error) { throw Fail(error); }
            finally { owner?.Dispose(); }
        }
        result = default;
        return false;
    }

    // A complete row in the current segment needs neither a SequenceReader for
    // its header nor a general message dispatch. Field framing is checked once
    // while building the reusable index. Fragmentation uses the common parser.
    internal bool TryReadAdoRow(MpgsqlQueryBatch batch)
    {
        return !batch.AdoConsumptionStopped && !_adoInput.IsEmpty && TryReadBufferedAdoRow(batch);
    }

    private bool TryReadBufferedAdoRow(MpgsqlQueryBatch batch)
    {
        if (_frames.HasPartialFrame)
            return false;
        if (_adoSegment.IsEmpty)
            _adoSegment = _adoInput.First;
        var bytes = _adoSegment.Span[_adoSegmentConsumed..];
        if (bytes.Length < 7 || bytes[0] != (byte)'D')
            return false;
        var length = BinaryPrimitives.ReadInt32BigEndian(bytes[1..]);
        if (length < 6 || length > BackendMessageReader.DefaultMaxMessageLength)
            throw Fail(new InvalidDataException("Invalid PostgreSQL DataRow message length."));
        if (bytes.Length < length + 1)
            return false;
        var count = BinaryPrimitives.ReadUInt16BigEndian(bytes[5..]);
        try
        {
            batch.AcceptBorrowedRow(count);
            _adoRow!.Initialize(_adoSegment.Slice(_adoSegmentConsumed + 5, length - 4), count);
            _adoSegmentConsumed += length + 1;
            return true;
        }
        catch (Exception error) { throw Fail(error); }
    }

    // Rows advance a numeric offset in their retained segment. Materialize the
    // sequence position only at common-parser or PipeReader ownership boundaries.
    private void CommitAdoSegment()
    {
        if (_adoSegmentConsumed != 0)
            _adoInput = _adoInput.Slice(_adoSegmentConsumed);
        _adoSegment = default;
        _adoSegmentConsumed = 0;
    }

    // The reader already attempted synchronous parsing. Await input itself and
    // retry that same movement loop, without a second event-parser awaitable.
    internal ValueTask<bool> WaitForAdoInputAsync(MpgsqlQueryBatch batch) => ReadAdoInputAsync(batch);

    internal async ValueTask DrainAdoAsync(MpgsqlQueryBatch batch)
    {
        lock (_gate)
        {
            ThrowIfStopped();
            if (_adoDraining)
                throw new InvalidOperationException("Concurrent ADO protocol drains are not supported.");
            _adoDraining = true;
        }
        try
        {
            await WaitForAdoIdleOwnerAsync().ConfigureAwait(false);
            ReleaseAdoRow();
            while (!batch.ProtocolCompleted)
            {
                while (TryReadAdoEvent(batch, out _, draining: true)) { }
                if (batch.ProtocolCompleted)
                    break;
                await ReadAdoInputAsync().ConfigureAwait(false);
            }
            // Return the session with no outstanding read, keeping any unread async
            // messages in the pipe for the next exclusive owner.
            if (_adoReadOutstanding)
            {
                CommitAdoSegment();
                _input.AdvanceTo(_adoInput.Start, _adoInput.Start);
                _adoReadOutstanding = false;
                _adoInput = default;
            }
        }
        catch (Exception error) { throw Fail(error); }
        finally
        {
            lock (_gate)
            {
                _adoDraining = false;
                _adoIdleSince = Environment.TickCount64;
                _adoDrainIdle?.TrySetResult();
                _adoDrainIdle = null;
            }
        }
    }

    // The movement owner or recovery drain awaits each input read exactly once;
    // disposal waits for that owner before completing the pipe. Its suspension
    // state can therefore be pooled without exposing a reusable public awaitable.
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<bool> ReadAdoInputAsync(MpgsqlQueryBatch? batch = null, AdoCursor? cursor = null)
    {
        try
        {
            var idleOwner = WaitForAdoIdleOwnerAsync();
            if (!idleOwner.IsCompletedSuccessfully)
            {
                await idleOwner.ConfigureAwait(false);
                lock (_gate) { ThrowIfStopped(); }
            }
            if (batch is not null)
            {
                if (batch.AdoConsumptionStopped)
                {
                    if (batch.AdoReaderDisposed)
                        throw new ObjectDisposedException(nameof(MpgsqlResultReader));
                    throw new OperationCanceledException(batch.RequestToken);
                }
                // RFQ can share the final completed transport buffer. Do not read
                // past a recovered protocol boundary just to discover its EOF.
                if (batch.ProtocolCompleted)
                    return false;
            }
            if (cursor is not null)
            {
                if (cursor.ConsumptionStopped)
                {
                    if (cursor.IsDisposed)
                        throw new ObjectDisposedException(nameof(AdoCursor));
                    throw new OperationCanceledException(cursor.RequestToken);
                }
                if (cursor.ProtocolCompleted)
                    return false;
            }
            // A preposted read owns a single-use awaitable even if lifetime has already
            // ended. Consume it first; after storing its buffer, the lifetime check below
            // invalidates the owner and disposal can safely complete the input endpoint.
            if (!_adoHasPendingInput)
                _lifetime.Token.ThrowIfCancellationRequested();
            if (_adoReadOutstanding)
            {
                CommitAdoSegment();
                _input.AdvanceTo(_adoInput.Start, _adoInput.End);
                _adoReadOutstanding = false;
                if (_adoReadCompleted)
                    throw new EndOfStreamException(_frames.HasPartialFrame ? "Truncated backend frame." : "The backend closed the transport.");
            }
            ReadResult read;
            if (_adoHasPendingInput)
            {
                var pending = _adoPendingInput;
                _adoPendingInput = default;
                _adoHasPendingInput = false;
                read = await pending.ConfigureAwait(false);
            }
            else
                read = await _input.ReadAsync().ConfigureAwait(false);
            _adoInput = read.Buffer;
            _adoSegment = default;
            _adoSegmentConsumed = 0;
            _adoReadOutstanding = true;
            _adoReadCompleted = read.IsCompleted;
            // CancelPendingRead is an ownership wake-up. Lifetime cancellation still
            // aborts transport; request cancellation is inspected by the movement owner.
            _lifetime.Token.ThrowIfCancellationRequested();
            return true;
        }
        catch (OperationCanceledException) when ((batch?.AdoConsumptionStopped == true || cursor?.ConsumptionStopped == true) && !_lifetime.IsCancellationRequested) { throw; }
        catch (ObjectDisposedException) when ((batch?.AdoConsumptionStopped == true || cursor?.ConsumptionStopped == true) && !_lifetime.IsCancellationRequested) { throw; }
        catch (Exception error) { throw Fail(error); }
    }
}
