using System.Buffers.Binary;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Mpgsql.Converters;

namespace Mpgsql.Benchmarks;

[MemoryDiagnoser, CategoriesColumn, GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class ValidationBenchmarks
{
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
    private byte[] _output = [];
    private byte[] _payload = [];
    private string?[] _values = [];

    [Params(32, 65536)]
    public int Length { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _payload = Utf8.GetBytes(new string('x', Length));
        _values = [new string('x', Length), "Я😀", null, "", new string('y', Length / 2)];
        _output = new byte[TextArrayConverter.GetByteCount(_values)];
        LegacyTextArrayWrite();
        var expected = _output.ToArray();
        TextArrayWrite();
        if (!expected
                .AsSpan()
                .SequenceEqual(_output))
        {
            throw new InvalidOperationException("Text array benchmark bytes differ.");
        }
    }

    [Benchmark(Baseline = true), BenchmarkCategory("BorrowedUtf8")]
    public int LegacyBorrowedUtf8()
    {
        ReadOnlySpan<byte> payload = _payload;
        if (payload.Contains((byte)0) || !System.Text.Unicode.Utf8.IsValid(payload))
        {
            throw new InvalidDataException();
        }
        return payload.Length + payload[0] + payload[^1];
    }

    [Benchmark, BenchmarkCategory("BorrowedUtf8")]
    public int BorrowedUtf8()
    {
        var payload = TextConverter.ReadUtf8(_payload);
        return payload.Length + payload[0] + payload[^1];
    }

    [Benchmark(Baseline = true), BenchmarkCategory("TextArrayWrite")]
    public int LegacyTextArrayWrite()
    {
        // Previous array encoder: validate/count every string for capacity, then do it again
        // for each element prefix before encoding. No temporary allocations in this baseline.
        var size = 20 + 4 * _values.Length;
        var hasNull = false;
        foreach (var value in _values)
        {
            if (value is null)
            {
                hasNull = true;
            }
            else
            {
                size += LegacyLength(value);
            }
        }
        if (_output.Length < size)
        {
            throw new ArgumentException();
        }
        BinaryPrimitives.WriteInt32BigEndian(_output, 1);
        BinaryPrimitives.WriteInt32BigEndian(_output.AsSpan(4), hasNull ? 1 : 0);
        BinaryPrimitives.WriteUInt32BigEndian(_output.AsSpan(8), TextConverter.TypeOid);
        BinaryPrimitives.WriteInt32BigEndian(_output.AsSpan(12), _values.Length);
        BinaryPrimitives.WriteInt32BigEndian(_output.AsSpan(16), 1);
        var offset = 20;
        foreach (var value in _values)
        {
            var length = value is null ? -1 : LegacyLength(value);
            BinaryPrimitives.WriteInt32BigEndian(_output.AsSpan(offset), length);
            offset += 4;
            if (value is null)
            {
                continue;
            }
            Utf8.GetBytes(value, _output.AsSpan(offset, length));
            offset += length;
        }
        return offset;
    }

    private static int LegacyLength(string value)
    {
        if (value
            .AsSpan()
            .Contains('\0'))
        {
            throw new ArgumentException();
        }
        return Utf8.GetByteCount(value);
    }

    [Benchmark, BenchmarkCategory("TextArrayWrite")]
    public int TextArrayWrite()
    {
        return TextArrayConverter.Write(_values, _output);
    }
}