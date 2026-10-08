using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using Mpgsql.Converters;
using Mpgsql.Tests.Protocol;
using Mpgsql.Types;

namespace Mpgsql.Tests.Converters;

public sealed class BuiltinEdgeTests
{
    [Fact]
    public void EveryDeclaredOidHasAnExplicitPublicConverter()
    {
        var assembly = typeof(Int64Converter).Assembly;
        foreach (var oid in Enum.GetValues<TypeOid>())
        {
            var name = oid.ToString();
            var type = assembly.GetType("Mpgsql.Converters." + name + "Converter");
            Assert.NotNull(type);
            var field = type.GetField(name.EndsWith("Array", StringComparison.Ordinal) ? "ArrayTypeOid" : "TypeOid", BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(field);
            Assert.Equal((uint)oid, field.GetRawConstantValue());
        }
    }

    [Fact]
    public void CountsRejectNegativesBeforeSizeArithmetic()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Int32ArrayConverter.GetByteCount(int.MinValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => NullableInt32ArrayConverter.GetByteCount(int.MinValue, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => NullableInt32ArrayConverter.GetByteCount(2, 3));
        Assert.Throws<OverflowException>(() => Int32ArrayConverter.GetByteCount(int.MaxValue));
    }

    [Theory, InlineData("80000000"), InlineData("00000001"), InlineData("7f800000"), InlineData("ff800000"), InlineData("7fc12345")]
    public void Float32PreservesEveryBitIncludingNaNsAndNegativeZero(string hex)
    {
        var bytes = TestWire.Bytes(hex);
        var bits = BinaryPrimitives.ReadInt32BigEndian(bytes);
        Assert.Equal(bits, BitConverter.SingleToInt32Bits(Float32Converter.Read(bytes)));
        Assert.Equal(bits, BitConverter.SingleToInt32Bits(Float32Converter.Read(TestWire.ByteSegments(bytes))));
        var result = new byte[4];
        Float32Converter.Write(BitConverter.Int32BitsToSingle(bits), result);
        Assert.Equal(bytes, result);
    }

    [Theory, InlineData("8000000000000000"), InlineData("0000000000000001"), InlineData("7ff0000000000000"), InlineData("fff0000000000000"), InlineData("7ff8123456789abc")]
    public void Float64PreservesEveryBitIncludingNaNsAndNegativeZero(string hex)
    {
        var bytes = TestWire.Bytes(hex);
        var bits = BinaryPrimitives.ReadInt64BigEndian(bytes);
        Assert.Equal(bits, BitConverter.DoubleToInt64Bits(Float64Converter.Read(bytes)));
        Assert.Equal(bits, BitConverter.DoubleToInt64Bits(Float64Converter.Read(TestWire.ByteSegments(bytes))));
        var result = new byte[8];
        Float64Converter.Write(BitConverter.Int64BitsToDouble(bits), result);
        Assert.Equal(bytes, result);
    }

    [Fact]
    public void CalendarBoundariesInfinitiesAndExactClrMappings()
    {
        var bytes = new byte[16];
        foreach (var days in new[]
                 {
                     PgDate.MinFiniteDays,
                     PgDate.MaxFiniteDays,
                     int.MinValue,
                     int.MaxValue
                 })
        {
            DateConverter.Write(new PgDate(days), bytes);
            Assert.Equal
            (
                days, DateConverter.Read(bytes.AsSpan(0, 4))
                    .DaysSinceEpoch
            );
        }
        DateConverter.Write(new DateOnly(2000, 1, 1), bytes);
        Assert.Equal(new byte[4], bytes[..4]);
        Assert.Equal
        (
            DateOnly.MinValue, PgDate
                .FromDateOnly(DateOnly.MinValue)
                .ToDateOnly()
        );
        Assert.Equal
        (
            DateOnly.MaxValue, PgDate
                .FromDateOnly(DateOnly.MaxValue)
                .ToDateOnly()
        );
        Assert.Throws<OverflowException>(() => PgDate.PositiveInfinity.ToDateOnly());
        foreach (var micros in new[]
                 {
                     PgTimestamp.MinFiniteMicroseconds,
                     PgTimestamp.MaxFiniteMicroseconds,
                     long.MinValue,
                     long.MaxValue
                 })
        {
            TimestampConverter.Write(new PgTimestamp(micros), bytes);
            TimestampTzConverter.Write(new PgTimestampTz(micros), bytes);
            Assert.Equal
            (
                micros, TimestampConverter.Read(bytes.AsSpan(0, 8))
                    .MicrosecondsSinceEpoch
            );
        }
        var beforeEpoch = new DateTime(1999, 12, 31, 23, 59, 59, DateTimeKind.Unspecified).AddTicks(9_999_990);
        Assert.Equal
        (
            -1, PgTimestamp.FromDateTime(beforeEpoch)
                .MicrosecondsSinceEpoch
        );
        Assert.Equal
        (
            beforeEpoch, PgTimestamp
                .FromDateTime(beforeEpoch)
                .ToDateTime()
        );
        Assert.Equal
        (
            DateTime.MinValue, PgTimestamp
                .FromDateTime(DateTime.MinValue)
                .ToDateTime()
        );
        Assert.Equal(new DateTime(2000, 1, 1), new PgTimestamp(0).ToDateTime());
        var offset = new DateTimeOffset(2000, 1, 1, 5, 0, 0, TimeSpan.FromHours(5));
        Assert.Equal
        (
            0, PgTimestampTz.FromDateTimeOffset(offset)
                .MicrosecondsSinceEpoch
        );
        Assert.Equal
        (
            DateTimeKind.Utc, new PgTimestampTz(0).ToDateTime()
                .Kind
        );
        Assert.Throws<ArgumentException>(() => TimestampConverter.Write(DateTime.UtcNow, bytes));
        Assert.Throws<ArgumentException>(() => TimestampTzConverter.Write(DateTime.SpecifyKind(DateTime.Now, DateTimeKind.Unspecified), bytes));
        Assert.Throws<ArgumentException>(() => PgTimestamp.FromDateTime(new DateTime(2000, 1, 1).AddTicks(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeConverter.Write(new PgTime(-1), bytes));
        TimeConverter.Write(new PgTime(PgTime.MicrosecondsPerDay), bytes);
        Assert.Equal
        (
            PgTime.MicrosecondsPerDay, TimeConverter.Read(bytes.AsSpan(0, 8))
                .Microseconds
        );
        Assert.Throws<OverflowException>(() => new PgTime(PgTime.MicrosecondsPerDay).ToTimeOnly());
        var interval = new PgInterval(1, 2, -3);
        Assert.Throws<InvalidOperationException>(() => interval.ToTimeSpan());
        Assert.Equal
        (
            TimeSpan.FromTicks(-10), PgInterval
                .FromTimeSpan(TimeSpan.FromTicks(-10))
                .ToTimeSpan()
        );
        IntervalConverter.Write(PgInterval.PositiveInfinity, bytes);
        Assert.Equal(PgInterval.PositiveInfinity, IntervalConverter.Read(bytes));
    }

    [Fact]
    public void InvalidScalarCalendarValuesFailBeforeOutputMutation()
    {
        var output = Enumerable
            .Repeat((byte)0xcc, 64)
            .ToArray();
        Assert.Throws<ArgumentOutOfRangeException>(() => DateConverter.Write(new PgDate(PgDate.MinFiniteDays - 1), output));
        Assert.Throws<ArgumentOutOfRangeException>(() => TimestampConverter.Write(new PgTimestamp(PgTimestamp.MaxFiniteMicroseconds + 1), output));
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeTzConverter.Write(new PgTimeTz(default, 57600), output));
        Assert.All(output, b => Assert.Equal((byte)0xcc, b));
        var payload = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(payload, -1);
        Assert.Throws<InvalidDataException>(() => TimeConverter.Read(payload));
        Assert.Throws<InvalidDataException>(() => TimeConverter.Read(TestWire.ByteSegments(payload)));
        BinaryPrimitives.WriteInt64BigEndian(payload, PgTimestamp.MinFiniteMicroseconds - 1);
        Assert.Throws<InvalidDataException>(() => TimestampConverter.Read(payload));
    }

    [Fact]
    public void FixedArraySizingDoesNotPrevalidateEveryValue()
    {
        PgTime[] values = [default, new PgTime(-1)];
        var payload = new byte[44];
        Assert.Equal(44, TimeArrayConverter.GetByteCount(values));
        Assert.Equal(44, TimeArrayConverter.Write(values, payload));
        Assert.Equal(-1L, BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(36)));
        Assert.Throws<InvalidDataException>(() => TimeArrayConverter.Read(payload));
        PgTime?[] nullable = [new PgTime(-1), null];
        Assert.Equal(36, NullableTimeArrayConverter.GetByteCount(nullable));
        Assert.Equal(36, NullableTimeArrayConverter.Write(nullable, payload));
    }

    [Fact]
    public void ClrArrayConversionErrorsCanLeaveEarlierElementsWritten()
    {
        DateTime[] values = [new DateTime(2000, 1, 1), new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)];
        var payload = Enumerable
            .Repeat((byte)0xcc, 64)
            .ToArray();
        Assert.Equal(44, TimestampArrayConverter.GetByteCount(values));
        Assert.Throws<ArgumentException>(() => TimestampArrayConverter.Write(values, payload));
        Assert.Equal(8, BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(20)));
        Assert.Equal(0L, BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(24)));
        Assert.All(payload[32..], b => Assert.Equal((byte)0xcc, b));
    }

    [Theory, InlineData("::", 0), InlineData("::1", 128), InlineData("2001:db8::1234", 48), InlineData("ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", 128)]
    public void IPv6RoundTripsAndRejectsCidrHostBits(string address, int prefix)
    {
        var value = PgInet.FromIPAddress(IPAddress.Parse(address), prefix);
        var payload = new byte[20];
        InetConverter.Write(value, payload);
        Assert.Equal(value, InetConverter.Read(payload));
        Assert.Equal(value, InetConverter.Read(TestWire.ByteSegments(payload)));
        Assert.Equal(IPAddress.Parse(address), value.ToIPAddress());
        if (address == "2001:db8::1234")
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => CidrConverter.Write(value, payload));
        }
        else
        {
            CidrConverter.Write(value, payload);
            Assert.Equal(value, CidrConverter.Read(payload));
        }
        payload[0] = 10;
        Assert.Throws<InvalidDataException>(() => InetConverter.Read(payload));
    }

    [Fact]
    public void OverlappingArraySourcesAreRejectedBeforeHeaderWrites()
    {
        var storage = new byte[256];
        var source = new ByteMemory(storage);
        int[] values = [1, 2];
        values.CopyTo(source.Memory.Span);
        var before = storage.ToArray();
        Assert.Throws<ArgumentException>(() => Int32ArrayConverter.Write(source.Memory[..2], storage));
        Assert.Equal(before, storage);
        ReadOnlyMemory<byte>[] byteArrays = [storage.AsMemory(0, 1)];
        Assert.Throws<ArgumentException>(() => ByteaArrayConverter.Write(byteArrays, storage));
        Assert.Equal(before, storage);
        var packet = ConverterAssertions.ArrayBytes(23, TestWire.Bytes("00000001"), TestWire.Bytes("00000002"));
        packet.CopyTo(storage, 0);
        before = storage.ToArray();
        Assert.Throws<ArgumentException>(() => Int32ArrayConverter.Read(storage.AsSpan(0, packet.Length), source.Memory.Span));
        Assert.Equal(before, storage);
    }

    private sealed class ByteMemory(byte[] bytes) : MemoryManager<int>
    {
        public override Span<int> GetSpan()
        {
            return MemoryMarshal.Cast<byte, int>(bytes.AsSpan());
        }
        public override MemoryHandle Pin(int elementIndex = 0)
        {
            throw new NotSupportedException();
        }
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }
}