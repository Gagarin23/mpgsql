using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Mpgsql.Converters;
using Mpgsql.Types;

namespace Mpgsql.Benchmarks;

// Frozen pre-SIMD numeric payload codec, including its original call boundaries.
// Unused Decimal conversion methods are omitted; measured methods are unchanged.
internal readonly struct ScalarNumericCodecBaseline : IBinaryCodec<PgNumeric>
{
    public static uint Oid => (uint)TypeOid.Numeric;
    public static int FixedSize => 0;
    public static bool MayOverlap => true;
    private static readonly UInt128 DecimalMax = ((UInt128)1 << 96) - 1;

    public static int Measure(PgNumeric value)
    {
        if (value.Digits.Length > ushort.MaxValue || value.Scale > 16383 || !ValidSign(value.Sign) ||
            !value.IsFinite && !value.Digits.IsEmpty)
        {
            throw new ArgumentException("Invalid PostgreSQL numeric header.", nameof(value));
        }
        return 8 + 2 * value.Digits.Length;
    }

    public static void CheckOverlap(PgNumeric value, Span<byte> destination)
    {
        BinaryPayload.RequireSeparate(MemoryMarshal.AsBytes(value.Digits.Span), destination);
    }

    public static int Write(PgNumeric value, Span<byte> destination)
    {
        return WriteParts(value.Weight, value.Scale, value.Sign, value.Digits.Span, destination);
    }

    internal static int WriteParts(short weight, ushort scale,
        PgNumericSign sign, ReadOnlySpan<ushort> digits,
        Span<byte> bytes)
    {
        // UInt16 ndigits, Int16 weight, UInt16 sign, UInt16 dscale, UInt16 base-10000 digits.
        BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)digits.Length);
        BinaryPrimitives.WriteInt16BigEndian(bytes[2..], weight);
        BinaryPrimitives.WriteUInt16BigEndian(bytes[4..], (ushort)sign);
        BinaryPrimitives.WriteUInt16BigEndian(bytes[6..], scale);
        var offset = 8;
        foreach (var digit in digits)
        {
            BinaryPrimitives.WriteUInt16BigEndian(bytes[offset..], digit);
            offset += 2;
        }
        return offset;
    }

    private static bool ValidSign(PgNumericSign sign)
    {
        return sign is PgNumericSign.Positive or PgNumericSign.Negative or
            PgNumericSign.NaN or PgNumericSign.PositiveInfinity or PgNumericSign.NegativeInfinity;
    }

    internal static int ReadHeader(ReadOnlySpan<byte> payload, out short weight,
        out ushort scale, out PgNumericSign sign)
    {
        if (payload.Length < 8)
        {
            throw new InvalidDataException("Truncated numeric header.");
        }
        int count = BinaryPrimitives.ReadUInt16BigEndian(payload);
        weight = BinaryPrimitives.ReadInt16BigEndian(payload[2..]);
        sign = (PgNumericSign)BinaryPrimitives.ReadUInt16BigEndian(payload[4..]);
        scale = BinaryPrimitives.ReadUInt16BigEndian(payload[6..]);
        ValidateHeader(count, scale, sign, payload.Length - 8);
        return count;
    }

    private static int ReadHeader(ref SequenceReader<byte> reader, out short weight,
        out ushort scale, out PgNumericSign sign)
    {
        if (!reader.TryReadBigEndian(out short countField) || !reader.TryReadBigEndian(out weight) ||
            !reader.TryReadBigEndian(out short signField) || !reader.TryReadBigEndian(out short scaleField))
        {
            throw new InvalidDataException("Truncated numeric header.");
        }
        int count = unchecked((ushort)countField);
        sign = (PgNumericSign)unchecked((ushort)signField);
        scale = unchecked((ushort)scaleField);
        ValidateHeader(count, scale, sign, reader.Remaining);
        return count;
    }

    private static void ValidateHeader(int count, ushort scale,
        PgNumericSign sign, long remaining)
    {
        if (remaining != 2L * count || scale > 16383 || !ValidSign(sign) ||
            sign is not (PgNumericSign.Positive or PgNumericSign.Negative) && count != 0)
        {
            throw new InvalidDataException("Invalid numeric header or length.");
        }
    }

    private static void ReadDigits(ReadOnlySpan<byte> records, Span<ushort> destination)
    {
        foreach (ref var item in destination)
        {
            item = BinaryPrimitives.ReadUInt16BigEndian(records);
            if (item > 9999)
            {
                throw new InvalidDataException("Invalid base-10000 numeric digit.");
            }
            records = records[2..];
        }
    }

    private static void ReadDigits(ref SequenceReader<byte> reader, Span<ushort> destination)
    {
        foreach (ref var item in destination)
        {
            if (!reader.TryReadBigEndian(out short digit) || unchecked((ushort)digit) > 9999)
            {
                throw new InvalidDataException("Invalid base-10000 numeric digit.");
            }
            item = unchecked((ushort)digit);
        }
    }

    public static PgNumeric Read(ReadOnlySpan<byte> payload)
    {
        var count = ReadHeader(payload, out var weight, out var scale, out var sign);
        var digits = count == 0 ? Array.Empty<ushort>() : GC.AllocateUninitializedArray<ushort>(count);
        ReadDigits(payload[8..], digits);
        return new PgNumeric(weight, scale, sign, digits);
    }

    public static PgNumeric Read(ReadOnlySequence<byte> payload)
    {
        if (payload.IsSingleSegment)
        {
            return Read(payload.FirstSpan);
        }
        var reader = new SequenceReader<byte>(payload);
        var count = ReadHeader(ref reader, out var weight, out var scale, out var sign);
        var digits = count == 0 ? Array.Empty<ushort>() : GC.AllocateUninitializedArray<ushort>(count);
        ReadDigits(ref reader, digits);
        return new PgNumeric(weight, scale, sign, digits);
    }

    internal static PgNumeric Read(ReadOnlySpan<byte> payload, Memory<ushort> destination)
    {
        var count = ReadHeader(payload, out var weight, out var scale, out var sign);
        BinaryPayload.RequireCapacity(count, destination.Length);
        destination = destination[..count];
        BinaryPayload.RequireSeparate(payload, MemoryMarshal.AsBytes(destination.Span));
        ReadDigits(payload[8..], destination.Span);
        return new PgNumeric(weight, scale, sign, destination);
    }

    internal static PgNumeric Read(ReadOnlySequence<byte> payload, Memory<ushort> destination)
    {
        if (payload.IsSingleSegment)
        {
            return Read(payload.FirstSpan, destination);
        }
        var reader = new SequenceReader<byte>(payload);
        var count = ReadHeader(ref reader, out var weight, out var scale, out var sign);
        BinaryPayload.RequireCapacity(count, destination.Length);
        destination = destination[..count];
        BinaryPayload.RequireSeparate(payload, destination.Span);
        ReadDigits(ref reader, destination.Span);
        return new PgNumeric(weight, scale, sign, destination);
    }

}