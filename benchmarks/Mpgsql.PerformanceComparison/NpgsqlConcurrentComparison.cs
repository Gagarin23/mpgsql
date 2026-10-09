using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Text.Json;

// Matched independent Sync boundaries. Shared-Sync policy experiments are separate.
internal static class NpgsqlConcurrentComparison
{
    internal static async Task RunAsync(string baselinePath, string candidatePath, string output)
    {
        var baseline = new VersionContext(baselinePath);
        var candidate = new VersionContext(candidatePath);
        var runtimeMetadata = ComparisonRunMetadata.Runtime();
        var baselineMetadata = ComparisonRunMetadata.Snapshot(baseline, baselinePath);
        var candidateMetadata = ComparisonRunMetadata.Snapshot(candidate, candidatePath);
        var results = new List<object>();
        const int requests = 8192;
        string[] profiles = ["C8_P1_W8", "C64_P1_W8", "C64_P4_W8", "C64_P1_W64", "C64_P4_W64"];
        string[] drivers = ["NpgsqlPool", "NpgsqlMultiplexed"];
        foreach (var profile in profiles)
        foreach (var driver in drivers)
        {
            await using var native = await Run.CreateAsync(baseline, profile, driver);
            await using var mpg = await Run.CreateAsync(candidate, profile, "Mpgsql");
            if (native.Callers != mpg.Callers || native.Connections != mpg.Connections
                || native.InFlight != mpg.InFlight || native.RowBudget != mpg.RowBudget)
            {
                throw new InvalidDataException("The concurrent profile metadata differs between snapshots.");
            }
            for (var warmup = 0; warmup < 3; warmup++)
            {
                await native.MeasureAsync(requests, 0);
                await mpg.MeasureAsync(requests, 0);
            }
            var pairs = new List<Pair>(40);
            for (var i = 0; i < 40; i++)
            {
                JsonElement n, m;
                if ((i & 1) == 0)
                {
                    n = await native.MeasureAsync(requests, i + 1);
                    m = await mpg.MeasureAsync(requests, i + 1);
                }
                else
                {
                    m = await mpg.MeasureAsync(requests, i + 1);
                    n = await native.MeasureAsync(requests, i + 1);
                }
                pairs.Add(new Pair(n, m));
            }
            object Summary(string metric, bool inverse = false)
            {
                var logs = pairs.Select(p => Math.Log(Number(p.Mpgsql, metric) / Number(p.Npgsql, metric)) * (inverse ? -1 : 1)).ToArray();
                var mean = logs.Average();
                var half = 2.023 * Math.Sqrt(logs.Sum(x => (x - mean) * (x - mean)) / 39 / 40);
                return new
                {
                    Npgsql = pairs.Average(p => Number(p.Npgsql, metric)),
                    Mpgsql = pairs.Average(p => Number(p.Mpgsql, metric)),
                    Ratio = Math.Exp(mean),
                    Ratio95Lower = Math.Exp(mean - half),
                    Ratio95Upper = Math.Exp(mean + half),
                    MeetsTenPercentBound = Math.Exp(mean + half) <= 0.90
                };
            }
            results.Add(new
            {
                Profile = profile, NativeDriver = driver, SyncGroupSize = 1,
                Callers = mpg.Callers, PhysicalConnectionLimit = mpg.Connections,
                MpgsqlMaxInFlightPerConnection = mpg.InFlight,
                MpgsqlPhysicalOutstandingLimit = Math.Min(mpg.Callers, mpg.Connections * mpg.InFlight),
                MpgsqlRowBudgetPerConnection = mpg.RowBudget,
                WindowCoversCallerPopulation = mpg.InFlight >= mpg.Callers,
                NativePublicInFlightLimit = "No matching public setting; caller population bounds submitted requests",
                NativePhysicalScheduling = driver == "NpgsqlPool"
                    ? "Exclusive leases; physical active commands are bounded by connection count"
                    : "Native multiplexed connector distribution; no fixed per-connection window matching Mpgsql",
                Samples = 40, RequestsPerSample = requests,
                InverseThroughput = Summary("RequestsPerSecond", true),
                MeanLatency = Summary("MeanMs"), P99Latency = Summary("P99Ms"),
                AllocatedBytesPerRequest = Summary("AllocatedBytesPerRequest"), Pairs = pairs
            });
            Console.WriteLine($"{profile}/{driver}: Npgsql {pairs.Average(p => Number(p.Npgsql, "RequestsPerSecond")):F0} -> Mpgsql {pairs.Average(p => Number(p.Mpgsql, "RequestsPerSecond")):F0} req/s; mean {pairs.Average(p => Number(p.Npgsql, "MeanMs")):F4} -> {pairs.Average(p => Number(p.Mpgsql, "MeanMs")):F4} ms");
            var fullOutput = Path.GetFullPath(output);
            Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
            using var process = Process.GetCurrentProcess();
            await File.WriteAllTextAsync(fullOutput, JsonSerializer.Serialize(new
            {
                Baseline = Path.GetFullPath(baselinePath), Candidate = Path.GetFullPath(candidatePath),
                RunMetadata = runtimeMetadata,
                BaselineSnapshot = baselineMetadata,
                CandidateSnapshot = candidateMetadata,
                Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                ProcessPriority = process.PriorityClass.ToString(), LogicalProcessors = Environment.ProcessorCount,
                StopwatchFrequency = Stopwatch.Frequency,
                ServerGC = GCSettings.IsServerGC,
                Confidence = "95% paired Student t on log ratios; 40 alternating pairs; t(39)=2.023; no outlier filtering",
                ThroughputRatio = "Npgsql/Mpgsql, so lower is better; target upper bound <=0.90",
                Scope = "Synthetic loopback TCP; prewarmed connections; independent Sync boundaries for both drivers; closed loop; no coordinated omission correction",
                AllocationScope = "Whole process, including identical TCP peer and timed load runner; worker timing arrays, gate and setup are excluded",
                RequestTiming = "One outer Stopwatch interval around the same cached fixture.ReadAsync delegate for each driver; no per-row observation callback",
                Limits = "Mpgsql W8 profiles retain their configured backpressure. W64 profiles cover all 64 callers, matching the caller-population upper bound of native multiplexing; native per-connection distribution remains different. Npgsql has no matching public window or row-budget setting",
                Profiles = profiles,
                Complete = results.Count == profiles.Length * drivers.Length, Results = results
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static double Number(JsonElement value, string name) => value.GetProperty(name).GetDouble();
    private sealed record Pair(JsonElement Npgsql, JsonElement Mpgsql);

    private sealed class Run(
        object fixture, int callers, int connections, int inFlight, long rowBudget,
        Func<int, bool, Task<long>> read, Func<int, long> expected,
        Action checkIdle, Func<(long Queries, long Syncs, long ReplyBytes, int Connections)> counters) : IAsyncDisposable
    {
        internal int Callers => callers;
        internal int Connections => connections;
        internal int InFlight => inFlight;
        internal long RowBudget => rowBudget;
        public ValueTask DisposeAsync() => ((IAsyncDisposable)fixture).DisposeAsync();
        internal static async Task<Run> CreateAsync(VersionContext context, string profileName, string driverName)
        {
            var assembly = context.Benchmarks;
            var profile = assembly.GetType("Mpgsql.Benchmarks.Queries.QueryLoadProfile", true)!.GetMethod("Find", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [profileName])!;
            var driver = Enum.Parse(assembly.GetType("Mpgsql.Benchmarks.Comparison.ComparisonDriver", true)!, driverName);
            var fixtureType = assembly.GetType("Mpgsql.Benchmarks.Comparison.TcpComparisonFixture", true)!;
            var pending = (Task)fixtureType.GetMethod("CreateAsync", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [driver, profile, 1, 1])!;
            await pending.ConfigureAwait(false);
            var fixture = pending.GetType().GetProperty("Result")!.GetValue(pending)!;
            const BindingFlags members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var callers = (int)profile.GetType().GetProperty("Callers", members)!.GetValue(profile)!;
            var connections = (int)profile.GetType().GetProperty("Connections", members)!.GetValue(profile)!;
            var inFlight = (int)profile.GetType().GetProperty("InFlight", members)!.GetValue(profile)!;
            var rowBudget = (long)profile.GetType().GetProperty("RowBudget", members)!.GetValue(profile)!;
            var read = fixtureType.GetMethod("ReadAsync", members)!.CreateDelegate<Func<int, bool, Task<long>>>(fixture);
            var checkIdle = fixtureType.GetMethod("CheckIdle", members)!.CreateDelegate<Action>(fixture);
            var peer = fixtureType.GetProperty("Peer", members)!.GetValue(fixture)!;
            var counters = peer.GetType().GetMethod("Counters", members)!
                .CreateDelegate<Func<(long Queries, long Syncs, long ReplyBytes, int Connections)>>(peer);
            var scenarioType = assembly.GetType("Mpgsql.Benchmarks.Queries.QueryScenario", true)!;
            var one = scenarioType.GetField("One", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            var expected = scenarioType.GetMethod("Expected", members)!.CreateDelegate<Func<int, long>>(one);
            return new Run(fixture, callers, connections, inFlight, rowBudget, read, expected, checkIdle, counters);
        }
        internal async Task<JsonElement> MeasureAsync(int requests, int sample)
        {
            checkIdle();
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = new Task<Worker>[callers];
            for (var worker = 0; worker < callers; worker++)
            {
                var count = worker >= requests ? 0 : (requests - 1 - worker) / callers + 1;
                pending[worker] = WorkAsync(worker, new long[count], expected(worker) * count);
            }
            var completion = Task.WhenAll(pending).WaitAsync(TimeSpan.FromMinutes(5));
            var before = counters();
            var allocatedBefore = GC.GetTotalAllocatedBytes(true);
            var g0 = GC.CollectionCount(0);
            var g1 = GC.CollectionCount(1);
            var g2 = GC.CollectionCount(2);
            var start = Stopwatch.GetTimestamp();
            gate.SetResult();
            var workers = await completion.ConfigureAwait(false);
            var elapsedTicks = Stopwatch.GetTimestamp() - start;
            var allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
            g0 = GC.CollectionCount(0) - g0;
            g1 = GC.CollectionCount(1) - g1;
            g2 = GC.CollectionCount(2) - g2;
            var after = counters();
            checkIdle();
            if (after.Queries - before.Queries != requests || after.Syncs - before.Syncs != requests)
            {
                throw new InvalidDataException("The independent load block must execute every request with exactly one Sync.");
            }
            long[] latencies = [.. workers.SelectMany(worker => worker.LatencyTicks)];
            Array.Sort(latencies);
            var seconds = (double)elapsedTicks / Stopwatch.Frequency;
            var milliseconds = 1000.0 / Stopwatch.Frequency;
            var metrics = new
            {
                Sample = sample,
                Requests = requests,
                Seconds = seconds,
                RequestsPerSecond = requests / seconds,
                MeanMs = latencies.Average(value => (double)value) * milliseconds,
                P99Ms = latencies[(int)Math.Ceiling(.99 * latencies.Length) - 1] * milliseconds,
                AllocatedBytes = allocated,
                AllocatedBytesPerRequest = (double)allocated / requests,
                Gen0 = g0,
                Gen1 = g1,
                Gen2 = g2,
                Queries = after.Queries - before.Queries,
                Syncs = after.Syncs - before.Syncs,
                PeerReplyBytes = after.ReplyBytes - before.ReplyBytes,
                PhysicalConnections = after.Connections,
                ElapsedTicks = elapsedTicks,
                Workers = workers
            };
            return JsonSerializer.SerializeToElement(metrics);

            async Task<Worker> WorkAsync(int worker, long[] timings, long expectedChecksum)
            {
                await gate.Task.ConfigureAwait(false);
                long checksum = 0;
                for (var i = 0; i < timings.Length; i++)
                {
                    var requestStart = Stopwatch.GetTimestamp();
                    var result = await read(worker, false).ConfigureAwait(false);
                    timings[i] = Stopwatch.GetTimestamp() - requestStart;
                    checksum += result;
                }
                if (checksum != expectedChecksum)
                {
                    throw new InvalidDataException("Concurrent worker returned an unexpected complete-query checksum.");
                }
                return new Worker(worker, checksum, timings);
            }
        }

        private readonly record struct Worker(int Index, long Checksum, long[] LatencyTicks);
    }
}
