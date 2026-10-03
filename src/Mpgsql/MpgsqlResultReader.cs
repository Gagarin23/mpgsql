using System.Buffers;
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
    private int _busy;
    private volatile bool _disposed;
    private readonly Lock _stateGate = new();
    private TaskCompletionSource? _idle;
    internal QueryExecution? Execution { get; set; }
    internal bool IsRowSet { get; private set; }
    public int QueryIndex { get; private set; } = -1;
    public ReadOnlyMemory<RowField> Columns { get; private set; }
    public string? CommandTag { get; private set; }

    internal MpgsqlResultReader(MpgsqlQueryBatch batch) => _batch = batch;
    internal async ValueTask InitializeAsync() => await MoveResultAsync().ConfigureAwait(false);

    public async ValueTask<bool> ReadAsync()
    {
        Enter();
        bool result = false;
        Exception? error = null;
        try { result = await ReadCoreAsync().ConfigureAwait(false); }
        catch (Exception ex)
        {
            _finished = true;
            _end = true;
            error = ex;
        }
        finally { Exit(); }
        if (error is not null)
        {
            if (Execution is { } execution)
                try { await execution.EndReaderAsync(discard: true).ConfigureAwait(false); } catch { }
            ExceptionDispatchInfo.Throw(error);
        }
        return result;
    }

    private async ValueTask<bool> ReadCoreAsync()
    {
        ReleaseCurrent();
        _batch.RequestToken.ThrowIfCancellationRequested();
        if (_end || _finished)
        {
            return false;
        }
        var result = await _batch.ReadEventAsync().ConfigureAwait(false);
        if (_disposed)
        {
            result?.Row?.Dispose();
            throw new ObjectDisposedException(nameof(MpgsqlResultReader));
        }
        if (result is not { } value)
        {
            throw new InvalidDataException("Result ended without CommandComplete.");
        }
        if (value.Row is { } row)
        {
            _row = row;
            return true;
        }
        if (!value.IsEnd)
        {
            throw new InvalidDataException("Unexpected result description inside rows.");
        }
        CommandTag = value.CommandTag;
        _end = true;
        return false;
    }

    public async ValueTask<bool> NextResultAsync()
    {
        Enter();
        bool result = false;
        Exception? error = null;
        try
        {
            while (await ReadCoreAsync().ConfigureAwait(false)) { }
            result = await MoveResultAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _finished = true;
            _end = true;
            error = ex;
        }
        finally { Exit(); }
        if (error is not null)
        {
            if (Execution is { } execution)
                try { await execution.EndReaderAsync(discard: true).ConfigureAwait(false); } catch { }
            ExceptionDispatchInfo.Throw(error);
        }
        if (_finished && Execution is { } owner) await owner.EndReaderAsync(discard: false).ConfigureAwait(false);
        return result;
    }

    private async ValueTask<bool> MoveResultAsync()
    {
        ReleaseCurrent();
        _batch.RequestToken.ThrowIfCancellationRequested();
        if (_finished)
        {
            return false;
        }
        var result = await _batch.ReadEventAsync().ConfigureAwait(false);
        if (_disposed) throw new ObjectDisposedException(nameof(MpgsqlResultReader));
        if (result is not { } value)
        {
            _finished = true;
            _end = true;
            return false;
        }
        if (value.Row is not null || value.IsEnd)
        {
            throw new InvalidDataException("Expected a result description.");
        }
        QueryIndex = value.QueryIndex;
        Columns = value.Columns;
        IsRowSet = value.IsRowSet;
        CommandTag = null;
        _end = false;
        return true;
    }

    /// <summary>Borrowed bytes valid until the next movement or reader/group disposal. SQL NULL is null.</summary>
    public ReadOnlySequence<byte>? GetRawValue(int ordinal)
    {
        ObjectDisposedException.ThrowIf(_disposed,
            this);
        if (_row is null)
        {
            throw new InvalidOperationException("ReadAsync must position the reader on a row.");
        }
        if ((uint)ordinal >= (uint)_row.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }
        return _row[ordinal];
    }

    public long? GetInt64(int ordinal)
    {
        RequireType(ordinal,
            Int64Converter.TypeOid);
        return Int64Converter.ReadNullable(GetRawValue(ordinal));
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
        RequireType(ordinal,
            Int64ArrayConverter.ArrayTypeOid);
        if (GetRawValue(ordinal) is not { } payload)
        {
            return null;
        }
        return Int64ArrayConverter.Read(payload);
    }

    public ReadOnlyMemory<long?>? GetNullableInt64Array(int ordinal)
    {
        RequireType(ordinal,
            NullableInt64ArrayConverter.ArrayTypeOid);
        if (GetRawValue(ordinal) is not { } payload)
        {
            return null;
        }
        return NullableInt64ArrayConverter.Read(payload);
    }

    private void RequireType(int ordinal,
        uint oid)
    {
        _ = GetRawValue(ordinal);
        var column = Columns.Span[ordinal];
        if (column.DataTypeOid != oid || column.Format != FormatCode.Binary)
        {
            throw new InvalidCastException($"Column {ordinal} is not binary PostgreSQL type {oid}.");
        }
    }

    private void Enter()
    {
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_busy != 0) throw new InvalidOperationException("Concurrent reader movement or disposal is not supported.");
            _busy = 1;
        }
    }

    private void Exit()
    {
        lock (_stateGate)
        {
            if (_disposed) ReleaseCurrent();
            _busy = 0;
            _idle?.TrySetResult();
            _idle = null;
        }
    }

    internal Task InvalidateFromOwner()
    {
        lock (_stateGate)
        {
            _disposed = true;
            if (_busy == 0) { ReleaseCurrent(); return Task.CompletedTask; }
            return (_idle ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
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
            ReleaseCurrent();
        }
        finally { Exit(); }
        if (Execution is { } execution) await execution.EndReaderAsync(discard: true).ConfigureAwait(false);
        else await _batch.DiscardResultsAsync().ConfigureAwait(false);
    }
}
