using System.Buffers;

namespace Mpgsql.Converters;

// Closed generic value-type codecs let the JIT specialize array loops. There are no
// converter objects, per-element delegates, boxing, or runtime OID lookups.
internal interface IBinaryCodec<T>
{
    static abstract uint Oid { get; }
    static abstract int FixedSize { get; } // zero means variable-sized
    static abstract bool MayOverlap { get; }
    static abstract int Measure(T value);
    static abstract void CheckOverlap(T value, Span<byte> destination);
    // Returns the actual length so arrays need not measure a variable-sized element twice.
    static abstract int Write(T value, Span<byte> destination);
    static abstract T Read(ReadOnlySpan<byte> payload);
    static abstract T Read(ReadOnlySequence<byte> payload);
}
