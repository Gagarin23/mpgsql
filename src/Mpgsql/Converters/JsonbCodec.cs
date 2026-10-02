using System.Buffers;

namespace Mpgsql.Converters;

internal readonly struct JsonbCodec : IBinaryCodec<string>
{
    public static uint Oid => (uint)TypeOid.Jsonb;
    public static int FixedSize => 0;
    public static bool NeedsValidation => true;
    public static bool MayOverlap => false;
    public static int Measure(string value) => Utf8Payload.Measure(value, true);
    public static void CheckOverlap(string value, Span<byte> destination) { }
    public static void Write(string value, Span<byte> destination) => Utf8Payload.Write(value, destination, true);
    public static string Read(ReadOnlySpan<byte> payload) => Utf8Payload.Read(payload, true);
    public static string Read(ReadOnlySequence<byte> payload) => Utf8Payload.Read(payload, true);
}