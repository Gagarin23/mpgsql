using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Text.Json;

internal static class ConcurrentComparison
{
    internal static async Task RunAsync(
        string oldPath, string newPath,
        string output
    )
    {
        var oldContext = new VersionContext(oldPath);
        var newContext = new VersionContext(newPath);
        var results = new List<object>();
        const int requests = 65536;
        foreach (var (profile, groupSize) in new[]
                 {
                     ("C64_P1_W8", 1),
                     ("C64_P4_W8", 1),
                     ("C64_P1_W8", 8)
                 })
        {
            await using var before = await LoadRun.CreateAsync(oldContext, profile, groupSize);
            await using var after = await LoadRun.CreateAsync(newContext, profile, groupSize);
            for (var warm = 0;
                 warm < 3;
                 warm++)
            {
                await before.MeasureAsync(requests, 0);
                await after.MeasureAsync(requests, 0);
            }
            var pairs = new List<(JsonElement Before, JsonElement After)>();
            for (var i = 0;
                 i < 40;
                 i++)
            {
                JsonElement oldValue,
                    newValue;
                if ((i & 1) == 0)
                {
                    oldValue = await before.MeasureAsync(requests, i + 1);
                    newValue = await after.MeasureAsync(requests, i + 1);
                }
                else
                {
                    newValue = await after.MeasureAsync(requests, i + 1);
                    oldValue = await before.MeasureAsync(requests, i + 1);
                }
                pairs.Add((oldValue, newValue));
            }

            object Summary(string metric, bool inverse = false)
            {
                var logs = pairs
                    .Select(p => Math.Log(Number(p.After, metric) / Number(p.Before, metric)) * (inverse ? -1 : 1))
                    .ToArray();
                double mean = logs.Average(),
                    sd = Math.Sqrt(logs.Sum(x => (x - mean) * (x - mean)) / 39),
                    half = 2.023 * sd / Math.Sqrt(40);
                return new
                {
                    Baseline = pairs.Average(p => Number(p.Before, metric)),
                    Candidate = pairs.Average(p => Number(p.After, metric)),
                    Ratio = Math.Exp(mean),
                    Ratio95Lower = Math.Exp(mean - half),
                    Ratio95Upper = Math.Exp(mean + half),
                    MeetsFivePercentBound = Math.Exp(mean + half) <= 1.05
                };
            }

            var rate = pairs.Average(p => Number(p.After, "RequestsPerSecond"));
            Console.WriteLine
            (
                $"{profile}/Sync{groupSize}: throughput {pairs.Average(p => Number(p.Before, "RequestsPerSecond")):F0} -> {rate:F0} req/s; latency {pairs.Average(p => Number(p.Before, "MeanMs")):F4} -> {pairs.Average(p => Number(p.After, "MeanMs")):F4} ms"
            );
            results.Add
            (
                new
                {
                    Profile = profile,
                    SyncGroupSize = groupSize,
                    Samples = 40,
                    RequestsPerSample = requests,
                    Throughput = Summary("RequestsPerSecond", true),
                    MeanLatency = Summary("MeanMs"),
                    P99Latency = Summary("P99Ms"),
                    AllocatedBytesPerRequest = Summary("AllocatedBytesPerRequest"),
                    Pairs = pairs.Select
                    (p => new
                        {
                            Baseline = p.Before,
                            Candidate = p.After
                        }
                    )
                }
            );
        }
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using var process = Process.GetCurrentProcess();
        await File.WriteAllTextAsync
        (
            output,
            JsonSerializer.Serialize
            (
                new
                {
                    Baseline = oldPath,
                    Candidate = newPath,
                    ProcessPriority = process.PriorityClass.ToString(),
                    LogicalProcessors = Environment.ProcessorCount,
                    ServerGC = GCSettings.IsServerGC,
                    Confidence = "95% paired Student t interval on log ratios, t(39)=2.023",
                    ThroughputRatio = "Baseline/candidate; upper <= 1.05 means latency-equivalent loss at most 5%",
                    AllocationScope = "Whole process, including TCP peer and existing timed runner",
                    Results = results
                }, new JsonSerializerOptions
                {
                    WriteIndented = true
                }
            )
        );
    }
    private static double Number(JsonElement value, string name)
    {
        return value
            .GetProperty(name)
            .GetDouble();
    }

    private sealed class LoadRun(object fixture, object profile, MethodInfo measure) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            return ((IAsyncDisposable)fixture).DisposeAsync();
        }
        internal static async Task<LoadRun> CreateAsync(
            VersionContext context, string profileName,
            int groupSize
        )
        {
            var assembly = context.Benchmarks;
            var profile = assembly.GetType("Mpgsql.Benchmarks.Queries.QueryLoadProfile", true)!.GetMethod("Find", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [profileName])!;
            var driver = Enum.Parse(assembly.GetType("Mpgsql.Benchmarks.Comparison.ComparisonDriver", true)!, "Mpgsql");
            var fixtureType = assembly.GetType("Mpgsql.Benchmarks.Comparison.TcpComparisonFixture", true)!;
            var pending = (Task)fixtureType.GetMethod("CreateAsync", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [driver, profile, groupSize, 1])!;
            await pending.ConfigureAwait(false);
            var fixture = pending
                .GetType()
                .GetProperty("Result")!.GetValue(pending)!;
            var measure = assembly.GetType("Mpgsql.Benchmarks.Comparison.TcpComparisonLoadRunner", true)!.GetMethod("MeasureAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            return new LoadRun(fixture, profile, measure);
        }
        internal async Task<JsonElement> MeasureAsync(int count, int sample)
        {
            var pending = (Task)measure.Invoke(null, [fixture, profile, count, sample])!;
            await pending.ConfigureAwait(false);
            var result = pending
                .GetType()
                .GetProperty("Result")!.GetValue(pending)!;
            var metrics = result
                .GetType()
                .GetProperty("Metrics")!.GetValue(result)!;
            return JsonSerializer.SerializeToElement(metrics, metrics.GetType());
        }
    }
}
