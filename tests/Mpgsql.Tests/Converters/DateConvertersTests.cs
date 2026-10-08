using System.Buffers;
using Mpgsql.Converters;
using Mpgsql.Types;

namespace Mpgsql.Tests.Converters;

public sealed class DateConvertersTests
{
    [Fact]
    public void LiteralPayloadsArraysBuffersAndSegmentBoundaries()
    {
        var value = new PgDate(-1);
        ConverterAssertions.CheckScalar
        (
            value, "ffffffff", DateConverter.GetByteCount, DateConverter.Write,
            DateConverter.Write, DateConverter.Read, DateConverter.Read
        );
        ConverterAssertions.CheckNullableScalar
        (
            DateConverter.Write, DateConverter.Write,
            DateConverter.GetByteCount, DateConverter.ReadNullable, DateConverter.ReadNullable
        );
        ConverterAssertions.CheckArray
        (
            new[]
            {
                value,
                value
            }, (uint)TypeOid.Date, "ffffffff",
            DateArrayConverter.GetByteCount, DateArrayConverter.Write, DateArrayConverter.Write,
            DateArrayConverter.Read, DateArrayConverter.Read, DateArrayConverter.Read, DateArrayConverter.Read
        );
        ConverterAssertions.CheckNullableArray<PgDate, DateCodec>(value, "ffffffff");
    }
    [Fact]
    public void RejectsTruncatedAndTrailingPayloads()
    {
        var bytes = Convert.FromHexString("ffffffff");
        Assert.Throws<InvalidDataException>(() => DateConverter.Read(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.Throws<InvalidDataException>(() => DateConverter.Read(new ReadOnlySequence<byte>(bytes.AsMemory(0, bytes.Length - 1))));
        byte[] trailing = [.. bytes, 0];
        Assert.Throws<InvalidDataException>(() => DateConverter.Read(trailing));
        Assert.Throws<InvalidDataException>(() => DateConverter.Read(new ReadOnlySequence<byte>(trailing)));
    }
}