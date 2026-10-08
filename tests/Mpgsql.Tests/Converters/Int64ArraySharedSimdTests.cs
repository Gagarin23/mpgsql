using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using Mpgsql.Converters;
using Mpgsql.Tests.Protocol;

namespace Mpgsql.Tests.Converters;

public class Int64ArraySharedSimdTests
{
    [Theory, InlineData(0), InlineData(1), InlineData(3), InlineData(4), InlineData(5), InlineData(9), InlineData(256)]
    public void UnalignedPayloadsAndSlicedStoragePreserveBytes(int count)
    {
        var values = Enumerable.Range(0, count + 2)
            .Select(i => unchecked((long)(0xfedcba9876543210UL * (ulong)(i + 1)))).ToArray();
        ReadOnlyMemory<long> source = values.AsMemory(1, count);
        var expected = EncodeScalar(source.Span);
        for (var offset = 1; offset <= 16; offset++)
        {
            var bytes = Enumerable.Repeat((byte)0xcc, expected.Length + offset + 17).ToArray();
            Assert.Equal(expected.Length, Int64ArrayConverter.Write(source, bytes.AsSpan(offset)));
            Assert.Equal(expected, bytes.AsSpan(offset, expected.Length).ToArray());
            Assert.All(bytes[..offset], value => Assert.Equal((byte)0xcc, value));
            Assert.All(bytes[(offset + expected.Length)..], value => Assert.Equal((byte)0xcc, value));
            var storage = Enumerable.Repeat(42L, count + 2).ToArray();
            Assert.Equal(count, Int64ArrayConverter.Read(bytes.AsSpan(offset, expected.Length), storage.AsSpan(1)));
            Assert.Equal(source.ToArray(), storage.AsSpan(1, count).ToArray());
            Assert.Equal(42, storage[0]);
            Assert.Equal(42, storage[^1]);
            Assert.Equal(source.ToArray(), Int64ArrayConverter.Read(bytes.AsSpan(offset, expected.Length)).ToArray());
        }
    }

    [Theory, InlineData(0), InlineData(1), InlineData(2), InlineData(3), InlineData(4), InlineData(5), InlineData(6), InlineData(7), InlineData(8)]
    public void InvalidPrefixPreservesExceptionAndWholeBlockMutationBoundary(int index)
    {
        var values = Enumerable.Range(1, 9).Select(i => (long)i).ToArray();
        var simd = BitConverter.IsLittleEndian && (Ssse3.IsSupported || AdvSimd.Arm64.IsSupported);
        var completed = simd ? index / 4 * 4 : index;
        foreach (var flags in new[] {0, 1})
        foreach (var length in new[] {-1, -2, 0, 7, 9, int.MaxValue})
        {
            var bytes = EncodeScalar(values);
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4), flags);
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20 + index * 12), length);
            var expected = Enumerable.Repeat(42L, 11).ToArray();
            values.AsSpan(0, completed).CopyTo(expected.AsSpan(1));
            var storage = Enumerable.Repeat(42L, 11).ToArray();
            var error = Assert.Throws<InvalidDataException>(() => Int64ArrayConverter.Read(bytes.AsSpan(), storage.AsSpan(1)));
            Assert.Equal("A non-NULL bigint[] element must have length 8.", error.Message);
            Assert.Equal(expected, storage);
            storage.AsSpan().Fill(42);
            error = Assert.Throws<InvalidDataException>(() => Int64ArrayConverter.Read(new ReadOnlySequence<byte>(bytes), storage.AsSpan(1)));
            Assert.Equal("A non-NULL bigint[] element must have length 8.", error.Message);
            Assert.Equal(expected, storage);
        }
    }

    [Fact]
    public void SharedReusablePathsDoNotAllocate()
    {
        var values = Enumerable.Range(0, 257).Select(i => (long)i).ToArray();
        var bytes = EncodeScalar(values);
        var storage = new long[values.Length];
        var sequence = TestWire.Chunks(bytes.AsMemory(0, 409), bytes.AsMemory(409));
        for (var i = 0; i < 128; i++)
        {
            Int64ArrayConverter.Write(values, bytes);
            Int64ArrayConverter.Read(bytes.AsSpan(), storage);
            Int64ArrayConverter.Read(sequence, storage);
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 128; i++)
        {
            Int64ArrayConverter.Write(values, bytes);
            Int64ArrayConverter.Read(bytes.AsSpan(), storage);
            Int64ArrayConverter.Read(sequence, storage);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static byte[] EncodeScalar(ReadOnlySpan<long> values)
    {
        var bytes = new byte[values.IsEmpty ? 12 : 20 + 12 * values.Length];
        BinaryPrimitives.WriteInt32BigEndian(bytes, values.IsEmpty ? 0 : 1);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 20);
        if (values.IsEmpty)
        {
            return bytes;
        }
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(12), values.Length);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), 1);
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20 + 12 * i), 8);
            BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(24 + 12 * i), values[i]);
        }
        return bytes;
    }
}