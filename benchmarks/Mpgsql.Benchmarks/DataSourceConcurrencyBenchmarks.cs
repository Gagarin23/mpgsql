using BenchmarkDotNet.Attributes;
using Mpgsql.Benchmarks.Queries;

namespace Mpgsql.Benchmarks;

[MemoryDiagnoser, JsonExporterAttribute.Full]
[Config(typeof(QueryBenchmarkConfig)), InvocationCount(1)]
public class DataSourceConcurrencyBenchmarks
{
    [Params("C1_P1_W1", "C8_P1_W8", "C64_P1_W8", "C64_P4_W8", "Mixed_C64_P1_W8", "Mixed_C64_P4_W8")]
    public string Profile { get; set; } = "C1_P1_W1";
    private QueryLoadProfile _profile = null!;
    private QuerySourceFixture _fixture = null!;
    [GlobalSetup]
    public async Task Setup()
    {
        _profile = QueryLoadProfile.Find(Profile);
        _fixture = await QuerySourceFixture.CreateAsync(_profile.CreateCatalog(),
            _profile.Connections, _profile.InFlight, _profile.RowBudget);
        await FixedWorkers();
        _fixture.CheckIdle();
    }
    // BDN normalizes the whole closed-loop wave to one completed request.
    [Benchmark(OperationsPerInvoke = 256)]
    public async Task<long> FixedWorkers()
    {
        var workers = new Task<long>[_profile.Callers];
        for (int worker = 0; worker < workers.Length; worker++) workers[worker] = WorkAsync(worker);
        long[] checksums = await Task.WhenAll(workers).ConfigureAwait(false);
        long checksum = 0;
        foreach (long value in checksums) checksum += value;
        return checksum;
    }
    private async Task<long> WorkAsync(int worker)
    {
        bool slow = _profile.Mixed && worker % 8 == 0;
        var scenario = slow ? QueryScenario.Slow : QueryScenario.One;
        long checksum = 0;
        for (int i = worker; i < 256; i += _profile.Callers)
        {
            if (slow)
                checksum += await QueryOperations.SlowAsync(_fixture, worker).ConfigureAwait(false);
            else
                checksum += await QueryOperations.DataSourceAsync(_fixture, scenario, worker).ConfigureAwait(false);
        }
        return checksum;
    }
    [GlobalCleanup] public async Task Cleanup() => await _fixture.DisposeAsync();
}
