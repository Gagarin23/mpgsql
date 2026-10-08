using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Mpgsql.Converters;

namespace Mpgsql.Copy;

/// <summary>Writes the binary COPY stream: header, tuples, fields and trailer.</summary>
/// <remarks>
///     The destination owns buffering/framing. Use CopyDataWriter for frontend CopyData frames.
///     Complete writes the Int16 -1 trailer; the connection must then send CopyDone and
///     consume CommandComplete/ReadyForQuery. This writer does not own a connection.
/// </remarks>
public sealed class BinaryCopyWriter
{
    private readonly IBufferWriter<byte> _destination;
    private int _column = -1;

    public BinaryCopyWriter(
        IBufferWriter<byte> destination,
        int columnCount
    )
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegative(columnCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan
        (
            columnCount,
            short.MaxValue
        );
        _destination = destination;
        ColumnCount = columnCount;
        BinaryCopyFormat.WriteHeader(destination);
    }
    public int ColumnCount { get; }
    public ulong RowsWritten { get; private set; }
    public bool IsCompleted { get; private set; }

    public void StartRow()
    {
        RequireRowBoundary();
        BinaryCopyFormat.WriteInt16
        (
            _destination,
            (short)ColumnCount
        );
        _column = 0;
        if (ColumnCount == 0)
        {
            RowsWritten++;
        }
    }

    public void WriteNull()
    {
        RequireColumn();
        BinaryPrimitives.WriteInt32BigEndian
        (
            _destination.GetSpan(4),
            -1
        );
        _destination.Advance(4);
        FinishColumn();
    }

    public void WriteInt64(long value)
    {
        RequireColumn();
        var bytes = _destination.GetSpan(12);
        BinaryPrimitives.WriteInt32BigEndian
        (
            bytes,
            8
        );
        BinaryPrimitives.WriteInt64BigEndian
        (
            bytes[4..],
            value
        );
        _destination.Advance(12);
        FinishColumn();
    }

    /// <summary>Writes a bigint value or the outer COPY field's SQL NULL length.</summary>
    public void WriteInt64(long? value)
    {
        if (value.HasValue)
        {
            WriteInt64(value.GetValueOrDefault());
        }
        else
        {
            WriteNull();
        }
    }

    /// <summary>Writes already encoded PostgreSQL field bytes; empty is distinct from NULL.</summary>
    public void WriteRaw(ReadOnlySpan<byte> value)
    {
        RequireColumn();
        var size = checked(4 + value.Length);
        var bytes = _destination
            .GetSpan(size)[..size];
        value.CopyTo(bytes[4..]); // Copy before the prefix also permits overlapping input.
        BinaryPrimitives.WriteInt32BigEndian
        (
            bytes,
            value.Length
        );
        _destination.Advance(size);
        FinishColumn();
    }

    /// <summary>Encodes a non-NULL bigint[] directly into the field's final output buffer.</summary>
    public void WriteLongArray(ReadOnlyMemory<long> value)
    {
        RequireColumn();
        var payloadSize = Int64ArrayConverter.GetByteCount(value);
        var size = checked(4 + payloadSize);
        var bytes = _destination
            .GetSpan(size)[..size];
        Int64ArrayConverter.Write
        (
            value,
            bytes[4..]
        );
        BinaryPrimitives.WriteInt32BigEndian
        (
            bytes,
            payloadSize
        );
        _destination.Advance(size);
        FinishColumn();
    }

    /// <summary>Encodes a non-NULL bigint[] containing nullable elements directly into the output.</summary>
    public void WriteNullableLongArray(ReadOnlyMemory<long?> value)
    {
        RequireColumn();
        NullableInt64ArrayConverter.WriteCopyField
        (
            value,
            _destination
        );
        FinishColumn();
    }

    /// <summary>Appends a batch of one-column bigint rows with a single output reservation.</summary>
    /// <remarks>For a bounded streaming destination, pass suitably sized batches.</remarks>
    public void WriteInt64Rows(ReadOnlySpan<long> values)
    {
        RequireRowBoundary();
        if (ColumnCount != 1)
        {
            throw new InvalidOperationException("A bigint row batch requires exactly one column.");
        }
        if (values.IsEmpty)
        {
            return;
        }
        var size = checked(14 * values.Length);
        var bytes = _destination
            .GetSpan(size)[..size];
        if (MemoryMarshal
            .AsBytes(values)
            .Overlaps(bytes))
        {
            throw new ArgumentException
            (
                "Input values must not overlap the COPY output.",
                nameof(values)
            );
        }
        for (int i = 0,
             offset = 0;
             i < values.Length;
             i++, offset += 14)
        {
            BinaryPrimitives.WriteInt16BigEndian
            (
                bytes[offset..],
                1
            );
            BinaryPrimitives.WriteInt32BigEndian
            (
                bytes[(offset + 2)..],
                8
            );
            BinaryPrimitives.WriteInt64BigEndian
            (
                bytes[(offset + 6)..],
                values[i]
            );
        }
        _destination.Advance(size);
        _column = ColumnCount;
        RowsWritten += (uint)values.Length;
    }

    public ulong Complete()
    {
        RequireRowBoundary();
        BinaryCopyFormat.WriteInt16
        (
            _destination,
            -1
        );
        IsCompleted = true;
        return RowsWritten;
    }

    private void RequireRowBoundary()
    {
        if (IsCompleted)
        {
            throw new InvalidOperationException("The binary COPY writer is completed.");
        }
        if (_column != -1 && _column != ColumnCount)
        {
            throw new InvalidOperationException("The current COPY row is incomplete.");
        }
    }

    private void RequireColumn()
    {
        if (IsCompleted || _column < 0 || _column >= ColumnCount)
        {
            throw new InvalidOperationException("Start a row before writing each of its columns.");
        }
    }

    private void FinishColumn()
    {
        if (++_column == ColumnCount)
        {
            RowsWritten++;
        }
    }
}