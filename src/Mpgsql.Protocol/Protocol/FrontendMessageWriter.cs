using System.Buffers;
using System.Buffers.Binary;

namespace Mpgsql.Protocol;

/// <summary>Compile-time encoder contract for a particular frontend value type.</summary>
/// <remarks>Implementations must validate fields before exposing a size and write exactly that body size.</remarks>
public interface IFrontendMessage<TSelf> where TSelf : struct, IFrontendMessage<TSelf>
{
    static abstract byte? GetMessageType(in TSelf message);
    static abstract int GetByteCount(in TSelf message);
    static abstract void WritePayload(
        in TSelf message,
        Span<byte> destination
    );
}

/// <summary>Writes typed frontend messages without dispatching on their kind.</summary>
public static class FrontendMessageWriter
{
    public static int GetByteCount<T>(in T message) where T : struct, IFrontendMessage<T>
    {
        return T.GetByteCount(in message);
    }

    /// <summary>Checks complete packet capacity before writing any bytes.</summary>
    public static int Write<T>(
        in T message,
        Span<byte> destination
    ) where T : struct, IFrontendMessage<T>
    {
        var length = T.GetByteCount(in message);
        if (destination.Length < length)
        {
            throw new ArgumentException
            (
                "The destination is too small for the PostgreSQL message.",
                nameof(destination)
            );
        }
        WriteCore
        (
            in message,
            destination[..length],
            length
        );
        return length;
    }

    public static void Write<T>(
        in T message,
        IBufferWriter<byte> destination
    ) where T : struct, IFrontendMessage<T>
    {
        ArgumentNullException.ThrowIfNull(destination);
        var length = T.GetByteCount(in message);
        WriteCore
        (
            in message,
            destination
                .GetSpan(length)[..length],
            length
        );
        destination.Advance(length);
    }

    private static void WriteCore<T>(
        in T message,
        Span<byte> destination,
        int length
    ) where T : struct, IFrontendMessage<T>
    {
        var type = T.GetMessageType(in message);
        var offset = 0;
        if (type.HasValue)
        {
            destination[0] = type.Value;
            offset = 1;
        }
        // Length includes its own four bytes and the body, but not the optional tag.
        BinaryPrimitives.WriteInt32BigEndian
        (
            destination.Slice
            (
                offset,
                4
            ),
            length - offset
        );
        T.WritePayload
        (
            in message,
            destination[(offset + 4)..]
        );
    }
}

internal static class FrontendSize
{
    internal static int Packet(
        int payloadLength,
        bool tagged = true
    )
    {
        return checked(payloadLength + (tagged ? 5 : 4));
    }
    internal static int Initialized(int length)
    {
        if (length == 0)
        {
            throw new InvalidOperationException("The frontend message is not initialized.");
        }
        return length;
    }
    internal static void Count(int count)
    {
        if ((uint)count > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException
            (
                nameof(count),
                "A PostgreSQL Int16 count cannot exceed 65535."
            );
        }
    }
    internal static void Format(FormatCode format)
    {
        if (format is not (FormatCode.Text or FormatCode.Binary))
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }
    }
    internal static void ParameterFormats(
        int formatCount,
        int valueCount
    )
    {
        if (formatCount != 0 && formatCount != 1 && formatCount != valueCount)
        {
            throw new ArgumentException("Parameter format count must be 0, 1, or the parameter count.");
        }
    }
    internal static int Formats(ReadOnlySpan<FormatCode> formats)
    {
        Count(formats.Length);
        foreach (var format in formats)
        {
            Format(format);
        }
        return checked(2 + 2 * formats.Length);
    }
    internal static int Values(ReadOnlySpan<ReadOnlyMemory<byte>?> values)
    {
        Count(values.Length);
        var length = 2;
        foreach (var value in values)
        {
            length = checked(length + 4 + value.GetValueOrDefault()
                .Length);
        }
        return length;
    }
}