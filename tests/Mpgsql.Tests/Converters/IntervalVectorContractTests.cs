using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Mpgsql.Converters;
using Mpgsql.Tests.Protocol;
using Mpgsql.Types;

namespace Mpgsql.Tests.Converters;

public sealed class IntervalVectorContractTests
{
    [Fact]
    public void ManagedLayoutMatchesMonthsDaysMicrosecondsWithoutPaddingOrReferences()
    {
        Assert.Equal(LayoutKind.Sequential, typeof(PgInterval).StructLayoutAttribute!.Value);
        Assert.Equal(16, Unsafe.SizeOf<PgInterval>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<PgInterval>());
        Assert.Equal(0, Marshal.OffsetOf<PgInterval>("<Months>k__BackingField").ToInt32());
        Assert.Equal(4, Marshal.OffsetOf<PgInterval>("<Days>k__BackingField").ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<PgInterval>("<Microseconds>k__BackingField").ToInt32());

        var value = new PgInterval(0x01020304, 0x05060708, 0x1112131415161718);
        var expected = new byte[16];
        if (BitConverter.IsLittleEndian)
        {
            BinaryPrimitives.WriteInt32LittleEndian(expected, value.Months);
            BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(4), value.Days);
            BinaryPrimitives.WriteInt64LittleEndian(expected.AsSpan(8), value.Microseconds);
        }
        else
        {
            BinaryPrimitives.WriteInt32BigEndian(expected, value.Months);
            BinaryPrimitives.WriteInt32BigEndian(expected.AsSpan(4), value.Days);
            BinaryPrimitives.WriteInt64BigEndian(expected.AsSpan(8), value.Microseconds);
        }
        var managed = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref value, 1));
        Assert.Equal(expected, managed.ToArray());
    }

    [Fact]
    public void LiteralFieldsSignsAndInfinitySentinelsSurviveAllSegmentBoundaries()
    {
        (PgInterval Value, string Payload)[] cases =
        [
            (new PgInterval(0x0c0d0e0f, 0x08090a0b, 0x0001020304050607), "000102030405060708090a0b0c0d0e0f"),
            (new PgInterval(1, -65537, -1), "fffffffffffffffffffeffff00000001"),
            (new PgInterval(3, 2, 1), "00000000000000010000000200000003"),
            (PgInterval.NegativeInfinity, "80000000000000008000000080000000"),
            (PgInterval.PositiveInfinity, "7fffffffffffffff7fffffff7fffffff")
        ];
        foreach (var (value, payload) in cases)
        {
            ConverterAssertions.CheckScalar
            (
                value, payload, IntervalConverter.GetByteCount, IntervalConverter.Write,
                IntervalConverter.Write, IntervalConverter.Read, IntervalConverter.Read
            );
            ConverterAssertions.CheckArray
            (
                new[] { value, value }, (uint)TypeOid.Interval, payload,
                IntervalArrayConverter.GetByteCount, IntervalArrayConverter.Write, IntervalArrayConverter.Write,
                IntervalArrayConverter.Read, IntervalArrayConverter.Read, IntervalArrayConverter.Read, IntervalArrayConverter.Read
            );
            ConverterAssertions.CheckNullableArray<PgInterval, IntervalCodec>(value, payload);
            var bytes = TestWire.Bytes(payload);
            for (var prefix = 0; prefix < 16; prefix++)
            {
                Assert.Throws<InvalidDataException>(() => IntervalConverter.Read(bytes.AsSpan(0, prefix)));
                Assert.Throws<InvalidDataException>(() => IntervalConverter.Read(TestWire.ByteSegments(bytes[..prefix])));
            }
            byte[] trailing = [.. bytes, 0];
            Assert.Throws<InvalidDataException>(() => IntervalConverter.Read(trailing));
            Assert.Throws<InvalidDataException>(() => IntervalConverter.Read(TestWire.ByteSegments(trailing)));
        }
    }

    [Fact]
    public void UnalignedSpanAndSingleSegmentValuesKeepAllFieldsAndSourceBytes()
    {
        (PgInterval Value, string Payload)[] cases =
        [
            (new PgInterval(0x0c0d0e0f, 0x08090a0b, 0x0001020304050607), "000102030405060708090a0b0c0d0e0f"),
            (new PgInterval(1, -65537, -1), "fffffffffffffffffffeffff00000001"),
            (PgInterval.NegativeInfinity, "80000000000000008000000080000000"),
            (PgInterval.PositiveInfinity, "7fffffffffffffff7fffffff7fffffff")
        ];
        foreach (var (value, payload) in cases)
        for (var offset = 1; offset <= 15; offset++)
        {
            var buffer = Enumerable.Repeat((byte)0xcc, 48).ToArray();
            var bytes = TestWire.Bytes(payload);
            bytes.CopyTo(buffer, offset);
            var before = buffer.ToArray();
            Assert.Equal(value, IntervalConverter.Read(buffer.AsSpan(offset, 16)));
            Assert.Equal(value, IntervalConverter.Read(new ReadOnlySequence<byte>(buffer.AsMemory(offset, 16))));
            Assert.Equal(before, buffer);
        }
    }

    [Fact]
    public void DurationRangeEdgesAgreeWithExactIntegerArithmeticAndRetainFinalOverflow()
    {
        int[] days = [-5_337_600, -5_337_599, -1, 0, 1, 5_337_599, 5_337_600, int.MinValue, int.MaxValue];
        long[] microseconds =
        [
            -461_168_601_842_738_791, -461_168_601_842_738_790, -1, 0, 1,
            461_168_601_842_738_790, 461_168_601_842_738_791, long.MinValue, long.MaxValue
        ];
        foreach (var day in days)
        foreach (var micros in microseconds)
        {
            var value = new PgInterval(0, day, micros);
            var exact = (BigInteger)day * TimeSpan.TicksPerDay + (BigInteger)micros * 10;
            var bytes = new byte[16];
            IntervalConverter.Write(value, bytes);
            if (exact < long.MinValue || exact > long.MaxValue)
            {
                Assert.Throws<OverflowException>(() => value.ToTimeSpan());
                Assert.Throws<OverflowException>(() => IntervalConverter.ReadTimeSpan(bytes));
                Assert.Throws<OverflowException>(() => IntervalConverter.ReadTimeSpan(TestWire.ByteSegments(bytes)));
            }
            else
            {
                var expected = TimeSpan.FromTicks((long)exact);
                Assert.Equal(expected, value.ToTimeSpan());
                Assert.Equal(expected, IntervalConverter.ReadTimeSpan(bytes));
                Assert.Equal(expected, IntervalConverter.ReadTimeSpan(TestWire.ByteSegments(bytes)));
            }
        }
    }

    [Theory, InlineData(int.MinValue), InlineData(-1), InlineData(1), InlineData(int.MaxValue)]
    public void EveryNonzeroMonthStillRejectsFixedTimeSpanConversion(int months)
    {
        var value = new PgInterval(months, 0, 0);
        var bytes = new byte[16];
        IntervalConverter.Write(value, bytes);
        Assert.Throws<InvalidOperationException>(() => value.ToTimeSpan());
        Assert.Throws<InvalidOperationException>(() => IntervalConverter.ReadTimeSpan(bytes));
        Assert.Throws<InvalidOperationException>(() => IntervalConverter.ReadTimeSpan(new ReadOnlySequence<byte>(bytes)));
        Assert.Throws<InvalidOperationException>(() => IntervalConverter.ReadTimeSpan(TestWire.ByteSegments(bytes)));
    }
}
