using System.Buffers;
using Mpgsql.Converters;
using Mpgsql.Types;

namespace Mpgsql.Tests.Converters;

public sealed class TimestampConvertersTests
{
    [Fact]
    public void LiteralPayloadsArraysBuffersAndSegmentBoundaries()
    {
        var value = new PgTimestamp(-1);
        ConverterAssertions.CheckScalar
        (
            value, "ffffffffffffffff", TimestampConverter.GetByteCount, TimestampConverter.Write,
            TimestampConverter.Write, TimestampConverter.Read, TimestampConverter.Read
        );
        ConverterAssertions.CheckNullableScalar
        (
            TimestampConverter.Write, TimestampConverter.Write,
            TimestampConverter.GetByteCount, TimestampConverter.ReadNullable, TimestampConverter.ReadNullable
        );
        ConverterAssertions.CheckArray
        (
            new[]
            {
                value,
                value
            }, (uint)TypeOid.Timestamp, "ffffffffffffffff",
            TimestampArrayConverter.GetByteCount, TimestampArrayConverter.Write, TimestampArrayConverter.Write,
            TimestampArrayConverter.Read, TimestampArrayConverter.Read, TimestampArrayConverter.Read, TimestampArrayConverter.Read
        );
        ConverterAssertions.CheckNullableArray<PgTimestamp, TimestampCodec>(value, "ffffffffffffffff");
    }
    [Fact]
    public void RejectsTruncatedAndTrailingPayloads()
    {
        var bytes = Convert.FromHexString("ffffffffffffffff");
        Assert.Throws<InvalidDataException>(() => TimestampConverter.Read(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.Throws<InvalidDataException>(() => TimestampConverter.Read(new ReadOnlySequence<byte>(bytes.AsMemory(0, bytes.Length - 1))));
        byte[] trailing = [.. bytes, 0];
        Assert.Throws<InvalidDataException>(() => TimestampConverter.Read(trailing));
        Assert.Throws<InvalidDataException>(() => TimestampConverter.Read(new ReadOnlySequence<byte>(trailing)));
    }
}