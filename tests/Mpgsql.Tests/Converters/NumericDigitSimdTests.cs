using System.Buffers.Binary;
using Mpgsql.Converters;
using Mpgsql.Tests.Protocol;
using Mpgsql.Types;

namespace Mpgsql.Tests.Converters;

public sealed class NumericDigitSimdTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(256)]
    [InlineData(4096)]
    public void NumericDigitsKeepExactWireBytesWithReusableAndSegmentedStorage(int count)
    {
        ushort[] digits = Enumerable.Range(0, count).Select(i => (ushort)(i * 7919 % 10000)).ToArray();
        var value = new PgNumeric(123, 37, PgNumericSign.Negative, digits);
        byte[] expected = new byte[8 + 2 * count];
        BinaryPrimitives.WriteUInt16BigEndian(expected, (ushort)count);
        BinaryPrimitives.WriteInt16BigEndian(expected.AsSpan(2), 123);
        BinaryPrimitives.WriteUInt16BigEndian(expected.AsSpan(4), 0x4000);
        BinaryPrimitives.WriteUInt16BigEndian(expected.AsSpan(6), 37);
        for (int i = 0; i < count; i++)
            BinaryPrimitives.WriteUInt16BigEndian(expected.AsSpan(8 + 2 * i), digits[i]);
        byte[] bytes = Enumerable.Repeat((byte)0xcc, expected.Length + 18).ToArray();
        Assert.Equal(expected.Length, NumericConverter.Write(value, bytes.AsSpan(1)));
        Assert.Equal(expected, bytes.AsSpan(1, expected.Length).ToArray());
        Assert.Equal((byte)0xcc, bytes[0]);
        Assert.All(bytes[(1 + expected.Length)..], b => Assert.Equal((byte)0xcc, b));
        var output = new ushort[count + 1];
        output[^1] = 12345;
        Assert.Equal(digits, NumericConverter.Read(bytes.AsSpan(1, expected.Length), output.AsMemory()).Digits.ToArray());
        Assert.Equal((ushort)12345, output[^1]);
        Assert.Equal(digits, NumericConverter.Read(expected).Digits.ToArray());
        var splits = count <= 17 ? Enumerable.Range(0, expected.Length + 1) : new[] {1, 7, 8, 9, 4095};
        foreach (int split in splits)
        {
            if (split > expected.Length) continue;
            var sequence = TestWire.Chunks(expected.AsMemory(0, split), expected.AsMemory(split));
            Assert.Equal(digits, NumericConverter.Read(sequence).Digits.ToArray());
            Assert.Equal(digits, NumericConverter.Read(sequence, output.AsMemory()).Digits.ToArray());
        }
        Assert.Equal(digits, NumericConverter.Read(TestWire.ByteSegments(expected), output.AsMemory()).Digits.ToArray());

        foreach (int i in Enumerable.Range(0, Math.Min(count, 17)).Append(count - 1).Where(i => i >= 0).Distinct())
        foreach (ushort invalid in new ushort[] {10000, 32767, 32768, ushort.MaxValue})
        {
            byte[] bad = expected.ToArray();
            BinaryPrimitives.WriteUInt16BigEndian(bad.AsSpan(8 + i * 2), invalid);
            Assert.Throws<InvalidDataException>(() => NumericConverter.Read(bad));
            Assert.Throws<InvalidDataException>(() => NumericConverter.Read(bad, output.AsMemory()));
            Assert.Throws<InvalidDataException>(() => NumericConverter.Read(TestWire.ByteSegments(bad), output.AsMemory()));
            Assert.Throws<InvalidDataException>(() => NumericConverter.Read(TestWire.Chunks(bad.AsMemory(0, 7), bad.AsMemory(7))));
        }
        // Raw writes leave digit semantics to numeric_recv, including inside vector blocks.
        if (count != 0)
        {
            digits[^1] = ushort.MaxValue;
            NumericConverter.Write(value, bytes);
            Assert.Equal(ushort.MaxValue, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(8 + (count - 1) * 2)));
        }
    }
}
