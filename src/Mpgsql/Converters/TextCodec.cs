using System.Buffers;

namespace Mpgsql.Converters;

internal readonly struct TextCodec : IBinaryCodec<string>
{
    public static uint Oid => (uint)TypeOid.Text;
    public static int FixedSize => 0;
    public static bool NeedsValidation => true;
    public static bool MayOverlap => false;
    public static int Measure(string value) => Utf8Payload.Measure(value, false);
    public static void CheckOverlap(string value, Span<byte> destination) { }
    public static void Write(string value, Span<byte> destination) => Utf8Payload.Write(value, destination, false);
    public static string Read(ReadOnlySpan<byte> payload) => Utf8Payload.Read(payload, false);
    public static string Read(ReadOnlySequence<byte> payload) => Utf8Payload.Read(payload, false);
}