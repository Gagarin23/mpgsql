using System.Buffers;
using System.Net;
using Mpgsql.Converters;
using Mpgsql.Types;

namespace Mpgsql.Tests.Converters;

public sealed class TimestampTzConvertersTests
{
    [Fact]
    public void LiteralPayloadsArraysBuffersAndSegmentBoundaries()
    {
        PgTimestampTz value = new PgTimestampTz(1000001);
        ConverterAssertions.CheckScalar(value, "00000000000f4241", TimestampTzConverter.GetByteCount, TimestampTzConverter.Write,
            TimestampTzConverter.Write, TimestampTzConverter.Read, TimestampTzConverter.Read);
        ConverterAssertions.CheckNullableScalar<PgTimestampTz>(TimestampTzConverter.Write, TimestampTzConverter.Write,
            TimestampTzConverter.GetByteCount, TimestampTzConverter.ReadNullable, TimestampTzConverter.ReadNullable);
        ConverterAssertions.CheckArray(new PgTimestampTz[] {value, value}, (uint)TypeOid.TimestampTz, "00000000000f4241",
            TimestampTzArrayConverter.GetByteCount, TimestampTzArrayConverter.Write, TimestampTzArrayConverter.Write,
            TimestampTzArrayConverter.Read, TimestampTzArrayConverter.Read, TimestampTzArrayConverter.Read, TimestampTzArrayConverter.Read);
        ConverterAssertions.CheckNullableArray<PgTimestampTz, TimestampTzCodec>(value, "00000000000f4241");
    }
    [Fact]
    public void RejectsTruncatedAndTrailingPayloads()
    {
        byte[] bytes = Convert.FromHexString("00000000000f4241");
        Assert.Throws<InvalidDataException>(() => TimestampTzConverter.Read(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.Throws<InvalidDataException>(() => TimestampTzConverter.Read(new ReadOnlySequence<byte>(bytes.AsMemory(0, bytes.Length - 1))));
        byte[] trailing = [.. bytes, 0];
        Assert.Throws<InvalidDataException>(() => TimestampTzConverter.Read(trailing));
        Assert.Throws<InvalidDataException>(() => TimestampTzConverter.Read(new ReadOnlySequence<byte>(trailing)));
    }
}