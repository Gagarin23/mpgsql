using System.Buffers;

namespace Mpgsql.Converters;

internal static class BinaryScalar<T, TCodec> where TCodec : struct, IBinaryCodec<T>
{
    internal static int Write(T value, Span<byte> destination)
    {
        var size = TCodec.Measure(value);
        BinaryPayload.RequireCapacity(size, destination.Length);
        destination = destination[..size];
        TCodec.CheckOverlap(value, destination);
        TCodec.Write(value, destination);
        return size;
    }

    internal static void Write(T value, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var size = TCodec.Measure(value);
        if (size == 0)
        {
            return;
        }
        var bytes = destination
            .GetSpan(size)[..size];
        TCodec.CheckOverlap(value, bytes);
        TCodec.Write(value, bytes);
        destination.Advance(size);
    }
}