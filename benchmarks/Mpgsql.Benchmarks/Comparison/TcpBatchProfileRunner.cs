using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Mpgsql.Benchmarks.Comparison;

internal static class TcpBatchProfileRunner
{
    private const int GroupsPerCohort = TcpFacadeBatchComparisonBenchmarks.GroupsPerIteration;
    private const int QueriesPerGroup = TcpFacadeBatchComparisonBenchmarks.QueriesPerGroup;
    private const long CohortChecksum = GroupsPerCohort * QueriesPerGroup * (QueriesPerGroup + 1L) / 2;

    internal static async Task RunAsync(string[] args)
    {
        var driver = Value(args, "--driver", "mpgsql")!;
        var path = Value(args, "--batch-path", "facade")!;
        var kind = Value(args, "--profile-kind", "trace")!;
        var cohorts = Number(args, "--cohorts", kind == "memory" ? 128 : 1024);
        var warmups = Number(args, "--warmup-cohorts", 32);
        if (driver is not ("mpgsql" or "npgsql") || path is not ("raw" or "facade") || kind is not ("trace" or "memory" or "none"))
        {
            throw new ArgumentException("Expected mpgsql/npgsql, raw/facade and trace/memory/none.");
        }
        var control = new BatchProfilerControl(kind, Value(args, "--profiler-api", null));
        var directory = Path.GetFullPath(Value(args, "--artifacts", "artifacts/query-profile/batch")!);
        Directory.CreateDirectory(directory);
        var result = await ExecuteAsync(driver, path, cohorts, warmups, control, false)
            .ConfigureAwait(false);
        var metadata = new
        {
            Runtime = RuntimeInformation.FrameworkDescription,
            OS = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            GCSettings.IsServerGC,
            LatencyMode = GCSettings.LatencyMode.ToString(),
            Stopwatch.Frequency,
            Environment.ProcessorCount,
            Args = args,
            ProfilerFeatures = control.Features,
            Scenario = "16 independent bigint queries, 1 row each, one shared Sync, full consume and Dispose",
            Cohorts = cohorts,
            WarmupCohorts = warmups,
            GroupsPerCohort,
            QueriesPerGroup,
            AllocationScope = "Process-wide execution-interval counters: driver, peer, runner and profiler overhead; no subtraction",
            Interpretation = "Diagnostic run under profiler, not a performance baseline. Setup and caller preparation are outside collection windows."
        };
        File.WriteAllText
        (
            Path.Combine(directory, "runner.json"), JsonSerializer.Serialize
            (
                new
                {
                    Completed = true,
                    Metadata = metadata,
                    Result = result
                }, new JsonSerializerOptions
                {
                    WriteIndented = true
                }
            )
        );
        Console.WriteLine($"PASS profile {driver}/{path}: {result.Groups} groups, {result.Queries} queries, {result.Syncs} Syncs, checksum {result.Checksum}; profiler {control.Features}");
    }

    internal static async Task VerifyAsync()
    {
        foreach (var driver in new[]
                 {
                     "mpgsql",
                     "npgsql"
                 })
        foreach (var path in new[]
                 {
                     "raw",
                     "facade"
                 })
        {
            await ExecuteAsync(driver, path, 2, 1, new BatchProfilerControl("none", null), true)
                .ConfigureAwait(false);
            Console.WriteLine($"PASS profiling runner {driver}/{path}: checksums, query/Sync counts, release, idle session and following cohort.");
        }
    }

    private static async Task<Result> ExecuteAsync(
        string driver, string path,
        int cohorts, int warmups,
        BatchProfilerControl control, bool verification
    )
    {
        var facade = path == "facade" ? new TcpFacadeBatchComparisonBenchmarks() : null;
        var raw = path == "raw" ? new TcpBatchComparisonBenchmarks() : null;
        var m = driver == "mpgsql";
        try
        {
            if (facade is not null)
            {
                if (m)
                {
                    await facade.SetupMpgsql();
                }
                else
                {
                    await facade.SetupNpgsql();
                }
            }
            else
            {
                if (m)
                {
                    await raw!.SetupMpgsql();
                }
                else
                {
                    await raw!.SetupNpgsql();
                }
            }
            var peer = facade?.Peer ?? raw!.Peer;
            for (var i = 0;
                 i < warmups;
                 i++)
            {
                Prepare();
                var warmup = await ConsumeAsync()
                    .ConfigureAwait(false);
                Release();
                Check(warmup == CohortChecksum, "Warmup checksum");
                Idle();
            }
            var before = peer.Counters();
            control.Begin();
            long checksum = 0,
                ticks = 0,
                allocated = 0;
            int gen0 = 0,
                gen1 = 0,
                gen2 = 0;
            for (var i = 0;
                 i < cohorts;
                 i++)
            {
                Prepare();
                var allocationStart = verification ? 0 : GC.GetTotalAllocatedBytes(true);
                int g0 = GC.CollectionCount(0),
                    g1 = GC.CollectionCount(1),
                    g2 = GC.CollectionCount(2);
                var start = verification ? 0 : Stopwatch.GetTimestamp();
                long value;
                control.Start();
                try
                {
                    value = await ConsumeAsync()
                        .ConfigureAwait(false);
                }
                finally { control.Stop(); }
                if (!verification)
                {
                    ticks += Stopwatch.GetTimestamp() - start;
                    allocated += GC.GetTotalAllocatedBytes(true) - allocationStart;
                    gen0 += GC.CollectionCount(0) - g0;
                    gen1 += GC.CollectionCount(1) - g1;
                    gen2 += GC.CollectionCount(2) - g2;
                }
                Release();
                Check(value == CohortChecksum, "Measured cohort checksum");
                checksum += value;
                Idle();
            }
            var after = peer.Counters();
            var groups = (long)cohorts * GroupsPerCohort;
            Check
            (
                after.Queries - before.Queries == groups * QueriesPerGroup && after.Syncs - before.Syncs == groups,
                "Batch query/Sync boundaries"
            );
            Check(checksum == cohorts * CohortChecksum, "Total checksum");
            control.Finish();
            // A subsequent group still works after snapshotting and all result disposal.
            Prepare();
            Check
            (
                await ConsumeAsync()
                    .ConfigureAwait(false) == CohortChecksum, "Post-profile probe"
            );
            Release();
            Idle();
            return new Result(groups, groups * QueriesPerGroup, after.Syncs - before.Syncs, checksum, ticks, allocated, gen0, gen1, gen2);

            void Prepare()
            {
                if (facade is not null)
                {
                    if (m)
                    {
                        facade.PrepareMpgsql();
                    }
                    else
                    {
                        facade.PrepareNpgsql();
                    }
                }
            }

            void Release()
            {
                facade?.ReleaseCompletedGroups();
            }

            void Idle()
            {
                if (facade is not null)
                {
                    facade.CheckIdle();
                }
                else
                {
                    raw!.CheckIdle();
                }
            }

            async Task<long> ConsumeAsync()
            {
                if (facade is not null)
                {
                    return await (m ? facade.MpgsqlFacadeBatch() : facade.NpgsqlFreshBatch()).ConfigureAwait(false);
                }
                long sum = 0;
                for (var group = 0;
                     group < GroupsPerCohort;
                     group++)
                {
                    sum += await (m ? raw!.MpgsqlSharedSync() : raw!.NpgsqlBatch()).ConfigureAwait(false);
                }
                return sum;
            }
        }
        finally
        {
            if (facade is not null)
            {
                await facade.Cleanup();
            }
            else
            {
                await raw!.Cleanup();
            }
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
    private static string? Value(
        string[] args, string name,
        string? fallback
    )
    {
        var index = Array.IndexOf(args, name);
        return index < 0 ? fallback : index + 1 < args.Length ? args[index + 1] : throw new ArgumentException("Missing value: " + name);
    }
    private static int Number(
        string[] args, string name,
        int fallback
    )
    {
        return int.TryParse(Value(args, name, fallback.ToString(CultureInfo.InvariantCulture)), out var value) && value is > 0 and <= 65536
            ? value
            : throw new ArgumentException("Expected positive bounded integer: " + name);
    }

    private sealed record Result
    (
        long Groups,
        long Queries,
        long Syncs,
        long Checksum,
        long ExecutionTicks,
        long ProcessAllocatedBytesInExecutionIntervals,
        int Gen0,
        int Gen1,
        int Gen2
    );
}