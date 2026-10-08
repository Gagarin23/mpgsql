using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using Mpgsql.Benchmarks.Queries;

namespace Mpgsql.Benchmarks.Comparison;

// Calls the existing BDN workload, including worker setup and full request disposal.
// Collection is continuous across cohorts so profiler API calls do not dominate short cohorts.
internal static class TcpPipelineProfileRunner
{
    private const int RequestsPerCohort = 256;
    internal static readonly string[] ProfileNames = ["C1_P1_W1", "C8_P1_W8", "C64_P1_W8", "C64_P4_W8"];

    internal static async Task RunAsync(string[] args)
    {
        var options = Options.Parse(args);
        var directory = Path.GetFullPath(options.Directory);
        if (File.Exists(Path.Combine(directory, "runner.json")))
        {
            throw new IOException("Choose a fresh profiling output directory.");
        }
        Directory.CreateDirectory(directory);
        var control = new BatchProfilerControl(options.Kind, options.ApiPath);
        var profile = QueryLoadProfile.Find(options.Profile);
        var result = await ExecuteAsync
            (
                options.Driver, profile, options.Cohorts, options.Warmups,
                control, false
            )
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
            Workload = nameof(TcpAdmissionComparisonBenchmarks),
            Profile = profile,
            profile.RowBudget,
            RequestsPerCohort,
            options.Cohorts,
            WarmupCohorts = options.Warmups,
            MaxInFlightPerConnection = options.Driver == ComparisonDriver.Mpgsql ? (int?)profile.InFlight : null,
            Scenario = "Independent bigint queries, one row each, one Sync per request, full consume and Dispose",
            Collection = "One continuous window across existing BDN worker cohorts; setup, warmup and checks excluded",
            AllocationScope = "Process-wide: driver, peer, runner and profiler overhead; no subtraction",
            Interpretation = "Diagnostic profiler run; elapsed workload time per request is not individual latency or a BDN baseline"
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
        Console.WriteLine($"PASS pipeline profile {options.Driver}/{profile.Name}: {result.Queries} queries, {result.Syncs} Syncs, checksum {result.Checksum}; {control.Features}");
    }

    internal static async Task VerifyAsync()
    {
        foreach (var name in ProfileNames)
        foreach (var driver in Enum.GetValues<ComparisonDriver>())
        {
            await ExecuteAsync
                (
                    driver, QueryLoadProfile.Find(name), 2, 1, new BatchProfilerControl("none", null),
                    true
                )
                .ConfigureAwait(false);
            Console.WriteLine($"PASS pipeline profiling runner {driver}/{name}: per-caller identity, checksum, query/Sync counts, idle and following cohort.");
        }
    }

    private static async Task<Result> ExecuteAsync(
        ComparisonDriver driver, QueryLoadProfile profile,
        int cohorts, int warmups,
        BatchProfilerControl control, bool verification
    )
    {
        var workload = new TcpAdmissionComparisonBenchmarks
        {
            Profile = profile.Name
        };
        var prepared = false;
        try
        {
            switch (driver)
            {
                case ComparisonDriver.Mpgsql:
                    await workload
                        .SetupMpgsql()
                        .ConfigureAwait(false); break;
                case ComparisonDriver.NpgsqlPool:
                    await workload
                        .SetupPool()
                        .ConfigureAwait(false); break;
                case ComparisonDriver.NpgsqlMultiplexed:
                    await workload
                        .SetupMultiplexed()
                        .ConfigureAwait(false); break;
                default: throw new ArgumentOutOfRangeException(nameof(driver));
            }
            prepared = true;
            long expected = 0;
            for (var worker = 0;
                 worker < profile.Callers;
                 worker++)
            {
                expected += ((RequestsPerCohort - 1 - worker) / profile.Callers + 1L) * QueryScenario.One.Expected(worker);
            }
            for (var i = 0;
                 i < warmups;
                 i++)
            {
                Check
                (
                    await ConsumeAsync()
                        .ConfigureAwait(false) == expected, "Warmup checksum"
                );
                workload.Fixture.CheckIdle();
            }
            if (verification)
            {
                var callers = new Task<long>[profile.Callers];
                for (var worker = 0;
                     worker < callers.Length;
                     worker++)
                {
                    callers[worker] = workload.Fixture.ReadAsync(worker, false);
                }
                var values = await Task
                    .WhenAll(callers)
                    .ConfigureAwait(false);
                for (var worker = 0;
                     worker < values.Length;
                     worker++)
                {
                    Check(values[worker] == QueryScenario.One.Expected(worker), "Per-caller response identity");
                }
                workload.Fixture.CheckIdle();
            }
            var before = workload.Fixture.Peer.Counters();
            var allocationStart = verification ? 0 : GC.GetTotalAllocatedBytes(true);
            int g0 = GC.CollectionCount(0),
                g1 = GC.CollectionCount(1),
                g2 = GC.CollectionCount(2);
            long start = verification ? 0 : Stopwatch.GetTimestamp(),
                checksum = 0;
            control.Begin();
            control.Start();
            try
            {
                for (var i = 0;
                     i < cohorts;
                     i++)
                {
                    checksum += await ConsumeAsync()
                        .ConfigureAwait(false);
                }
            }
            finally { control.Stop(); }
            var ticks = verification ? 0 : Stopwatch.GetTimestamp() - start;
            var allocated = verification ? 0 : GC.GetTotalAllocatedBytes(true) - allocationStart;
            int gen0 = GC.CollectionCount(0) - g0,
                gen1 = GC.CollectionCount(1) - g1,
                gen2 = GC.CollectionCount(2) - g2;
            var after = workload.Fixture.Peer.Counters();
            var queries = (long)cohorts * RequestsPerCohort;
            Check(checksum == cohorts * expected, "Measured checksum");
            Check
            (
                after.Queries - before.Queries == queries && after.Syncs - before.Syncs == queries,
                "Independent request/Sync boundaries"
            );
            workload.Fixture.CheckIdle();
            control.Finish();
            Check
            (
                await ConsumeAsync()
                    .ConfigureAwait(false) == expected, "Following cohort checksum"
            );
            workload.Fixture.CheckIdle();
            return new Result(queries, after.Syncs - before.Syncs, checksum, ticks, allocated, gen0, gen1, gen2);

            Task<long> ConsumeAsync()
            {
                return driver switch
                {
                    ComparisonDriver.Mpgsql     => workload.MpgsqlDataSource(),
                    ComparisonDriver.NpgsqlPool => workload.NpgsqlPool(),
                    _                           => workload.NpgsqlMultiplexed()
                };
            }
        }
        finally
        {
            if (prepared)
            {
                await workload
                    .Cleanup()
                    .ConfigureAwait(false);
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

    private sealed record Result
    (
        long Queries,
        long Syncs,
        long Checksum,
        long ExecutionTicks,
        long ProcessAllocatedBytes,
        int Gen0,
        int Gen1,
        int Gen2
    );

    private sealed record Options
    (
        ComparisonDriver Driver,
        string Profile,
        string Kind,
        int Cohorts,
        int Warmups,
        string Directory,
        string? ApiPath
    )
    {
        internal static Options Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0;
                 i < args.Length;
                 i++)
            {
                var key = args[i];
                if (key == "--query-pipeline-profile")
                {
                    continue;
                }
                if (key is not ("--driver" or "--profile" or "--profile-kind" or "--cohorts"
                        or "--warmup-cohorts" or "--artifacts" or "--profiler-api") || ++i == args.Length
                                                                                    || !values.TryAdd(key, args[i]))
                {
                    throw new ArgumentException("Invalid or duplicate option: " + key);
                }
            }

            string Get(string key, string fallback)
            {
                return values.GetValueOrDefault(key, fallback);
            }

            int Number(
                string key, int fallback,
                int maximum
            )
            {
                return int.TryParse
                       (
                           Get
                           (
                               key,
                               fallback.ToString(CultureInfo.InvariantCulture)
                           ), out var value
                       )
                       && value > 0 && value <= maximum
                    ? value
                    : throw new ArgumentException("Invalid bounded integer: " + key);
            }

            var driver = Get("--driver", "mpgsql") switch
            {
                "mpgsql"             => ComparisonDriver.Mpgsql,
                "npgsql-pool"        => ComparisonDriver.NpgsqlPool,
                "npgsql-multiplexed" => ComparisonDriver.NpgsqlMultiplexed,
                _                    => throw new ArgumentException("Expected mpgsql, npgsql-pool or npgsql-multiplexed.")
            };
            string profile = Get("--profile", "C64_P1_W8"),
                kind = Get("--profile-kind", "trace");
            if (!ProfileNames.Contains(profile) || kind is not ("trace" or "none"))
            {
                throw new ArgumentException("Expected one of the four baseline profiles and trace/none.");
            }
            return new Options
            (
                driver, profile, kind, Number("--cohorts", 4096, 8192), Number("--warmup-cohorts", 32, 1024),
                Get("--artifacts", "artifacts/query-profile/pipeline"), values.GetValueOrDefault("--profiler-api")
            );
        }
    }
}