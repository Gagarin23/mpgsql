using System.Buffers;
using System.Buffers.Binary;

namespace Mpgsql.Converters;

internal static partial class BinaryArray<T, TCodec> where TCodec : struct, IBinaryCodec<T>
{
    internal static int Measure(ReadOnlySpan<T> source)
    {
        var elementBytes = 0;
        if (TCodec.FixedSize != 0)
        {
            elementBytes = checked(source.Length * TCodec.FixedSize);
        }
        else
        {
            foreach (var item in source)
            {
                elementBytes = checked(elementBytes + TCodec.Measure(item));
            }
        }
        return ArrayPayload.Measure(source.Length, elementBytes);
    }

    internal static int Write(ReadOnlyMemory<T> value, Span<byte> destination)
    {
        var source = value.Span;
        var size = Measure(source);
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
        var size = Measure(source);
        var bytes = destination
            .GetSpan(size)[..size];
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
            {
                TCodec.CheckOverlap(item, destination);
            }
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
        var offset = source.IsEmpty ? ArrayPayload.EmptyHeaderSize : ArrayPayload.HeaderSize;
        if (CanUseNumericSimd && source.Length >= NumericVectorCount)
        {
            var written = WriteNumericVectors(source, bytes[offset..]);
            offset += written * (4 + TCodec.FixedSize);
            source = source[written..];
        }
        foreach (var item in source)
        {
            var lengthOffset = offset;
            offset += 4;
            var length = TCodec.Write(item, bytes[offset..]);
            BinaryPrimitives.WriteInt32BigEndian(bytes[lengthOffset..], length);
            offset += length;
        }
        return offset;
    }

    internal static ReadOnlyMemory<T> Read(ReadOnlySpan<byte> payload)
    {
        var count = ArrayPayload.ReadHeader(payload, TCodec.Oid, TCodec.FixedSize, out var headerSize);
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
        var count = ArrayPayload.ReadHeader(payload, TCodec.Oid, TCodec.FixedSize, out var headerSize);
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
            var length = ArrayPayload.ReadLength(records);
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
        var recordSize = 4 + TCodec.FixedSize;
        var read = CanUseNumericSimd && destination.Length >= NumericVectorCount
            ? ReadNumericVectors(records, destination)
            : 0;
        var offset = read * recordSize;
        destination = destination[read..];
        foreach (ref var item in destination)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(records[offset..]);
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
        var count = ArrayPayload.ReadHeader(ref reader, TCodec.Oid, TCodec.FixedSize);
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
        var count = ArrayPayload.ReadHeader(ref reader, TCodec.Oid, TCodec.FixedSize);
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
            var length = ArrayPayload.ReadLength(ref reader);
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
        var recordSize = 4 + TCodec.FixedSize;
        var written = 0;
        Span<byte> scratch = stackalloc byte[20];
        while (written < destination.Length)
        {
            // Whole records in a segment use the contiguous loop. Only a split small
            // scalar uses scratch, so there is no sequence slice/scan for every element.
            var count = Math.Min(reader.UnreadSpan.Length / recordSize, destination.Length - written);
            if (count != 0)
            {
                var byteCount = count * recordSize;
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