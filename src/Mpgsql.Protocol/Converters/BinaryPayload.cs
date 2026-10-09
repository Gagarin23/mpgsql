using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Mpgsql.Converters;

internal static class BinaryPayload
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void RequireLength(long actual, int expected)
    {
        if (actual != expected)
        {
            throw new InvalidDataException($"The binary value requires exactly {expected} bytes.");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void RequireCapacity(int size, int capacity)
    {
        if (capacity < size)
        {
            throw new ArgumentException("The destination is too small for the converted value.", "destination");
        }
    }

    internal static void RequireSeparate(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.Overlaps(destination))
        {
            throw new ArgumentException("Input and output storage must not overlap.", "destination");
        }
    }

    // These byte views are only used for address comparisons, never for encoding CLR layout.
    // Unlike MemoryMarshal.AsBytes, this also supports Nullable<T> without assuming its layout.
    internal static ReadOnlySpan<byte> StorageBytes<T>(ReadOnlySpan<T> source)
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            return default;
        }
        return MemoryMarshal.CreateReadOnlySpan
        (
            ref Unsafe.As<T, byte>(ref MemoryMarshal.GetReference(source)),
            checked(source.Length * Unsafe.SizeOf<T>())
        );
    }

    internal static Span<byte> StorageBytes<T>(Span<T> destination)
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            return default;
        }
        return MemoryMarshal.CreateSpan
        (
            ref Unsafe.As<T, byte>(ref MemoryMarshal.GetReference(destination)),
            checked(destination.Length * Unsafe.SizeOf<T>())
        );
    }

    internal static void RequireSeparate<T>(ReadOnlySequence<byte> source, Span<T> destination)
    {
        var output = StorageBytes(destination);
        if (output.IsEmpty)
        {
            return;
        }
        foreach (var segment in source)
        {
            RequireSeparate(segment.Span, output);
        }
    }

    // Each fixed-field codec validates its exact payload length. Keep a contiguous
    // value on that path; stack storage is needed only for fragmented fields.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static T ReadSmall<T, TCodec>(ReadOnlySequence<byte> payload, int size)
        where TCodec : struct, IBinaryCodec<T>
    {
        if (payload.IsSingleSegment)
        {
            return TCodec.Read(payload.FirstSpan);
        }
        return ReadSmallFragmented<T, TCodec>(payload, size);
    }

    // For small split fixed fields only; never consolidates a variable-sized payload.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static T ReadSmallFragmented<T, TCodec>(ReadOnlySequence<byte> payload, int size)
        where TCodec : struct, IBinaryCodec<T>
    {
        RequireLength(payload.Length, size);
        Span<byte> bytes = stackalloc byte[20];
        payload.CopyTo(bytes[..size]);
        return TCodec.Read(bytes[..size]);
    }

    internal static ulong ReadUnsigned(ReadOnlySequence<byte> payload, int size)
    {
        RequireLength(payload.Length, size);
        ulong result = 0;
        foreach (var segment in payload)
        foreach (var part in segment.Span)
        {
            result = result << 8 | part;
        }
        return result;
    }
}
