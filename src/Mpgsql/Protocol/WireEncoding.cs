using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;

namespace Mpgsql.Protocol;

internal static class WireEncoding
{
    // The codec requires client_encoding=UTF8. Never replace malformed input silently.
    internal static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int CStringLength(string value)
    {
        return checked(Utf8.GetByteCount(value) + 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static string Decode(in ReadOnlySequence<byte> value)
    {
        try
        {
            return Utf8.GetString(value);
        }
        catch (DecoderFallbackException error)
        {
            throw new InvalidDataException("Invalid UTF-8 in a PostgreSQL string.", error);
        }
    }
}