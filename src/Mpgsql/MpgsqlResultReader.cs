using System.Buffers;
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
    private bool _disposed;
    public int QueryIndex { get; private set; } = -1;
    public ReadOnlyMemory<RowField> Columns { get; private set; }
    public string? CommandTag { get; private set; }

    internal MpgsqlResultReader(MpgsqlQueryBatch batch) => _batch = batch;
    internal async ValueTask InitializeAsync() => await MoveResultAsync().ConfigureAwait(false);

    public async ValueTask<bool> ReadAsync()
    {
        Enter();
        try { return await ReadCoreAsync().ConfigureAwait(false); }
        catch
        {
            _finished = true;
            _end = true;
            throw;
        }
        finally
        {
            Volatile.Write(ref _busy,
                0);
        }
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
        try
        {
            while (await ReadCoreAsync().ConfigureAwait(false)) { }
            return await MoveResultAsync().ConfigureAwait(false);
        }
        catch
        {
            _finished = true;
            _end = true;
            throw;
        }
        finally
        {
            Volatile.Write(ref _busy,
                0);
        }
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
        ObjectDisposedException.ThrowIf(_disposed,
            this);
        if (Interlocked.CompareExchange(ref _busy,
                1,
                0) != 0)
        {
            throw new InvalidOperationException("Concurrent reader movement or disposal is not supported.");
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
            return;
        }
        Enter();
        try
        {
            _disposed = true;
            ReleaseCurrent();
            await _batch.DiscardResultsAsync().ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _busy,
                0);
        }
    }
}