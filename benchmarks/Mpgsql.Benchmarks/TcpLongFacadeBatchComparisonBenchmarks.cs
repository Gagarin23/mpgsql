using BenchmarkDotNet.Attributes;

namespace Mpgsql.Benchmarks;

// Caller construction stays in iteration setup, identically for both fresh batch APIs.
[MemoryDiagnoser, JsonExporterAttribute.Full, Config(typeof(QueryBenchmarkConfig)), InvocationCount(1)]
public class TcpLongFacadeBatchComparisonBenchmarks
{
    internal const int GroupsPerIteration = 2048;

    private readonly TcpFacadeBatchComparisonBenchmarks _inner = new TcpFacadeBatchComparisonBenchmarks
    {
        PreparedGroupsPerIteration = GroupsPerIteration
    };

    [GlobalSetup(Target = nameof(MpgsqlFacadeBatch))]
    public Task SetupMpgsql()
    {
        return _inner.SetupMpgsql();
    }

    [GlobalSetup(Target = nameof(NpgsqlFreshBatch))]
    public Task SetupNpgsql()
    {
        return _inner.SetupNpgsql();
    }

    [IterationSetup(Target = nameof(MpgsqlFacadeBatch))]
    public void PrepareMpgsql()
    {
        _inner.PrepareMpgsql();
    }

    [IterationSetup(Target = nameof(NpgsqlFreshBatch))]
    public void PrepareNpgsql()
    {
        _inner.PrepareNpgsql();
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = GroupsPerIteration)]
    public Task<long> MpgsqlFacadeBatch()
    {
        return _inner.MpgsqlFacadeBatch();
    }

    [Benchmark(OperationsPerInvoke = GroupsPerIteration)]
    public Task<long> NpgsqlFreshBatch()
    {
        return _inner.NpgsqlFreshBatch();
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
            var benchmark = new TcpLongFacadeBatchComparisonBenchmarks();
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
                for (var repeat = 0; repeat < 2; repeat++)
                {
                    if (native)
                    {
                        benchmark.PrepareNpgsql();
                    }
                    else
                    {
                        benchmark.PrepareMpgsql();
                    }
                    var before = benchmark._inner.Peer.Counters();
                    Check(await (native ? benchmark.NpgsqlFreshBatch() : benchmark.MpgsqlFacadeBatch())
                          == 136L * GroupsPerIteration, "Long fresh Batch16 checksum");
                    var after = benchmark._inner.Peer.Counters();
                    Check(after.Queries - before.Queries == 16 * GroupsPerIteration
                          && after.Syncs - before.Syncs == GroupsPerIteration, "Long fresh Batch16 boundaries");
                    benchmark._inner.CheckIdle();
                }
            }
            finally { await benchmark.Cleanup(); }
        }
        Console.WriteLine("PASS long fresh Batch16: fresh groups, checksum, query/Sync counts and idle sessions.");
    }

    private static void Check(bool value, string message)
    {
        if (!value)
        {
            throw new InvalidOperationException(message);
        }
    }
}