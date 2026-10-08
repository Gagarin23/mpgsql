using System.Buffers;
using Mpgsql.Converters;

namespace Mpgsql.Tests.Converters;

public sealed class StringAliasConvertersTests
{
    private const string Value = "Я😀  ";
    private const string Hex = "d0aff09f98802020";

    [Fact]
    public void VarCharPayloadArrayFramingAndReusableRead()
    {
        ConverterAssertions.CheckScalar(Value, Hex, VarCharConverter.GetByteCount, VarCharConverter.Write,
            VarCharConverter.Write, VarCharConverter.Read, VarCharConverter.Read);
        ConverterAssertions.CheckArray(new[] {Value, Value}, 1043, Hex,
            VarCharArrayConverter.GetByteCount, VarCharArrayConverter.Write, VarCharArrayConverter.Write,
            VarCharArrayConverter.Read, VarCharArrayConverter.Read, VarCharArrayConverter.Read, VarCharArrayConverter.Read);
        Assert.Null(VarCharConverter.ReadNullable((ReadOnlyMemory<byte>?)null));
        Assert.Null(VarCharConverter.ReadNullable((ReadOnlySequence<byte>?)null));
        Assert.Equal(0, VarCharConverter.Write(null, Span<byte>.Empty));
    }

    [Fact]
    public void BpCharPayloadArrayFramingAndReusableReadRetainSpaces()
    {
        ConverterAssertions.CheckScalar(Value, Hex, BpCharConverter.GetByteCount, BpCharConverter.Write,
            BpCharConverter.Write, BpCharConverter.Read, BpCharConverter.Read);
        ConverterAssertions.CheckArray(new[] {Value, Value}, 1042, Hex,
            BpCharArrayConverter.GetByteCount, BpCharArrayConverter.Write, BpCharArrayConverter.Write,
            BpCharArrayConverter.Read, BpCharArrayConverter.Read, BpCharArrayConverter.Read, BpCharArrayConverter.Read);
        Assert.Null(BpCharConverter.ReadNullable((ReadOnlyMemory<byte>?)null));
        Assert.Null(BpCharConverter.ReadNullable((ReadOnlySequence<byte>?)null));
        Assert.Equal(0, BpCharConverter.Write(null, Span<byte>.Empty));
    }

    [Fact]
    public void NamePayloadArrayFramingAndReusableRead()
    {
        ConverterAssertions.CheckScalar(Value, Hex, NameConverter.GetByteCount, NameConverter.Write,
            NameConverter.Write, NameConverter.Read, NameConverter.Read);
        ConverterAssertions.CheckArray(new[] {Value, Value}, 19, Hex,
            NameArrayConverter.GetByteCount, NameArrayConverter.Write, NameArrayConverter.Write,
            NameArrayConverter.Read, NameArrayConverter.Read, NameArrayConverter.Read, NameArrayConverter.Read);
        Assert.Null(NameConverter.ReadNullable((ReadOnlyMemory<byte>?)null));
        Assert.Null(NameConverter.ReadNullable((ReadOnlySequence<byte>?)null));
        Assert.Equal(0, NameConverter.Write(null, Span<byte>.Empty));
        // Payload encoding does not impose the server build's NAMEDATALEN.
        Assert.Equal(128, NameConverter.GetByteCount(new string('a', 128)));
    }

    [Fact]
    public void RawUtf8IsBorrowedAndCopiedWithoutValueValidation()
    {
        byte[] input = [0xd0, 0, 0xff];
        var output = new byte[3];
        Assert.True(VarCharConverter.ReadUtf8(input).SequenceEqual(input));
        Assert.True(BpCharConverter.ReadUtf8(input).SequenceEqual(input));
        Assert.True(NameConverter.ReadUtf8(input).SequenceEqual(input));
        Assert.Equal(3, VarCharConverter.WriteUtf8(input, output));
        Assert.Equal(input, output);
        Assert.Equal(3, BpCharConverter.WriteUtf8(input, output));
        Assert.Equal(input, output);
        Assert.Equal(3, NameConverter.WriteUtf8(input, output));
        Assert.Equal(input, output);
        input[0] = (byte)'a';
        Assert.Equal((byte)'a', NameConverter.ReadUtf8(input)[0]);
        Assert.Throws<InvalidDataException>(() => VarCharConverter.Read(input));
        Assert.Throws<InvalidDataException>(() => BpCharConverter.Read(input));
        Assert.Throws<InvalidDataException>(() => NameConverter.Read(input));
    }
}