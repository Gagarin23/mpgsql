using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Mpgsql.Converters;

internal readonly partial struct NumericCodec
{
    private static bool CanShuffleDigits => BitConverter.IsLittleEndian &&
        (Ssse3.IsSupported || AdvSimd.Arm64.IsSupported);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int WriteVectorParts(short weight, ushort scale, Mpgsql.Types.PgNumericSign sign,
        ReadOnlySpan<ushort> digits, Span<byte> bytes)
    {
        // Keep the larger span operation outside the short numeric/Decimal loop.
        BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)digits.Length);
        BinaryPrimitives.WriteInt16BigEndian(bytes[2..], weight);
        BinaryPrimitives.WriteUInt16BigEndian(bytes[4..], (ushort)sign);
        BinaryPrimitives.WriteUInt16BigEndian(bytes[6..], scale);
        var output = MemoryMarshal.Cast<byte, ushort>(bytes.Slice(8, digits.Length * 2));
        if (BitConverter.IsLittleEndian)
        {
            BinaryPrimitives.ReverseEndianness(digits, output);
        }
        else
        {
            digits.CopyTo(output);
        }
        return 8 + digits.Length * 2;
    }

    private static int ReadDigitVectors(ReadOnlySpan<byte> source, Span<ushort> destination)
    {
        var reverse = Vector128.Create((byte)1, 0, 3, 2, 5, 4, 7, 6, 9, 8, 11, 10, 13, 12, 15, 14);
        var maximum = Vector128.Create((ushort)9999);
        ref byte input = ref MemoryMarshal.GetReference(source);
        ref ushort output = ref MemoryMarshal.GetReference(destination);
        int i = 0;
        for (; i <= destination.Length - 8; i += 8)
        {
            var digits = Vector128.Shuffle(Vector128.LoadUnsafe(ref input, (nuint)(i * 2)), reverse).AsUInt16();
            // Validate base-10000 digits in the decoding pass, before storing this block.
            if (Vector128.GreaterThanAny(digits, maximum))
            {
                throw new InvalidDataException("Invalid base-10000 numeric digit.");
            }
            digits.StoreUnsafe(ref output, (nuint)i);
        }
        return i;
    }
}
