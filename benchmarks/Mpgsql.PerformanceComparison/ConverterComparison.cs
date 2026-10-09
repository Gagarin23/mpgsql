using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

// The public benchmark methods retain the catalog's typed results. Binding their
// delegates happens once, before timing; the synchronous loop has no reflection,
// async state machine or value boxing.
internal static class ConverterComparison
{
    private const int Samples = 40;
    private const int MaximumInvocations = 50_000_000;
    private static long _checksumSink;

    internal static void Run(string baselinePath, string candidatePath, string output,
        bool focused = false, string[]? selectedIds = null)
    {
        if (selectedIds is not null && !focused)
        {
            throw new ArgumentException("Explicit converter IDs require focused mode.", nameof(selectedIds));
        }
        string[] requiredCaseIds = focused
            ? selectedIds ?? ["numeric.decimal", "interval.pg", "interval.clr"]
            : [];
        if (focused)
        {
            if (requiredCaseIds.Length == 0 || requiredCaseIds.Any(string.IsNullOrWhiteSpace))
            {
                throw new ArgumentException("Focused converter IDs must be nonempty.", nameof(selectedIds));
            }
            requiredCaseIds = requiredCaseIds.Select(id => id.Trim()).ToArray();
            if (requiredCaseIds.Distinct(StringComparer.Ordinal).Count() != requiredCaseIds.Length)
            {
                throw new ArgumentException("Focused converter IDs must not contain duplicates.", nameof(selectedIds));
            }
        }
        var requiredCaseSet = requiredCaseIds.ToHashSet(StringComparer.Ordinal);
        var warmupMilliseconds = focused ? 5000 : 350;
        var targetSampleMilliseconds = focused ? 120 : 20;
        var baseline = new VersionContext(baselinePath);
        var candidate = new VersionContext(candidatePath);
        var runtimeMetadata = ComparisonRunMetadata.Runtime();
        var baselineMetadata = ComparisonRunMetadata.Snapshot(baseline, baselinePath);
        var candidateMetadata = ComparisonRunMetadata.Snapshot(candidate, candidatePath);
        var baselineProfiles = Profiles(baseline);
        var candidateProfiles = Profiles(candidate);
        var baselineHashes = Hashes(baselinePath);
        var candidateHashes = Hashes(candidatePath);
        var rows = new List<object>();
        var shapes = new[]
        {
            (Name: "Scalar", Class: "ConverterScalarBenchmarks", Count: 1),
            (Name: "Array", Class: "ConverterArrayBenchmarks", Count: 256),
            (Name: "NullableArray", Class: "ConverterNullableArrayBenchmarks", Count: 256)
        };
        if (focused)
        {
            shapes = shapes[..1];
        }
        string[] SelectedCases(Type type) => focused
            ? Cases(type).Where(requiredCaseSet.Contains).ToArray()
            : Cases(type);
        if (focused)
        {
            foreach (var context in new[] { baseline, candidate })
            {
                var scalarCases = SelectedCases(BenchmarkType(context, "ConverterScalarBenchmarks"));
                if (scalarCases.Length != requiredCaseIds.Length
                    || !requiredCaseSet.SetEquals(scalarCases))
                {
                    throw new InvalidDataException("The focused comparison requires every selected scalar profile exactly once: "
                        + string.Join(", ", requiredCaseIds) + ".");
                }
            }
        }
        var skippedCandidateProfiles = candidateProfiles.Keys.Except(baselineProfiles.Keys).ToArray();
        var expectedProfiles = 0;
        foreach (var shape in shapes)
        {
            var nativeType = BenchmarkType(baseline, shape.Class);
            var mpgType = BenchmarkType(candidate, shape.Class);
            var nativeCases = SelectedCases(nativeType);
            var mpgCases = Cases(mpgType).ToHashSet(StringComparer.Ordinal);
            foreach (var profileId in nativeCases)
            {
                if (!mpgCases.Contains(profileId))
                {
                    throw new InvalidDataException("Candidate is missing converter profile " + profileId + ".");
                }
                var oldInfo = baselineProfiles[profileId];
                var newInfo = candidateProfiles[profileId];
                if (oldInfo != newInfo || oldInfo.Shape != shape.Name)
                {
                    throw new InvalidDataException("Converter profile metadata changed: " + profileId + ".");
                }
                expectedProfiles++;
            }
        }
        var expectedSeries = expectedProfiles * 2;
        Save();

        foreach (var shape in shapes)
        {
            var nativeType = BenchmarkType(baseline, shape.Class);
            var mpgType = BenchmarkType(candidate, shape.Class);
            foreach (var profileId in SelectedCases(nativeType))
            foreach (var operation in new[] { "Read", "Write" })
            {
                using var native = new ConverterRun(nativeType, "Npgsql" + operation, profileId, shape.Count);
                using var mpg = new ConverterRun(mpgType, "Mpgsql" + operation, profileId, shape.Count);
                Warmup(native, mpg, warmupMilliseconds);
                var invocations = Pilot(native, mpg, targetSampleMilliseconds);
                var pairs = new List<ConverterPair>(Samples);
                for (var sample = 0; sample < Samples; sample++)
                {
                    ConverterMeasurement n, m;
                    if ((sample & 1) == 0)
                    {
                        n = native.Measure(invocations);
                        m = mpg.Measure(invocations);
                    }
                    else
                    {
                        m = mpg.Measure(invocations);
                        n = native.Measure(invocations);
                    }
                    if (n.ElapsedTicks <= 0 || m.ElapsedTicks <= 0)
                    {
                        throw new InvalidDataException("A converter sample is shorter than the clock resolution.");
                    }
                    pairs.Add(new ConverterPair(sample, n, m));
                }
                var logs = pairs.Select(p => Math.Log(p.Mpgsql.Nanoseconds / p.Npgsql.Nanoseconds)).ToArray();
                var meanLog = logs.Average();
                var sd = Math.Sqrt(logs.Sum(value => (value - meanLog) * (value - meanLog)) / (Samples - 1));
                var half = 2.023 * sd / Math.Sqrt(Samples);
                var ratio = Math.Exp(meanLog);
                var lower = Math.Exp(meanLog - half);
                var upper = Math.Exp(meanLog + half);
                var nMean = pairs.Average(p => p.Npgsql.Nanoseconds);
                var mMean = pairs.Average(p => p.Mpgsql.Nanoseconds);
                var profile = candidateProfiles[profileId];
                rows.Add(new
                {
                    Profile = profileId,
                    profile.PostgreSqlType,
                    profile.Representation,
                    Shape = shape.Name,
                    Operation = operation,
                    Count = shape.Count,
                    NullEvery = shape.Name == "NullableArray" ? 8 : 0,
                    PayloadBytes = mpg.PayloadLength,
                    NpgsqlPayloadBytes = native.PayloadLength,
                    native.NpgsqlConverter,
                    Samples,
                    InvocationsPerSample = invocations,
                    NpgsqlReturnedValue = native.ExpectedResult,
                    MpgsqlReturnedValue = mpg.ExpectedResult,
                    NpgsqlNanoseconds = nMean,
                    MpgsqlNanoseconds = mMean,
                    NpgsqlOperationsPerSecond = 1e9 / nMean,
                    MpgsqlOperationsPerSecond = 1e9 / mMean,
                    NpgsqlThreadBytes = pairs.Average(p => p.Npgsql.ThreadBytes),
                    MpgsqlThreadBytes = pairs.Average(p => p.Mpgsql.ThreadBytes),
                    NpgsqlProcessBytes = pairs.Average(p => p.Npgsql.ProcessBytes),
                    MpgsqlProcessBytes = pairs.Average(p => p.Mpgsql.ProcessBytes),
                    MpgsqlOverNpgsql = ratio,
                    ArithmeticMeanRatio = mMean / nMean,
                    Ratio95Lower = lower,
                    Ratio95Upper = upper,
                    LatencyReductionPercent = (1 - ratio) * 100,
                    GeometricRatioCriterionPassed = upper <= 0.90,
                    MeetsTenPercentLatencyBound = upper <= 0.90 && mMean / nMean <= 0.90,
                    Pairs = pairs
                });
                Console.WriteLine($"{shape.Name}/{profileId}/{operation}: Npgsql {nMean:F2} ns; Mpgsql {mMean:F2} ns; ratio {ratio:F4} [{lower:F4}, {upper:F4}]; mean ratio {mMean / nMean:F4}; >=10% lower latency: {upper <= 0.90 && mMean / nMean <= 0.90}");
                Save();
            }
        }

        void Save()
        {
            var fullOutput = Path.GetFullPath(output);
            Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
            using var process = Process.GetCurrentProcess();
            var json = JsonSerializer.Serialize(new
            {
                Baseline = Path.GetFullPath(baselinePath),
                Candidate = Path.GetFullPath(candidatePath),
                BaselineAssemblies = baselineHashes,
                CandidateAssemblies = candidateHashes,
                RunMetadata = runtimeMetadata,
                BaselineSnapshot = baselineMetadata,
                CandidateSnapshot = candidateMetadata,
                Runtime = RuntimeInformation.FrameworkDescription,
                OS = RuntimeInformation.OSDescription,
                ProcessPriority = process.PriorityClass.ToString(),
                LogicalProcessors = Environment.ProcessorCount,
                ServerGC = GCSettings.IsServerGC,
                Samples,
                WarmupMilliseconds = warmupMilliseconds,
                TargetSampleMilliseconds = targetSampleMilliseconds,
                Focused = focused,
                SelectedIDs = focused ? requiredCaseIds : null,
                ExpectedProfiles = expectedProfiles,
                ExpectedSeries = expectedSeries,
                CompletedSeries = rows.Count,
                SkippedCandidateProfiles = skippedCandidateProfiles,
                Confidence = "95% paired Student t on log latency ratios; 40 pairs; t(39)=2.023; no outlier filtering",
                Acceptance = "Upper paired geometric ratio confidence bound <= 0.90 and arithmetic mean ratio <= 0.90; confirm in independent repeats. The interval is for log ratios, not arithmetic means; an unstable mean remains inconclusive.",
                Scope = "Actual version-pinned Npgsql converters vs public Mpgsql converter APIs; same catalog fixtures; no SQL, sockets or outer field framing; owned reads and sized writes",
                HarnessOverhead = "Both loops call a cached Func<int> benchmark delegate and add its result to a checksum. No overhead subtraction. Delegate overhead matters for scalar methods lasting only a few nanoseconds; use focused BenchmarkDotNet confirmation when needed.",
                AllocationScope = "ThreadBytes uses GC.GetAllocatedBytesForCurrentThread on the synchronous measurement thread. ProcessBytes uses precise GC.GetTotalAllocatedBytes for the whole managed process, including background threads. Fixture, pilot and warmup allocations are excluded; both counters are outside the timed loop.",
                ChecksumValidation = "Each implementation must return its own stable setup value. Npgsql and Mpgsql return values are recorded separately and need not be numerically equal; complete payload equivalence is checked by the benchmark fixture outside timing.",
                Complete = rows.Count == expectedSeries,
                Results = rows
            }, new JsonSerializerOptions { WriteIndented = true });
            var temporary = fullOutput + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, fullOutput, true);
        }
    }

    private static void Warmup(ConverterRun native, ConverterRun mpg, int warmupMilliseconds)
    {
        var end = Stopwatch.GetTimestamp() + (long)(warmupMilliseconds / 1000.0 * Stopwatch.Frequency);
        do
        {
            native.Measure(4);
            mpg.Measure(4);
        }
        while (Stopwatch.GetTimestamp() < end);
    }

    private static int Pilot(ConverterRun native, ConverterRun mpg, int targetSampleMilliseconds)
    {
        var invocations = 1;
        while (true)
        {
            var n = native.Measure(invocations);
            var m = mpg.Measure(invocations);
            var elapsedNs = Math.Max(n.Nanoseconds, m.Nanoseconds) * invocations;
            if (elapsedNs >= 2_000_000 || invocations == MaximumInvocations)
            {
                return (int)Math.Clamp(targetSampleMilliseconds * 1_000_000.0 /
                    Math.Max(n.Nanoseconds, m.Nanoseconds), 1, MaximumInvocations);
            }
            invocations = Math.Min(MaximumInvocations, checked(invocations * 4));
        }
    }

    private static Type BenchmarkType(VersionContext context, string name)
    {
        return context.Benchmarks.GetType("Mpgsql.Benchmarks.Converters." + name, true)!;
    }

    private static string[] Cases(Type type)
    {
        var instance = Activator.CreateInstance(type)!;
        var values = (IEnumerable)type.GetProperty("Cases")!.GetValue(instance)!;
        return values.Cast<string>().ToArray();
    }

    private static Dictionary<string, ConverterProfileInfo> Profiles(VersionContext context)
    {
        var catalog = context.Benchmarks.GetType("Mpgsql.Benchmarks.Converters.ConverterCatalog", true)!;
        var profiles = (IEnumerable)catalog.GetProperty("Profiles", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var result = new Dictionary<string, ConverterProfileInfo>(StringComparer.Ordinal);
        foreach (var profile in profiles)
        {
            var type = profile.GetType();
            string Get(string name) => type.GetProperty(name)!.GetValue(profile)!.ToString()!;
            result.Add(Get("Id"), new ConverterProfileInfo(Get("PostgreSqlType"), Get("Representation"), Get("Shape")));
        }
        return result;
    }

    private static Dictionary<string, string> Hashes(string benchmarkPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(benchmarkPath))!;
        string[] names = ["Mpgsql.Benchmarks.dll", "Mpgsql.Protocol.dll", "Mpgsql.dll", "Mpgsql.Sessions.dll", "Mpgsql.Multiplexing.dll", "Npgsql.dll"];
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var file = Path.Combine(directory, name);
            if (!File.Exists(file))
            {
                continue;
            }
            using var stream = File.OpenRead(file);
            hashes.Add(name, Convert.ToHexString(SHA256.HashData(stream)));
        }
        return hashes;
    }

    private sealed class ConverterRun : IDisposable
    {
        private readonly Action _cleanup;
        private readonly Func<int> _run;

        internal ConverterRun(Type type, string method, string profile, int count)
        {
            var instance = Activator.CreateInstance(type)!;
            type.GetProperty("Case")!.SetValue(instance, profile);
            type.GetProperty("Count")?.SetValue(instance, count);
            _cleanup = type.GetMethod("Cleanup")!.CreateDelegate<Action>(instance);
            try
            {
                type.GetMethod("Setup")!.CreateDelegate<Action>(instance)();
                _run = type.GetMethod(method)!.CreateDelegate<Func<int>>(instance);
                var fixture = type.GetField("_case", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
                var fixtureType = fixture.GetType();
                const BindingFlags members = BindingFlags.Instance | BindingFlags.NonPublic;
                PayloadLength = (int)fixtureType.GetProperty("PayloadLength", members)!.GetValue(fixture)!;
                NpgsqlConverter = (string)fixtureType.GetProperty("NpgsqlConverter", members)!.GetValue(fixture)!;
                ExpectedResult = _run();
                if (ExpectedResult < 0)
                {
                    throw new InvalidDataException("Converter benchmark returned a negative value.");
                }
            }
            catch
            {
                // Setup may fail before assigning its fixture; preserve the original
                // failure if that benchmark's cleanup cannot handle partial setup.
                try { _cleanup(); }
                catch { }
                throw;
            }
        }

        internal int ExpectedResult { get; }
        internal int PayloadLength { get; }
        internal string NpgsqlConverter { get; }

        internal ConverterMeasurement Measure(int invocations)
        {
            var processBefore = GC.GetTotalAllocatedBytes(true);
            var threadBefore = GC.GetAllocatedBytesForCurrentThread();
            var run = _run;
            long checksum = 0;
            var start = Stopwatch.GetTimestamp();
            for (var i = 0; i < invocations; i++)
            {
                checksum += run();
            }
            var ticks = Stopwatch.GetTimestamp() - start;
            var threadBytes = GC.GetAllocatedBytesForCurrentThread() - threadBefore;
            var processBytes = GC.GetTotalAllocatedBytes(true) - processBefore;
            Volatile.Write(ref _checksumSink, checksum);
            // A one-invocation pilot can be shorter than the clock resolution.
            // Calibration grows the block; completed timed samples must be positive.
            if (checksum != (long)ExpectedResult * invocations || ticks < 0)
            {
                throw new InvalidDataException("Unstable converter checksum or invalid elapsed time.");
            }
            return new ConverterMeasurement(ticks * 1e9 / Stopwatch.Frequency / invocations,
                (double)threadBytes / invocations, (double)processBytes / invocations, checksum, ticks);
        }

        public void Dispose()
        {
            _cleanup();
        }
    }

    private sealed record ConverterProfileInfo(string PostgreSqlType, string Representation, string Shape);
    private readonly record struct ConverterMeasurement(double Nanoseconds, double ThreadBytes, double ProcessBytes, long Checksum, long ElapsedTicks);
    private readonly record struct ConverterPair(int Sample, ConverterMeasurement Npgsql, ConverterMeasurement Mpgsql);
}
