using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

// This experiment varies read buffers in the loopback fixtures only. Both driver
// methods come from the same frozen benchmark assembly, which exposes the override.
// Production transport defaults are unchanged; a fixture-only result is diagnostic.
internal static class NpgsqlBufferComparison
{
    private const int Samples = 40;
    private const int WarmupSeconds = 5;
    private const int TargetSampleMilliseconds = 120;

    internal static async Task RunAsync(string baselinePath, string candidatePath, string output)
    {
        baselinePath = Path.GetFullPath(baselinePath);
        candidatePath = Path.GetFullPath(candidatePath);
        var baselineNativePath = Path.Combine(Path.GetDirectoryName(baselinePath)!, "Npgsql.dll");
        var candidateNativePath = Path.Combine(Path.GetDirectoryName(candidatePath)!, "Npgsql.dll");
        var baselineNativeHash = Hash(baselineNativePath);
        var candidateNativeHash = Hash(candidateNativePath);
        if (baselineNativeHash != candidateNativeHash)
        {
            throw new InvalidDataException("The read-buffer sweep must use the unchanged baseline Npgsql binary.");
        }
        var context = new VersionContext(candidatePath);
        var runtimeMetadata = ComparisonRunMetadata.Runtime();
        var snapshotMetadata = ComparisonRunMetadata.Snapshot(context, candidatePath);
        // Older frozen snapshots predate the internal explicit default and used
        // StreamPipeReader's 4096-byte default. Read the loaded snapshot, not source.
        var sessions = context.LoadFromAssemblyName(new AssemblyName("Mpgsql.Sessions"));
        var defaultField = sessions.GetType("Mpgsql.MpgsqlMessageSession", true)!
            .GetField("DefaultReadBufferSize", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        var defaultMpgsqlReadBufferSize = defaultField is null ? 4096 : (int)defaultField.GetRawConstantValue()!;
        var nativeAssembly = context.LoadFromAssemblyName(new AssemblyName("Npgsql"));
        var nativeVersion = nativeAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? nativeAssembly.GetName().Version?.ToString();
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var assembly in new[] { "Mpgsql.Benchmarks", "Mpgsql", "Mpgsql.Sessions", "Mpgsql.Protocol", "Mpgsql.Multiplexing", "Npgsql" })
        {
            var file = Path.Combine(Path.GetDirectoryName(candidatePath)!, assembly + ".dll");
            hashes.Add(assembly, Hash(file));
        }
        const BindingFlags members = BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var scenarioType = context.Benchmarks.GetType("Mpgsql.Benchmarks.Queries.QueryScenario", true)!;
        var findScenario = scenarioType.GetMethod("Find", members)!;
        var expectedMethod = scenarioType.GetMethod("Expected", members)!;
        var results = new List<object>();
        string[] cases = ["OneBigint", "Rows128Columns8", "Rows4096", "Bytea64KiB"];
        int[] buffers = [4096, 8192, 32768, 65536];
        var fullOutput = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
        await SaveAsync();
        foreach (var scenario in cases)
        foreach (var bufferSize in buffers)
        {
            var query = findScenario.Invoke(null, [scenario])!;
            var expected = expectedMethod.CreateDelegate<Func<int, long>>(query)(0);
            await using var native = await BenchmarkRun.CreateAsync(context,
                "TcpAdoReaderBenchmarks", "NpgsqlReusedTyped", scenario, bufferSize);
            await using var mpg = await BenchmarkRun.CreateAsync(context,
                "TcpAdoReaderBenchmarks", "ReusedTyped", scenario, bufferSize);
            if (native.Checksum != expected || mpg.Checksum != expected || native.Operations != 1 || mpg.Operations != 1)
            {
                throw new InvalidDataException("The read-buffer sweep has mismatched checksums or operation counts.");
            }
            if (native.ConfiguredReadBufferSize != bufferSize || mpg.ConfiguredReadBufferSize != bufferSize
                || native.ConnectionString is null)
            {
                throw new InvalidDataException("The read-buffer sweep did not apply matching driver configuration.");
            }
            var warming = Stopwatch.StartNew();
            while (warming.Elapsed < TimeSpan.FromSeconds(WarmupSeconds))
            {
                await native.MeasureAsync(1);
                await mpg.MeasureAsync(1);
            }
            var pilotNative = await native.MeasureAsync(8);
            var pilotMpgsql = await mpg.MeasureAsync(8);
            var invocations = Math.Clamp((int)(TargetSampleMilliseconds * 1_000_000d /
                Math.Max(pilotNative.Nanoseconds, pilotMpgsql.Nanoseconds)), 1, 4096);
            var pairs = new List<Pair>(Samples);
            for (var sample = 0; sample < Samples; sample++)
            {
                Measurement n, m;
                if ((sample & 1) == 0)
                {
                    n = await native.MeasureAsync(invocations);
                    m = await mpg.MeasureAsync(invocations);
                }
                else
                {
                    m = await mpg.MeasureAsync(invocations);
                    n = await native.MeasureAsync(invocations);
                }
                if (!double.IsFinite(n.Nanoseconds) || !double.IsFinite(m.Nanoseconds)
                    || n.Nanoseconds <= 0 || m.Nanoseconds <= 0)
                {
                    throw new InvalidDataException("The read-buffer sweep produced an invalid timing sample.");
                }
                pairs.Add(new Pair(n.Nanoseconds, m.Nanoseconds, n.Bytes, m.Bytes));
            }
            var logs = pairs.Select(p => Math.Log(p.CandidateNanoseconds / p.BaselineNanoseconds)).ToArray();
            var meanLog = logs.Average();
            var sd = Math.Sqrt(logs.Sum(value => (value - meanLog) * (value - meanLog)) / (Samples - 1));
            var half = 2.023 * sd / Math.Sqrt(Samples);
            var ratio = Math.Exp(meanLog);
            var lower = Math.Exp(meanLog - half);
            var upper = Math.Exp(meanLog + half);
            var nativeMean = pairs.Average(p => p.BaselineNanoseconds);
            var mpgMean = pairs.Average(p => p.CandidateNanoseconds);
            results.Add(new
            {
                Case = scenario,
                Path = "ADO.NET/ReusedTyped",
                ReadBufferSize = bufferSize,
                NpgsqlConfiguredReadBufferSize = native.ConfiguredReadBufferSize,
                MpgsqlConfiguredReadBufferSize = mpg.ConfiguredReadBufferSize,
                MpgsqlMinimumReadSize = 1024,
                NpgsqlConnectionString = native.ConnectionString,
                Samples,
                InvocationsPerSample = invocations,
                OperationsPerInvocation = 1,
                Checksum = expected,
                NpgsqlMicroseconds = nativeMean / 1000,
                MpgsqlMicroseconds = mpgMean / 1000,
                NpgsqlOperationsPerSecond = 1e9 / nativeMean,
                MpgsqlOperationsPerSecond = 1e9 / mpgMean,
                NpgsqlBytes = pairs.Average(p => p.BaselineBytes),
                MpgsqlBytes = pairs.Average(p => p.CandidateBytes),
                MpgsqlOverNpgsql = ratio,
                Ratio95Lower = lower,
                Ratio95Upper = upper,
                LatencyReductionPercent = (1 - ratio) * 100,
                MeetsTenPercentLatencyBound = upper <= 0.90,
                Pairs = pairs
            });
            Console.WriteLine($"ReadBuffer={bufferSize}/{scenario}: Npgsql {nativeMean / 1000:F2} us; Mpgsql {mpgMean / 1000:F2} us; ratio {ratio:F4} [{lower:F4}, {upper:F4}]; >=10% lower latency: {upper <= 0.90}");
            await SaveAsync();
        }

        async Task SaveAsync()
        {
            using var process = Process.GetCurrentProcess();
            await File.WriteAllTextAsync(fullOutput, JsonSerializer.Serialize(new
            {
                BaselineReference = baselinePath,
                BenchmarkSnapshot = candidatePath,
                RunMetadata = runtimeMetadata,
                SnapshotProvenance = snapshotMetadata,
                DriverAssemblySource = "Both native Npgsql and Mpgsql benchmark methods are loaded from BenchmarkSnapshot",
                BaselineNpgsqlSha256 = baselineNativeHash,
                NpgsqlSha256 = candidateNativeHash,
                NpgsqlVersion = nativeVersion,
                AssemblySha256 = hashes,
                Runtime = RuntimeInformation.FrameworkDescription,
                OS = RuntimeInformation.OSDescription,
                ProcessPriority = process.PriorityClass.ToString(),
                LogicalProcessors = Environment.ProcessorCount,
                ServerGC = GCSettings.IsServerGC,
                Confidence = "95% paired Student t on log latency ratios; 40 alternating pairs; t(39)=2.023; no outlier filtering",
                WarmupSeconds,
                TargetSampleMilliseconds,
                SampleSizing = "Same invocation count for both drivers; pilot targets 120ms for the slower driver, capped at 4096 invocations",
                ReadBufferSizes = buffers,
                CurrentDefaultMpgsqlReadBufferSize = defaultMpgsqlReadBufferSize,
                CurrentDefaultNpgsqlReadBufferSize = 8192,
                MinimumReadSize = "Mpgsql 1024 (remaining-space allocation threshold)",
                OtherBufferSettings = "Write buffers and synthetic peer buffers retain their existing defaults",
                LargeRowBuffers = "Configured size is reported; Npgsql may allocate an oversized buffer for Bytea64KiB",
                Acceptance = "Upper paired ratio confidence bound <= 0.90; independent repeats and production transport validation required",
                AllocationScope = "Whole managed process, including the identical synthetic TCP peer; no subtraction",
                Scope = "Explicit fixture read-buffer experiment relative to the loaded snapshot's default; SQL execution, TLS and physical connection startup excluded",
                Complete = results.Count == cases.Length * buffers.Length,
                Results = results
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
