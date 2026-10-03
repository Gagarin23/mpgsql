using System.Buffers;
using System.Buffers.Binary;

namespace Mpgsql.Converters;

internal static class BinaryArray<T, TCodec> where TCodec : struct, IBinaryCodec<T>
{
    internal static int Measure(ReadOnlySpan<T> source)
    {
        int elementBytes = 0;
        if (TCodec.FixedSize != 0)
        {
            elementBytes = checked(source.Length * TCodec.FixedSize);
        }
        else
        {
            foreach (var item in source)
                elementBytes = checked(elementBytes + TCodec.Measure(item));
        }
        return ArrayPayload.Measure(source.Length, elementBytes);
    }

    internal static int Write(ReadOnlyMemory<T> value, Span<byte> destination)
    {
        var source = value.Span;
        int size = Measure(source);
        BinaryPayload.RequireCapacity(size, destination.Length);
        destination = destination[..size];
        CheckOverlap(source, destination);
        WriteCore(source, destination);
        return size;
    }

    internal static void Write(ReadOnlyMemory<T> value, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var source = value.Span;
        int size = Measure(source);
        var bytes = destination.GetSpan(size)[..size];
        CheckOverlap(source, bytes);
        WriteCore(source, bytes);
        destination.Advance(size);
    }

    private static void CheckOverlap(ReadOnlySpan<T> source, Span<byte> destination)
    {
        BinaryPayload.RequireSeparate(BinaryPayload.StorageBytes(source), destination);
        if (TCodec.MayOverlap)
        {
            foreach (var item in source)
                TCodec.CheckOverlap(item, destination);
        }
    }

    // The query encoder already measured the complete packet and checked its capacity.
    internal static int WriteMeasured(ReadOnlyMemory<T> value, Span<byte> destination)
    {
        CheckOverlap(value.Span, destination);
        return WriteCore(value.Span, destination);
    }

    private static int WriteCore(ReadOnlySpan<T> source, Span<byte> bytes)
    {
        ArrayPayload.WriteHeader(bytes, source.Length, false, TCodec.Oid);
        int offset = source.IsEmpty ? ArrayPayload.EmptyHeaderSize : ArrayPayload.HeaderSize;
        foreach (var item in source)
        {
            int lengthOffset = offset;
            offset += 4;
            int length = TCodec.Write(item, bytes[offset..]);
            BinaryPrimitives.WriteInt32BigEndian(bytes[lengthOffset..], length);
            offset += length;
        }
        return offset;
    }

    internal static ReadOnlyMemory<T> Read(ReadOnlySpan<byte> payload)
    {
        int count = ArrayPayload.ReadHeader(payload, TCodec.Oid, TCodec.FixedSize, out int headerSize);
        if (count == 0)
        {
            return ReadOnlyMemory<T>.Empty;
        }
        var result = GC.AllocateUninitializedArray<T>(count);
        ReadRecords(payload[headerSize..], result);
        return result;
    }

    internal static int Read(ReadOnlySpan<byte> payload, Span<T> destination)
    {
        int count = ArrayPayload.ReadHeader(payload, TCodec.Oid, TCodec.FixedSize, out int headerSize);
        BinaryPayload.RequireCapacity(count, destination.Length);
        destination = destination[..count];
        BinaryPayload.RequireSeparate(payload, BinaryPayload.StorageBytes(destination));
        ReadRecords(payload[headerSize..], destination);
        return count;
    }

    private static void ReadRecords(ReadOnlySpan<byte> records, Span<T> destination)
    {
        if (TCodec.FixedSize != 0 && records.Length == (4L + TCodec.FixedSize) * destination.Length)
        {
            ReadFixedRecords(records, destination);
            return;
        }
        foreach (ref var item in destination)
        {
            int length = ArrayPayload.ReadLength(records);
            if (length == -1)
            {
                throw new NotSupportedException("Use the nullable array converter for NULL elements.");
            }
            item = TCodec.Read(records.Slice(4, length));
            records = records[(4 + length)..];
        }
        if (!records.IsEmpty)
        {
            throw new InvalidDataException("Unexpected trailing PostgreSQL array bytes.");
        }
    }

    private static void ReadFixedRecords(ReadOnlySpan<byte> records, Span<T> destination)
    {
        int recordSize = 4 + TCodec.FixedSize;
        int offset = 0;
        foreach (ref var item in destination)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(records[offset..]);
            if (length != TCodec.FixedSize)
            {
                ThrowFixedLength(length);
            }
            item = TCodec.Read(records.Slice(offset + 4, TCodec.FixedSize));
            offset += recordSize;
        }
    }

    private static void ThrowFixedLength(int length)
    {
        if (length == -1)
        {
            throw new NotSupportedException("Use the nullable array converter for NULL elements.");
        }
        throw new InvalidDataException("Invalid fixed-size PostgreSQL array element length.");
    }

    internal static ReadOnlyMemory<T> Read(ReadOnlySequence<byte> payload)
    {
        if (payload.IsSingleSegment)
        {
            return Read(payload.FirstSpan);
        }
        var reader = new SequenceReader<byte>(payload);
        int count = ArrayPayload.ReadHeader(ref reader, TCodec.Oid, TCodec.FixedSize);
        if (count == 0)
        {
            return ReadOnlyMemory<T>.Empty;
        }
        var result = GC.AllocateUninitializedArray<T>(count);
        ReadRecords(ref reader, result);
        return result;
    }

    internal static int Read(ReadOnlySequence<byte> payload, Span<T> destination)
    {
        if (payload.IsSingleSegment)
        {
            return Read(payload.FirstSpan, destination);
        }
        var reader = new SequenceReader<byte>(payload);
        int count = ArrayPayload.ReadHeader(ref reader, TCodec.Oid, TCodec.FixedSize);
        BinaryPayload.RequireCapacity(count, destination.Length);
        destination = destination[..count];
        BinaryPayload.RequireSeparate(payload, destination);
        ReadRecords(ref reader, destination);
        return count;
    }

    private static void ReadRecords(ref SequenceReader<byte> reader, Span<T> destination)
    {
        if (TCodec.FixedSize != 0 && reader.Remaining == (4L + TCodec.FixedSize) * destination.Length)
        {
            ReadFixedRecords(ref reader, destination);
            return;
        }
        foreach (ref var item in destination)
        {
            int length = ArrayPayload.ReadLength(ref reader);
            if (length == -1)
            {
                throw new NotSupportedException("Use the nullable array converter for NULL elements.");
            }
            item = reader.UnreadSpan.Length >= length
                ? TCodec.Read(reader.UnreadSpan[..length])
                : TCodec.Read(reader.Sequence.Slice(reader.Position, length));
            reader.Advance(length);
        }
        if (reader.Remaining != 0)
        {
            throw new InvalidDataException("Unexpected trailing PostgreSQL array bytes.");
        }
    }

    private static void ReadFixedRecords(ref SequenceReader<byte> reader, Span<T> destination)
    {
        int recordSize = 4 + TCodec.FixedSize;
        int written = 0;
        Span<byte> scratch = stackalloc byte[20];
        while (written < destination.Length)
        {
            // Whole records in a segment use the contiguous loop. Only a split small
            // scalar uses scratch, so there is no sequence slice/scan for every element.
            int count = Math.Min(reader.UnreadSpan.Length / recordSize, destination.Length - written);
            if (count != 0)
            {
                int byteCount = count * recordSize;
                ReadFixedRecords(reader.UnreadSpan[..byteCount], destination.Slice(written, count));
                reader.Advance(byteCount);
                written += count;
                continue;
            }
            if (!reader.TryReadBigEndian(out int length))
            {
                throw new InvalidDataException("Truncated PostgreSQL array element.");
            }
            if (length != TCodec.FixedSize)
            {
                ThrowFixedLength(length);
            }
            if (reader.UnreadSpan.Length >= TCodec.FixedSize)
            {
                destination[written++] = TCodec.Read(reader.UnreadSpan[..TCodec.FixedSize]);
            }
            else
            {
                if (!reader.TryCopyTo(scratch[..TCodec.FixedSize]))
                {
                    throw new InvalidDataException("Truncated PostgreSQL array value.");
                }
                destination[written++] = TCodec.Read(scratch[..TCodec.FixedSize]);
            }
            reader.Advance(TCodec.FixedSize);
        }
    }
}
