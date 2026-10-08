using System.Buffers;
using System.Text;
using Mpgsql.Protocol;

namespace Mpgsql.Converters;

internal static class Utf8Payload
{
    internal static int Measure(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return WireEncoding.Utf8.GetByteCount(value);
    }

    internal static int Write(string value, Span<byte> destination)
    {
        return WireEncoding.Utf8.GetBytes(value, destination);
    }

    internal static ReadOnlySpan<byte> ReadUtf8(ReadOnlySpan<byte> payload, bool jsonb)
    {
        if (jsonb)
        {
            if (payload.IsEmpty || payload[0] != 1)
            {
                throw new InvalidDataException("Unsupported or missing jsonb version.");
            }
            payload = payload[1..];
        }
        // Borrowed bytes are opaque. Value validation belongs to PostgreSQL or text decoding.
        return payload;
    }

    internal static ReadOnlySequence<byte> ReadUtf8(ReadOnlySequence<byte> payload, bool jsonb)
    {
        if (jsonb)
        {
            var reader = new SequenceReader<byte>(payload);
            if (!reader.TryRead(out var version) || version != 1)
            {
                throw new InvalidDataException("Unsupported or missing jsonb version.");
            }
            payload = payload.Slice(reader.Position);
        }
        return payload;
    }

    internal static string Read(ReadOnlySpan<byte> payload)
    {
        try
        {
            return WireEncoding.Utf8.GetString(payload);
        }
        catch (DecoderFallbackException error)
        {
            throw new InvalidDataException("Invalid PostgreSQL UTF-8 string.", error);
        }
    }

    internal static string Read(ReadOnlySequence<byte> payload)
    {
        if (payload.IsSingleSegment)
        {
            return Read(payload.FirstSpan);
        }
        var count = CountCharacters(payload);
        // One result string; no Decoder object, temporary char array or flattened byte payload.
        return string.Create(count, payload, static (output, input) => Decode(input, output));
    }

    private static int WholeScalarPrefix(ReadOnlySpan<byte> bytes)
    {
        var last = bytes.Length - 1;
        if (last < 0)
        {
            return 0;
        }
        var start = last;
        while (start > 0 && (bytes[start] & 0xc0) == 0x80 && last - start < 3)
        {
            start--;
        }
        var lead = bytes[start];
        var needed = lead < 0x80 ? 1 :
            lead is >= 0xc2 and <= 0xdf ? 2 :
            lead is >= 0xe0 and <= 0xef ? 3 :
            lead is >= 0xf0 and <= 0xf4 ? 4 : 1;
        return bytes.Length - start < needed ? start : bytes.Length;
    }

    private static int CountCharacters(ReadOnlySequence<byte> input)
    {
        var reader = new SequenceReader<byte>(input);
        Span<byte> scratch = stackalloc byte[4];
        var count = 0;
        try
        {
            while (reader.Remaining != 0)
            {
                var bytes = reader.UnreadSpan;
                var prefix = WholeScalarPrefix(bytes);
                if (prefix != 0)
                {
                    bytes = bytes[..prefix];
                    count = checked(count + WireEncoding.Utf8.GetCharCount(bytes));
                    reader.Advance(prefix);
                    continue;
                }
                var length = (int)Math.Min(4, reader.Remaining);
                reader.TryCopyTo(scratch[..length]);
                if (Rune.DecodeFromUtf8(scratch[..length], out var scalar, out var consumed) != OperationStatus.Done)
                {
                    throw new InvalidDataException("Invalid PostgreSQL UTF-8 string.");
                }
                count = checked(count + scalar.Utf16SequenceLength);
                reader.Advance(consumed);
            }
        }
        catch (DecoderFallbackException error)
        {
            throw new InvalidDataException("Invalid PostgreSQL UTF-8 string.", error);
        }
        return count;
    }

    private static void Decode(ReadOnlySequence<byte> input, Span<char> output)
    {
        var reader = new SequenceReader<byte>(input);
        Span<byte> scratch = stackalloc byte[4];
        var written = 0;
        while (reader.Remaining != 0)
        {
            var prefix = WholeScalarPrefix(reader.UnreadSpan);
            if (prefix != 0)
            {
                written += WireEncoding.Utf8.GetChars(reader.UnreadSpan[..prefix], output[written..]);
                reader.Advance(prefix);
                continue;
            }
            var length = (int)Math.Min(4, reader.Remaining);
            reader.TryCopyTo(scratch[..length]);
            if (Rune.DecodeFromUtf8(scratch[..length], out var scalar, out var consumed) != OperationStatus.Done)
            {
                throw new InvalidDataException("Invalid PostgreSQL UTF-8 string.");
            }
            written += scalar.EncodeToUtf16(output[written..]);
            reader.Advance(consumed);
        }
    }

    internal static int WriteUtf8(ReadOnlySpan<byte> value, Span<byte> destination,
        bool jsonb)
    {
        var size = checked(value.Length + (jsonb ? 1 : 0));
        BinaryPayload.RequireCapacity(size, destination.Length);
        destination = destination[..size];
        BinaryPayload.RequireSeparate(value, destination);
        if (jsonb)
        {
            destination[0] = 1;
            destination = destination[1..];
        }
        value.CopyTo(destination);
        return size;
    }
}