using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Mpgsql.Types;

namespace Mpgsql.Converters;

internal readonly partial struct NumericCodec : IBinaryCodec<PgNumeric>
{
    public static uint Oid => (uint)TypeOid.Numeric;
    public static int FixedSize => 0;
    public static bool MayOverlap => true;
    private static readonly UInt128 DecimalMax = ((UInt128)1 << 96) - 1;

    public static int Measure(PgNumeric value)
    {
        if (value.Digits.Length > ushort.MaxValue || value.Scale > 16383 || !ValidSign(value.Sign) ||
            (!value.IsFinite && !value.Digits.IsEmpty))
        {
            throw new ArgumentException("Invalid PostgreSQL numeric header.", nameof(value));
        }
        return 8 + 2 * value.Digits.Length;
    }

    public static void CheckOverlap(PgNumeric value, Span<byte> destination)
        => BinaryPayload.RequireSeparate(MemoryMarshal.AsBytes(value.Digits.Span), destination);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Write(PgNumeric value, Span<byte> destination)
    {
        var digits = value.Digits.Span;
        return digits.Length >= 8
            ? WriteVectorParts(value.Weight, value.Scale, value.Sign, digits, destination)
            : WriteParts(value.Weight, value.Scale, value.Sign, digits, destination);
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
        int offset = 8;
        foreach (ushort digit in digits)
        {
            BinaryPrimitives.WriteUInt16BigEndian(bytes[offset..], digit);
            offset += 2;
        }
        return offset;
    }

    private static bool ValidSign(PgNumericSign sign) => sign is PgNumericSign.Positive or PgNumericSign.Negative or
        PgNumericSign.NaN or PgNumericSign.PositiveInfinity or PgNumericSign.NegativeInfinity;

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
            (sign is not (PgNumericSign.Positive or PgNumericSign.Negative) && count != 0))
        {
            throw new InvalidDataException("Invalid numeric header or length.");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReadDigits(ReadOnlySpan<byte> records, Span<ushort> destination)
    {
        if (CanShuffleDigits && destination.Length >= 8)
        {
            int read = ReadDigitVectors(records, destination);
            records = records[(read * 2)..];
            destination = destination[read..];
        }
        foreach (ref ushort item in destination)
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
        while (!destination.IsEmpty)
        {
            int count = Math.Min(reader.UnreadSpan.Length / 2, destination.Length);
            if (count != 0)
            {
                ReadDigits(reader.UnreadSpan[..(count * 2)], destination[..count]);
                reader.Advance(count * 2);
                destination = destination[count..];
                continue;
            }
            if (!reader.TryReadBigEndian(out short digit) || unchecked((ushort)digit) > 9999)
            {
                throw new InvalidDataException("Invalid base-10000 numeric digit.");
            }
            destination[0] = unchecked((ushort)digit);
            destination = destination[1..];
        }
    }

    public static PgNumeric Read(ReadOnlySpan<byte> payload)
    {
        int count = ReadHeader(payload, out short weight, out ushort scale, out var sign);
        var digits = count == 0 ? Array.Empty<ushort>() : GC.AllocateUninitializedArray<ushort>(count);
        ReadDigits(payload[8..], digits);
        return new(weight, scale, sign, digits);
    }

    public static PgNumeric Read(ReadOnlySequence<byte> payload)
    {
        if (payload.IsSingleSegment)
        {
            return Read(payload.FirstSpan);
        }
        var reader = new SequenceReader<byte>(payload);
        int count = ReadHeader(ref reader, out short weight, out ushort scale, out var sign);
        var digits = count == 0 ? Array.Empty<ushort>() : GC.AllocateUninitializedArray<ushort>(count);
        ReadDigits(ref reader, digits);
        return new(weight, scale, sign, digits);
    }

    internal static PgNumeric Read(ReadOnlySpan<byte> payload, Memory<ushort> destination)
    {
        int count = ReadHeader(payload, out short weight, out ushort scale, out var sign);
        BinaryPayload.RequireCapacity(count, destination.Length);
        destination = destination[..count];
        BinaryPayload.RequireSeparate(payload, MemoryMarshal.AsBytes(destination.Span));
        ReadDigits(payload[8..], destination.Span);
        return new(weight, scale, sign, destination);
    }

    internal static PgNumeric Read(ReadOnlySequence<byte> payload, Memory<ushort> destination)
    {
        if (payload.IsSingleSegment)
        {
            return Read(payload.FirstSpan, destination);
        }
        var reader = new SequenceReader<byte>(payload);
        int count = ReadHeader(ref reader, out short weight, out ushort scale, out var sign);
        BinaryPayload.RequireCapacity(count, destination.Length);
        destination = destination[..count];
        BinaryPayload.RequireSeparate(payload, destination.Span);
        ReadDigits(ref reader, destination.Span);
        return new(weight, scale, sign, destination);
    }

    internal static int DecimalParts(decimal value, Span<ushort> digits,
        out short weight, out ushort scale,
        out PgNumericSign sign)
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);
        scale = (ushort)((bits[3] >> 16) & 0xff);
        sign = bits[3] < 0 ? PgNumericSign.Negative : PgNumericSign.Positive;
        UInt128 coefficient = (UInt128)(uint)bits[2] << 64 | (UInt128)(uint)bits[1] << 32 | (uint)bits[0];
        int padding = (4 - scale % 4) % 4;
        for (int i = 0; i < padding; i++) coefficient *= 10;
        int start = digits.Length;
        while (coefficient != 0)
        {
            digits[--start] = (ushort)(coefficient % 10000);
            coefficient /= 10000;
        }
        int count = digits.Length - start;
        weight = count == 0 ? (short)0 : (short)(count - (scale + padding) / 4 - 1);
        if (count == 0)
        {
            sign = PgNumericSign.Positive;
        }
        while (count > 0 && digits[start + count - 1] == 0) count--;
        digits.Slice(start, count).CopyTo(digits);
        return count;
    }

    internal static PgNumeric FromDecimal(decimal value)
    {
        Span<ushort> scratch = stackalloc ushort[8];
        int count = DecimalParts(value, scratch, out short weight, out ushort scale, out var sign);
        return new(weight, scale, sign, scratch[..count].ToArray());
    }

    internal static decimal ToDecimal(short weight, ushort scale,
        PgNumericSign sign, ReadOnlySpan<ushort> digits)
    {
        if (sign is not (PgNumericSign.Positive or PgNumericSign.Negative))
        {
            throw new OverflowException("Decimal cannot represent numeric NaN or infinity.");
        }
        int start = 0, end = digits.Length;
        while (start < end && digits[start] == 0) start++;
        while (end > start && digits[end - 1] == 0) end--;
        if (start == end)
        {
            return new decimal(0, 0, 0, sign == PgNumericSign.Negative, (byte)Math.Min(scale, (ushort)28));
        }
        UInt128 coefficient = 0;
        for (int i = start; i < end; i++)
        {
            ushort digit = digits[i];
            if (digit > 9999)
            {
                throw new InvalidDataException("Invalid numeric digit.");
            }
            coefficient = checked(coefficient * 10000 + digit);
        }
        int power = 4 * (weight - end + 1);
        while (power < 0 && coefficient % 10 == 0)
        {
            coefficient /= 10;
            power++;
        }
        if (power > 28 || power < -28)
        {
            throw new OverflowException("Numeric is outside Decimal's exact range.");
        }
        while (power > 0)
        {
            coefficient = checked(coefficient * 10);
            power--;
        }
        int resultScale = -power;
        if (coefficient > DecimalMax)
        {
            throw new OverflowException("Numeric is outside Decimal's exact range.");
        }
        // Preserve display scale where it fits; adding zeroes must never round the value.
        int desiredScale = Math.Min(scale, (ushort)28);
        while (resultScale < desiredScale && coefficient <= DecimalMax / 10)
        {
            coefficient *= 10;
            resultScale++;
        }
        return new decimal((int)(uint)coefficient, (int)(uint)(coefficient >> 32), (int)(uint)(coefficient >> 64),
            sign == PgNumericSign.Negative, (byte)resultScale);
    }

    internal static decimal ReadDecimal(ReadOnlySpan<byte> payload)
    {
        int count = ReadHeader(payload, out short weight, out ushort scale, out var sign);
        // Decimal can only need eight significant base-10000 digits. Strip redundant zero
        // groups before deciding; no buffer allocation is needed even for out-of-range numeric.
        int start = 0, end = count;
        while (start < end && BinaryPrimitives.ReadUInt16BigEndian(payload[(8 + start * 2)..]) == 0) start++;
        while (end > start && BinaryPrimitives.ReadUInt16BigEndian(payload[(8 + (end - 1) * 2)..]) == 0) end--;
        if (start == end)
        {
            return ToDecimal(weight, scale, sign, ReadOnlySpan<ushort>.Empty);
        }
        if (end - start > 8)
        {
            throw new OverflowException("Numeric is outside Decimal's exact range.");
        }
        Span<ushort> digits = stackalloc ushort[8];
        ReadDigits(payload.Slice(8 + start * 2, (end - start) * 2), digits[..(end - start)]);
        return ToDecimal(checked((short)(weight - start)), scale, sign, digits[..(end - start)]);
    }

    internal static decimal ReadDecimal(ReadOnlySequence<byte> payload)
    {
        if (payload.IsSingleSegment)
        {
            return ReadDecimal(payload.FirstSpan);
        }
        var reader = new SequenceReader<byte>(payload);
        int count = ReadHeader(ref reader, out short weight, out ushort scale, out var sign);
        Span<ushort> digits = stackalloc ushort[8];
        int start = 0, stored = 0, pendingZeroes = 0;
        for (int i = 0; i < count; i++)
        {
            if (!reader.TryReadBigEndian(out short field) || unchecked((ushort)field) > 9999)
            {
                throw new InvalidDataException("Invalid numeric digit.");
            }
            ushort digit = unchecked((ushort)field);
            if (stored == 0 && digit == 0)
            {
                start++;
                continue;
            }
            if (digit == 0)
            {
                pendingZeroes++;
                continue;
            }
            if (stored + pendingZeroes >= 8)
            {
                throw new OverflowException("Numeric is outside Decimal's exact range.");
            }
            digits.Slice(stored, pendingZeroes).Clear();
            stored += pendingZeroes;
            pendingZeroes = 0;
            digits[stored++] = digit;
        }
        return ToDecimal(stored == 0 ? weight : checked((short)(weight - start)), scale, sign, digits[..stored]);
    }
}
