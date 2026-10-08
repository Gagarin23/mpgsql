using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using Perfolizer.Horology;

namespace Mpgsql.Benchmarks.Converters;

public sealed class ConverterBenchmarkConfig : ManualConfig
{
    public ConverterBenchmarkConfig()
    {
        AddJob(Job.Default.WithWarmupCount(3).WithIterationCount(8)
            .WithIterationTime(TimeInterval.FromMilliseconds(150))
            .DontEnforcePowerPlan().WithId("Converters"));
    }
}

[MemoryDiagnoser, Config(typeof(ConverterBenchmarkConfig)), GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory), CategoriesColumn]
public class ConverterScalarBenchmarks
{
    private ConverterCase _case = null!;
    public IEnumerable<string> Cases => ConverterCatalog.Ids(ConverterShape.Scalar);
    [ParamsSource(nameof(Cases))]
    public string Case { get; set; } = null!;
    [GlobalSetup]
    public void Setup()
    {
        _case = ConverterCatalog.Get(Case).Create(1, 0);
    }
    [GlobalCleanup]
    public void Cleanup()
    {
        _case.Dispose();
    }
    [Benchmark(Baseline = true), BenchmarkCategory("Write")]
    public int NpgsqlWrite()
    {
        return _case.NpgsqlWrite();
    }
    [Benchmark, BenchmarkCategory("Write")]
    public int MpgsqlWrite()
    {
        return _case.MpgsqlWrite();
    }
    [Benchmark(Baseline = true), BenchmarkCategory("Read")]
    public int NpgsqlRead()
    {
        return _case.NpgsqlRead();
    }
    [Benchmark, BenchmarkCategory("Read")]
    public int MpgsqlRead()
    {
        return _case.MpgsqlRead();
    }
}

[MemoryDiagnoser, Config(typeof(ConverterBenchmarkConfig)), GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory), CategoriesColumn]
public class ConverterArrayBenchmarks
{
    private ConverterCase _case = null!;
    public IEnumerable<string> Cases => ConverterCatalog.Ids(ConverterShape.Array);
    [ParamsSource(nameof(Cases))]
    public string Case { get; set; } = null!;
    public IEnumerable<int> Counts => ArrayCounts();
    [ParamsSource(nameof(Counts))]
    public int Count { get; set; }
    [GlobalSetup]
    public void Setup()
    {
        _case = ConverterCatalog.Get(Case).Create(Count, 0);
    }
    [GlobalCleanup]
    public void Cleanup()
    {
        _case.Dispose();
    }
    [Benchmark(Baseline = true), BenchmarkCategory("Write")]
    public int NpgsqlWrite()
    {
        return _case.NpgsqlWrite();
    }
    [Benchmark, BenchmarkCategory("Write")]
    public int MpgsqlWrite()
    {
        return _case.MpgsqlWrite();
    }
    [Benchmark(Baseline = true), BenchmarkCategory("Read")]
    public int NpgsqlRead()
    {
        return _case.NpgsqlRead();
    }
    [Benchmark, BenchmarkCategory("Read")]
    public int MpgsqlRead()
    {
        return _case.MpgsqlRead();
    }

    internal static int[] ArrayCounts()
    {
        var setting = Environment.GetEnvironmentVariable("MPGSQL_BENCHMARK_ARRAY_COUNTS");
        var counts = setting is null ? [256] : setting.Split(',').Select(int.Parse).Distinct().ToArray();
        if (counts.Length == 0 || counts.Any(c => c < 0))
        {
            throw new ArgumentException("Invalid array benchmark counts.");
        }
        return counts;
    }
}

[MemoryDiagnoser, Config(typeof(ConverterBenchmarkConfig)), GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory), CategoriesColumn]
public class ConverterNullableArrayBenchmarks
{
    private ConverterCase _case = null!;
    public IEnumerable<string> Cases => ConverterCatalog.Ids(ConverterShape.NullableArray);
    [ParamsSource(nameof(Cases))]
    public string Case { get; set; } = null!;
    public IEnumerable<int> Counts => ConverterArrayBenchmarks.ArrayCounts();
    [ParamsSource(nameof(Counts))]
    public int Count { get; set; }
    [GlobalSetup]
    public void Setup()
    {
        _case = ConverterCatalog.Get(Case).Create(Count, 8);
    }
    [GlobalCleanup]
    public void Cleanup()
    {
        _case.Dispose();
    }
    [Benchmark(Baseline = true), BenchmarkCategory("Write")]
    public int NpgsqlWrite()
    {
        return _case.NpgsqlWrite();
    }
    [Benchmark, BenchmarkCategory("Write")]
    public int MpgsqlWrite()
    {
        return _case.MpgsqlWrite();
    }
    [Benchmark(Baseline = true), BenchmarkCategory("Read")]
    public int NpgsqlRead()
    {
        return _case.NpgsqlRead();
    }
    [Benchmark, BenchmarkCategory("Read")]
    public int MpgsqlRead()
    {
        return _case.MpgsqlRead();
    }
}