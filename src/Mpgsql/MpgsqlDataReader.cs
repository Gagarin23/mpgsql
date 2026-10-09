using System.Buffers;
using System.Collections;
using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Mpgsql.Internal;
using Mpgsql.Protocol;

namespace Mpgsql;

public sealed partial class MpgsqlDataReader : DbDataReader
{
    private static readonly Task<bool> ReadTrueTask = Task.FromResult(true);
    private static readonly Task<bool> ReadFalseTask = Task.FromResult(false);
    private readonly bool _closeConnection;
    private readonly MpgsqlConnection _connection;
    private readonly QueryExecution _execution;
    private readonly AdoCursor _reader;
    private readonly MpgsqlTypeMapper? _typeMapper;
    private Task? _close;

    private bool _prefetched,
        _hasRows,
        _positioned,
        _closed,
        _complete;

    internal MpgsqlDataReader(
        AdoCursor reader, QueryExecution execution,
        MpgsqlConnection connection, CommandBehavior behavior
    )
    {
        _reader = reader;
        _execution = execution;
        _connection = connection;
        _typeMapper = connection.TypeMapper;
        _closeConnection = (behavior & CommandBehavior.CloseConnection) != 0;
    }
    public override int Depth => 0;
    public override int FieldCount => _reader.Columns.Length;
    public override bool HasRows => _hasRows;
    public override bool IsClosed => _closed || _reader.IsDisposed && !_complete;
    public long RecordsAffected64 => _execution.RecordsAffected;
    public override int RecordsAffected => checked((int)RecordsAffected64);
    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));
    public ReadOnlyMemory<RowField> Columns => _reader.Columns;
    public int QueryIndex => _reader.QueryIndex;
    internal bool IsRowSet => _reader.IsRowSet;
    public string? CommandTag => _reader.CommandTag;
    internal static void ValidateBehavior(CommandBehavior behavior)
    {
        if ((behavior & ~(CommandBehavior.SequentialAccess | CommandBehavior.CloseConnection)) != 0)
        {
            throw new NotSupportedException($"CommandBehavior {behavior} is not supported.");
        }
    }
    internal void Initialize(bool hasRows)
    {
        _prefetched = _hasRows = hasRows;
    }
    public override string GetName(int ordinal)
    {
        return _reader.Columns.Span[ordinal].Name;
    }
    public override int GetOrdinal(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var columns = _reader.Columns.Span;
        for (var i = 0;
             i < columns.Length;
             i++)
        {
            if (columns[i].Name == name)
            {
                return i;
            }
        }
        for (var i = 0;
             i < columns.Length;
             i++)
        {
            if (string.Equals(columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        throw new IndexOutOfRangeException(name);
    }
    public override string GetDataTypeName(int ordinal)
    {
        return ResultValue.TypeName(_reader.Columns.Span[ordinal].DataTypeOid);
    }
    public override Type GetFieldType(int ordinal)
    {
        return ResultValue.ClrType(_reader.Columns.Span[ordinal].DataTypeOid);
    }
    private void CheckRow()
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (!_positioned)
        {
            throw new InvalidOperationException("ReadAsync must position the reader on a row.");
        }
    }
    /// <summary>Borrowed bytes remain valid only until the next movement, close, or owner disposal.</summary>
    public ReadOnlySequence<byte>? GetRawValue(int ordinal)
    {
        CheckRow();
        return _reader.GetRawValue(ordinal);
    }
    public override bool IsDBNull(int ordinal)
    {
        CheckRow();
        return _reader.IsDBNull(ordinal);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override T GetFieldValue<T>(int ordinal)
    {
        CheckRow();
        if (typeof(T) == typeof(long) && _reader.TryGetBorrowedBigint(ordinal, out var bigint))
        {
            return (T)(object)(bigint ?? throw new InvalidCastException("The value is SQL NULL."));
        }
        return GetFieldValueSlow<T>(ordinal);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private T GetFieldValueSlow<T>(int ordinal)
    {
        if (typeof(T) == typeof(object))
        {
            return (T)GetValue(ordinal);
        }
        try
        {
            return _typeMapper is null ? _reader.GetFieldValue<T>(ordinal) : _reader.GetFieldValue<T>(ordinal, _typeMapper);
        }
        catch (InvalidOperationException) when (_reader.IsDBNull(ordinal)) { throw new InvalidCastException("The value is SQL NULL."); }
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override object GetValue(int ordinal)
    {
        CheckRow();
        if (_reader.TryGetBorrowedBigint(ordinal, out var bigint))
        {
            return bigint.HasValue ? (object)bigint.GetValueOrDefault() : DBNull.Value;
        }
        var payload = _reader.GetRawValue(ordinal);
        return payload is { } bytes ? ResultValue.Read(_reader.Columns.Span[ordinal].DataTypeOid, bytes) : DBNull.Value;
    }
    public override int GetValues(object[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        CheckRow();
        var count = Math.Min(values.Length, FieldCount);
        for (var i = 0;
             i < count;
             i++)
        {
            values[i] = GetValue(i);
        }
        return count;
    }
    public override bool GetBoolean(int ordinal)
    {
        return GetFieldValue<bool>(ordinal);
    }
    public override short GetInt16(int ordinal)
    {
        return GetFieldValue<short>(ordinal);
    }
    public override int GetInt32(int ordinal)
    {
        return GetFieldValue<int>(ordinal);
    }
    public override long GetInt64(int ordinal)
    {
        return GetFieldValue<long>(ordinal);
    }
    public override float GetFloat(int ordinal)
    {
        return GetFieldValue<float>(ordinal);
    }
    public override double GetDouble(int ordinal)
    {
        return GetFieldValue<double>(ordinal);
    }
    public override decimal GetDecimal(int ordinal)
    {
        return GetFieldValue<decimal>(ordinal);
    }
    public override Guid GetGuid(int ordinal)
    {
        return GetFieldValue<Guid>(ordinal);
    }
    public override string GetString(int ordinal)
    {
        return GetFieldValue<string>(ordinal);
    }
    public override DateTime GetDateTime(int ordinal)
    {
        return GetFieldValue<DateTime>(ordinal);
    }
    public override byte GetByte(int ordinal)
    {
        throw new NotSupportedException("PostgreSQL has no byte scalar representation.");
    }
    public override char GetChar(int ordinal)
    {
        var value = GetString(ordinal);
        return value.Length == 1 ? value[0] : throw new InvalidCastException("The string is not a single character.");
    }
    public ReadOnlyMemory<long>? GetInt64Array(int ordinal)
    {
        CheckRow();
        return _reader.GetInt64Array(ordinal);
    }
    public ReadOnlyMemory<long?>? GetNullableInt64Array(int ordinal)
    {
        CheckRow();
        return _reader.GetNullableInt64Array(ordinal);
    }
    public override long GetBytes(
        int ordinal, long dataOffset,
        byte[]? buffer, int bufferOffset,
        int length
    )
    {
        if (_reader.Columns.Span[ordinal].DataTypeOid != (uint)TypeOid.Bytea)
        {
            throw new InvalidCastException("GetBytes requires bytea.");
        }
        var bytes = GetRawValue(ordinal) ?? throw new InvalidCastException("The value is SQL NULL.");
        if (buffer is null)
        {
            return bytes.Length;
        }
        ValidateCopy(dataOffset, bytes.Length, buffer.Length, bufferOffset, length);
        var count = (int)Math.Min(length, bytes.Length - dataOffset);
        var destination = buffer.AsSpan(bufferOffset, count);
        if (dataOffset == 0 && count == bytes.Length)
        {
            bytes.CopyTo(destination);
        }
        else
        {
            bytes
                .Slice(dataOffset, count)
                .CopyTo(destination);
        }
        return count;
    }
    public override long GetChars(
        int ordinal, long dataOffset,
        char[]? buffer, int bufferOffset,
        int length
    )
    {
        var value = GetString(ordinal);
        if (buffer is null)
        {
            return value.Length;
        }
        ValidateCopy(dataOffset, value.Length, buffer.Length, bufferOffset, length);
        var count = (int)Math.Min(length, value.Length - dataOffset);
        value
            .AsSpan((int)dataOffset, count)
            .CopyTo(buffer.AsSpan(bufferOffset, count));
        return count;
    }
    private static void ValidateCopy(
        long offset, long total,
        int capacity, int destinationOffset,
        int length
    )
    {
        if (offset < 0 || offset > total)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }
        if (destinationOffset < 0 || length < 0 || destinationOffset > capacity - length)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }
    }
    public override bool Read()
    {
        throw new NotSupportedException("Use ReadAsync.");
    }
    public override bool NextResult()
    {
        throw new NotSupportedException("Use NextResultAsync.");
    }
    public override IEnumerator GetEnumerator()
    {
        throw new NotSupportedException("Use ReadAsync.");
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        return TryReadRow(cancellationToken, out var row, out var pending)
            ? row ? ReadTrueTask : ReadFalseTask
            : pending.AsTask();
    }
    public override Task<bool> NextResultAsync(CancellationToken cancellationToken)
    {
        return NextResultValueTaskAsync(cancellationToken)
            .AsTask();
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<bool> ReadValueTaskAsync(CancellationToken cancellationToken = default)
    {
        return TryReadRow(cancellationToken, out var row, out var pending)
            ? new ValueTask<bool>(row)
            : pending;
    }
    // Both public APIs use one movement. A pending movement retains its lower
    // reader ownership and follows the existing cancellation/recovery path.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReadRow(CancellationToken cancellationToken, out bool row, out ValueTask<bool> pending)
    {
        row = false;
        pending = default;
        ObjectDisposedException.ThrowIf(_closed, this);
        if (_complete)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                pending = CancelMovementAsync(cancellationToken);
                return false;
            }
            if (_execution.IsCancellationRequested)
            {
                pending = FailCancelledMovementAsync();
                return false;
            }
            return true;
        }
        // Admission must precede all facade state changes, including prefetch.
        // A rejected competing operation must not recover/discard its owner.
        if (!TryEnterReaderMovement())
        {
            pending = FailCancelledMovementAsync();
            return false;
        }
        var ownsMovement = true;
        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                ownsMovement = false;
                _reader.ExitReaderMovement();
                pending = CancelMovementAsync(cancellationToken);
                return false;
            }
            if (_execution.IsCancellationRequested)
            {
                ownsMovement = false;
                _reader.ExitReaderMovement();
                pending = FailCancelledMovementAsync();
                return false;
            }
            if (cancellationToken.CanBeCanceled)
            {
                ownsMovement = false;
                pending = AwaitReadAsync(default, cancellationToken, startMovement: true);
                return false;
            }
            var next = ReadWithinMovementAsync();
            if (next.IsCompletedSuccessfully)
            {
                row = next.Result;
                _positioned = row;
                ownsMovement = false;
                _reader.ExitReaderMovement();
                return true;
            }
            ownsMovement = false; // AwaitReadAsync releases before recovery.
            pending = AwaitReadAsync(next, cancellationToken);
            return false;
        }
        catch (Exception error)
        {
            if (ownsMovement)
                _reader.ExitReaderMovement();
            pending = FailReadAsync(error);
            return false;
        }
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ValueTask<bool> ReadWithinMovementAsync()
    {
        if (_prefetched)
        {
            _prefetched = false;
            _positioned = true;
            return new(true);
        }
        _positioned = false;
        return _reader.ReadWithinMovementAsync();
    }
    private async ValueTask<bool> FailReadAsync(Exception error)
    {
        await FailAsync(error).ConfigureAwait(false);
        return false;
    }
    private async ValueTask<bool> CancelMovementAsync(CancellationToken token)
    {
        _execution.Cancel(token);
        await FailAsync(new OperationCanceledException(token))
            .ConfigureAwait(false);
        return false;
    }
    private async ValueTask<bool> FailCancelledMovementAsync()
    {
        await FailAsync(new OperationCanceledException())
            .ConfigureAwait(false);
        return false;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryEnterReaderMovement()
    {
        try
        {
            _reader.EnterReaderMovement();
            return true;
        }
        catch (ObjectDisposedException) when (_execution.IsCancellationRequested)
        {
            // Cancellation can hand an idle cursor to recovery before this call.
            // Join that recovery without changing state or interrupting an owner.
            return false;
        }
    }
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<bool> AwaitReadAsync(ValueTask<bool> movement, CancellationToken token, bool startMovement = false)
    {
        CancellationTokenRegistration registration = default;
        Exception? failure = null;
        var row = false;
        try
        {
            registration = token.CanBeCanceled ? token.UnsafeRegister(static (state, cancelled) => ((QueryExecution)state!).Cancel(cancelled), _execution) : default;
            if (startMovement)
            {
                // Register before starting input: even a throwing cancellation
                // callback cannot leave an unawaited movement behind recovery.
                _execution.ThrowIfCancelled();
                movement = ReadWithinMovementAsync();
            }
            row = await movement.ConfigureAwait(false);
            _execution.ThrowIfCancelled();
            _positioned = row;
        }
        catch (Exception error) { failure = error; }
        finally
        {
            _reader.ExitReaderMovement();
            registration.Dispose();
        }
        if (failure is not null)
            await FailAsync(failure).ConfigureAwait(false);
        return row;
    }
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<bool> NextResultValueTaskAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (_complete)
        {
            if (cancellationToken.IsCancellationRequested)
                return await CancelMovementAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
        if (!TryEnterReaderMovement())
            return await FailCancelledMovementAsync().ConfigureAwait(false);
        CancellationTokenRegistration registration = default;
        Exception? failure = null;
        var next = false;
        try
        {
            registration = cancellationToken.CanBeCanceled ? cancellationToken.UnsafeRegister(static (state, cancelled) => ((QueryExecution)state!).Cancel(cancelled), _execution) : default;
            _execution.ThrowIfCancelled();
            _prefetched = _positioned = false;
            // The session reader already drains the current result in NextResultAsync.
            // Do not perform a second movement just to rediscover CommandComplete.
            next = await _reader
                .NextResultWithinMovementAsync()
                .ConfigureAwait(false);
            _execution.ThrowIfCancelled();
            _complete = !next;
            _hasRows = false;
            if (next)
            {
                _prefetched = _hasRows = await _reader
                    .ReadWithinMovementAsync()
                    .ConfigureAwait(false);
                _execution.ThrowIfCancelled();
            }
        }
        catch (Exception error) { failure = error; }
        finally
        {
            _reader.ExitReaderMovement();
            registration.Dispose();
        }
        if (failure is not null)
            await FailAsync(failure).ConfigureAwait(false);
        else if (!next)
            await _execution.FinishAsync(false).ConfigureAwait(false);
        return next;
    }
    private async ValueTask FailAsync(Exception error)
    {
        try
        {
            await _execution
                .FinishAsync(true)
                .ConfigureAwait(false);
        }
        catch { }
        ExceptionDispatchInfo.Throw(_execution.Map(error));
    }
    public override void Close()
    {
        if (_closed)
        {
            return;
        }
        _closed = true;
        _positioned = false;
        if (!_execution.IsEnded)
        {
            _execution.Abort();
        }
        _close = CloseCoreAsync();
        _ = ObserveCloseAsync(_close);
    }
    private static async Task ObserveCloseAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch { }
    }
    public override Task CloseAsync()
    {
        if (_close is not null)
        {
            return _close;
        }
        _closed = true;
        _positioned = false;
        return _close = CloseCoreAsync();
    }
    private async Task CloseCoreAsync()
    {
        try
        {
            await _execution
                .FinishAsync(true)
                .ConfigureAwait(false);
        }
        finally
        {
            if (_closeConnection)
            {
                await _connection
                    .CloseAsync()
                    .ConfigureAwait(false);
            }
        }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Close();
        }
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        await CloseAsync()
            .ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
    public override DataTable? GetSchemaTable()
    {
        throw new NotSupportedException("Extended schema discovery is not supported.");
    }
}
