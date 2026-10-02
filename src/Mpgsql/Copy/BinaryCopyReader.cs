using System.Buffers;
using Mpgsql.Protocol;

namespace Mpgsql.Copy;

/// <summary>Incrementally reads unframed binary COPY data, including segmented fields.</summary>
/// <remarks>
/// CopyData boundaries need not match rows or scalars. Combine their payloads as a sequence.
/// Incomplete reads leave input and caller-owned field storage unchanged. Returned rows borrow
/// both buffers and field storage until the next read or the transport releases its memory.
/// </remarks>
public sealed class BinaryCopyReader
{
    private readonly int _maxFieldLength;
    private readonly int _maxHeaderExtensionLength;
    public int ColumnCount { get; }
    public bool HeaderRead { get; private set; }
    public bool IsCompleted { get; private set; }
    public ulong RowsRead { get; private set; }

    public BinaryCopyReader(int columnCount,
        int maxFieldLength = BackendMessageReader.DefaultMaxMessageLength,
        int maxHeaderExtensionLength = 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(columnCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(columnCount,
            short.MaxValue);
        ArgumentOutOfRangeException.ThrowIfNegative(maxFieldLength);
        ArgumentOutOfRangeException.ThrowIfNegative(maxHeaderExtensionLength);
        ColumnCount = columnCount;
        _maxFieldLength = maxFieldLength;
        _maxHeaderExtensionLength = maxHeaderExtensionLength;
    }

    public bool TryReadHeader(ref ReadOnlySequence<byte> input)
    {
        if (HeaderRead)
        {
            throw new InvalidOperationException("The COPY header has already been read.");
        }
        if (input.Length < BinaryCopyFormat.HeaderSize)
        {
            return false;
        }
        var reader = new SequenceReader<byte>(input);
        foreach (byte expected in BinaryCopyFormat.Signature)
            if (!reader.TryRead(out byte actual) || expected != actual)
            {
                throw new InvalidDataException("Invalid binary COPY signature.");
            }
        reader.TryReadBigEndian(out int flags);
        reader.TryReadBigEndian(out int extensionLength);
        if ((flags & unchecked((int)0xffff0000)) != 0)
        {
            throw new NotSupportedException("Unsupported critical binary COPY flags, including legacy OIDs.");
        }
        if (extensionLength < 0 || extensionLength > _maxHeaderExtensionLength)
        {
            throw new InvalidDataException("Invalid binary COPY header extension length.");
        }
        if (reader.Remaining < extensionLength)
        {
            return false;
        }
        reader.Advance(extensionLength); // Unknown low flags/extensions are backward-compatible.
        input = input.Slice(reader.Position);
        HeaderRead = true;
        return true;
    }

    public BinaryCopyReadStatus TryReadRow(ref ReadOnlySequence<byte> input,
        Memory<ReadOnlySequence<byte>?> fields,
        out BinaryCopyRow row)
    {
        row = default;
        if (!HeaderRead)
        {
            throw new InvalidOperationException("Read the COPY header first.");
        }
        if (IsCompleted)
        {
            if (!input.IsEmpty)
            {
                throw new InvalidDataException("Data follows the binary COPY trailer.");
            }
            return BinaryCopyReadStatus.Completed;
        }
        if (ColumnCount == 1 && input.IsSingleSegment)
        {
            return TryReadSingleColumn(ref input,
                fields,
                out row);
        }
        var reader = new SequenceReader<byte>(input);
        if (!reader.TryReadBigEndian(out short count))
        {
            return BinaryCopyReadStatus.NeedMoreData;
        }
        if (count == -1)
        {
            if (reader.Remaining != 0)
            {
                throw new InvalidDataException("Data follows the binary COPY trailer.");
            }
            input = input.Slice(reader.Position);
            IsCompleted = true;
            return BinaryCopyReadStatus.Completed;
        }
        if (count != ColumnCount)
        {
            throw new InvalidDataException("The binary COPY row field count differs from CopyResponse.");
        }
        if (fields.Length < count)
        {
            throw new ArgumentException("Field storage is too small for the COPY row.",
                nameof(fields));
        }

        var valuesStart = reader.Position;
        for (int i = 0; i < count; i++)
        {
            if (!reader.TryReadBigEndian(out int length))
            {
                return BinaryCopyReadStatus.NeedMoreData;
            }
            if (length < -1 || length > _maxFieldLength)
            {
                throw new InvalidDataException("Invalid binary COPY field length.");
            }
            if (length == -1)
            {
                continue;
            }
            if (reader.Remaining < length)
            {
                return BinaryCopyReadStatus.NeedMoreData;
            }
            reader.Advance(length);
        }

        // Validate the complete row before modifying reusable field storage.
        var values = new SequenceReader<byte>(input.Slice(valuesStart));
        for (int i = 0; i < count; i++)
        {
            values.TryReadBigEndian(out int length);
            fields.Span[i] = length == -1
                ? null
                : values.Sequence.Slice(values.Position,
                    length);
            if (length > 0)
            {
                values.Advance(length);
            }
        }
        input = input.Slice(reader.Position);
        row = new BinaryCopyRow(fields[..count]);
        RowsRead++;
        return BinaryCopyReadStatus.Row;
    }

    private BinaryCopyReadStatus TryReadSingleColumn(ref ReadOnlySequence<byte> input,
        Memory<ReadOnlySequence<byte>?> fields,
        out BinaryCopyRow row)
    {
        row = default;
        var bytes = input.FirstSpan;
        if (bytes.Length < 2)
        {
            return BinaryCopyReadStatus.NeedMoreData;
        }
        short count = System.Buffers.Binary.BinaryPrimitives.ReadInt16BigEndian(bytes);
        if (count == -1)
        {
            if (bytes.Length != 2)
            {
                throw new InvalidDataException("Data follows the binary COPY trailer.");
            }
            input = input.Slice(2);
            IsCompleted = true;
            return BinaryCopyReadStatus.Completed;
        }
        if (count != 1)
        {
            throw new InvalidDataException("The binary COPY row field count differs from CopyResponse.");
        }
        if (fields.IsEmpty)
        {
            throw new ArgumentException("Field storage is too small for the COPY row.",
                nameof(fields));
        }
        if (bytes.Length < 6)
        {
            return BinaryCopyReadStatus.NeedMoreData;
        }
        int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes[2..]);
        if (length < -1 || length > _maxFieldLength)
        {
            throw new InvalidDataException("Invalid binary COPY field length.");
        }
        long size = 6L + Math.Max(length,
            0);
        if (bytes.Length < size)
        {
            return BinaryCopyReadStatus.NeedMoreData;
        }
        // A complete one-field row needs no second validation/indexing pass.
        fields.Span[0] = length == -1
            ? null
            : input.Slice(6,
                length);
        input = input.Slice(size);
        row = new BinaryCopyRow(fields[..1]);
        RowsRead++;
        return BinaryCopyReadStatus.Row;
    }

    /// <summary>Checks that CopyDone did not end a truncated header, tuple or trailer.</summary>
    public void EndData()
    {
        if (!HeaderRead || !IsCompleted)
        {
            throw new InvalidDataException("The binary COPY stream ended before its trailer.");
        }
    }
}