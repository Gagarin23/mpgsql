#if PROTOCOL_BASELINE
extern alias baseline;
#endif

using Mpgsql.Protocol;

namespace Mpgsql.Benchmarks;

internal static class PacketVerification
{
    internal static void Check<T>(in T message) where T : struct, IFrontendMessage<T>
    {
        int size = FrontendMessageWriter.GetByteCount(in message);
        var bytes = new byte[size];
        if (FrontendMessageWriter.Write(in message,
                bytes) != size)
        {
            throw new InvalidOperationException("The encoder returned an incorrect packet size.");
        }
    }

#if PROTOCOL_BASELINE
    internal static void Check<T>(in T message,
        baseline::Mpgsql.Protocol.FrontendMessage original)
        where T : struct, IFrontendMessage<T>
    {
        int size = FrontendMessageWriter.GetByteCount(in message);
        if (size != original.GetByteCount())
        {
            throw new InvalidOperationException("Packet sizes differ from the baseline.");
        }
        var actual = new byte[size];
        var expected = new byte[size];
        if (FrontendMessageWriter.Write(in message,
                actual) != size || original.Write(expected) != size ||
            !actual.AsSpan().SequenceEqual(expected))
        {
            throw new InvalidOperationException("Encoded packets differ from the baseline.");
        }
    }
#endif
}