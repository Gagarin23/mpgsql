using BenchmarkDotNet.Attributes;
using Mpgsql.Benchmarks.Comparison;
using Mpgsql.Benchmarks.Queries;

namespace Mpgsql.Benchmarks;

// Policy experiment: the opt-in shared mode has different transaction/error semantics.
// The native independent reference remains visible; the separate cohort benchmark has matching Sync boundaries.
[MemoryDiagnoser, JsonExporterAttribute.Full, Config(typeof(QueryBenchmarkConfig)), InvocationCount(1)]
public class TcpSharedSyncBenchmarks
{
    private TcpComparisonFixture _fixture = null!;
    private QueryLoadProfile _profile = null!;
    [Params("C1_P1_W1", "C8_P1_W8", "C64_P1_W8", "C64_P4_W8", "Mixed_C64_P1_W8", "Mixed_C64_P4_W8")]
    public string Profile { get; set; } = "C1_P1_W1";

    private async Task Setup(ComparisonDriver driver, int size)
    {
        _profile = QueryLoadProfile.Find(Profile);
        _fixture = await TcpComparisonFixture.CreateAsync(driver, _profile, size);
    }
    [GlobalSetup(Target = nameof(MpgsqlIndependent))]
    public Task SetupIndependent()
    {
        return Setup(ComparisonDriver.Mpgsql, 1);
    }
    [GlobalSetup(Target = nameof(MpgsqlSharedN8X1))]
    public Task SetupShared()
    {
        return Setup(ComparisonDriver.Mpgsql, 8);
    }
    [GlobalSetup(Target = nameof(NpgsqlNativeIndependent))]
    public Task SetupNative()
    {
        return Setup(ComparisonDriver.NpgsqlMultiplexed, 1);
    }
    [Benchmark(Baseline = true, OperationsPerInvoke = 256)]
    public Task<long> MpgsqlIndependent()
    {
        return Workers();
    }
    [Benchmark(OperationsPerInvoke = 256)]
    public Task<long> MpgsqlSharedN8X1()
    {
        return Workers();
    }
    [Benchmark(OperationsPerInvoke = 256)]
    public Task<long> NpgsqlNativeIndependent()
    {
        return Workers();
    }
    private async Task<long> Workers()
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

    internal static async Task VerifyAsync()
    {
        foreach (var profile in QueryLoadProfile.All)
        {
            await using var fixture = await TcpComparisonFixture.CreateAsync(ComparisonDriver.Mpgsql, profile, 8);
            var before = fixture.Peer.Counters();
            var workers = new Task[profile.Callers];
            for (var worker = 0;
                 worker < workers.Length;
                 worker++)
            {
                workers[worker] = Work(worker);
            }
            await Task
                .WhenAll(workers)
                .WaitAsync(TimeSpan.FromSeconds(60));
            var after = fixture.Peer.Counters();
            var requests = profile.Callers * 2;
            TcpComparisonVerification.Check
            (
                after.Queries - before.Queries == requests
                && after.Syncs - before.Syncs >= (requests + 7) / 8 && after.Syncs - before.Syncs <= requests,
                "Shared profile query/Sync counts"
            );
            fixture.CheckIdle();
            TcpComparisonVerification.Check(await fixture.ReadAsync(0, false) == 1, "Shared profile following probe");
            fixture.CheckIdle();
            Console.WriteLine("PASS TCP shared Sync N8/X1 " + profile.Name);

            async Task Work(int worker)
            {
                var slow = profile.Mixed && worker % 8 == 0;
                var scenario = slow ? QueryScenario.Slow : QueryScenario.One;
                for (var repeat = 0;
                     repeat < 2;
                     repeat++)
                {
                    await using var reader = await fixture.Mpgsql!.Source.ExecuteReaderAsync
                    (
                        scenario.Sql,
                        fixture
                            .Catalog.Inputs[fixture.Catalog.Index(scenario)][worker]
                    );
                    TcpComparisonVerification.Check(reader.QueryIndex == 0, "Shared logical QueryIndex");
                    var sum = await TcpQueryOperations.ConsumeAsync(reader, scenario, fixture.Mpgsql.Buffers[worker]);
                    TcpComparisonVerification.Check(sum == scenario.Expected(worker), "Shared caller identity/row count");
                }
            }
        }
    }
}