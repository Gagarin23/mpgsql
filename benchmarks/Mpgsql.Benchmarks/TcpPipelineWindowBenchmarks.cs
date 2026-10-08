using BenchmarkDotNet.Attributes;
using Mpgsql.Benchmarks.Comparison;
using Mpgsql.Benchmarks.Queries;

namespace Mpgsql.Benchmarks;

// Only Mpgsql enforces InFlight. Native Npgsql remains unconstrained by this setting.
// These cases diagnose admission policy; they do not replace the fixed-window baseline.
[MemoryDiagnoser, JsonExporterAttribute.Full]
[Config(typeof(QueryBenchmarkConfig)), InvocationCount(1)]
public class TcpPipelineWindowBenchmarks
{
    [Params(1, 4)] public int Connections { get; set; } = 1;
    [Params(8, 16, 32, 64)] public int InFlight { get; set; } = 8;
    private TcpComparisonFixture _fixture = null!;

    private async Task Setup(ComparisonDriver driver) => _fixture = await TcpComparisonFixture.CreateAsync(driver,
        QueryLoadProfile.Find($"C64_P{Connections}_W{InFlight}"));

    [GlobalSetup(Target = nameof(MpgsqlDataSource))] public Task SetupMpgsql() => Setup(ComparisonDriver.Mpgsql);
    [GlobalSetup(Target = nameof(NpgsqlPool))] public Task SetupPool() => Setup(ComparisonDriver.NpgsqlPool);
    [GlobalSetup(Target = nameof(NpgsqlMultiplexed))] public Task SetupMultiplexed() => Setup(ComparisonDriver.NpgsqlMultiplexed);
    [Benchmark(Baseline = true, OperationsPerInvoke = 256)] public Task<long> MpgsqlDataSource() => FixedWorkers();
    [Benchmark(OperationsPerInvoke = 256)] public Task<long> NpgsqlPool() => FixedWorkers();
    [Benchmark(OperationsPerInvoke = 256)] public Task<long> NpgsqlMultiplexed() => FixedWorkers();

    private async Task<long> FixedWorkers()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = new Task<long>[64];
        for (int worker = 0; worker < workers.Length; worker++) workers[worker] = WorkAsync(worker);
        gate.SetResult();
        var values = await Task.WhenAll(workers).ConfigureAwait(false);
        long sum = 0;
        foreach (long value in values) sum += value;
        return sum;

        async Task<long> WorkAsync(int worker)
        {
            await gate.Task.ConfigureAwait(false);
            long checksum = 0;
            for (int i = worker; i < 256; i += 64)
                checksum += await _fixture.ReadAsync(worker, false).ConfigureAwait(false);
            return checksum;
        }
    }

    [GlobalCleanup] public async Task Cleanup() { _fixture.CheckIdle(); await _fixture.DisposeAsync(); }

    internal static async Task VerifyAsync()
    {
        foreach (int connections in new[] { 1, 4 })
        foreach (int window in new[] { 8, 16, 32, 64 })
        foreach (var driver in Enum.GetValues<ComparisonDriver>())
        {
            var benchmark = new TcpPipelineWindowBenchmarks { Connections = connections, InFlight = window };
            try
            {
                await benchmark.Setup(driver).ConfigureAwait(false);
                var before = benchmark._fixture.Peer.Counters();
                for (int repeat = 0; repeat < 2; repeat++)
                    TcpComparisonVerification.Check(await benchmark.FixedWorkers().ConfigureAwait(false) == 8320,
                        "Window sweep worker identities/checksum");
                var after = benchmark._fixture.Peer.Counters();
                TcpComparisonVerification.Check(after.Queries - before.Queries == 512 && after.Syncs - before.Syncs == 512,
                    "Window sweep independent Sync boundaries");
                benchmark._fixture.CheckIdle();
                TcpComparisonVerification.Check(await benchmark._fixture.ReadAsync(0, false).ConfigureAwait(false) == 1,
                    "Window sweep following request");
            }
            finally { await benchmark.Cleanup().ConfigureAwait(false); }
        }
        Console.WriteLine("PASS TCP window sweep: 64 workers, 1/4 connections, windows 8/16/32/64; values, Sync boundaries, idle buffers and following request.");
    }
}
