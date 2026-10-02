using System.Buffers;

namespace Mpgsql.Converters;

internal readonly struct ByteaCodec : IBinaryCodec<ReadOnlyMemory<byte>>
{
    public static uint Oid => (uint)TypeOid.Bytea;
    public static int FixedSize => 0;
    public static bool NeedsValidation => false;
    public static bool MayOverlap => true;
    public static int Measure(ReadOnlyMemory<byte> value) => value.Length;
    public static void CheckOverlap(ReadOnlyMemory<byte> value, Span<byte> destination)
        => BinaryPayload.RequireSeparate(value.Span, destination);
    public static void Write(ReadOnlyMemory<byte> value, Span<byte> destination) => value.Span.CopyTo(destination);
    public static ReadOnlyMemory<byte> Read(ReadOnlySpan<byte> payload) => payload.ToArray();
    public static ReadOnlyMemory<byte> Read(ReadOnlySequence<byte> payload) => payload.ToArray();
}