using System.Buffers;
using System.Buffers.Binary;
using Mpgsql.Converters;
using Mpgsql.Protocol;
using Mpgsql.Tests.Protocol;

namespace Mpgsql.Tests.Converters;

internal static class ConverterAssertions
{
    internal delegate int ScalarWrite<T>(T value, Span<byte> output);

    internal delegate T ScalarRead<T>(ReadOnlySpan<byte> input);

    internal delegate int ArrayWrite<T>(ReadOnlyMemory<T> value, Span<byte> output);

    internal delegate ReadOnlyMemory<T> ArrayRead<T>(ReadOnlySpan<byte> input);

    internal delegate int ArrayReadInto<T>(ReadOnlySpan<byte> input, Span<T> output);

    internal delegate int SequenceReadInto<T>(ReadOnlySequence<byte> input, Span<T> output);

    internal static void CheckScalar<T>(T value, string hex,
        Func<T, int> size,
        ScalarWrite<T> write, Action<T, IBufferWriter<byte>> writeBuffered,
        ScalarRead<T> read, Func<ReadOnlySequence<byte>, T> readSequence)
    {
        byte[] expected = TestWire.Bytes(hex);
        Assert.Equal(expected.Length, size(value));
        byte[] output = Enumerable.Repeat((byte)0xcc, expected.Length + 10).ToArray();
        Assert.Equal(expected.Length, write(value, output.AsSpan(3)));
        Assert.Equal(expected, output.AsSpan(3, expected.Length).ToArray());
        Assert.All(output[..3].Concat(output[(3 + expected.Length)..]), b => Assert.Equal((byte)0xcc, b));
        if (expected.Length != 0)
        {
            Assert.Throws<ArgumentException>(() => write(value, output.AsSpan(0, expected.Length - 1)));
            Assert.Equal(expected, output.AsSpan(3, expected.Length).ToArray());
        }
        var writer = new RecordingWriter(expected.Length);
        writeBuffered(value, writer);
        Assert.Equal(expected, writer.Bytes.AsSpan(0, expected.Length).ToArray());
        Assert.Equal(expected.Length, writer.SizeHint);
        Assert.Equal(expected.Length, writer.Advanced);
        Assert.Equal(expected.Length == 0 ? 0 : 1, writer.Reservations);
        Assert.All(writer.Bytes[expected.Length..], b => Assert.Equal((byte)0xcc, b));
        byte[] rewritten = new byte[expected.Length];
        Assert.Equal(expected.Length, write(read(expected), rewritten));
        Assert.Equal(expected, rewritten);
        for (int split = 0; split <= expected.Length; split++)
        {
            var input = TestWire.Chunks(ReadOnlyMemory<byte>.Empty, expected.AsMemory(0, split),
                ReadOnlyMemory<byte>.Empty, expected.AsMemory(split), ReadOnlyMemory<byte>.Empty);
            write(readSequence(input), rewritten);
            Assert.Equal(expected, rewritten);
        }
        write(readSequence(TestWire.ByteSegments(expected)), rewritten);
        Assert.Equal(expected, rewritten);
    }

    internal static void CheckNullableScalar<T>(ScalarWrite<T?> write, Action<T?, IBufferWriter<byte>> writeBuffered,
        Func<T?, int> size, Func<ReadOnlyMemory<byte>?, T?> readNullable,
        Func<ReadOnlySequence<byte>?, T?> readNullableSequence) where T : struct
    {
        byte[] bytes = Enumerable.Repeat((byte)0xcc, 32).ToArray();
        var writer = new RecordingWriter(32);
        Assert.Equal(0, size(null));
        Assert.Equal(0, write(null, bytes));
        writeBuffered(null, writer);
        Assert.Null(readNullable(null));
        Assert.Null(readNullableSequence(null));
        Assert.Equal(0, writer.Reservations);
        Assert.Equal(0, writer.Advanced);
        Assert.All(bytes, b => Assert.Equal((byte)0xcc, b));
    }

    internal static void CheckArray<T>(T[] values, uint oid,
        string elementHex, Func<ReadOnlyMemory<T>, int> size,
        ArrayWrite<T> write, Action<ReadOnlyMemory<T>, IBufferWriter<byte>> writeBuffered,
        ArrayRead<T> read, Func<ReadOnlySequence<byte>, ReadOnlyMemory<T>> readSequence,
        ArrayReadInto<T> readInto, SequenceReadInto<T> readSequenceInto)
    {
        byte[] element = TestWire.Bytes(elementHex);
        byte[] expected = ArrayBytes(oid, element, element);
        Assert.Equal(expected.Length, size(values));
        byte[] bytes = Enumerable.Repeat((byte)0xcc, expected.Length + 9).ToArray();
        Assert.Equal(expected.Length, write(values, bytes.AsSpan(3)));
        Assert.Equal(expected, bytes.AsSpan(3, expected.Length).ToArray());
        Assert.All(bytes[..3].Concat(bytes[(3 + expected.Length)..]), b => Assert.Equal((byte)0xcc, b));
        byte[] original = bytes.ToArray();
        Assert.Throws<ArgumentException>(() => write(values, bytes.AsSpan(0, expected.Length - 1)));
        Assert.Equal(original, bytes);
        var writer = new RecordingWriter(expected.Length);
        writeBuffered(values, writer);
        Assert.Equal(1, writer.Reservations);
        Assert.Equal(expected.Length, writer.Advanced);
        Assert.Equal(expected.Length, writer.SizeHint);
        Assert.Equal(expected, writer.Bytes[..expected.Length]);
        byte[] rewritten = new byte[expected.Length];
        write(read(expected), rewritten);
        Assert.Equal(expected, rewritten);
        T[] scratch = new T[values.Length + 1];
        Assert.Equal(values.Length, readInto(expected, scratch));
        write(scratch.AsMemory(0, values.Length), rewritten);
        Assert.Equal(expected, rewritten);
        for (int split = 0; split <= expected.Length; split++)
        {
            var input = TestWire.Chunks(ReadOnlyMemory<byte>.Empty, expected.AsMemory(0, split), expected.AsMemory(split));
            write(readSequence(input), rewritten);
            Assert.Equal(expected, rewritten);
            Assert.Equal(values.Length, readSequenceInto(input, scratch));
            write(scratch.AsMemory(0, values.Length), rewritten);
            Assert.Equal(expected, rewritten);
        }
        var fragmented = TestWire.ByteSegments(expected);
        Assert.Equal(values.Length, readSequenceInto(fragmented, scratch));
        write(scratch.AsMemory(0, values.Length), rewritten);
        Assert.Equal(expected, rewritten);

        // Empty arrays use ndim=0, never a SQL NULL or a fake singleton dimension.
        byte[] empty = ArrayBytes(oid);
        Assert.Equal(12, size(ReadOnlyMemory<T>.Empty));
        Assert.Equal(12, write(ReadOnlyMemory<T>.Empty, rewritten));
        Assert.Equal(empty, rewritten[..12]);
        Assert.True(read(empty).IsEmpty);
        Assert.True(readSequence(TestWire.ByteSegments(empty)).IsEmpty);
        byte[] bounded = expected.ToArray();
        BinaryPrimitives.WriteInt32BigEndian(bounded.AsSpan(16), -3);
        write(read(bounded), rewritten);
        Assert.Equal(expected, rewritten);

        // Exact framing, allocation bounds, wrong OID, negative count, and dimensionality.
        foreach (byte[] bad in MalformedArrays(expected))
        {
            Assert.Throws<InvalidDataException>(() => read(bad));
            Assert.Throws<InvalidDataException>(() => readSequence(TestWire.ByteSegments(bad)));
        }
        var multi = expected.ToArray();
        BinaryPrimitives.WriteInt32BigEndian(multi, 2);
        Assert.Throws<NotSupportedException>(() => read(multi));
        Assert.Throws<ArgumentException>(() => readInto(expected, new T[1]));
        Assert.Throws<ArgumentException>(() => readSequenceInto(fragmented, new T[1]));

        // Verify converter payloads inside complete binary Bind and DataRow frames.
        var bind = FrontendMessage.Bind(parameters: new ReadOnlyMemory<byte>?[] {expected},
            parameterFormats: new[] {FormatCode.Binary}, resultFormats: new[] {FormatCode.Binary});
        byte[] packet = new byte[FrontendMessageWriter.GetByteCount(in bind)];
        FrontendMessageWriter.Write(in bind, packet);
        byte[] expectedBind = new byte[17 + expected.Length];
        expectedBind[0] = (byte)'B';
        BinaryPrimitives.WriteInt32BigEndian(expectedBind.AsSpan(1), expectedBind.Length - 1);
        BinaryPrimitives.WriteInt16BigEndian(expectedBind.AsSpan(7), 1);
        BinaryPrimitives.WriteInt16BigEndian(expectedBind.AsSpan(9), 1);
        BinaryPrimitives.WriteInt16BigEndian(expectedBind.AsSpan(11), 1);
        BinaryPrimitives.WriteInt32BigEndian(expectedBind.AsSpan(13), expected.Length);
        // Result format count and its value follow the payload.
        Array.Resize(ref expectedBind, expectedBind.Length + 4);
        BinaryPrimitives.WriteInt32BigEndian(expectedBind.AsSpan(1), expectedBind.Length - 1);
        expected.CopyTo(expectedBind, 17);
        BinaryPrimitives.WriteInt16BigEndian(expectedBind.AsSpan(17 + expected.Length), 1);
        BinaryPrimitives.WriteInt16BigEndian(expectedBind.AsSpan(19 + expected.Length), 1);
        Assert.Equal(expectedBind, packet);
        byte[] row = new byte[11 + expected.Length];
        row[0] = (byte)'D';
        BinaryPrimitives.WriteInt32BigEndian(row.AsSpan(1), row.Length - 1);
        BinaryPrimitives.WriteInt16BigEndian(row.AsSpan(5), 1);
        BinaryPrimitives.WriteInt32BigEndian(row.AsSpan(7), expected.Length);
        expected.CopyTo(row, 11);
        var sequence = TestWire.ByteSegments(row);
        Assert.True(BackendMessageReader.TryRead(ref sequence, out var message));
        Assert.True(sequence.IsEmpty);
        var fields = message.GetDataRow().GetEnumerator();
        Assert.True(fields.MoveNext());
        var field = fields.Current!.Value;
        write(readSequence(field), rewritten);
        Assert.Equal(expected, rewritten);
    }

    internal static void CheckNullableArray<T, TCodec>(T value, string hex) where T : struct where TCodec : struct, IBinaryCodec<T>
    {
        byte[] expected = ArrayBytes(TCodec.Oid, TestWire.Bytes(hex), null, TestWire.Bytes(hex));
        T?[] values = [value, null, value];
        var output = new byte[expected.Length];
        BinaryNullableArray<T, TCodec>.Write(values, output);
        Assert.Equal(expected, output);
        var writer = new RecordingWriter(expected.Length);
        BinaryNullableArray<T, TCodec>.Write(values, writer);
        Assert.Equal(expected, writer.Bytes[..expected.Length]);
        T?[] scratch = [value, value, value, value];
        for (int split = 0; split <= expected.Length; split++)
        {
            var input = TestWire.Chunks(expected.AsMemory(0, split), expected.AsMemory(split));
            Assert.Equal(3, BinaryNullableArray<T, TCodec>.Read(input, scratch));
            Assert.Null(scratch[1]);
            Assert.NotNull(scratch[3]);
            BinaryNullableArray<T, TCodec>.Write(scratch.AsMemory(0, 3), output);
            Assert.Equal(expected, output);
        }
        // PostgreSQL accepts flags=0 even when prefixes encode NULLs.
        expected.AsSpan(4, 4).Clear();
        Assert.Null(BinaryNullableArray<T, TCodec>.Read(expected).Span[1]);
        Assert.Null(BinaryNullableArray<T, TCodec>.Read(TestWire.ByteSegments(expected)).Span[1]);
        byte[] allNull = ArrayBytes(TCodec.Oid, null, null);
        Assert.All(BinaryNullableArray<T, TCodec>.Read(TestWire.ByteSegments(allNull)).ToArray(), Assert.Null);
        Assert.Throws<NotSupportedException>(() => BinaryArray<T, TCodec>.Read(allNull));
    }

    internal static byte[] ArrayBytes(uint oid, params byte[]?[] elements)
    {
        int size = elements.Length == 0 ? 12 : 20 + elements.Sum(e => 4 + (e?.Length ?? 0));
        byte[] result = new byte[size];
        BinaryPrimitives.WriteInt32BigEndian(result, elements.Length == 0 ? 0 : 1);
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(4), elements.Any(e => e is null) ? 1 : 0);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(8), oid);
        if (elements.Length == 0)
        {
            return result;
        }
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(12), elements.Length);
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(16), 1);
        int offset = 20;
        foreach (byte[]? element in elements)
        {
            BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(offset), element?.Length ?? -1);
            offset += 4;
            if (element is null)
            {
                continue;
            }
            element.CopyTo(result, offset);
            offset += element.Length;
        }
        return result;
    }

    private static IEnumerable<byte[]> MalformedArrays(byte[] valid)
    {
        yield return valid[..11];
        yield return valid[..19];
        yield return valid[..^1];
        yield return [.. valid, 0];
        foreach (var (offset, number) in new[] {(0, -1), (0, 7), (4, 2), (8, 99999), (12, -1), (12, int.MaxValue), (16, int.MaxValue), (20, -2), (20, int.MaxValue)})
        {
            byte[] bad = valid.ToArray();
            BinaryPrimitives.WriteInt32BigEndian(bad.AsSpan(offset), number);
            yield return bad;
        }
    }

    internal sealed class RecordingWriter(int size) : IBufferWriter<byte>
    {
        internal byte[] Bytes { get; } = Enumerable.Repeat((byte)0xcc, size + 17).ToArray();
        internal int SizeHint { get; private set; }
        internal int Advanced { get; private set; }
        internal int Reservations { get; private set; }
        public void Advance(int count) => Advanced += count;
        public Memory<byte> GetMemory(int sizeHint = 0) => throw new InvalidOperationException("Use GetSpan.");
        public Span<byte> GetSpan(int sizeHint = 0)
        {
            SizeHint = sizeHint;
            Reservations++;
            return Bytes;
        }
    }
}
