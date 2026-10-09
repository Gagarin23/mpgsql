using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

// Complete driver operations against identical synthetic loopback TCP transcripts.
// Native mode loads Npgsql from the immutable baseline. Mpgsql-baseline mode
// compares the same Mpgsql methods in both snapshots for a direct optimization CI.
internal static class NpgsqlComparison
{
    internal static async Task RunAsync(string baselinePath, string candidatePath, string output,
        bool compareMpgsqlBaseline = false, string[]? selectedCases = null,
        bool adoOnly = false, bool standardAdo = false, AdoComparisonOptions? adoOptions = null,
        bool executionOnly = false, bool liveOnly = false)
    {
        if (standardAdo && !adoOnly)
            throw new ArgumentException("Standard ADO comparisons require an ADO-only matrix.");
        if (executionOnly && (!adoOnly || !standardAdo || compareMpgsqlBaseline))
            throw new ArgumentException("ADO execution comparison requires native Npgsql and standard ADO-only mode.");
        if (liveOnly && (!adoOnly || executionOnly || compareMpgsqlBaseline))
            throw new ArgumentException("Live ADO comparisons require native Npgsql and the reader/batch ADO-only matrix.");
        if (adoOnly)
        {
            adoOptions ??= AdoComparisonOptions.FromEnvironment();
            adoOptions.Validate();
        }
        var coalescedDiagnostic = adoOnly && adoOptions!.CoalesceReplies;
        if (coalescedDiagnostic && (executionOnly || compareMpgsqlBaseline || liveOnly))
            throw new ArgumentException("Coalesced replies are supported only for the native synthetic ADO reader/batch diagnostic.");
        var assessesAdoParity = adoOnly && !compareMpgsqlBaseline && !coalescedDiagnostic;
        string[] availableCases = executionOnly
            ? ["ParameterMutation32", "ScalarEmpty", "ScalarOneBigint", "ScalarNull", "ScalarRows128", "ReusedBatch16", "NonQueryZero"]
            : ["Empty", "OneBigint", "Rows128Columns8", "Rows4096", "Bytea64KiB", "Batch16"];
        var requestedCases = selectedCases ?? availableCases;
        if (requestedCases.Length == 0 || requestedCases.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("TCP case IDs must be nonempty.", nameof(selectedCases));
        }
        requestedCases = requestedCases.Select(value => value.Trim()).ToArray();
        var selectedCaseSet = requestedCases.ToHashSet(StringComparer.Ordinal);
        if (selectedCaseSet.Count != requestedCases.Length)
        {
            throw new ArgumentException("TCP case IDs must not contain duplicates.", nameof(selectedCases));
        }
        var unknownCases = requestedCases.Except(availableCases, StringComparer.Ordinal).ToArray();
        if (unknownCases.Length != 0)
        {
            throw new ArgumentException("Unknown TCP case IDs: " + string.Join(", ", unknownCases) + ".", nameof(selectedCases));
        }
        var selectedCaseIds = availableCases.Where(selectedCaseSet.Contains).ToArray();
        var cases = selectedCaseIds.Where(value => value != "Batch16").ToArray();
        var includesBatch = selectedCaseSet.Contains("Batch16");
        var expectedCount = executionOnly ? selectedCaseIds.Length : cases.Length * (adoOnly ? 3 : 4) + (includesBatch ? 1 : 0);
        var usesCandidateNativeWrapper = executionOnly || coalescedDiagnostic || liveOnly;
        var nativeBenchmarkPath = usesCandidateNativeWrapper ? candidatePath : baselinePath;
        string? nativeReferenceHash = null;
        if (usesCandidateNativeWrapper)
        {
            nativeReferenceHash = Hash(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(baselinePath))!, "Npgsql.dll"));
            var actualNativeHash = Hash(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(candidatePath))!, "Npgsql.dll"));
            if (actualNativeHash != nativeReferenceHash)
                throw new InvalidDataException("Current-wrapper ADO comparisons must use the immutable baseline Npgsql binary.");
        }
        var baseline = new VersionContext(nativeBenchmarkPath);
        var candidate = new VersionContext(candidatePath);
        var runtimeMetadata = ComparisonRunMetadata.Runtime();
        var baselineMetadata = ComparisonRunMetadata.Snapshot(baseline, nativeBenchmarkPath);
        var candidateMetadata = ComparisonRunMetadata.Snapshot(candidate, candidatePath);
        var baselineBenchmarkHash = Hash(baselinePath);
        var candidateBenchmarkHash = Hash(candidatePath);
        var baselineProvider = compareMpgsqlBaseline ? "Mpgsql" : "Npgsql";
        var results = new List<object>();
        var acceptedAdoSeries = 0;
        if (executionOnly)
        {
            foreach (var scenario in selectedCaseIds)
                await CompareAsync(scenario, "ADO.NET/Execution/" + scenario,
                    "TcpAdoExecutionBenchmarks", "NpgsqlExecute", "TcpAdoExecutionBenchmarks", "MpgsqlExecute");
        }
        else
        foreach (var scenario in cases)
        {
            foreach (var path in new[] { "ReusedTyped", "FreshTyped", "ReusedObject" })
            {
                var candidateMethod = standardAdo && path != "ReusedObject" ? path + "Standard" : path;
                await CompareAsync(scenario, "ADO.NET/" + path,
                    liveOnly ? "PostgresAdoBenchmarks" : "TcpAdoReaderBenchmarks", "Npgsql" + path,
                    liveOnly ? "PostgresAdoBenchmarks" : "TcpAdoReaderBenchmarks", candidateMethod);
            }
            if (!adoOnly)
            {
                await CompareAsync(scenario, compareMpgsqlBaseline ? "Multiplexing/IndependentQueries" : "Multiplexing/PersistentNative",
                    "TcpReaderComparisonBenchmarks", "NpgsqlConnection",
                    "TcpReaderComparisonBenchmarks", "MpgsqlDataSource");
            }
        }
        if (includesBatch)
        {
            await CompareAsync("Batch16", "ADO.NET/FreshBatchExecution",
                liveOnly ? "PostgresAdoBenchmarks" : "TcpFacadeBatchComparisonBenchmarks", "NpgsqlFreshBatch",
                liveOnly ? "PostgresAdoBenchmarks" : "TcpFacadeBatchComparisonBenchmarks", standardAdo ? "MpgsqlFacadeBatchStandard" : "MpgsqlFacadeBatch");
        }
        await SaveAsync();

        async Task CompareAsync(string scenario, string path,
            string nativeClass, string nativeMethod, string candidateClass, string candidateMethod)
        {
            await using var native = await BenchmarkRun.CreateAsync(baseline,
                compareMpgsqlBaseline ? candidateClass : nativeClass,
                compareMpgsqlBaseline ? candidateMethod : nativeMethod, scenario,
                instrumentTransport: adoOnly && compareMpgsqlBaseline ? false : null,
                validateWireCounters: adoOnly && !liveOnly, coalesceReplies: adoOnly ? coalescedDiagnostic : null);
            await using var mpg = await BenchmarkRun.CreateAsync(candidate, candidateClass, candidateMethod, scenario,
                instrumentTransport: adoOnly ? false : null, validateWireCounters: adoOnly && !liveOnly,
                coalesceReplies: adoOnly ? coalescedDiagnostic : null);
            if (native.Checksum != mpg.Checksum || native.Operations != mpg.Operations
                || native.QueriesPerOperation != mpg.QueriesPerOperation || native.SyncsPerOperation != mpg.SyncsPerOperation)
            {
                throw new InvalidDataException("Baseline/candidate checksum or operation-count mismatch.");
            }
            var warming = Stopwatch.StartNew();
            while (warming.Elapsed < TimeSpan.FromSeconds(adoOnly ? adoOptions!.WarmupSeconds : 5))
            {
                await native.MeasureAsync(1);
                await mpg.MeasureAsync(1);
            }
            var pilotNative = await native.MeasureAsync(8);
            var pilotMpgsql = await mpg.MeasureAsync(8);
            var nsPerInvocation = Math.Max(pilotNative.Nanoseconds, pilotMpgsql.Nanoseconds) * native.Operations;
            if (!double.IsFinite(nsPerInvocation) || nsPerInvocation <= 0)
                throw new InvalidDataException("Invalid pilot duration.");
            var targetMilliseconds = adoOnly ? adoOptions!.TargetBlockMilliseconds : 120;
            var invocationLimit = adoOnly ? adoOptions!.MaximumInvocations : 4096;
            var invocations = InvocationCount(targetMilliseconds, nsPerInvocation, invocationLimit);
            var pairCount = adoOnly ? adoOptions!.PairCount : 40;
            var pilotPairs = new List<TimedPair>();
            double? projectedHalfWidthPercent = null;
            if (adoOnly && adoOptions!.AdaptivePilot)
            {
                for (var i = 0; i < adoOptions.PilotPairCount; i++)
                    pilotPairs.Add(await MeasurePairAsync(native, mpg, invocations, i, compareMpgsqlBaseline));
                var pilotStats = Statistics(pilotPairs.Select(p => p.Sample).ToArray(), pairCount);
                var targetLogHalfWidth = Math.Log(1 + adoOptions.TargetHalfWidthPercent / 100);
                var scale = Math.Max(1, Math.Pow(pilotStats.HalfWidth / targetLogHalfWidth, 2));
                targetMilliseconds = (int)Math.Ceiling(Math.Min(adoOptions.MaximumBlockMilliseconds,
                    targetMilliseconds * scale));
                // Duration scaling is a pilot heuristic, not a precision guarantee.
                // Fix invocation and pair counts before collecting any final sample.
                var pilotInvocations = invocations;
                var calibratedNs = Math.Max(pilotPairs.Average(p => p.Sample.BaselineNanoseconds),
                    pilotPairs.Average(p => p.Sample.CandidateNanoseconds)) * native.Operations;
                invocations = InvocationCount(targetMilliseconds, calibratedNs, invocationLimit);
                var projectedHalf = pilotStats.HalfWidth * Math.Sqrt((double)pilotInvocations / invocations);
                var requiredPairs = pairCount * Math.Max(1, Math.Pow(projectedHalf / targetLogHalfWidth, 2));
                pairCount = (int)Math.Min(adoOptions.MaximumPairCount, Math.Ceiling(requiredPairs / 2) * 2);
                projectedHalfWidthPercent = (Math.Exp(projectedHalf * Math.Sqrt((double)adoOptions.PairCount / pairCount)) - 1) * 100;
            }
            var pairs = new List<Pair>(pairCount);
            var timedPairs = new List<TimedPair>(adoOnly ? pairCount : 0);
            for (var i = 0; i < pairCount; i++)
            {
                if (adoOnly)
                {
                    var timedPair = await MeasurePairAsync(native, mpg, invocations, i, compareMpgsqlBaseline);
                    timedPairs.Add(timedPair);
                    pairs.Add(timedPair.Sample);
                }
                else
                {
                    Measurement n, m;
                    if ((i & 1) == 0)
                    {
                        n = await native.MeasureAsync(invocations);
                        m = await mpg.MeasureAsync(invocations);
                    }
                    else
                    {
                        m = await mpg.MeasureAsync(invocations);
                        n = await native.MeasureAsync(invocations);
                    }
                    pairs.Add(new Pair(n.Nanoseconds, m.Nanoseconds, n.Bytes, m.Bytes));
                }
            }
            var stats = Statistics(pairs, pairCount);
            var ratio = stats.Ratio;
            var lower = stats.Lower;
            var upper = stats.Upper;
            var nativeMean = pairs.Average(p => p.BaselineNanoseconds);
            var mpgMean = pairs.Average(p => p.CandidateNanoseconds);
            var arithmeticMeanRatio = mpgMean / nativeMean;
            var meetsAdoBound = upper <= 1.01 && arithmeticMeanRatio <= 1.01;
            var row = new Dictionary<string, object?>
            {
                ["Case"] = scenario,
                ["Path"] = path,
                ["BaselineProvider"] = baselineProvider,
                ["CandidateProvider"] = "Mpgsql",
                ["Samples"] = pairCount,
                ["InvocationsPerSample"] = invocations,
                ["OperationsPerInvocation"] = native.Operations,
                [compareMpgsqlBaseline ? "BaselineMeanBlockMilliseconds" : "NpgsqlMeanBlockMilliseconds"] = nativeMean * native.Operations * invocations / 1_000_000,
                [compareMpgsqlBaseline ? "CandidateMeanBlockMilliseconds" : "MpgsqlMeanBlockMilliseconds"] = mpgMean * mpg.Operations * invocations / 1_000_000,
                ["Checksum"] = native.Checksum,
                ["BaselineConfiguredReadBufferSize"] = native.ConfiguredReadBufferSize,
                ["CandidateConfiguredReadBufferSize"] = mpg.ConfiguredReadBufferSize,
                [compareMpgsqlBaseline ? "BaselineMicroseconds" : "NpgsqlMicroseconds"] = nativeMean / 1000,
                [compareMpgsqlBaseline ? "CandidateMicroseconds" : "MpgsqlMicroseconds"] = mpgMean / 1000,
                [compareMpgsqlBaseline ? "BaselineOperationsPerSecond" : "NpgsqlOperationsPerSecond"] = 1e9 / nativeMean,
                [compareMpgsqlBaseline ? "CandidateOperationsPerSecond" : "MpgsqlOperationsPerSecond"] = 1e9 / mpgMean,
                [compareMpgsqlBaseline ? "BaselineBytes" : "NpgsqlBytes"] = pairs.Average(p => p.BaselineBytes),
                [compareMpgsqlBaseline ? "CandidateBytes" : "MpgsqlBytes"] = pairs.Average(p => p.CandidateBytes),
                [compareMpgsqlBaseline ? "CandidateOverBaseline" : "MpgsqlOverNpgsql"] = ratio,
                ["Ratio95Lower"] = lower,
                ["Ratio95Upper"] = upper,
                ["LatencyReductionPercent"] = (1 - ratio) * 100,
                ["Pairs"] = pairs
            };
            if (adoOnly)
            {
                var candidateAsyncApi = standardAdo || path == "ADO.NET/ReusedObject"
                    ? "Task (standard ADO.NET)" : "ValueTask (typed extension)";
                row.Add("BaselineMethod", compareMpgsqlBaseline ? candidateMethod : nativeMethod);
                row.Add("CandidateMethod", candidateMethod);
                row.Add("BaselineAsyncAPI", compareMpgsqlBaseline ? candidateAsyncApi : "Task (standard ADO.NET)");
                row.Add("CandidateAsyncAPI", candidateAsyncApi);
                row.Add("BaselineTransportInstrumented", native.TransportInstrumented);
                row.Add("CandidateTransportInstrumented", mpg.TransportInstrumented);
                row.Add("BaselineCoalesceReplies", native.CoalesceReplies);
                row.Add("CandidateCoalesceReplies", mpg.CoalesceReplies);
                row.Add("ReplyPolicy", liveOnly ? "Real PostgreSQL endpoint" : coalescedDiagnostic
                    ? "Synthetic extended replies buffered until frontend Sync/Flush; startup and setup-only Simple Query flush immediately"
                    : "Original synthetic peer: flush each available reply-channel wave");
                if (liveOnly)
                {
                    row.Add("BaselineEndpoint", native.LiveEndpoint);
                    row.Add("CandidateEndpoint", mpg.LiveEndpoint);
                    row.Add("BaselineBackendProcessId", native.LiveBackendProcessId);
                    row.Add("CandidateBackendProcessId", mpg.LiveBackendProcessId);
                    row.Add("BaselineServerVersion", native.LiveServerVersion);
                    row.Add("CandidateServerVersion", mpg.LiveServerVersion);
                }
                row.Add("QueriesPerOperation", mpg.QueriesPerOperation);
                row.Add("SyncsPerOperation", mpg.SyncsPerOperation);
                row.Add("QueriesPerInvocation", (long)mpg.Operations * mpg.QueriesPerOperation);
                row.Add("SyncsPerInvocation", (long)mpg.Operations * mpg.SyncsPerOperation);
                row.Add("Normalization", executionOnly
                    ? "Latency and allocations per logical operation: one parameter mutation+execution, one reused batch execution, or one scalar/nonquery call"
                    : "Latency and allocations per command execution or one fresh batch execution");
                if (executionOnly && scenario.StartsWith("Scalar", StringComparison.Ordinal))
                    row.Add("ScalarReturnEncoding", "null (no row) = -2; DBNull.Value (SQL NULL) = -1; Int64 value = value; other CLR types rejected");
                if (scenario == "Bytea64KiB")
                {
                    var candidateByteaApi = standardAdo || path == "ADO.NET/ReusedObject"
                        ? "GetBytes: one full copy into a reusable destination"
                        : "Borrowed GetRawValue.CopyTo: one full copy into a reusable destination";
                    row.Add("BaselineByteaAPI", compareMpgsqlBaseline ? candidateByteaApi
                        : "GetBytes: one full copy into a reusable destination");
                    row.Add("CandidateByteaAPI", candidateByteaApi);
                }
                if (assessesAdoParity)
                {
                    row.Add("MeetsOnePercentLogConfidenceBound", upper <= 1.01);
                    row.Add("MeetsOnePercentArithmeticMeanBound", arithmeticMeanRatio <= 1.01);
                    row.Add("MeetsOnePercentLatencyBound", meetsAdoBound);
                    row.Add("AcceptanceStatus", meetsAdoBound ? "Pass" : lower > 1.01 ? "Fail" : "Inconclusive");
                    if (meetsAdoBound) acceptedAdoSeries++;
                }
                row.Add("ArithmeticMeanRatio", arithmeticMeanRatio);
                row.Add("Ratio95UpperHalfWidthPercent", (Math.Exp(stats.HalfWidth) - 1) * 100);
                row.Add("TargetSampleMilliseconds", targetMilliseconds);
                row.Add("ProjectedPilotHalfWidthPercent", projectedHalfWidthPercent);
                row.Add("PilotPairs", pilotPairs);
                row.Add("TimedPairs", timedPairs);
                row.Add("WireValidation", liveOnly
                    ? "Every block: full expected checksum and healthy idle connection/PID; query and Sync counts below describe intended operations, not captured wire counters"
                    : "Every block: full checksum; healthy idle fixtures; query and Sync deltas; unchanged physical connection count");
            }
            else
                row.Add("MeetsTenPercentLatencyBound", upper <= 0.90);
            if (compareMpgsqlBaseline && !adoOnly)
            {
                row.Add("MeetsFivePercentRegressionBound", upper <= 1.05);
            }
            results.Add(row);
            var baselineLabel = compareMpgsqlBaseline ? "Mpgsql baseline" : "Npgsql";
            var acceptance = coalescedDiagnostic
                ? $"Coalesced synthetic diagnostic only; arithmetic ratio {arithmeticMeanRatio:F4}; {pairCount} pairs"
                : adoOnly && compareMpgsqlBaseline
                ? $"Mpgsql self-comparison only; arithmetic ratio {arithmeticMeanRatio:F4}; {pairCount} pairs"
                : adoOnly ? $"<=1% slower (CI and mean): {meetsAdoBound}; arithmetic ratio {arithmeticMeanRatio:F4}; {pairCount} pairs"
                : $">=10% lower latency: {upper <= 0.90}";
            Console.WriteLine($"{path}/{scenario}: {baselineLabel} {nativeMean / 1000:F2} us; Mpgsql candidate {mpgMean / 1000:F2} us; ratio {ratio:F4} [{lower:F4}, {upper:F4}]; {acceptance}");
            // Keep completed measurements if a later scenario fails.
            await SaveAsync();
        }

        async Task SaveAsync()
        {
            var fullOutput = System.IO.Path.GetFullPath(output);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullOutput)!);
            using var process = Process.GetCurrentProcess();
            await File.WriteAllTextAsync(fullOutput, JsonSerializer.Serialize(new
            {
                Baseline = System.IO.Path.GetFullPath(baselinePath),
                Candidate = System.IO.Path.GetFullPath(candidatePath),
                BaselineProvider = baselineProvider,
                CandidateProvider = "Mpgsql",
                BaselineBenchmarkSha256 = baselineBenchmarkHash,
                CandidateBenchmarkSha256 = candidateBenchmarkHash,
                NativeBenchmarkWrapper = compareMpgsqlBaseline ? null : System.IO.Path.GetFullPath(nativeBenchmarkPath),
                NativeBenchmarkWrapperSha256 = compareMpgsqlBaseline ? null : usesCandidateNativeWrapper ? candidateBenchmarkHash : baselineBenchmarkHash,
                ImmutableNativeReferenceSha256 = nativeReferenceHash,
                RunMetadata = runtimeMetadata,
                BaselineSnapshot = baselineMetadata,
                CandidateSnapshot = candidateMetadata,
                Runtime = RuntimeInformation.FrameworkDescription,
                OS = RuntimeInformation.OSDescription,
                ProcessPriority = process.PriorityClass.ToString(),
                LogicalProcessors = Environment.ProcessorCount,
                ServerGC = GCSettings.IsServerGC,
                Confidence = adoOnly
                    ? "Individual nominal 95% paired Student t intervals on log latency ratios, not arithmetic mean ratio intervals; final N >= 40; conservative t(39)=2.023; serial-pair independence assumption; no outlier filtering; not simultaneous bands"
                    : "95% paired Student t on log latency ratios; 40 pairs; t(39)=2.023; no outlier filtering",
                WarmupSeconds = adoOnly ? adoOptions!.WarmupSeconds : 5,
                TargetSampleMilliseconds = adoOnly ? adoOptions!.TargetBlockMilliseconds : 120,
                Mode = liveOnly ? standardAdo ? "NativeAdoLiveStandard" : "NativeAdoLiveTypedExtensions"
                    : coalescedDiagnostic ? standardAdo ? "NativeAdoCoalescedStandardDiagnostic" : "NativeAdoCoalescedTypedDiagnostic"
                    : adoOnly && compareMpgsqlBaseline ? "MpgsqlAdoBaseline" : executionOnly ? "NativeAdoExecution" : adoOnly ? standardAdo ? "NativeAdoStandard" : "NativeAdoTypedExtensions" : compareMpgsqlBaseline ? "MpgsqlBaseline" : "NativeLegacy21",
                AdoOptions = adoOnly ? adoOptions : null,
                StopwatchFrequency = Stopwatch.Frequency,
                SelectedCases = selectedCaseIds,
                ExpectedSeries = expectedCount,
                CompletedSeries = results.Count,
                AcceptedAdoSeries = assessesAdoParity ? (int?)acceptedAdoSeries : null,
                AllSelectedAdoSeriesPass = assessesAdoParity ? (bool?)(results.Count == expectedCount && acceptedAdoSeries == expectedCount) : null,
                Acceptance = coalescedDiagnostic
                    ? "Informational synthetic coalesced-reply diagnostic; no acceptance criterion; does not replace original synthetic or real PostgreSQL acceptance runs"
                    : adoOnly && compareMpgsqlBaseline
                    ? "Informational Mpgsql candidate/baseline comparison using identical methods; no acceptance criterion or Npgsql parity assessment"
                    : adoOnly
                    ? "All selected series: upper paired log Mpgsql/Npgsql latency ratio confidence bound <= 1.01 AND arithmetic mean latency ratio <= 1.01 in each independent repeat; interval is not an arithmetic mean CI; crossing interval or contradictory arithmetic ratio is inconclusive; never select the best repeat"
                    : "Upper paired ratio confidence bound <= 0.90; confirm in independent repeats",
                AllocationScope = liveOnly ? "Managed client process only; PostgreSQL/proxy server processes excluded; no subtraction"
                    : "Whole managed process, including the identical synthetic TCP peer; no subtraction",
                Scope = liveOnly
                    ? "Real PostgreSQL/proxy endpoint; current benchmark wrappers for both providers; immutable Npgsql binary verified by SHA-256; preopened exclusive connection; real SQL execution and complete reader lifecycle included; no CountingPipeWriter; physical startup/TLS and per-operation leasing excluded; fresh Batch16 caller construction excluded, execution and disposal included"
                    : coalescedDiagnostic
                    ? "Separate synthetic reply-coalescing diagnostic; current benchmark wrappers for both providers; immutable Npgsql binary verified by SHA-256; same ADO16 methods, payloads, checksums, counters and timers; preopened exclusive connections; no CountingPipeWriter; SQL execution, TLS, startup and per-operation leasing excluded"
                    : executionOnly
                    ? "Standard Task ADO.NET execution suite; current benchmark wrappers for both providers; immutable Npgsql binary verified by SHA-256; preopened exclusive connection; command and reused batch construction/disposal outside timing; parameter mutation, scalar/nonquery, full result consumption and reader disposal timed; SQL execution, TLS, startup and pool leasing excluded; no CountingPipeWriter"
                    : adoOnly
                    ? compareMpgsqlBaseline
                        ? "Frozen Mpgsql baseline vs frozen Mpgsql candidate ADO.NET only; identical methods and API representations; preopened exclusive connection; complete reader lifecycle; no CountingPipeWriter; SQL execution, TLS, startup and per-operation pool leasing excluded; Batch16 caller construction excluded, execution and disposal included"
                        : "Native immutable Npgsql vs Mpgsql ADO.NET only; preopened exclusive connection; complete reader lifecycle; no CountingPipeWriter; SQL execution, TLS, startup and per-operation pool leasing excluded; Batch16 caller construction excluded, execution and disposal included"
                    : compareMpgsqlBaseline
                    ? "Original Mpgsql vs candidate Mpgsql using identical ADO.NET and multiplexing methods; SQL execution, TLS and physical connection startup excluded"
                    : "Native Npgsql 10.0.3 vs Mpgsql; SQL execution, TLS and physical connection startup excluded",
                Complete = results.Count == expectedCount,
                Results = results
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static int InvocationCount(int milliseconds, double nsPerInvocation, int limit)
        => (int)Math.Clamp(milliseconds * 1_000_000d / nsPerInvocation, 1, limit);

    private static (double Ratio, double Lower, double Upper, double HalfWidth) Statistics(IReadOnlyList<Pair> pairs, int inferencePairCount)
    {
        if (pairs.Any(p => !double.IsFinite(p.CandidateNanoseconds) || p.CandidateNanoseconds <= 0
            || !double.IsFinite(p.BaselineNanoseconds) || p.BaselineNanoseconds <= 0))
            throw new InvalidDataException("Latency samples must be finite and positive.");
        var logs = pairs.Select(p => Math.Log(p.CandidateNanoseconds / p.BaselineNanoseconds)).ToArray();
        var mean = logs.Average();
        var sd = Math.Sqrt(logs.Sum(value => (value - mean) * (value - mean)) / (pairs.Count - 1));
        var half = AdoComparisonOptions.CriticalValue * sd / Math.Sqrt(inferencePairCount);
        return (Math.Exp(mean), Math.Exp(mean - half), Math.Exp(mean + half), half);
    }

    private static async Task<TimedPair> MeasurePairAsync(BenchmarkRun baseline, BenchmarkRun candidate, int invocations, int index,
        bool compareMpgsqlBaseline = false)
    {
        var utc = DateTimeOffset.UtcNow;
        var start = Stopwatch.GetTimestamp();
        Measurement n, m;
        if ((index & 1) == 0)
        {
            n = await baseline.MeasureAsync(invocations);
            m = await candidate.MeasureAsync(invocations);
        }
        else
        {
            m = await candidate.MeasureAsync(invocations);
            n = await baseline.MeasureAsync(invocations);
        }
        var firstProvider = (index & 1) == 0
            ? compareMpgsqlBaseline ? "Mpgsql baseline" : "Npgsql"
            : compareMpgsqlBaseline ? "Mpgsql candidate" : "Mpgsql";
        return new TimedPair(index, firstProvider, utc, start, Stopwatch.GetTimestamp(),
            new Pair(n.Nanoseconds, m.Nanoseconds, n.Bytes, m.Bytes));
    }

    private sealed record TimedPair(int Index, string FirstProvider, DateTimeOffset StartedUtc,
        long StartTimestamp, long EndTimestamp, Pair Sample);

    private static string Hash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
