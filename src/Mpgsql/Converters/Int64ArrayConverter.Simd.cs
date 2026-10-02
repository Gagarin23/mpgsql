using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Mpgsql.Converters;

public static partial class Int64ArrayConverter
{
    private static bool CanShuffle => BitConverter.IsLittleEndian && (Ssse3.IsSupported || AdvSimd.Arm64.IsSupported);

    private static void WriteRecords(ReadOnlySpan<long> source,
        Span<byte> destination)
    {
        int i = 0;
        int offset = 0;
        if (CanShuffle && source.Length >= 4)
        {
            // Four host-endian longs (32 bytes) become four [BE length=8][BE long] records (48 bytes).
            // 255 selects zero; the OR constants insert ready big-endian prefixes in the same pass.
            var firstMask = Vector128.Create((byte)255,
                255,
                255,
                255,
                7,
                6,
                5,
                4,
                3,
                2,
                1,
                0,
                255,
                255,
                255,
                255);
            var middleLeftMask = Vector128.Create((byte)15,
                14,
                13,
                12,
                11,
                10,
                9,
                8,
                255,
                255,
                255,
                255,
                255,
                255,
                255,
                255);
            var middleRightMask = Vector128.Create((byte)255,
                255,
                255,
                255,
                255,
                255,
                255,
                255,
                255,
                255,
                255,
                255,
                7,
                6,
                5,
                4);
            var lastMask = Vector128.Create((byte)3,
                2,
                1,
                0,
                255,
                255,
                255,
                255,
                15,
                14,
                13,
                12,
                11,
                10,
                9,
                8);
            var firstPrefix = Vector128.Create(0x08000000u,
                0u,
                0u,
                0x08000000u).AsByte();
            var middlePrefix = Vector128.Create(0u,
                0u,
                0x08000000u,
                0u).AsByte();
            var lastPrefix = Vector128.Create(0u,
                0x08000000u,
                0u,
                0u).AsByte();
            ref long input = ref MemoryMarshal.GetReference(source);
            ref byte output = ref MemoryMarshal.GetReference(destination);
            for (; i <= source.Length - 4; i += 4, offset += 4 * RecordSize)
            {
                // Public entry points have checked complete capacity. No load/store crosses this block.
                var left = Vector128.LoadUnsafe(ref input,
                    (nuint)i).AsByte();
                var right = Vector128.LoadUnsafe(ref input,
                    (nuint)(i + 2)).AsByte();
                (Vector128.Shuffle(left,
                    firstMask) | firstPrefix).StoreUnsafe(ref output,
                    (nuint)offset);
                (Vector128.Shuffle(left,
                        middleLeftMask) | Vector128.Shuffle(right,
                        middleRightMask) | middlePrefix)
                    .StoreUnsafe(ref output,
                        (nuint)(offset + 16));
                (Vector128.Shuffle(right,
                    lastMask) | lastPrefix).StoreUnsafe(ref output,
                    (nuint)(offset + 32));
            }
        }
        for (; i < source.Length; i++, offset += RecordSize)
        {
            BinaryPrimitives.WriteInt32BigEndian(destination[offset..],
                sizeof(long));
            BinaryPrimitives.WriteInt64BigEndian(destination[(offset + 4)..],
                source[i]);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReadRecords(ReadOnlySpan<byte> source,
        Span<long> destination)
    {
        int i = CanShuffle && destination.Length >= 4 ? ReadVectorRecords(source,
            destination) : 0;
        for (int offset = i * RecordSize; i < destination.Length; i++, offset += RecordSize)
            destination[i] = ReadRecord(source[offset..]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ReadRecord(ReadOnlySpan<byte> source)
    {
        if (BinaryPrimitives.ReadInt32BigEndian(source) != sizeof(long))
        {
            throw new InvalidDataException("A non-NULL bigint[] element must have length 8.");
        }
        return BinaryPrimitives.ReadInt64BigEndian(source[4..]);
    }

    private static int ReadVectorRecords(ReadOnlySpan<byte> source,
        Span<long> destination)
    {
        var firstLeftMask = Vector128.Create((byte)11,
            10,
            9,
            8,
            7,
            6,
            5,
            4,
            255,
            255,
            255,
            255,
            255,
            255,
            255,
            255);
        var firstRightMask = Vector128.Create((byte)255,
            255,
            255,
            255,
            255,
            255,
            255,
            255,
            7,
            6,
            5,
            4,
            3,
            2,
            1,
            0);
        var lastLeftMask = Vector128.Create((byte)255,
            255,
            255,
            255,
            15,
            14,
            13,
            12,
            255,
            255,
            255,
            255,
            255,
            255,
            255,
            255);
        var lastRightMask = Vector128.Create((byte)3,
            2,
            1,
            0,
            255,
            255,
            255,
            255,
            15,
            14,
            13,
            12,
            11,
            10,
            9,
            8);
        ref byte input = ref MemoryMarshal.GetReference(source);
        ref long output = ref MemoryMarshal.GetReference(destination);
        int i = 0;
        for (int offset = 0; i <= destination.Length - 4; i += 4, offset += 4 * RecordSize)
        {
            var first = Vector128.LoadUnsafe(ref input,
                (nuint)offset);
            var middle = Vector128.LoadUnsafe(ref input,
                (nuint)(offset + 16));
            var last = Vector128.LoadUnsafe(ref input,
                (nuint)(offset + 32));
            // Prefix validation stays mandatory, including the lengths embedded inside SIMD blocks.
            uint invalid = (first.AsUInt32().GetElement(0) ^ 0x08000000u) |
                           (first.AsUInt32().GetElement(3) ^ 0x08000000u) |
                           (middle.AsUInt32().GetElement(2) ^ 0x08000000u) |
                           (last.AsUInt32().GetElement(1) ^ 0x08000000u);
            if (invalid != 0)
            {
                throw new InvalidDataException("A non-NULL bigint[] element must have length 8.");
            }
            (Vector128.Shuffle(first,
                    firstLeftMask) | Vector128.Shuffle(middle,
                    firstRightMask))
                .AsInt64().StoreUnsafe(ref output,
                    (nuint)i);
            (Vector128.Shuffle(middle,
                    lastLeftMask) | Vector128.Shuffle(last,
                    lastRightMask))
                .AsInt64().StoreUnsafe(ref output,
                    (nuint)(i + 2));
        }
        return i;
    }
}