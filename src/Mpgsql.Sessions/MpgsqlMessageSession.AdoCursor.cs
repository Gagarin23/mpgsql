using System.Buffers.Binary;
using Mpgsql.Internal;
using Mpgsql.Protocol;

namespace Mpgsql;

public sealed partial class MpgsqlMessageSession
{
    // Unlike the general response FIFO, an exclusive provider has one concrete
    // operation whose reader and protocol state have the same lifetime.
    private AdoCursor? _adoCursor;

    internal AdoCursor CreateAdoCursor(CancellationToken request, IResultExecutionOwner execution)
    {
        lock (_gate)
        {
            ThrowIfStopped();
            if (!_adoSession || _adoCursor is not null || _batches.Count != 0 || _responses.Count != 0 || _collecting is not null)
                throw new InvalidOperationException("The session must be exclusively owned and idle before creating an ADO cursor.");
            var cursor = new AdoCursor(this, request, execution);
            _adoCursor = cursor;
            StopAdoIdleOwner();
            return cursor;
        }
    }

    internal void ReleaseAdoCursor(AdoCursor cursor)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_adoCursor, cursor))
            {
                _adoCursor = null;
                _adoIdleSince = Environment.TickCount64;
            }
        }
    }

    internal bool TryReadAdoCursorEvent(AdoCursor cursor, out CursorEvent result, bool draining = false)
    {
        if (!draining && cursor.ConsumptionStopped)
        {
            result = CursorEvent.None;
            return false;
        }
        while (!_adoInput.IsEmpty)
        {
            if (!draining && !cursor.DiscardsRows && TryReadBufferedAdoRow(cursor))
            {
                result = CursorEvent.Row;
                return true;
            }
            CommitAdoSegment();
            if (!_frames.TryRead(ref _adoInput, !_adoIdlePartialFrame && (draining || cursor.DiscardsRows),
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
                    cursor.ValidateRow(skippedColumns);
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
                    cursor.AcceptError(diagnostics, terminal);
                    if (terminal)
                        throw new MpgsqlServerException(diagnostics, null, null);
                    continue;
                }
                if (message.Kind is BackendMessageKind.CopyInResponse or BackendMessageKind.CopyOutResponse or BackendMessageKind.CopyBothResponse)
                    throw new NotSupportedException("COPY requires the separate COPY API and an exclusively owned connection.");
                if (message.Kind == BackendMessageKind.DataRow)
                {
                    cursor.ValidateRow(message.GetDataRow().Count);
                    if (draining || cursor.DiscardsRows)
                    {
                        OwnedRow.ValidateValues(message);
                        continue;
                    }
                    _adoRow!.Initialize(message);
                    _adoFrameOwner = owner;
                    owner = null;
                    result = CursorEvent.Row;
                    return true;
                }
                result = cursor.Accept(message, draining);
                if (result == CursorEvent.ProtocolEnd)
                    break;
                if (!draining && !cursor.DiscardsRows && result != CursorEvent.None)
                    return true;
            }
            catch (Exception error) { throw Fail(error); }
            finally { owner?.Dispose(); }
        }
        result = CursorEvent.None;
        return false;
    }

    internal void RecordAdoReadyForQuery(AdoCursor cursor, TransactionStatus status)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_adoCursor, cursor))
                throw new InvalidDataException("ADO cursor does not own ReadyForQuery.");
            _transactionStatus = status;
            _unrecoveredError = null;
        }
    }

    internal bool TryReadAdoRow(AdoCursor cursor)
        => !cursor.ConsumptionStopped && !_adoInput.IsEmpty && TryReadBufferedAdoRow(cursor);

    private bool TryReadBufferedAdoRow(AdoCursor cursor)
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
            cursor.ValidateRow(count);
            _adoRow!.Initialize(_adoSegment.Slice(_adoSegmentConsumed + 5, length - 4), count);
            _adoSegmentConsumed += length + 1;
            return true;
        }
        catch (Exception error) { throw Fail(error); }
    }

    internal ValueTask<bool> WaitForAdoInputAsync(AdoCursor cursor) => ReadAdoInputAsync(cursor: cursor);

    internal async ValueTask DrainAdoAsync(AdoCursor cursor)
    {
        lock (_gate)
        {
            ThrowIfStopped();
            if (!ReferenceEquals(_adoCursor, cursor) || _adoDraining)
                throw new InvalidOperationException("Concurrent ADO protocol drains are not supported.");
            _adoDraining = true;
        }
        try
        {
            await WaitForAdoIdleOwnerAsync().ConfigureAwait(false);
            cursor.ReleaseCurrent();
            ReleaseAdoRow();
            while (!cursor.ProtocolCompleted)
            {
                while (TryReadAdoCursorEvent(cursor, out _, draining: true)) { }
                if (cursor.ProtocolCompleted)
                    break;
                await ReadAdoInputAsync().ConfigureAwait(false);
            }
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
}
