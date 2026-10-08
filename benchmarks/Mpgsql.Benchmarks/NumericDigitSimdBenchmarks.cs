using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Mpgsql.Converters;
using Mpgsql.Types;

namespace Mpgsql.Benchmarks;

[MemoryDiagnoser]
[WarmupCount(3)]
[IterationCount(6)]
[IterationTime(100)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class NumericDigitSimdBenchmarks
{
    [Params(7, 8, 256, 4096)]
    public int Count { get; set; }

    private PgNumeric _value;
    private ushort[] _scratch = [];
    private byte[] _payload = [];

    [GlobalSetup]
    public void Setup()
    {
        _value = new PgNumeric(1, 37, PgNumericSign.Negative,
            Enumerable.Range(0, Count).Select(i => (ushort)(i * 7919 % 10000)).ToArray());
        _scratch = new ushort[Count];
        _payload = new byte[NumericConverter.GetByteCount(_value)];
        WriteScalar();
        byte[] reference = _payload.ToArray();
        WriteSimd();
        if (!reference.AsSpan().SequenceEqual(_payload)) throw new InvalidDataException("Numeric digit encoder mismatch.");
        ReadScalar();
        if (!_value.Digits.Span.SequenceEqual(_scratch)) throw new InvalidDataException("Numeric scalar decoder mismatch.");
        ReadSimd();
        if (!_value.Digits.Span.SequenceEqual(_scratch)) throw new InvalidDataException("Numeric SIMD decoder mismatch.");
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Write")]
    public int WriteScalar() => BinaryScalar<PgNumeric, ScalarNumericCodecBaseline>.Write(_value, _payload);

    [Benchmark, BenchmarkCategory("Write")]
    public int WriteSimd() => NumericConverter.Write(_value, _payload);

    [Benchmark(Baseline = true), BenchmarkCategory("Read")]
    public int ReadScalar() => ScalarNumericCodecBaseline.Read(_payload, _scratch.AsMemory()).Digits.Length;

    [Benchmark, BenchmarkCategory("Read")]
    public int ReadSimd() => NumericConverter.Read(_payload, _scratch.AsMemory()).Digits.Length;
}
