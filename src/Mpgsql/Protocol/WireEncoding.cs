using System.Buffers;
using System.Text;
using System.Text.Unicode;

namespace Mpgsql.Protocol;

internal static class WireEncoding
{
    // The codec requires client_encoding=UTF8. Never replace malformed input silently.
    internal static readonly UTF8Encoding Utf8 = new(false, true);

    internal static int CStringLength(string? value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.AsSpan().Contains('\0'))
            throw new ArgumentException("A PostgreSQL string cannot contain NUL.", nameof(value));

        return checked(Utf8.GetByteCount(value) + 1);
    }

    internal static void ValidateUtf8(in ReadOnlySequence<byte> value)
    {
        if (value.IsSingleSegment)
        {
            if (!System.Text.Unicode.Utf8.IsValid(value.FirstSpan))
                throw new InvalidDataException("Invalid UTF-8 in a PostgreSQL string.");
            return;
        }

        // A Unicode scalar may straddle any segment boundary. No concatenation is needed.
        var reader = new SequenceReader<byte>(value);
        Span<byte> scalar = stackalloc byte[4];
        while (reader.Remaining > 0)
        {
            int length = (int)Math.Min(4, reader.Remaining);
            reader.TryCopyTo(scalar[..length]);
            if (Rune.DecodeFromUtf8(scalar[..length], out _, out int consumed) != OperationStatus.Done)
                throw new InvalidDataException("Invalid UTF-8 in a PostgreSQL string.");
            reader.Advance(consumed);
        }
    }

    internal static string Decode(in ReadOnlySequence<byte> value) => Utf8.GetString(value);
}
