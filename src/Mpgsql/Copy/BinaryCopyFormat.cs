using System.Buffers;
using System.Buffers.Binary;

namespace Mpgsql.Copy;

internal static class BinaryCopyFormat
{
    internal const int HeaderSize = 19;
    internal static ReadOnlySpan<byte> Signature => [0x50, 0x47, 0x43, 0x4f, 0x50, 0x59, 0x0a, 0xff, 0x0d, 0x0a, 0x00];

    internal static void WriteHeader(IBufferWriter<byte> destination)
    {
        var bytes = destination.GetSpan(HeaderSize)[..HeaderSize];
        Signature.CopyTo(bytes);
        bytes[11..].Clear(); // Int32 flags=0, Int32 extension length=0, both big-endian.
        destination.Advance(HeaderSize);
    }

    internal static void WriteInt16(IBufferWriter<byte> destination, short value)
    {
        BinaryPrimitives.WriteInt16BigEndian(destination.GetSpan(2), value);
        destination.Advance(2);
    }
}
