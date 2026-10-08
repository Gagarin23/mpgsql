using BenchmarkDotNet.Attributes;
using Mpgsql.Benchmarks.Comparison;
using Mpgsql.Benchmarks.Queries;

namespace Mpgsql.Benchmarks;

[MemoryDiagnoser, JsonExporterAttribute.Full, Config(typeof(QueryBenchmarkConfig)), InvocationCount(1)]
public class TcpConcurrencyComparisonBenchmarks
{
    private TcpComparisonFixture _fixture = null!;
    private QueryLoadProfile _profile = null!;
    [Params("C1_P1_W1", "C8_P1_W8", "C64_P1_W8", "C64_P4_W8", "Mixed_C64_P1_W8", "Mixed_C64_P4_W8")]
    public string Profile { get; set; } = "C1_P1_W1";
    private async Task Setup(ComparisonDriver driver)
    {
        _profile = QueryLoadProfile.Find(Profile);
        _fixture = await TcpComparisonFixture.CreateAsync(driver, _profile);
    }
    [GlobalSetup(Target = nameof(MpgsqlDataSource))]
    public Task SetupMpgsql()
    {
        return Setup(ComparisonDriver.Mpgsql);
    }
    [GlobalSetup(Target = nameof(NpgsqlPool))]
    public Task SetupPool()
    {
        return Setup(ComparisonDriver.NpgsqlPool);
    }
    [GlobalSetup(Target = nameof(NpgsqlMultiplexed))]
    public Task SetupMultiplexed()
    {
        return Setup(ComparisonDriver.NpgsqlMultiplexed);
    }
    [Benchmark(Baseline = true, OperationsPerInvoke = 256)]
    public Task<long> MpgsqlDataSource()
    {
        return FixedWorkers();
    }
    [Benchmark(OperationsPerInvoke = 256)]
    public Task<long> NpgsqlPool()
    {
        return FixedWorkers();
    }
    [Benchmark(OperationsPerInvoke = 256)]
    public Task<long> NpgsqlMultiplexed()
    {
        return FixedWorkers();
    }
    private async Task<long> FixedWorkers()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = new Task<long>[_profile.Callers];
        for (var worker = 0;
             worker < workers.Length;
             worker++)
        {
            workers[worker] = WorkAsync(worker);
        }
        gate.SetResult();
        var values = await Task
            .WhenAll(workers)
            .ConfigureAwait(false);
        long sum = 0;
        foreach (var value in values)
        {
            sum += value;
        }
        return sum;

        async Task<long> WorkAsync(int worker)
        {
            var slow = _profile.Mixed && worker % 8 == 0;
            long checksum = 0;
            await gate.Task.ConfigureAwait(false);
            for (var i = worker;
                 i < 256;
                 i += _profile.Callers)
            {
                checksum += await _fixture
                    .ReadAsync(worker, slow)
                    .ConfigureAwait(false);
            }
            return checksum;
        }
    }
    [GlobalCleanup]
    public async Task Cleanup()
    {
        _fixture.CheckIdle();
        await _fixture.DisposeAsync();
    }
}