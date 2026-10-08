using BenchmarkDotNet.Attributes;

namespace Mpgsql.Benchmarks;

// The same complete Batch16 operation, repeated long enough to observe a steady workload.
[MemoryDiagnoser, JsonExporterAttribute.Full, Config(typeof(QueryBenchmarkConfig)), InvocationCount(4096)]
public class TcpLongBatchComparisonBenchmarks
{
    private readonly TcpBatchComparisonBenchmarks _inner = new TcpBatchComparisonBenchmarks();

    [GlobalSetup(Target = nameof(MpgsqlSharedSync))]
    public Task SetupMpgsql()
    {
        return _inner.SetupMpgsql();
    }

    [GlobalSetup(Target = nameof(NpgsqlBatch))]
    public Task SetupNpgsql()
    {
        return _inner.SetupNpgsql();
    }

    [Benchmark(Baseline = true)]
    public Task<long> MpgsqlSharedSync()
    {
        return _inner.MpgsqlSharedSync();
    }

    [Benchmark]
    public Task<long> NpgsqlBatch()
    {
        return _inner.NpgsqlBatch();
    }

    [GlobalCleanup]
    public Task Cleanup()
    {
        return _inner.Cleanup();
    }

    internal static async Task VerifyAsync()
    {
        foreach (var native in new[] {false, true})
        {
            var benchmark = new TcpLongBatchComparisonBenchmarks();
            try
            {
                if (native)
                {
                    await benchmark.SetupNpgsql();
                }
                else
                {
                    await benchmark.SetupMpgsql();
                }
                for (var round = 0; round < 2; round++)
                {
                    var before = benchmark._inner.Peer.Counters();
                    for (var repeat = 0; repeat < 8; repeat++)
                    {
                        Check(await (native ? benchmark.NpgsqlBatch() : benchmark.MpgsqlSharedSync()) == 136,
                            "Long raw Batch16 checksum");
                    }
                    var after = benchmark._inner.Peer.Counters();
                    Check(after.Queries - before.Queries == 128 && after.Syncs - before.Syncs == 8,
                        "Long raw Batch16 repeated boundaries");
                    benchmark._inner.CheckIdle();
                }
            }
            finally { await benchmark.Cleanup(); }
        }
        Console.WriteLine("PASS long raw Batch16: repeated checksum, query/Sync counts and idle sessions.");
    }

    private static void Check(bool value, string message)
    {
        if (!value)
        {
            throw new InvalidOperationException(message);
        }
    }
}