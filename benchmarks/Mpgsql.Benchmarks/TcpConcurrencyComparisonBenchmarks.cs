using BenchmarkDotNet.Attributes;
using Mpgsql.Benchmarks.Comparison;
using Mpgsql.Benchmarks.Queries;

namespace Mpgsql.Benchmarks;

[MemoryDiagnoser, JsonExporterAttribute.Full]
[Config(typeof(QueryBenchmarkConfig)), InvocationCount(1)]
public class TcpConcurrencyComparisonBenchmarks
{
    [Params("C1_P1_W1", "C8_P1_W8", "C64_P1_W8", "C64_P4_W8", "Mixed_C64_P1_W8", "Mixed_C64_P4_W8")]
    public string Profile { get; set; } = "C1_P1_W1";
    private QueryLoadProfile _profile = null!;
    private TcpComparisonFixture _fixture = null!;
    private async Task Setup(ComparisonDriver driver)
    {
        _profile = QueryLoadProfile.Find(Profile); _fixture = await TcpComparisonFixture.CreateAsync(driver, _profile);
    }
    [GlobalSetup(Target = nameof(MpgsqlDataSource))] public Task SetupMpgsql() => Setup(ComparisonDriver.Mpgsql);
    [GlobalSetup(Target = nameof(NpgsqlPool))] public Task SetupPool() => Setup(ComparisonDriver.NpgsqlPool);
    [GlobalSetup(Target = nameof(NpgsqlMultiplexed))] public Task SetupMultiplexed() => Setup(ComparisonDriver.NpgsqlMultiplexed);
    [Benchmark(Baseline = true, OperationsPerInvoke = 256)] public Task<long> MpgsqlDataSource() => FixedWorkers();
    [Benchmark(OperationsPerInvoke = 256)] public Task<long> NpgsqlPool() => FixedWorkers();
    [Benchmark(OperationsPerInvoke = 256)] public Task<long> NpgsqlMultiplexed() => FixedWorkers();
    private async Task<long> FixedWorkers()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = new Task<long>[_profile.Callers];
        for (int worker = 0; worker < workers.Length; worker++) workers[worker] = WorkAsync(worker);
        gate.SetResult();
        var values = await Task.WhenAll(workers).ConfigureAwait(false);
        long sum = 0; foreach (long value in values) sum += value;
        return sum;
        async Task<long> WorkAsync(int worker)
        {
            bool slow = _profile.Mixed && worker % 8 == 0;
            long checksum = 0;
            await gate.Task.ConfigureAwait(false);
            for (int i = worker; i < 256; i += _profile.Callers) checksum += await _fixture.ReadAsync(worker, slow).ConfigureAwait(false);
            return checksum;
        }
    }
    [GlobalCleanup] public async Task Cleanup() { _fixture.CheckIdle(); await _fixture.DisposeAsync(); }
}
