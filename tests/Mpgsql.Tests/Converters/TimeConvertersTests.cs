using System.Buffers;
using System.Net;
using Mpgsql.Converters;
using Mpgsql.Types;

namespace Mpgsql.Tests.Converters;

public sealed class TimeConvertersTests
{
    [Fact]
    public void LiteralPayloadsArraysBuffersAndSegmentBoundaries()
    {
        PgTime value = new PgTime(1000001);
        ConverterAssertions.CheckScalar(value, "00000000000f4241", TimeConverter.GetByteCount, TimeConverter.Write,
            TimeConverter.Write, TimeConverter.Read, TimeConverter.Read);
        ConverterAssertions.CheckNullableScalar<PgTime>(TimeConverter.Write, TimeConverter.Write,
            TimeConverter.GetByteCount, TimeConverter.ReadNullable, TimeConverter.ReadNullable);
        ConverterAssertions.CheckArray(new PgTime[] {value, value}, (uint)TypeOid.Time, "00000000000f4241",
            TimeArrayConverter.GetByteCount, TimeArrayConverter.Write, TimeArrayConverter.Write,
            TimeArrayConverter.Read, TimeArrayConverter.Read, TimeArrayConverter.Read, TimeArrayConverter.Read);
        ConverterAssertions.CheckNullableArray<PgTime, TimeCodec>(value, "00000000000f4241");
    }
    [Fact]
    public void RejectsTruncatedAndTrailingPayloads()
    {
        byte[] bytes = Convert.FromHexString("00000000000f4241");
        Assert.Throws<InvalidDataException>(() => TimeConverter.Read(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.Throws<InvalidDataException>(() => TimeConverter.Read(new ReadOnlySequence<byte>(bytes.AsMemory(0, bytes.Length - 1))));
        byte[] trailing = [.. bytes, 0];
        Assert.Throws<InvalidDataException>(() => TimeConverter.Read(trailing));
        Assert.Throws<InvalidDataException>(() => TimeConverter.Read(new ReadOnlySequence<byte>(trailing)));
    }
}