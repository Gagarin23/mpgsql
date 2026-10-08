using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Mpgsql.Converters;

internal static partial class BinaryArray<T, TCodec>
{
    // The JIT folds this closed-codec whitelist. Only these CLR primitives have
    // exactly the wire value's bit layout; nullable and PostgreSQL structs do not.
    internal static bool CanUseNumericSimd => BitConverter.IsLittleEndian &&
                                              (Ssse3.IsSupported || AdvSimd.Arm64.IsSupported) &&
                                              (typeof(TCodec) == typeof(Int16Codec) || typeof(TCodec) == typeof(Int32Codec) ||
                                               typeof(TCodec) == typeof(Float32Codec) || typeof(TCodec) == typeof(Float64Codec) ||
                                               typeof(TCodec) == typeof(OidCodec) || typeof(TCodec) == typeof(MoneyCodec) ||
                                               typeof(TCodec) == typeof(Int64Codec));

    private static int NumericVectorCount => TCodec.FixedSize == 2 ? 8 : 4;

    // Int64ArrayConverter shares these kernels while retaining its existing header,
    // NULL, capacity, overlap, and scalar-tail contracts. Callers check CanUseNumericSimd
    // and complete block capacity before entering either vector method.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int WriteNumericVectors(ReadOnlySpan<T> source, Span<byte> destination)
    {
        ref var input = ref Unsafe.As<T, byte>(ref MemoryMarshal.GetReference(source));
        ref var output = ref MemoryMarshal.GetReference(destination);
        return TCodec.FixedSize switch
        {
            2 => WriteInt16Vectors(ref input, ref output, source.Length),
            4 => WriteInt32Vectors(ref input, ref output, source.Length),
            _ => WriteInt64Vectors(ref input, ref output, source.Length)
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int ReadNumericVectors(ReadOnlySpan<byte> source, Span<T> destination)
    {
        ref var input = ref MemoryMarshal.GetReference(source);
        ref var output = ref Unsafe.As<T, byte>(ref MemoryMarshal.GetReference(destination));
        return TCodec.FixedSize switch
        {
            2 => ReadInt16Vectors(ref input, ref output, destination.Length),
            4 => ReadInt32Vectors(ref input, ref output, destination.Length),
            _ => ReadInt64Vectors(ref input, ref output, destination.Length)
        };
    }

    // Each block fuses endian reversal with [Int32 BE length][BE value] framing.
    // Capacity and disjoint storage have been checked before any vector load/store.
    // 255 shuffle indices select zero bytes, then OR inserts the length prefixes.
    private static int WriteInt16Vectors(ref byte input, ref byte output,
        int count)
    {
        var firstMask = Vector128.Create(255, 255, 255, 255, 1, 0, 255, 255, 255, 255, 3, 2, 255, 255, 255, 255);
        var middleMask = Vector128.Create(5, 4, 255, 255, 255, 255, 7, 6, 255, 255, 255, 255, 9, 8, 255, 255);
        var lastMask = Vector128.Create(255, 255, 11, 10, 255, 255, 255, 255, 13, 12, 255, 255, 255, 255, 15, 14);
        var firstPrefix = Vector128.Create((byte)0, 0, 0, 2, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0, 0, 2);
        var middlePrefix = Vector128.Create((byte)0, 0, 0, 0, 0, 2, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0);
        var lastPrefix = Vector128.Create((byte)0, 2, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0, 0, 2, 0, 0);
        var i = 0;
        for (var offset = 0; i <= count - 8; i += 8, offset += 48)
        {
            var values = Vector128.LoadUnsafe(ref input, (nuint)(i * 2));
            (Vector128.Shuffle(values, firstMask) | firstPrefix).StoreUnsafe(ref output, (nuint)offset);
            (Vector128.Shuffle(values, middleMask) | middlePrefix).StoreUnsafe(ref output, (nuint)(offset + 16));
            (Vector128.Shuffle(values, lastMask) | lastPrefix).StoreUnsafe(ref output, (nuint)(offset + 32));
        }
        return i;
    }

    private static int WriteInt32Vectors(ref byte input, ref byte output,
        int count)
    {
        var firstMask = Vector128.Create(255, 255, 255, 255, 3, 2, 1, 0, 255, 255, 255, 255, 7, 6, 5, 4);
        var lastMask = Vector128.Create(255, 255, 255, 255, 11, 10, 9, 8, 255, 255, 255, 255, 15, 14, 13, 12);
        var prefix = Vector128.Create(0x04000000u, 0u, 0x04000000u, 0u).AsByte();
        var i = 0;
        for (var offset = 0; i <= count - 4; i += 4, offset += 32)
        {
            var values = Vector128.LoadUnsafe(ref input, (nuint)(i * 4));
            (Vector128.Shuffle(values, firstMask) | prefix).StoreUnsafe(ref output, (nuint)offset);
            (Vector128.Shuffle(values, lastMask) | prefix).StoreUnsafe(ref output, (nuint)(offset + 16));
        }
        return i;
    }

    private static int WriteInt64Vectors(ref byte input, ref byte output,
        int count)
    {
        var firstMask = Vector128.Create(255, 255, 255, 255, 7, 6, 5, 4, 3, 2, 1, 0, 255, 255, 255, 255);
        var middleLeftMask = Vector128.Create(15, 14, 13, 12, 11, 10, 9, 8, 255, 255, 255, 255, 255, 255, 255, 255);
        var middleRightMask = Vector128.Create(255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 7, 6, 5, 4);
        var lastMask = Vector128.Create(3, 2, 1, 0, 255, 255, 255, 255, 15, 14, 13, 12, 11, 10, 9, 8);
        var firstPrefix = Vector128.Create(0x08000000u, 0u, 0u, 0x08000000u).AsByte();
        var middlePrefix = Vector128.Create(0u, 0u, 0x08000000u, 0u).AsByte();
        var lastPrefix = Vector128.Create(0u, 0x08000000u, 0u, 0u).AsByte();
        ref var values = ref Unsafe.As<byte, long>(ref input);
        var i = 0;
        for (var offset = 0; i <= count - 4; i += 4, offset += 48)
        {
            var left = Vector128.LoadUnsafe(ref values, (nuint)i).AsByte();
            var right = Vector128.LoadUnsafe(ref values, (nuint)(i + 2)).AsByte();
            (Vector128.Shuffle(left, firstMask) | firstPrefix).StoreUnsafe(ref output, (nuint)offset);
            (Vector128.Shuffle(left, middleLeftMask) | Vector128.Shuffle(right, middleRightMask) | middlePrefix)
                .StoreUnsafe(ref output, (nuint)(offset + 16));
            (Vector128.Shuffle(right, lastMask) | lastPrefix).StoreUnsafe(ref output, (nuint)(offset + 32));
        }
        return i;
    }

    private static int ReadInt16Vectors(ref byte input, ref byte output,
        int count)
    {
        var firstMask = Vector128.Create(5, 4, 11, 10, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255);
        var middleMask = Vector128.Create(255, 255, 255, 255, 1, 0, 7, 6, 13, 12, 255, 255, 255, 255, 255, 255);
        var lastMask = Vector128.Create(255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 3, 2, 9, 8, 15, 14);
        var firstLengthsMask = Vector128.Create(0, 1, 2, 3, 6, 7, 8, 9, 12, 13, 14, 15, 255, 255, 255, 255);
        var middleLengthsMask = Vector128.Create(255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 2, 3, 4, 5);
        var middleLastLengthsMask = Vector128.Create(8, 9, 10, 11, 14, 15, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255);
        var lastLengthsMask = Vector128.Create(255, 255, 255, 255, 255, 255, 0, 1, 4, 5, 6, 7, 10, 11, 12, 13);
        var prefix = Vector128.Create(0x02000000u);
        var i = 0;
        for (var offset = 0; i <= count - 8; i += 8, offset += 48)
        {
            var first = Vector128.LoadUnsafe(ref input, (nuint)offset);
            var middle = Vector128.LoadUnsafe(ref input, (nuint)(offset + 16));
            var last = Vector128.LoadUnsafe(ref input, (nuint)(offset + 32));
            var firstLengths = Vector128.Shuffle(first, firstLengthsMask) | Vector128.Shuffle(middle, middleLengthsMask);
            var lastLengths = Vector128.Shuffle(middle, middleLastLengthsMask) | Vector128.Shuffle(last, lastLengthsMask);
            if (!Vector128.EqualsAll(firstLengths.AsUInt32(), prefix) || !Vector128.EqualsAll(lastLengths.AsUInt32(), prefix))
            {
                ThrowNumericVectorLength(ref input, offset, 8, 2);
            }
            (Vector128.Shuffle(first, firstMask) | Vector128.Shuffle(middle, middleMask) | Vector128.Shuffle(last, lastMask))
                .StoreUnsafe(ref output, (nuint)(i * 2));
        }
        return i;
    }

    private static int ReadInt32Vectors(ref byte input, ref byte output,
        int count)
    {
        var firstMask = Vector128.Create(7, 6, 5, 4, 15, 14, 13, 12, 255, 255, 255, 255, 255, 255, 255, 255);
        var lastMask = Vector128.Create(255, 255, 255, 255, 255, 255, 255, 255, 7, 6, 5, 4, 15, 14, 13, 12);
        var lengthMask = Vector128.Create(uint.MaxValue, 0u, uint.MaxValue, 0u);
        var prefix = Vector128.Create(0x04000000u, 0u, 0x04000000u, 0u);
        var i = 0;
        for (var offset = 0; i <= count - 4; i += 4, offset += 32)
        {
            var first = Vector128.LoadUnsafe(ref input, (nuint)offset);
            var last = Vector128.LoadUnsafe(ref input, (nuint)(offset + 16));
            if (!Vector128.EqualsAll(first.AsUInt32() & lengthMask, prefix) || !Vector128.EqualsAll(last.AsUInt32() & lengthMask, prefix))
            {
                ThrowNumericVectorLength(ref input, offset, 4, 4);
            }
            (Vector128.Shuffle(first, firstMask) | Vector128.Shuffle(last, lastMask)).StoreUnsafe(ref output, (nuint)(i * 4));
        }
        return i;
    }

    private static int ReadInt64Vectors(ref byte input, ref byte output,
        int count)
    {
        var firstLeftMask = Vector128.Create(11, 10, 9, 8, 7, 6, 5, 4, 255, 255, 255, 255, 255, 255, 255, 255);
        var firstRightMask = Vector128.Create(255, 255, 255, 255, 255, 255, 255, 255, 7, 6, 5, 4, 3, 2, 1, 0);
        var lastLeftMask = Vector128.Create(255, 255, 255, 255, 15, 14, 13, 12, 255, 255, 255, 255, 255, 255, 255, 255);
        var lastRightMask = Vector128.Create(3, 2, 1, 0, 255, 255, 255, 255, 15, 14, 13, 12, 11, 10, 9, 8);
        ref var values = ref Unsafe.As<byte, long>(ref output);
        var i = 0;
        for (var offset = 0; i <= count - 4; i += 4, offset += 48)
        {
            var first = Vector128.LoadUnsafe(ref input, (nuint)offset);
            var middle = Vector128.LoadUnsafe(ref input, (nuint)(offset + 16));
            var last = Vector128.LoadUnsafe(ref input, (nuint)(offset + 32));
            var invalid = first.AsUInt32().GetElement(0) ^ 0x08000000u |
                          first.AsUInt32().GetElement(3) ^ 0x08000000u |
                          middle.AsUInt32().GetElement(2) ^ 0x08000000u |
                          last.AsUInt32().GetElement(1) ^ 0x08000000u;
            if (invalid != 0)
            {
                // Preserve bigint[]'s existing exception and non-returning cold path.
                // The closed codec check disappears from valid-input machine code.
                if (typeof(TCodec) == typeof(Int64Codec))
                {
                    throw new InvalidDataException("A non-NULL bigint[] element must have length 8.");
                }
                ThrowNumericVectorLength(ref input, offset, 4, 8);
            }
            (Vector128.Shuffle(first, firstLeftMask) | Vector128.Shuffle(middle, firstRightMask))
                .AsInt64().StoreUnsafe(ref values, (nuint)i);
            (Vector128.Shuffle(middle, lastLeftMask) | Vector128.Shuffle(last, lastRightMask))
                .AsInt64().StoreUnsafe(ref values, (nuint)(i + 2));
        }
        return i;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowNumericVectorLength(ref byte input, int offset,
        int count, int size)
    {
        // Inspect only a failing block to preserve the existing NULL exception type.
        // Valid input checks its framing in the decoding pass, without another walk.
        for (var i = 0; i < count; i++, offset += 4 + size)
        {
            var length = BinaryPrimitives.ReverseEndianness(
                Unsafe.ReadUnaligned<int>(ref Unsafe.Add(ref input, offset)));
            if (length != size)
            {
                ThrowFixedLength(length);
            }
        }
    }
}