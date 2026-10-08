using System.Buffers;
using Mpgsql.Converters;
using Mpgsql.Types;

namespace Mpgsql.Tests.Converters;

public sealed class TimeTzConvertersTests
{
    [Fact]
    public void LiteralPayloadsArraysBuffersAndSegmentBoundaries()
    {
        var value = new PgTimeTz(new PgTime(1000001), 18000);
        ConverterAssertions.CheckScalar(value, "00000000000f4241ffffb9b0", TimeTzConverter.GetByteCount, TimeTzConverter.Write,
            TimeTzConverter.Write, TimeTzConverter.Read, TimeTzConverter.Read);
        ConverterAssertions.CheckNullableScalar(TimeTzConverter.Write, TimeTzConverter.Write,
            TimeTzConverter.GetByteCount, TimeTzConverter.ReadNullable, TimeTzConverter.ReadNullable);
        ConverterAssertions.CheckArray(new[] {value, value}, (uint)TypeOid.TimeTz, "00000000000f4241ffffb9b0",
            TimeTzArrayConverter.GetByteCount, TimeTzArrayConverter.Write, TimeTzArrayConverter.Write,
            TimeTzArrayConverter.Read, TimeTzArrayConverter.Read, TimeTzArrayConverter.Read, TimeTzArrayConverter.Read);
        ConverterAssertions.CheckNullableArray<PgTimeTz, TimeTzCodec>(value, "00000000000f4241ffffb9b0");
    }
    [Fact]
    public void RejectsTruncatedAndTrailingPayloads()
    {
        var bytes = Convert.FromHexString("00000000000f4241ffffb9b0");
        Assert.Throws<InvalidDataException>(() => TimeTzConverter.Read(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.Throws<InvalidDataException>(() => TimeTzConverter.Read(new ReadOnlySequence<byte>(bytes.AsMemory(0, bytes.Length - 1))));
        byte[] trailing = [.. bytes, 0];
        Assert.Throws<InvalidDataException>(() => TimeTzConverter.Read(trailing));
        Assert.Throws<InvalidDataException>(() => TimeTzConverter.Read(new ReadOnlySequence<byte>(trailing)));
    }
}