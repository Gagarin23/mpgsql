using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Mpgsql.Converters;
using Mpgsql.Protocol;
using Mpgsql.Tests.Protocol;

namespace Mpgsql.Tests.Converters;

public sealed class NumericArraySimdTests
{
    [Theory, InlineData(0), InlineData(1), InlineData(3), InlineData(4), InlineData(5), InlineData(7), InlineData(8), InlineData(9), InlineData(15), InlineData(16), InlineData(17), InlineData(31), InlineData(32), InlineData(33),
     InlineData(256), InlineData(4096)]
    public void NumericWidthsPreserveBytesAtVectorAndSegmentBoundaries(int count)
    {
        Check<short, Int16Codec>(Values(count, new short[] {short.MinValue, short.MaxValue, -1, 0, 1, 0x1234, -9876}),
            Int16ArrayConverter.Write, Int16ArrayConverter.Write, Int16ArrayConverter.Read, Int16ArrayConverter.Read,
            Int16ArrayConverter.Read, Int16ArrayConverter.Read);
        Check<int, Int32Codec>(Values(count, new[] {int.MinValue, int.MaxValue, -1, 0, 1, 0x12345678, -987654321}),
            Int32ArrayConverter.Write, Int32ArrayConverter.Write, Int32ArrayConverter.Read, Int32ArrayConverter.Read,
            Int32ArrayConverter.Read, Int32ArrayConverter.Read);
        Check<uint, OidCodec>(Values(count, new uint[] {0, uint.MaxValue, 1, 0x80000000, 0x12345678, 42, 0xaabbccdd}),
            OidArrayConverter.Write, OidArrayConverter.Write, OidArrayConverter.Read, OidArrayConverter.Read,
            OidArrayConverter.Read, OidArrayConverter.Read);
        Check<long, MoneyCodec>(Values(count, new[] {long.MinValue, long.MaxValue, -1, 0, 1, 0x123456789abcdef, -987654321012345}),
            MoneyArrayConverter.Write, MoneyArrayConverter.Write, MoneyArrayConverter.Read, MoneyArrayConverter.Read,
            MoneyArrayConverter.Read, MoneyArrayConverter.Read);
        // Include both signs of zero, subnormals, infinities, signaling/quiet NaNs with payloads.
        Check<float, Float32Codec>(Values(count, new uint[]
            {
                0, 0x80000000, 1, 0x807fffff, 0x7f800000, 0xff800000,
                0x7f812345, 0xffc12345, 0x3fc00000, 0xc0200000
            }.Select(BitConverter.UInt32BitsToSingle).ToArray()),
            Float32ArrayConverter.Write, Float32ArrayConverter.Write, Float32ArrayConverter.Read, Float32ArrayConverter.Read,
            Float32ArrayConverter.Read, Float32ArrayConverter.Read);
        Check<double, Float64Codec>(Values(count, new ulong[]
            {
                0, 0x8000000000000000, 1, 0x800fffffffffffff,
                0x7ff0000000000000, 0xfff0000000000000, 0x7ff0123456789abc, 0xfff8123456789abc,
                0x3ff8000000000000, 0xc004000000000000
            }.Select(BitConverter.UInt64BitsToDouble).ToArray()),
            Float64ArrayConverter.Write, Float64ArrayConverter.Write, Float64ArrayConverter.Read, Float64ArrayConverter.Read,
            Float64ArrayConverter.Read, Float64ArrayConverter.Read);
    }

    private static T[] Values<T>(int count, T[] pattern)
    {
        return Enumerable.Range(0, count).Select(i => pattern[i % pattern.Length]).ToArray();
    }

    private static void Check<T, TCodec>(T[] values, ConverterAssertions.ArrayWrite<T> write,
        Action<ReadOnlyMemory<T>, IBufferWriter<byte>> writeBuffered,
        ConverterAssertions.ArrayRead<T> read, Func<ReadOnlySequence<byte>, ReadOnlyMemory<T>> readSequence,
        ConverterAssertions.ArrayReadInto<T> readInto, ConverterAssertions.SequenceReadInto<T> readSequenceInto)
        where T : unmanaged where TCodec : struct, IBinaryCodec<T>
    {
        var elements = values.Select(value =>
        {
            var bytes = new byte[TCodec.FixedSize];
            TCodec.Write(value, bytes);
            return bytes;
        }).ToArray();
        var expected = ConverterAssertions.ArrayBytes(TCodec.Oid, elements);
        var padded = new T[values.Length + 3];
        values.CopyTo(padded, 1);
        // Offsets 1..16 exercise unaligned vector loads/stores and untouched sentinels.
        for (var offset = 1; offset <= 16; offset++)
        {
            var bytes = Enumerable.Repeat((byte)0xcc, expected.Length + offset + 16).ToArray();
            Assert.Equal(expected.Length, write(padded.AsMemory(1, values.Length), bytes.AsSpan(offset)));
            Assert.Equal(expected, bytes.AsSpan(offset, expected.Length).ToArray());
            Assert.All(bytes[..offset].Concat(bytes[(offset + expected.Length)..]), b => Assert.Equal((byte)0xcc, b));
            var output = new T[values.Length + 3];
            Assert.Equal(values.Length, readInto(bytes.AsSpan(offset, expected.Length), output.AsSpan(1)));
            Assert.Equal(MemoryMarshal.AsBytes(values.AsSpan()).ToArray(),
                MemoryMarshal.AsBytes(output.AsSpan(1, values.Length)).ToArray());
        }
        var measured = new byte[expected.Length];
        Assert.Equal(expected.Length, BinaryArray<T, TCodec>.WriteMeasured(values, measured));
        Assert.Equal(expected, measured);
        var writer = new ConverterAssertions.RecordingWriter(expected.Length);
        writeBuffered(values, writer);
        Assert.Equal(1, writer.Reservations);
        Assert.Equal(expected.Length, writer.SizeHint);
        Assert.Equal(expected.Length, writer.Advanced);
        Assert.Equal(expected, writer.Bytes[..expected.Length]);
        Assert.Equal(MemoryMarshal.AsBytes(values.AsSpan()).ToArray(), MemoryMarshal.AsBytes(read(expected).Span).ToArray());

        var splits = values.Length <= 33 ? Enumerable.Range(0, expected.Length + 1) : new[] {1, 19, 20, 21, 4096};
        foreach (var split in splits)
        {
            if (split > expected.Length)
            {
                continue;
            }
            var input = TestWire.Chunks(ReadOnlyMemory<byte>.Empty, expected.AsMemory(0, split),
                ReadOnlyMemory<byte>.Empty, expected.AsMemory(split), ReadOnlyMemory<byte>.Empty);
            var scratch = new T[values.Length + 1];
            Assert.Equal(values.Length, readSequenceInto(input, scratch));
            Assert.Equal(MemoryMarshal.AsBytes(values.AsSpan()).ToArray(), MemoryMarshal.AsBytes(scratch.AsSpan(0, values.Length)).ToArray());
            Assert.Equal(MemoryMarshal.AsBytes(values.AsSpan()).ToArray(), MemoryMarshal.AsBytes(readSequence(input).Span).ToArray());
        }
        Assert.Equal(values.Length, readSequenceInto(TestWire.ByteSegments(expected), new T[values.Length]));

        // Reusable span and sequence paths allocate no arrays or intermediate payloads.
        var sequence = TestWire.Chunks(expected.AsMemory(0, Math.Min(21, expected.Length)),
            expected.AsMemory(Math.Min(21, expected.Length)));
        var reusable = new T[values.Length];
        for (var i = 0; i < 128; i++)
        {
            write(values, measured);
            readInto(expected, reusable);
            readSequenceInto(sequence, reusable);
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 128; i++)
        {
            write(values, measured);
            readInto(expected, reusable);
            readSequenceInto(sequence, reusable);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        // Capacity failures occur before vector stores and leave output untouched.
        var unchanged = Enumerable.Repeat((byte)0xcc, expected.Length).ToArray();
        Assert.Throws<ArgumentException>(() => write(values, unchanged.AsSpan(0, expected.Length - 1)));
        Assert.All(unchanged, b => Assert.Equal((byte)0xcc, b));
        if (values.Length == 0)
        {
            return;
        }
        var small = values[..^1];
        var old = MemoryMarshal.AsBytes(small.AsSpan()).ToArray();
        Assert.Throws<ArgumentException>(() => readInto(expected, small));
        Assert.Throws<ArgumentException>(() => readSequenceInto(TestWire.ByteSegments(expected), small));
        Assert.Equal(old, MemoryMarshal.AsBytes(small.AsSpan()).ToArray());

        // Alias checks happen before the array header or any decoded block is written.
        var storage = new byte[expected.Length + 64];
        using var alias = new ByteMemory<T>(storage);
        values.CopyTo(alias.Memory.Span);
        var original = storage.ToArray();
        Assert.Throws<ArgumentException>(() => write(alias.Memory[..values.Length], storage));
        Assert.Equal(original, storage);
        expected.CopyTo(storage, 0);
        original = storage.ToArray();
        Assert.Throws<ArgumentException>(() => readInto(storage.AsSpan(0, expected.Length), alias.Memory.Span));
        Assert.Throws<ArgumentException>(() => readSequenceInto(
            TestWire.Chunks(storage.AsMemory(0, 19), storage.AsMemory(19, expected.Length - 19)), alias.Memory.Span));
        Assert.Equal(original, storage);

        var lowerBound = expected.ToArray();
        BinaryPrimitives.WriteInt32BigEndian(lowerBound.AsSpan(16), -9);
        Assert.Equal(MemoryMarshal.AsBytes(values.AsSpan()).ToArray(), MemoryMarshal.AsBytes(read(lowerBound).Span).ToArray());
        var flags = expected.ToArray();
        BinaryPrimitives.WriteInt32BigEndian(flags.AsSpan(4), 1);
        Assert.Equal(MemoryMarshal.AsBytes(values.AsSpan()).ToArray(), MemoryMarshal.AsBytes(read(flags).Span).ToArray());

        // Every prefix in vector blocks and the scalar tail must be checked.
        if (values.Length <= 33)
        {
            for (var i = 0; i < values.Length; i++)
            {
                foreach (var invalid in new[] {-2, -1, 0, TCodec.FixedSize - 1, TCodec.FixedSize + 1, int.MaxValue})
                {
                    var bad = expected.ToArray();
                    BinaryPrimitives.WriteInt32BigEndian(bad.AsSpan(20 + i * (4 + TCodec.FixedSize)), invalid);
                    var error = invalid == -1 ? typeof(NotSupportedException) : typeof(InvalidDataException);
                    Assert.Throws(error, () => read(bad));
                    Assert.Throws(error, () => readInto(bad, new T[values.Length]));
                    Assert.Throws(error, () => readSequence(TestWire.Chunks(bad.AsMemory(0, 19), bad.AsMemory(19))));
                    Assert.Throws(error, () => readSequenceInto(TestWire.ByteSegments(bad), new T[values.Length]));
                }
            }
        }

        // Check the vector-sized payload inside a complete Extended Query message.
        var bind = FrontendMessage.Bind(parameters: new ReadOnlyMemory<byte>?[] {expected},
            parameterFormats: new[] {FormatCode.Binary});
        var packet = new byte[FrontendMessageWriter.GetByteCount(in bind)];
        FrontendMessageWriter.Write(in bind, packet);
        Assert.Equal((byte)'B', packet[0]);
        Assert.Equal(packet.Length - 1, BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(1)));
        Assert.Equal(expected, packet.AsSpan(17, expected.Length).ToArray());
    }

    private sealed class ByteMemory<T>(byte[] bytes) : MemoryManager<T> where T : unmanaged
    {
        public override Span<T> GetSpan()
        {
            return MemoryMarshal.Cast<byte, T>(bytes.AsSpan());
        }
        public override MemoryHandle Pin(int elementIndex = 0)
        {
            throw new NotSupportedException();
        }
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }
}