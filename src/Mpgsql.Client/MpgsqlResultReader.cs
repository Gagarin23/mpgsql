using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Mpgsql.Converters;
using Mpgsql.Internal;
using Mpgsql.Protocol;

namespace Mpgsql;

/// <summary>Forward-only results in query order. Concurrent movement/disposal is not supported.</summary>
public sealed class MpgsqlResultReader : IAsyncDisposable
{
    private readonly MpgsqlQueryBatch _batch;
    private OwnedRow? _row;
    private bool _end;
    private bool _finished;
    private const int ActiveMovement = 1;
    private const int Closed = 2;
    private int _movementState;
    private volatile bool _disposed;
    private TaskCompletionSource? _idle;
    internal QueryExecution? Execution { get; set; }
    internal bool IsRowSet { get; private set; }
    public int QueryIndex { get; private set; } = -1;
    public ReadOnlyMemory<RowField> Columns { get; private set; }
    public string? CommandTag { get; private set; }

    internal MpgsqlResultReader(MpgsqlQueryBatch batch) => _batch = batch;
    internal ValueTask<bool> InitializeAsync() => MoveAsync(Movement.FirstResult);
    public ValueTask<bool> ReadAsync()
    {
        bool entered = false;
        try
        {
            Enter();
            entered = true;
            ReleaseCurrent();
            _batch.RequestToken.ThrowIfCancellationRequested();
            if (_finished || _end) { Exit(); return new(false); }
            if (!_batch.TryReadEvent(out var value)) return MoveAsync(Movement.Row, entered: true);
            if (_disposed)
            {
                value.Row?.Dispose();
                throw new ObjectDisposedException(nameof(MpgsqlResultReader));
            }
            bool result;
            if (value.Row is { } row) { _row = row; result = true; }
            else if (value.IsEnd) { CommandTag = value.CommandTag; _end = true; result = false; }
            else throw new InvalidDataException("Unexpected result description inside rows.");
            Exit();
            return new(result);
        }
        catch (Exception error)
        {
            if (!entered) return ValueTask.FromException<bool>(error);
            _finished = _end = true;
            Exit();
            return FailMovementAsync(error);
        }
    }
    public ValueTask<bool> NextResultAsync() => MoveAsync(Movement.NextResult);

    private enum Movement { FirstResult, Row, NextResult }

    // Each movement awaits the availability source directly: no nested row/event async machines.
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<bool> MoveAsync(Movement movement, bool entered = false)
    {
        if (!entered) Enter();
        bool result = false;
        Exception? error = null;
        try
        {
            if (!entered) ReleaseCurrent();
            _batch.RequestToken.ThrowIfCancellationRequested();
            bool description = movement == Movement.FirstResult || _end;
            if (!_finished && (movement != Movement.Row || !_end))
            {
                while (true)
                {
                    _batch.RequestToken.ThrowIfCancellationRequested();
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (_batch.TryReadEvent(out var value))
                    {
                        if (_disposed)
                        {
                            value.Row?.Dispose();
                            throw new ObjectDisposedException(nameof(MpgsqlResultReader));
                        }
                        if (description)
                        {
                            if (value.Row is not null || value.IsEnd)
                            {
                                value.Row?.Dispose();
                                throw new InvalidDataException("Expected a result description.");
                            }
                            QueryIndex = value.QueryIndex;
                            Columns = value.Columns;
                            IsRowSet = value.IsRowSet;
                            CommandTag = null;
                            _end = false;
                            result = true;
                            break;
                        }
                        if (value.Row is { } row)
                        {
                            if (movement == Movement.Row) { _row = row; result = true; break; }
                            row.Dispose(); // NextResult drains rows before accepting the next description.
                            continue;
                        }
                        if (!value.IsEnd)
                            throw new InvalidDataException("Unexpected result description inside rows.");
                        CommandTag = value.CommandTag;
                        _end = true;
                        if (movement == Movement.Row) break;
                        description = true;
                        continue;
                    }
                    bool available;
                    try { available = await _batch.WaitForEventAsync().ConfigureAwait(false); }
                    catch
                    {
                        // A filter would run before the availability source's GetResult lock
                        // unwinds. Inspecting batch completion there reverses the producer's
                        // batch -> event-buffer lock order during cancellation/transport failure.
                        if (!_batch.Completion.IsCompleted && !_batch.RequestToken.IsCancellationRequested)
                            throw;
                        _batch.RequestToken.ThrowIfCancellationRequested();
                        await _batch.ObserveCompletionAsync().ConfigureAwait(false);
                        available = false;
                    }
                    if (available) continue;
                    _batch.RequestToken.ThrowIfCancellationRequested();
                    await _batch.ObserveCompletionAsync().ConfigureAwait(false);
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (!description) throw new InvalidDataException("Result ended without CommandComplete.");
                    _finished = _end = true;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _finished = true;
            _end = true;
            error = ex;
        }
        finally { Exit(); }
        if (error is not null)
            return await FailMovementAsync(error).ConfigureAwait(false);
        if (_finished && movement == Movement.NextResult && Execution is { } owner)
            await owner.EndReaderAsync(discard: false).ConfigureAwait(false);
        return result;
    }

    private async ValueTask<bool> FailMovementAsync(Exception error)
    {
        if (Execution is { } execution)
            try { await execution.EndReaderAsync(discard: true).ConfigureAwait(false); } catch { }
        ExceptionDispatchInfo.Throw(error);
        return false;
    }

    /// <summary>Borrowed bytes valid until the next movement or reader/group disposal. SQL NULL is null.</summary>
    public ReadOnlySequence<byte>? GetRawValue(int ordinal)
    {
        ObjectDisposedException.ThrowIf(_disposed,
            this);
        if (_row is not { } row)
        {
            throw new InvalidOperationException("ReadAsync must position the reader on a row.");
        }
        return row[ordinal];
    }

    public long? GetInt64(int ordinal)
    {
        return Int64Converter.ReadNullable(RequireType(ordinal, Int64Converter.TypeOid));
    }

    public bool IsDBNull(int ordinal) => GetRawValue(ordinal) is null;

    /// <summary>Reads an explicitly supported CLR representation. Raw bytes remain borrowed, while
    /// decoded arrays and strings own their storage. SQL NULL requires a nullable representation.</summary>
    public T GetFieldValue<T>(int ordinal)
    {
        var payload = GetRawValue(ordinal);
        var column = Columns.Span[ordinal];
        if (column.Format != FormatCode.Binary || !FieldValueDecoder<T>.Supports(column.DataTypeOid))
            throw new InvalidCastException($"Column {ordinal} cannot be read as {typeof(T)}.");
        if (payload is not { } bytes)
        {
            if (default(T) is null) return default!;
            throw new InvalidOperationException("SQL NULL requires a nullable CLR representation.");
        }
        return FieldValueDecoder<T>.Read(column.DataTypeOid, bytes);
    }

    public ReadOnlyMemory<long>? GetInt64Array(int ordinal)
    {
        if (RequireType(ordinal, Int64ArrayConverter.ArrayTypeOid) is not { } payload)
        {
            return null;
        }
        return Int64ArrayConverter.Read(payload);
    }

    public ReadOnlyMemory<long?>? GetNullableInt64Array(int ordinal)
    {
        if (RequireType(ordinal, NullableInt64ArrayConverter.ArrayTypeOid) is not { } payload)
        {
            return null;
        }
        return NullableInt64ArrayConverter.Read(payload);
    }

    private ReadOnlySequence<byte>? RequireType(int ordinal,
        uint oid)
    {
        var payload = GetRawValue(ordinal);
        var column = Columns.Span[ordinal];
        if (column.DataTypeOid != oid || column.Format != FormatCode.Binary)
        {
            throw new InvalidCastException($"Column {ordinal} is not binary PostgreSQL type {oid}.");
        }
        return payload;
    }

    private void Enter()
    {
        int previous = Interlocked.CompareExchange(ref _movementState, ActiveMovement, 0);
        if (previous != 0)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            throw new InvalidOperationException("Concurrent reader movement or disposal is not supported.");
        }
        // Owner invalidation publishes the flag before closing admission. If it raced
        // with this successful claim, release the claim before rejecting the movement.
        if (!_disposed) return;
        Exit();
        throw new ObjectDisposedException(nameof(MpgsqlResultReader));
    }

    private void Exit()
    {
        if (Interlocked.CompareExchange(ref _movementState, 0, ActiveMovement) == ActiveMovement) return;
        FinishClosing();
    }

    private void FinishClosing()
    {
        try { ReleaseCurrent(); }
        finally
        {
            // A late observer must not see idle until all borrowed ownership is released.
            Volatile.Write(ref _movementState, Closed);
            Volatile.Read(ref _idle)?.TrySetResult();
        }
    }

    internal Task InvalidateFromOwner()
    {
        _disposed = true;
        int state = Volatile.Read(ref _movementState);
        while (true)
        {
            if (state == Closed) return Task.CompletedTask;
            if (state == (Closed | ActiveMovement)) return WaitForClosing();
            int previous = Interlocked.CompareExchange(ref _movementState, Closed | ActiveMovement, state);
            if (previous != state) { state = previous; continue; }
            if (state == ActiveMovement) return WaitForClosing();
            // Claim an idle reader's cleanup as movement ownership too. A second
            // invalidator then waits instead of racing nullable row storage access.
            FinishClosing();
            return Task.CompletedTask;
        }
    }

    private Task WaitForClosing()
    {
        var idle = Volatile.Read(ref _idle);
        if (idle is null)
        {
            var created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            idle = Interlocked.CompareExchange(ref _idle, created, null) ?? created;
        }
        // FinishClosing may have run before this promise was installed.
        if (Volatile.Read(ref _movementState) == Closed) idle.TrySetResult();
        return idle.Task;
    }

    internal void ReleaseCurrent()
    {
        _row?.Dispose();
        _row = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            if (Execution is { } owner) await owner.EndReaderAsync(discard: true).ConfigureAwait(false);
            return;
        }
        Enter();
        try
        {
            _disposed = true;
            Interlocked.Or(ref _movementState, Closed);
            ReleaseCurrent();
        }
        finally { Exit(); }
        if (Execution is { } execution) await execution.EndReaderAsync(discard: true).ConfigureAwait(false);
        else await _batch.DiscardResultsAsync().ConfigureAwait(false);
    }
}
