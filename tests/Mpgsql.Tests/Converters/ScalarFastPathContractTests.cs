using System.Buffers;
using System.Buffers.Binary;
using Mpgsql.Converters;
using Mpgsql.Tests.Protocol;
using Mpgsql.Types;

namespace Mpgsql.Tests.Converters;

public sealed class ScalarFastPathContractTests
{
    [Fact]
    public void DecimalScalarWritersPreserveCompletePayloadScaleAndCanonicalZero()
    {
        (decimal Value, string Payload)[] cases =
        [
            (0m, "0000000000000000"),
            (new decimal(0, 0, 0, true, 28), "000000000000001c"),
            (1m, "00010000000000000001"),
            (10000m, "00010001000000000001"),
            (12345.6700m, "0003000100000004000109291a2c"),
            (-12345.67m, "0003000140000002000109291a2c"),
            (0.0000000000000000000000000001m, "0001fff90000001c0001")
        ];
        foreach (var (value, payload) in cases)
        {
            ConverterAssertions.CheckScalar
            (
                value, payload, NumericConverter.GetByteCount, NumericConverter.Write,
                NumericConverter.Write, NumericConverter.ReadDecimal, NumericConverter.ReadDecimal
            );
        }
        ConverterAssertions.CheckNullableScalar<decimal>
        (
            NumericConverter.Write, NumericConverter.Write, NumericConverter.GetByteCount,
            NumericConverter.ReadNullableDecimal, NumericConverter.ReadNullableDecimal
        );
    }

    [Fact]
    public void DecimalWritersKeepBoundsAndNullDestinationFailuresBeforeMutation()
    {
        decimal[] values = [decimal.MinValue, decimal.MaxValue, 12345.6700m, 0m];
        foreach (var value in values)
        {
            var output = Enumerable.Repeat((byte)0xcc, NumericConverter.GetByteCount(value) - 1).ToArray();
            Assert.Throws<ArgumentException>(() => NumericConverter.Write(value, output));
            Assert.All(output, b => Assert.Equal((byte)0xcc, b));
            var writer = new ArrayBufferWriter<byte>();
            NumericConverter.Write(value, writer);
            Assert.Equal(value, NumericConverter.ReadDecimal(writer.WrittenSpan));
            Assert.Equal(NumericConverter.GetByteCount(value), writer.WrittenCount);
        }
        Assert.Throws<ArgumentNullException>(() => NumericConverter.Write(1m, (IBufferWriter<byte>)null!));
        Assert.Throws<ArgumentNullException>(() => NumericConverter.Write((decimal?)null, (IBufferWriter<byte>)null!));
    }

    [Fact]
    public void FixedStructReadersRejectWrongCompleteLengthWithEmptyAndFragmentedSegments()
    {
        (int Size, Action<ReadOnlySequence<byte>> Read)[] readers =
        [
            (16, value => { _ = UuidConverter.Read(value); }),
            (12, value => { _ = TimeTzConverter.Read(value); }),
            (16, value => { _ = IntervalConverter.Read(value); })
        ];
        foreach (var (size, read) in readers)
        foreach (var length in new[] { 0, size - 1, size + 1 })
        {
            var bytes = new byte[length];
            Assert.Throws<InvalidDataException>(() => read(new ReadOnlySequence<byte>(bytes)));
            var fragmented = TestWire.Chunks
            (
                ReadOnlyMemory<byte>.Empty, bytes.AsMemory(0, length / 2),
                ReadOnlyMemory<byte>.Empty, bytes.AsMemory(length / 2), ReadOnlyMemory<byte>.Empty
            );
            Assert.Throws<InvalidDataException>(() => read(fragmented));
        }
    }

    [Fact]
    public void DateOnlyReadersRetainBothClrBoundsAndRejectAdjacentDatesAndInfinities()
    {
        foreach (var value in new[] { DateOnly.MinValue, new DateOnly(2000, 1, 1), DateOnly.MaxValue })
        {
            var bytes = new byte[4];
            DateConverter.Write(value, bytes);
            Assert.Equal(value, DateConverter.ReadDateOnly(bytes));
            foreach (var sequence in Inputs(bytes))
            {
                Assert.Equal(value, DateConverter.ReadDateOnly(sequence));
            }
        }
        var minimum = PgDate.FromDateOnly(DateOnly.MinValue).DaysSinceEpoch;
        var maximum = PgDate.FromDateOnly(DateOnly.MaxValue).DaysSinceEpoch;
        foreach (var days in new[] { minimum - 1, maximum + 1, int.MinValue, int.MaxValue })
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(bytes, days);
            Assert.Throws<OverflowException>(() => new PgDate(days).ToDateOnly());
            Assert.Throws<OverflowException>(() => DateConverter.ReadDateOnly(bytes));
            foreach (var sequence in Inputs(bytes))
            {
                Assert.Throws<OverflowException>(() => DateConverter.ReadDateOnly(sequence));
            }
        }
    }

    [Fact]
    public void TimeOnlyReadersRetainPrecisionAndDistinguishMidnightFromTwentyFourHours()
    {
        foreach (var micros in new[] { 0L, 1, PgTime.MicrosecondsPerDay - 1 })
        {
            var value = new TimeOnly(micros * 10);
            var bytes = new byte[8];
            TimeConverter.Write(new PgTime(micros), bytes);
            Assert.Equal(value, new PgTime(micros).ToTimeOnly());
            Assert.Equal(value, TimeConverter.ReadTimeOnly(bytes));
            foreach (var sequence in Inputs(bytes))
            {
                Assert.Equal(value, TimeConverter.ReadTimeOnly(sequence));
            }
        }
        var midnight = new byte[8];
        TimeConverter.Write(new PgTime(PgTime.MicrosecondsPerDay), midnight);
        Assert.Throws<OverflowException>(() => TimeConverter.ReadTimeOnly(midnight));
        foreach (var sequence in Inputs(midnight))
        {
            Assert.Throws<OverflowException>(() => TimeConverter.ReadTimeOnly(sequence));
        }
        foreach (var micros in new[] { -1L, PgTime.MicrosecondsPerDay + 1, long.MinValue, long.MaxValue })
        {
            Assert.Throws<OverflowException>(() => new PgTime(micros).ToTimeOnly());
            var bytes = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(bytes, micros);
            Assert.Throws<InvalidDataException>(() => TimeConverter.ReadTimeOnly(bytes));
            foreach (var sequence in Inputs(bytes))
            {
                Assert.Throws<InvalidDataException>(() => TimeConverter.ReadTimeOnly(sequence));
            }
        }
    }

    [Fact]
    public void TimestampTzClrReadersPreserveUtcBoundsPrecisionAndInfinityFailures()
    {
        DateTime[] values =
        [
            DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc),
            new DateTime(1999, 12, 31, 23, 59, 59, DateTimeKind.Utc).AddTicks(9_999_990),
            new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(DateTime.MaxValue.Ticks - 9, DateTimeKind.Utc)
        ];
        foreach (var value in values)
        {
            var bytes = new byte[8];
            TimestampTzConverter.Write(value, bytes);
            CheckTimestamp(value, TimestampTzConverter.ReadDateTime(bytes), TimestampTzConverter.ReadDateTimeOffset(bytes));
            foreach (var sequence in Inputs(bytes))
            {
                CheckTimestamp(value, TimestampTzConverter.ReadDateTime(sequence), TimestampTzConverter.ReadDateTimeOffset(sequence));
            }
        }
        var minimum = PgTimestampTz.FromDateTime(values[0]).MicrosecondsSinceEpoch;
        var maximum = PgTimestampTz.FromDateTime(values[^1]).MicrosecondsSinceEpoch;
        foreach (var micros in new[] { minimum - 1, maximum + 1, long.MinValue, long.MaxValue })
        {
            var bytes = new byte[8];
            TimestampTzConverter.Write(new PgTimestampTz(micros), bytes);
            Assert.Throws<OverflowException>(() => TimestampTzConverter.ReadDateTime(bytes));
            Assert.Throws<OverflowException>(() => TimestampTzConverter.ReadDateTimeOffset(bytes));
            foreach (var sequence in Inputs(bytes))
            {
                Assert.Throws<OverflowException>(() => TimestampTzConverter.ReadDateTime(sequence));
                Assert.Throws<OverflowException>(() => TimestampTzConverter.ReadDateTimeOffset(sequence));
            }
        }
        var imprecise = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(1);
        Assert.Throws<ArgumentException>(() => PgTimestampTz.FromDateTime(imprecise));
        Assert.Throws<ArgumentException>(() => PgTimestampTz.FromDateTimeOffset(new DateTimeOffset(imprecise)));
    }

    [Fact]
    public void IntervalReadersPreserveLargeOppositeComponentsAndFinalOverflow()
    {
        (PgInterval Value, long Ticks)[] cases =
        [
            (new PgInterval(0, 20_000_000, -1_727_999_999_999_999_958), 420),
            (new PgInterval(0, -20_000_000, 1_727_999_999_999_999_958), -420)
        ];
        foreach (var (value, ticks) in cases)
        {
            var expected = TimeSpan.FromTicks(ticks);
            var bytes = new byte[16];
            IntervalConverter.Write(value, bytes);
            Assert.Equal(expected, value.ToTimeSpan());
            Assert.Equal(expected, IntervalConverter.ReadTimeSpan(bytes));
            foreach (var sequence in Inputs(bytes))
            {
                Assert.Equal(expected, IntervalConverter.ReadTimeSpan(sequence));
            }
        }
        foreach (var value in new[] { new PgInterval(0, 0, long.MinValue), new PgInterval(0, 0, long.MaxValue) })
        {
            var bytes = new byte[16];
            IntervalConverter.Write(value, bytes);
            Assert.Throws<OverflowException>(() => IntervalConverter.ReadTimeSpan(bytes));
            foreach (var sequence in Inputs(bytes))
            {
                Assert.Throws<OverflowException>(() => IntervalConverter.ReadTimeSpan(sequence));
            }
        }
        Assert.Throws<InvalidOperationException>(() => PgInterval.NegativeInfinity.ToTimeSpan());
        Assert.Throws<InvalidOperationException>(() => PgInterval.PositiveInfinity.ToTimeSpan());
    }

    private static IEnumerable<ReadOnlySequence<byte>> Inputs(byte[] bytes)
    {
        yield return new ReadOnlySequence<byte>(bytes);
        yield return TestWire.ByteSegments(bytes);
        yield return TestWire.Chunks
        (
            ReadOnlyMemory<byte>.Empty, bytes.AsMemory(0, bytes.Length / 2),
            ReadOnlyMemory<byte>.Empty, bytes.AsMemory(bytes.Length / 2), ReadOnlyMemory<byte>.Empty
        );
    }

    private static void CheckTimestamp(DateTime expected, DateTime actual, DateTimeOffset offset)
    {
        Assert.Equal(expected, actual);
        Assert.Equal(DateTimeKind.Utc, actual.Kind);
        Assert.Equal(expected.Ticks, offset.Ticks);
        Assert.Equal(TimeSpan.Zero, offset.Offset);
    }
}
