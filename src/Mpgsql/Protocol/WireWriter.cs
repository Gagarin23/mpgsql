using System.Buffers.Binary;

namespace Mpgsql.Protocol;

internal ref struct WireWriter(Span<byte> destination)
{
    private Span<byte> _remaining = destination;

    internal void Byte(byte value)
    {
        _remaining[0] = value;
        _remaining = _remaining[1..];
    }

    internal void Int16(short value)
    {
        BinaryPrimitives.WriteInt16BigEndian(_remaining,
            value);
        _remaining = _remaining[2..];
    }

    internal void Count(int value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(_remaining,
            checked((ushort)value));
        _remaining = _remaining[2..];
    }

    internal void Int32(int value)
    {
        BinaryPrimitives.WriteInt32BigEndian(_remaining,
            value);
        _remaining = _remaining[4..];
    }

    internal void UInt32(uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(_remaining,
            value);
        _remaining = _remaining[4..];
    }

    internal void CString(string value)
    {
        int written = WireEncoding.Utf8.GetBytes(value.AsSpan(),
            _remaining);
        _remaining = _remaining[written..];
        Byte(0);
    }

    internal void Bytes(ReadOnlySpan<byte> value)
    {
        value.CopyTo(_remaining);
        _remaining = _remaining[value.Length..];
    }

    internal void Value(ReadOnlyMemory<byte>? value)
    {
        Int32(value?.Length ?? -1);
        if (value.HasValue)
        {
            Bytes(value.Value.Span);
        }
    }

    internal void Formats(ReadOnlySpan<FormatCode> formats)
    {
        Count(formats.Length);
        foreach (var format in formats)
            Int16((short)format);
    }

    internal void Values(ReadOnlySpan<ReadOnlyMemory<byte>?> values)
    {
        Count(values.Length);
        foreach (var value in values)
            Value(value);
    }
}