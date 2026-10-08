using System.Buffers;

namespace Mpgsql.Benchmarks.Queries;

internal static class FrontendFrameParser
{
    // The tag is outside the Int32 length; that length includes its own four bytes.
    internal static bool TryRead(ref ReadOnlySequence<byte> input, out byte tag,
        out ReadOnlySequence<byte> payload, out ReadOnlySequence<byte> frame)
    {
        tag = default; payload = default; frame = default;
        if (input.Length < 5) return false;
        var reader = new SequenceReader<byte>(input);
        reader.TryRead(out tag);
        reader.TryReadBigEndian(out int length);
        if (length is < 4 or > 64 * 1024 * 1024)
            throw new InvalidDataException("Invalid synthetic-peer frontend length.");
        if (input.Length < 1L + length) return false;
        frame = input.Slice(0, 1L + length);
        payload = frame.Slice(5);
        input = input.Slice(frame.End);
        return true;
    }
}
