using System.Buffers;
using System.Net;
using Mpgsql.Converters;
using Mpgsql.Types;
using Mpgsql.Benchmarks.NpgsqlBaseline;

namespace Mpgsql.Benchmarks;

internal static class BuiltinConverterVerification
{
    internal static void Run()
    {
        Check<bool, BooleanCodec>(true);
        Check<short, Int16Codec>(-1234);
        Check<int, Int32Codec>(-123456789);
        Check<float, Float32Codec>(1.5f);
        Check<double, Float64Codec>(-2.5d);
        Check<long, MoneyCodec>(-12345);
        Check<uint, OidCodec>(uint.MaxValue);
        Check<Guid, UuidCodec>(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"));
        Check<PgDate, DateCodec>(new(-1));
        Check<PgTime, TimeCodec>(new(1000001));
        Check<PgTimeTz, TimeTzCodec>(new(new(1000001), 18000));
        Check<PgTimestamp, TimestampCodec>(new(-1));
        Check<PgTimestampTz, TimestampTzCodec>(new(1000001));
        Check<PgInterval, IntervalCodec>(new(-1, 2, -3));
        Check<PgInet, InetCodec>(PgInet.FromIPAddress(IPAddress.Parse("2001:db8::1234"), 48));
        Check<PgInet, CidrCodec>(PgInet.FromIPAddress(IPAddress.Parse("2001:db8::"), 48));
        Check<decimal, DecimalCodec>(decimal.MaxValue);
        Check<DateOnly, DateClrCodec>(new(2026, 10, 2));
        Check<TimeOnly, TimeClrCodec>(new(12, 34, 56));
        Check<DateTime, TimestampClrCodec>(new(2000, 1, 1));
        Check<DateTimeOffset, TimestampTzClrCodec>(new(2000, 1, 1, 5, 0, 0, TimeSpan.FromHours(5)));
        Check<TimeSpan, IntervalClrCodec>(TimeSpan.FromTicks(-10));
        CheckVariableWrites();
        Console.WriteLine("All built-in codecs: zero managed allocations for scalar value/reusable array paths and variable-payload writes verified.");
    }

    private static void Check<T, TCodec>(T value) where T : struct where TCodec : struct, IBinaryCodec<T>
    {
        foreach (int count in new[] {0, 1, 256, 4096}) Check<T, TCodec>(value, count);
    }

    private static void Check<T, TCodec>(T value, int count) where T : struct where TCodec : struct, IBinaryCodec<T>
    {
        T[] values = Enumerable.Repeat(value, count).ToArray();
        T?[] nullable = values.Select((v, i) => i % 3 == 0 ? (T?)null : v).ToArray();
        var scalar = new byte[TCodec.Measure(value)];
        var bytes = new byte[BinaryArray<T, TCodec>.Measure(values)];
        var nullBytes = new byte[BinaryNullableArray<T, TCodec>.Measure(nullable, out _)];
        BinaryScalar<T, TCodec>.Write(value, scalar);
        BinaryArray<T, TCodec>.Write(values, bytes);
        BinaryNullableArray<T, TCodec>.Write(nullable, nullBytes);
        var scalarSequence = NpgsqlArrayVerification.Sequence(scalar, 1);
        var sequence = NpgsqlArrayVerification.Sequence(bytes, 7);
        var nullSequence = NpgsqlArrayVerification.Sequence(nullBytes, 7);
        var scratch = new T[values.Length];
        var nullScratch = new T?[values.Length];
        var writer = new FixedBufferWriter(Math.Max(scalar.Length, Math.Max(bytes.Length, nullBytes.Length)));
        for (int i = 0; i < 32; i++) Exercise();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 128; i++) Exercise();
        if (GC.GetAllocatedBytesForCurrentThread() != before)
        {
            throw new InvalidOperationException($"{typeof(TCodec).Name} allocated managed storage in reusable paths.");
        }

        void Exercise()
        {
            BinaryScalar<T, TCodec>.Write(value, scalar);
            _ = TCodec.Read(scalar);
            _ = TCodec.Read(scalarSequence);
            writer.Reset();
            BinaryScalar<T, TCodec>.Write(value, writer);
            BinaryArray<T, TCodec>.Write(values, bytes);
            BinaryArray<T, TCodec>.Read(bytes, scratch);
            BinaryArray<T, TCodec>.Read(sequence, scratch);
            writer.Reset();
            BinaryArray<T, TCodec>.Write(values, writer);
            BinaryNullableArray<T, TCodec>.Write(nullable, nullBytes);
            BinaryNullableArray<T, TCodec>.Read(nullBytes, nullScratch);
            BinaryNullableArray<T, TCodec>.Read(nullSequence, nullScratch);
            writer.Reset();
            BinaryNullableArray<T, TCodec>.Write(nullable, writer);
        }
    }

    private static void CheckVariableWrites()
    {
        ReadOnlyMemory<byte> value = new byte[128];
        ReadOnlyMemory<byte>?[] arrays = [value, null, value];
        var numeric = PgNumeric.FromDecimal(decimal.MaxValue);
        PgNumeric?[] numbers = [numeric, null, numeric];
        string text = new('x', 4096);
        string?[] strings = [text, null, text];
        Memory<byte> jsonb = System.Text.Encoding.UTF8.GetBytes(text);
        Memory<byte>?[] jsonbValues = [jsonb, null, jsonb];
        var output = new byte[16384];
        var writer = new FixedBufferWriter(output.Length);
        ushort[] digits = new ushort[8];
        byte[] numericBytes = new byte[NumericConverter.GetByteCount(numeric)];
        NumericConverter.Write(numeric, numericBytes);
        var numericSequence = NpgsqlArrayVerification.Sequence(numericBytes, 1);
        for (int i = 0; i < 32; i++) Exercise();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 128; i++) Exercise();
        if (GC.GetAllocatedBytesForCurrentThread() != before)
        {
            throw new InvalidOperationException("Variable-sized payload writes or borrowed/reusable reads allocated storage.");
        }

        void Exercise()
        {
            ByteaConverter.Write(value, output);
            ByteaConverter.Read(value.Span, output);
            NullableByteaArrayConverter.Write(arrays, output);
            NumericConverter.Write(numeric, output);
            NumericConverter.Read(numericBytes, digits.AsMemory());
            NumericConverter.Read(numericSequence, digits.AsMemory());
            NullableNumericArrayConverter.Write(numbers, output);
            TextConverter.Write(text, output);
            TextArrayConverter.Write(strings, output);
            JsonConverter.Write(text, output);
            JsonbConverter.Write(jsonb, output);
            NullableJsonbArrayConverter.Write(jsonbValues, output);
            writer.Reset();
            JsonbConverter.Write(jsonb, writer);
            writer.Reset();
            NullableJsonbArrayConverter.Write(jsonbValues, writer);
            XmlConverter.Write(text, output);
            writer.Reset();
            TextArrayConverter.Write(strings, writer);
        }
    }
}
