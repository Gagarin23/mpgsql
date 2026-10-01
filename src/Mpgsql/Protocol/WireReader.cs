using System.Buffers;

namespace Mpgsql.Protocol;

internal ref struct WireReader(ReadOnlySequence<byte> payload)
{
    private SequenceReader<byte> _reader = new(payload);

    internal readonly long Remaining => _reader.Remaining;

    internal byte Byte()
    {
        if (!_reader.TryRead(out byte value))
            throw new InvalidDataException("Truncated PostgreSQL message body.");
        return value;
    }

    internal short Int16()
    {
        if (!_reader.TryReadBigEndian(out short value))
            throw new InvalidDataException("Truncated PostgreSQL Int16.");
        return value;
    }

    internal ushort Count() => unchecked((ushort)Int16());

    internal int Int32()
    {
        if (!_reader.TryReadBigEndian(out int value))
            throw new InvalidDataException("Truncated PostgreSQL Int32.");
        return value;
    }

    internal uint UInt32() => unchecked((uint)Int32());

    internal ReadOnlySequence<byte> Bytes(int length)
    {
        if (length < 0 || length > _reader.Remaining)
            throw new InvalidDataException("Invalid PostgreSQL value length.");
        var value = _reader.Sequence.Slice(_reader.Position, length);
        _reader.Advance(length);
        return value;
    }

    internal ReadOnlySequence<byte> Rest()
    {
        var value = _reader.UnreadSequence;
        _reader.Advance(_reader.Remaining);
        return value;
    }

    internal ReadOnlySequence<byte>? Value()
    {
        int length = Int32();
        return length == -1 ? null : Bytes(length);
    }

    internal void SkipValue()
    {
        int length = Int32();
        if (length == -1)
            return;
        if (length < 0 || length > _reader.Remaining)
            throw new InvalidDataException("Invalid PostgreSQL value length.");
        // Validation does not need to construct a borrowed sequence for each value.
        _reader.Advance(length);
    }

    internal ReadOnlySequence<byte> CStringBytes()
    {
        if (!_reader.TryReadTo(out ReadOnlySequence<byte> value, (byte)0, advancePastDelimiter: true))
            throw new InvalidDataException("Unterminated PostgreSQL string.");
        WireEncoding.ValidateUtf8(value);
        return value;
    }

    internal string CString() => WireEncoding.Decode(CStringBytes());

    internal FormatCode Format()
    {
        var format = (FormatCode)Int16();
        if (format is not (FormatCode.Text or FormatCode.Binary))
            throw new InvalidDataException("Unknown PostgreSQL format code.");
        return format;
    }

    internal void RequireElements(int count, int minimumSize)
    {
        if (count < 0 || count > Remaining / minimumSize)
            throw new InvalidDataException("Invalid PostgreSQL element count.");
    }

    internal readonly void End()
    {
        if (Remaining != 0)
            throw new InvalidDataException("Unexpected trailing bytes in a PostgreSQL message.");
    }
}
