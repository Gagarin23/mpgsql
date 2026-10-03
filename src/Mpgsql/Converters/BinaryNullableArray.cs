using System.Buffers;
using System.Buffers.Binary;

namespace Mpgsql.Converters;

internal static class BinaryNullableArray<T, TCodec>
    where T : struct
    where TCodec : struct, IBinaryCodec<T>
{
    internal static int Measure(ReadOnlySpan<T?> source, out bool hasNull)
    {
        hasNull = false;
        int elementBytes = 0;
        foreach (var item in source)
        {
            if (!item.HasValue)
            {
                hasNull = true;
                continue;
            }
            elementBytes = checked(elementBytes + (TCodec.FixedSize != 0 ? TCodec.FixedSize : TCodec.Measure(item.GetValueOrDefault())));
        }
        return ArrayPayload.Measure(source.Length, elementBytes);
    }

    internal static int Write(ReadOnlyMemory<T?> value, Span<byte> destination)
    {
        var source = value.Span;
        int size = Measure(source, out bool hasNull);
        BinaryPayload.RequireCapacity(size, destination.Length);
        destination = destination[..size];
        CheckOverlap(source, destination);
        WriteCore(source, destination, hasNull);
        return size;
    }

    internal static void Write(ReadOnlyMemory<T?> value, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var source = value.Span;
        int size = Measure(source, out bool hasNull);
        var bytes = destination.GetSpan(size)[..size];
        CheckOverlap(source, bytes);
        WriteCore(source, bytes, hasNull);
        destination.Advance(size);
    }

    private static void CheckOverlap(ReadOnlySpan<T?> source, Span<byte> destination)
    {
        BinaryPayload.RequireSeparate(BinaryPayload.StorageBytes(source), destination);
        if (TCodec.MayOverlap)
        {
            foreach (var item in source)
                if (item.HasValue)
                {
                    TCodec.CheckOverlap(item.GetValueOrDefault(), destination);
                }
        }
    }

    // Called only after the query encoder measured all parameters and checked packet capacity.
    internal static int WriteMeasured(ReadOnlyMemory<T?> value, Span<byte> destination)
    {
        CheckOverlap(value.Span, destination);
        return WriteCore(value.Span, destination, false);
    }

    private static int WriteCore(ReadOnlySpan<T?> source, Span<byte> bytes,
        bool hasNull)
    {
        ArrayPayload.WriteHeader(bytes, source.Length, hasNull, TCodec.Oid);
        int offset = source.IsEmpty ? ArrayPayload.EmptyHeaderSize : ArrayPayload.HeaderSize;
        foreach (var item in source)
        {
            int lengthOffset = offset;
            offset += 4;
            if (!item.HasValue)
            {
                hasNull = true;
                BinaryPrimitives.WriteInt32BigEndian(bytes[lengthOffset..], -1);
                continue;
            }
            int length = TCodec.Write(item.GetValueOrDefault(), bytes[offset..]);
            BinaryPrimitives.WriteInt32BigEndian(bytes[lengthOffset..], length);
            offset += length;
        }
        if (hasNull) BinaryPrimitives.WriteInt32BigEndian(bytes[4..], 1);
        return offset;
    }

    internal static ReadOnlyMemory<T?> Read(ReadOnlySpan<byte> payload)
    {
        int count = ArrayPayload.ReadHeader(payload, TCodec.Oid, TCodec.FixedSize, out int headerSize);
        if (count == 0)
        {
            return ReadOnlyMemory<T?>.Empty;
        }
        var result = GC.AllocateUninitializedArray<T?>(count);
        ReadRecords(payload[headerSize..], result);
        return result;
    }

    internal static int Read(ReadOnlySpan<byte> payload, Span<T?> destination)
    {
        int count = ArrayPayload.ReadHeader(payload, TCodec.Oid, TCodec.FixedSize, out int headerSize);
        BinaryPayload.RequireCapacity(count, destination.Length);
        destination = destination[..count];
        BinaryPayload.RequireSeparate(payload, BinaryPayload.StorageBytes(destination));
        ReadRecords(payload[headerSize..], destination);
        return count;
    }

    private static void ReadRecords(ReadOnlySpan<byte> records, Span<T?> destination)
    {
        foreach (ref var item in destination)
        {
            int length = ArrayPayload.ReadLength(records);
            item = length == -1 ? null : TCodec.Read(records.Slice(4, length));
            records = records[(length == -1 ? 4 : 4 + length)..];
        }
        if (!records.IsEmpty)
        {
            throw new InvalidDataException("Unexpected trailing PostgreSQL array bytes.");
        }
    }

    internal static ReadOnlyMemory<T?> Read(ReadOnlySequence<byte> payload)
    {
        if (payload.IsSingleSegment)
        {
            return Read(payload.FirstSpan);
        }
        var reader = new SequenceReader<byte>(payload);
        int count = ArrayPayload.ReadHeader(ref reader, TCodec.Oid, TCodec.FixedSize);
        if (count == 0)
        {
            return ReadOnlyMemory<T?>.Empty;
        }
        var result = GC.AllocateUninitializedArray<T?>(count);
        ReadRecords(ref reader, result);
        return result;
    }

    internal static int Read(ReadOnlySequence<byte> payload, Span<T?> destination)
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

    private static void ReadRecords(ref SequenceReader<byte> reader, Span<T?> destination)
    {
        foreach (ref var item in destination)
        {
            int length = ArrayPayload.ReadLength(ref reader);
            if (length == -1)
            {
                item = null;
                continue;
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
}
