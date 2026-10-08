using System.Buffers;
using Mpgsql.Converters;
using Mpgsql.Types;

namespace Mpgsql.Tests.Converters;

public sealed class IntervalConvertersTests
{
    [Fact]
    public void LiteralPayloadsArraysBuffersAndSegmentBoundaries()
    {
        var value = new PgInterval(-2, 3, -1000001);
        ConverterAssertions.CheckScalar
        (
            value, "fffffffffff0bdbf00000003fffffffe", IntervalConverter.GetByteCount, IntervalConverter.Write,
            IntervalConverter.Write, IntervalConverter.Read, IntervalConverter.Read
        );
        ConverterAssertions.CheckNullableScalar
        (
            IntervalConverter.Write, IntervalConverter.Write,
            IntervalConverter.GetByteCount, IntervalConverter.ReadNullable, IntervalConverter.ReadNullable
        );
        ConverterAssertions.CheckArray
        (
            new[]
            {
                value,
                value
            }, (uint)TypeOid.Interval, "fffffffffff0bdbf00000003fffffffe",
            IntervalArrayConverter.GetByteCount, IntervalArrayConverter.Write, IntervalArrayConverter.Write,
            IntervalArrayConverter.Read, IntervalArrayConverter.Read, IntervalArrayConverter.Read, IntervalArrayConverter.Read
        );
        ConverterAssertions.CheckNullableArray<PgInterval, IntervalCodec>(value, "fffffffffff0bdbf00000003fffffffe");
    }
    [Fact]
    public void RejectsTruncatedAndTrailingPayloads()
    {
        var bytes = Convert.FromHexString("fffffffffff0bdbf00000003fffffffe");
        Assert.Throws<InvalidDataException>(() => IntervalConverter.Read(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.Throws<InvalidDataException>(() => IntervalConverter.Read(new ReadOnlySequence<byte>(bytes.AsMemory(0, bytes.Length - 1))));
        byte[] trailing = [.. bytes, 0];
        Assert.Throws<InvalidDataException>(() => IntervalConverter.Read(trailing));
        Assert.Throws<InvalidDataException>(() => IntervalConverter.Read(new ReadOnlySequence<byte>(trailing)));
    }
}