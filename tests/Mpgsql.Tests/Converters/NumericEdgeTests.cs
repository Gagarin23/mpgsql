using System.Buffers.Binary;
using Mpgsql.Converters;
using Mpgsql.Tests.Protocol;
using Mpgsql.Types;

namespace Mpgsql.Tests.Converters;

public sealed class NumericEdgeTests
{
    [Fact]
    public void DecimalExtremesScalesAndRandomValuesHaveExactRoundTrips()
    {
        var random = new Random(42);
        decimal[] special = [decimal.MinValue, decimal.MaxValue, 0, 1, -1, 0.0000000000000000000000000001m, 12345.6700m, -0.01m];
        foreach (decimal value in special) CheckDecimal(value);
        for (int i = 0; i < 2048; i++)
            CheckDecimal(new decimal(random.Next(), random.Next(), random.Next(), i % 2 == 0, (byte)(i % 29)));
    }

    private static void CheckDecimal(decimal value)
    {
        byte[] payload = new byte[NumericConverter.GetByteCount(value)];
        NumericConverter.Write(value, payload);
        Assert.Equal(value, NumericConverter.ReadDecimal(payload));
        Assert.Equal(value, NumericConverter.ReadDecimal(TestWire.ByteSegments(payload)));
        Assert.Equal(value, NumericConverter.Read(payload).ToDecimal());
        Assert.Equal(value, PgNumeric.FromDecimal(value).ToDecimal());
        ushort[] digits = new ushort[8];
        Assert.Equal(value, NumericConverter.Read(payload, digits.AsMemory()).ToDecimal());
        Assert.Equal(value, NumericConverter.Read(TestWire.ByteSegments(payload), digits.AsMemory()).ToDecimal());
        byte[] expected = new byte[payload.Length];
        NumericConverter.Write(PgNumeric.FromDecimal(value), expected);
        Assert.Equal(payload, expected);
    }

    [Theory]
    [InlineData("00000000c0000000", PgNumericSign.NaN)]
    [InlineData("00000000d0000000", PgNumericSign.PositiveInfinity)]
    [InlineData("00000000f0000000", PgNumericSign.NegativeInfinity)]
    [InlineData("00000000d0000020", PgNumericSign.PositiveInfinity)]
    [InlineData("00000000f0000020", PgNumericSign.NegativeInfinity)]
    public void SpecialNumericsPreserveSignAndCannotSilentlyBecomeDecimal(string hex, PgNumericSign sign)
    {
        byte[] payload = TestWire.Bytes(hex);
        Assert.Equal(sign, NumericConverter.Read(payload).Sign);
        Assert.Equal(sign, NumericConverter.Read(TestWire.ByteSegments(payload)).Sign);
        Assert.Throws<OverflowException>(() => NumericConverter.ReadDecimal(payload));
        Assert.Throws<OverflowException>(() => NumericConverter.ReadDecimal(TestWire.ByteSegments(payload)));
    }

    [Fact]
    public void HugeNumericIsLosslessWithoutDecimalTruncation()
    {
        var value = new PgNumeric(short.MaxValue, 16383, PgNumericSign.Positive, new ushort[] {1234, 5678});
        byte[] payload = new byte[NumericConverter.GetByteCount(value)];
        NumericConverter.Write(value, payload);
        var decoded = NumericConverter.Read(TestWire.ByteSegments(payload));
        Assert.Equal(value.Weight, decoded.Weight);
        Assert.Equal(value.Scale, decoded.Scale);
        Assert.Equal(value.Digits.ToArray(), decoded.Digits.ToArray());
        Assert.Throws<OverflowException>(() => NumericConverter.ReadDecimal(payload));
        Assert.Throws<OverflowException>(() => NumericConverter.ReadDecimal(TestWire.ByteSegments(payload)));
        byte[] tiny = TestWire.Bytes("0001fff8000000200001"); // 1e-32
        Assert.Throws<OverflowException>(() => NumericConverter.ReadDecimal(tiny));
        byte[] badDigit = payload.ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(badDigit.AsSpan(8), 10000);
        Assert.Throws<InvalidDataException>(() => NumericConverter.Read(badDigit));
        Assert.Throws<InvalidDataException>(() => NumericConverter.Read(TestWire.ByteSegments(badDigit)));
        byte[] output = Enumerable.Repeat((byte)0xcc, 32).ToArray();
        Assert.Throws<ArgumentException>(() => NumericConverter.Write(value with {Digits = new ushort[] {10000}}, output));
        Assert.All(output, b => Assert.Equal((byte)0xcc, b));
    }

    [Fact]
    public void NullableDecimalArrayKeepsEmptyNullAndZeroDistinct()
    {
        decimal?[] values = [null, 0, decimal.MaxValue, -0.001m, null];
        byte[] payload = new byte[NullableNumericArrayConverter.GetByteCount(values)];
        NullableNumericArrayConverter.Write(values, payload);
        Assert.Equal(values, NullableNumericArrayConverter.ReadDecimals(payload).ToArray());
        Assert.Equal(values, NullableNumericArrayConverter.ReadDecimals(TestWire.ByteSegments(payload)).ToArray());
    }

    [Fact]
    public void RedundantZeroGroupsDoNotOverflowAnOtherwiseZeroDecimal()
    {
        byte[] payload = TestWire.Bytes("00018000000000000000");
        Assert.Equal(0m, NumericConverter.ReadDecimal(payload));
        Assert.Equal(0m, NumericConverter.ReadDecimal(TestWire.ByteSegments(payload)));
    }
}