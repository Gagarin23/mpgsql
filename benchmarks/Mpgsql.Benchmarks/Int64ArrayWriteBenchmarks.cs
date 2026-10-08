using BenchmarkDotNet.Attributes;
using Mpgsql.Converters;

namespace Mpgsql.Benchmarks;

[MemoryDiagnoser, WarmupCount(3), IterationCount(8), IterationTime(150)]
public class Int64ArrayWriteBenchmarks
{
    private byte[] _destination = [];
    private ReadOnlyMemory<long> _value;
    [Params(1, 4, 8, 256, 4096, 65536)]
    public int Count { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var value = new long[Count];
        for (var i = 0; i < value.Length; i++)
        {
            value[i] = unchecked((long)(0x0123456789abcdefUL * (ulong)(i + 1)));
        }
        _value = value;
        _destination = new byte[Int64ArrayConverter.GetByteCount(_value)];
        Scalar();
        var expected = _destination.ToArray();
        Check(expected,
            FusedSimd());
        Check(expected,
            Packed());
        Check(expected,
            PackedFill());
        Check(expected,
            PooledPacked());
        Check(expected,
            PooledPackedFill());
    }

    private void Check(byte[] expected,
        int size)
    {
        if (size != expected.Length || !expected.AsSpan().SequenceEqual(_destination))
        {
            throw new InvalidOperationException("An array encoder differs from the scalar wire bytes.");
        }
    }

    public void CheckReusableAllocations()
    {
        long allocated = 0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            for (var i = 0; i < 32; i++)
            {
                FusedSimd();
            }
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0;
                 i < Math.Max(16,
                     4096
                     / Math.Max(1,
                         Count));
                 i++)
            {
                FusedSimd();
            }
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (allocated == 0)
            {
                return;
            }
        }
        throw new InvalidOperationException($"Writing {Count} elements allocated {allocated} bytes.");
    }

    [Benchmark(Baseline = true)]
    public int Scalar()
    {
        return Int64ArrayReference.WriteScalar(_value,
            _destination);
    }
    [Benchmark]
    public int FusedSimd()
    {
        return Int64ArrayConverter.Write(_value,
            _destination);
    }
    [Benchmark]
    public int Packed()
    {
        return Int64ArrayReference.WritePacked(_value,
            _destination,
            false);
    }
    [Benchmark]
    public int PackedFill()
    {
        return Int64ArrayReference.WritePacked(_value,
            _destination,
            true);
    }
    [Benchmark]
    public int PooledPacked()
    {
        return Int64ArrayReference.WritePooled(_value,
            _destination,
            false);
    }
    [Benchmark]
    public int PooledPackedFill()
    {
        return Int64ArrayReference.WritePooled(_value,
            _destination,
            true);
    }
}