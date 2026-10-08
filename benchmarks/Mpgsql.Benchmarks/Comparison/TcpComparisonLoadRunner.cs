using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Mpgsql.Benchmarks.Queries;

namespace Mpgsql.Benchmarks.Comparison;

internal static class TcpComparisonLoadRunner
{
    private const int WarmupRequests = 256;

    internal static async Task RunAsync(string[] args)
    {
        var options = Options(args);
        Directory.CreateDirectory(options.Directory);
        var metadata = QueryRunMetadata.Capture("Loopback TCP; shared fixed protocol 3.0 transcripts; synthetic trusted Startup; no PostgreSQL.");
        var results = new List<Result>();
        Console.WriteLine($"TCP comparison load: {options.Requests} requests/sample, {options.Samples} samples; {WarmupRequests} warmups; closed-loop; whole-process allocations.");
        foreach (var profile in options.Profiles)
        foreach (var driver in Enum.GetValues<ComparisonDriver>())
        {
            await using var fixture = await TcpComparisonFixture
                .CreateAsync
                (
                    driver, profile,
                    options.SyncGroupSize, options.SyncGroupTimeoutMs
                )
                .ConfigureAwait(false);
            _ = await MeasureAsync(fixture, profile, WarmupRequests, 0)
                .ConfigureAwait(false);
            var samples = new Sample[options.Samples];
            for (var i = 0;
                 i < samples.Length;
                 i++)
            {
                samples[i] = await MeasureAsync(fixture, profile, options.Requests, i + 1)
                    .ConfigureAwait(false);
                Console.WriteLine
                (
                    FormattableString.Invariant
                    (
                        $"{profile.Name} {driver} sample {i + 1}: {samples[i].Metrics.RequestsPerSecond:F0} req/s; p99 {samples[i].Metrics.P99Ms:F3} ms; {samples[i].Metrics.AllocatedBytesPerRequest:F1} B/request"
                    )
                );
            }
            results.Add
            (
                new Result
                (
                    driver.ToString(), profile, fixture.Mpgsql is null ? null : profile.InFlight,
                    fixture.Mpgsql is null ? null : profile.RowBudget,
                    fixture.Mpgsql is null ? null : options.SyncGroupSize, fixture.Mpgsql is null ? null : options.SyncGroupTimeoutMs,
                    fixture.Peer.Counters()
                        .Connections,
                    fixture.Npgsql?.ConnectionString, fixture.Catalog.PreparedReplyBytes, Aggregate(samples), samples
                )
            );
            Save(options, metadata, results, false);
        }
        Save(options, metadata, results, true);
        Console.WriteLine("TCP comparison artifacts: " + options.Directory);
    }

    private static async Task<Sample> MeasureAsync(
        TcpComparisonFixture fixture, QueryLoadProfile profile,
        int requests, int number
    )
    {
        fixture.CheckIdle();
        fixture.Mpgsql?.ResetObservation();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new Task<WorkerSample>[profile.Callers];
        for (var worker = 0;
             worker < pending.Length;
             worker++)
        {
            var count = worker >= requests ? 0 : (requests - 1 - worker) / profile.Callers + 1;
            pending[worker] = WorkAsync(worker, new long[count], new long[count]);
        }
        var before = Counters(fixture);
        var allocated = GC.GetTotalAllocatedBytes(true);
        int g0 = GC.CollectionCount(0),
            g1 = GC.CollectionCount(1),
            g2 = GC.CollectionCount(2);
        var start = Stopwatch.GetTimestamp();
        gate.SetResult();
        var workers = await Task
            .WhenAll(pending)
            .WaitAsync(TimeSpan.FromMinutes(5))
            .ConfigureAwait(false);
        var end = Stopwatch.GetTimestamp();
        allocated = GC.GetTotalAllocatedBytes(true) - allocated;
        g0 = GC.CollectionCount(0) - g0;
        g1 = GC.CollectionCount(1) - g1;
        g2 = GC.CollectionCount(2) - g2;
        var after = Counters(fixture);
        fixture.CheckIdle();
        var groupSize = fixture.Mpgsql?.Source.Options.SyncGroupSize ?? 1;
        var syncs = after.Syncs - before.Syncs;
        TcpComparisonVerification.Check
        (
            after.Queries - before.Queries == requests
            && syncs >= (requests + (long)groupSize - 1) / groupSize && syncs <= requests,
            "TCP load request/Sync counts (independent or explicitly shared boundaries)"
        );
        return new Sample
        (
            number, Summarize
            (
                workers, (double)(end - start) / Stopwatch.Frequency, allocated, g0, g1, g2,
                after.Queries - before.Queries, after.Syncs - before.Syncs, after.ReplyBytes - before.ReplyBytes,
                after.Flushes - before.Flushes, after.Bytes - before.Bytes, after.Copied - before.Copied, fixture.Mpgsql?.MaxObservedBuffered
            ), workers
        );

        async Task<WorkerSample> WorkAsync(
            int worker, long[] latencies,
            long[] firstRows
        )
        {
            var slow = profile.Mixed && worker % 8 == 0;
            long expected = (slow ? QueryScenario.Slow : QueryScenario.One).Expected(worker),
                checksum = 0;
            await gate.Task.ConfigureAwait(false);
            for (var i = 0;
                 i < latencies.Length;
                 i++)
            {
                var timing = await fixture
                    .TimedAsync(worker, slow)
                    .ConfigureAwait(false);
                latencies[i] = timing.LatencyTicks;
                firstRows[i] = timing.FirstRowTicks;
                checksum += timing.Checksum;
            }
            TcpComparisonVerification.Check(checksum == expected * latencies.Length, "TCP load worker checksum");
            return new WorkerSample(worker, slow, checksum, latencies, firstRows);
        }
    }
    private static (long Queries, long Syncs, long ReplyBytes, long? Flushes, long? Bytes, long? Copied) Counters(TcpComparisonFixture fixture)
    {
        var peer = fixture.Peer.Counters();
        return (peer.Queries, peer.Syncs, peer.ReplyBytes,
            fixture.Mpgsql?.Transports.Sum(x => x.Writer.Flushes), fixture.Mpgsql?.Transports.Sum(x => x.Writer.Bytes),
            fixture.Mpgsql?.Transports.Sum(x => x.Session.CopiedRowBytes));
    }
    private static Metrics Summarize(
        WorkerSample[] workers, double seconds,
        long allocated, int g0,
        int g1, int g2,
        long queries, long syncs,
        long replies, long? flushes,
        long? bytes, long? copied,
        long? observed
    )
    {
        long[] latencies = [.. workers.SelectMany(x => x.LatencyTicks)],
            first = [.. workers.SelectMany(x => x.FirstRowTicks)];
        Array.Sort(latencies);
        Array.Sort(first);
        var unit = 1000.0 / Stopwatch.Frequency;

        double Percentile(long[] sorted, double p)
        {
            return QueryLoadRunner.Rank(sorted, p) * unit;
        }

        double? RoleP99(bool slow)
        {
            long[] values =
            [
                .. workers
                    .Where(x => x.Slow == slow)
                    .SelectMany(x => x.LatencyTicks)
            ];
            if (values.Length == 0)
            {
                return null;
            }
            Array.Sort(values);
            return Percentile(values, .99);
        }

        return new Metrics
        (
            latencies.LongLength, seconds, latencies.Length / seconds, latencies.Average(x => (double)x) * unit,
            Percentile(latencies, .50), Percentile(latencies, .95), Percentile(latencies, .99), latencies[^1] * unit,
            Percentile(first, .50), Percentile(first, .95), Percentile(first, .99), allocated, (double)allocated / latencies.Length,
            g0, g1, g2, queries, syncs, replies, flushes, bytes, copied, observed, RoleP99(false), RoleP99(true)
        );
    }
    private static Metrics Aggregate(Sample[] samples)
    {
        var m = samples
            .Select(x => x.Metrics)
            .ToArray();
        return Summarize
        (
            [.. samples.SelectMany(x => x.Workers)], m.Sum(x => x.Seconds), m.Sum(x => x.AllocatedBytes),
            m.Sum(x => x.Gen0), m.Sum(x => x.Gen1), m.Sum(x => x.Gen2), m.Sum(x => x.Queries), m.Sum(x => x.Syncs),
            m.Sum(x => x.PeerReplyBytes), NullableSum(x => x.ClientFlushes), NullableSum(x => x.ClientBytes),
            NullableSum(x => x.CopiedRowBytes), m.All(x => x.MaxObservedBufferedRowBytesPerConnection is null)
                ? null
                : m.Max(x => x.MaxObservedBufferedRowBytesPerConnection)
        );

        long? NullableSum(Func<Metrics, long?> select)
        {
            return m.All(x => select(x) is null) ? null : m.Sum(x => select(x));
        }
    }
    private static void Save(
        LoadOptions options, object metadata,
        List<Result> results, bool completed
    )
    {
        var document = new
        {
            SchemaVersion = 2,
            Completed = completed,
            Metadata = metadata,
            Settings = new
            {
                options.Requests,
                options.Samples,
                Profiles = options
                    .Profiles.Select(x => x.Name)
                    .ToArray(),
                WarmupRequests,
                SlowReaderDelayMs = 1,
                SlowReaderEveryRows = 8,
                options.SyncGroupSize,
                options.SyncGroupTimeoutMs,
                SyncBoundary = options.SyncGroupSize == 1
                    ? "Independent per-request boundaries for both drivers."
                    : "Mpgsql explicitly shares transaction/error boundaries; Npgsql native references retain independent requests. These semantics differ.",
                CommandLifetime = "Npgsql command and typed parameters reused per worker; Mpgsql one-shot logical batches; caller command construction excluded.",
                ByteaConsumption = "Full copy into reusable per-worker destination for both drivers.",
                NpgsqlTypeLoading = false,
                NpgsqlNoResetOnClose = true,
                NpgsqlAutoPrepare = 0,
                MissingNpgsqlMetrics = "Client FlushAsync, CopiedRowBytes and BufferedRowBytes unavailable through public native Npgsql API; recorded as null."
            },
            Results = results
        };
        File.WriteAllText
        (
            Path.Combine(options.Directory, "comparison-load.json"), JsonSerializer.Serialize
            (
                document, new JsonSerializerOptions
                {
                    WriteIndented = true
                }
            )
        );
        var csv = new StringBuilder
        (
            "Driver,Profile,Sample,Requests,Seconds,RequestsPerSecond,MeanMs,P50Ms,P95Ms,P99Ms,MaxMs,FirstRowP50Ms,FirstRowP95Ms,FirstRowP99Ms,AllocatedBytes,AllocatedBytesPerRequest,Gen0,Gen1,Gen2,Queries,Syncs,PeerReplyBytes,ClientFlushes,ClientBytes,CopiedRowBytes,MaxObservedBufferedRowBytesPerConnection,ShortRequestP99Ms,SlowRequestP99Ms\n"
        );
        var md = new StringBuilder
        (
            "# Mpgsql / Npgsql TCP comparison load\n\n" +
            "Shared synthetic PostgreSQL protocol 3.0 peer on loopback TCP. Whole-process allocations include driver, peer and timed runner. All physical connections prewarmed. Fixed sequential workers: closed loop, no fixed arrival rate or coordinated-omission correction. Native Npgsql pool and multiplexed pool are separate series. Npgsql commands/typed parameters reused, auto-prepare disabled, type loading disabled, NoResetOnClose=true. Bytea copied fully into reusable destination for both drivers. Npgsql has no public equivalent for the Mpgsql in-flight limit or row budget; those limits apply only to Mpgsql.\n\n"
            +
            "Npgsql FlushAsync/copy/buffer counters are unavailable and left blank. Mpgsql observed buffered bytes measure sampled per-connection payload reservations, not physical memory or an exact peak. Slow timings include Task.Delay and OS scheduling.\n\n"
        );
        md.AppendLine(completed ? $"All {results.Count} series completed.\n" : "Partial run.\n");
        if (options.SyncGroupSize > 1)
        {
            md.AppendLine
            (
                $"Mpgsql opt-in shared Sync: N={options.SyncGroupSize}, X={options.SyncGroupTimeoutMs} ms from first admission per transport. Npgsql native references keep independent transaction/error boundaries. This is a policy experiment with different semantics, not the original like-for-like baseline.\n"
            );
        }
        md.AppendLine
        (
            "| Profile | Driver | Requests/s | p50 ms | p95 ms | p99 ms | Short p99 ms | Slow p99 ms | B/request | Flush/request | Copied B/request | Max observed buffered B |\n| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |"
        );
        foreach (var result in results)
        {
            Csv(result, "aggregate", result.Aggregate);
            foreach (var sample in result.Samples)
            {
                Csv(result, sample.Number.ToString(CultureInfo.InvariantCulture), sample.Metrics);
            }
            var m = result.Aggregate;
            md.AppendLine
            (
                FormattableString.Invariant
                (
                    $"| {result.Workload.Name} | {result.Driver} | {m.RequestsPerSecond:F0} | {m.P50Ms:F3} | {m.P95Ms:F3} | {m.P99Ms:F3} | {m.ShortRequestP99Ms:F3} | {m.SlowRequestP99Ms:F3} | {m.AllocatedBytesPerRequest:F1} | {(double?)m.ClientFlushes / m.Requests:F2} | {(double?)m.CopiedRowBytes / m.Requests:F1} | {m.MaxObservedBufferedRowBytesPerConnection} |"
                )
            );
        }
        File.WriteAllText(Path.Combine(options.Directory, "comparison-load.csv"), csv.ToString());
        File.WriteAllText(Path.Combine(options.Directory, "comparison-load.md"), md.ToString());

        void Csv(
            Result r, string sample,
            Metrics m
        )
        {
            csv.AppendLine
            (
                FormattableString.Invariant
                (
                    $"{r.Driver},{r.Workload.Name},{sample},{m.Requests},{m.Seconds:F6},{m.RequestsPerSecond:F6},{m.MeanMs:F6},{m.P50Ms:F6},{m.P95Ms:F6},{m.P99Ms:F6},{m.MaxMs:F6},{m.FirstRowP50Ms:F6},{m.FirstRowP95Ms:F6},{m.FirstRowP99Ms:F6},{m.AllocatedBytes},{m.AllocatedBytesPerRequest:F6},{m.Gen0},{m.Gen1},{m.Gen2},{m.Queries},{m.Syncs},{m.PeerReplyBytes},{m.ClientFlushes},{m.ClientBytes},{m.CopiedRowBytes},{m.MaxObservedBufferedRowBytesPerConnection},{m.ShortRequestP99Ms:F6},{m.SlowRequestP99Ms:F6}"
                )
            );
        }
    }
    private static LoadOptions Options(string[] args)
    {
        int requests = 4096,
            samples = 5;
        int syncGroupSize = 1,
            syncGroupTimeoutMs = 1;
        var directory = Path.Combine(QueryRunMetadata.RepositoryRoot(), "artifacts", "query-compare");
        var profiles = QueryLoadProfile.All;
        for (var i = 0;
             i < args.Length;
             i++)
        {
            if (args[i] == "--query-compare-load")
            {
                continue;
            }
            if (args[i] is not ("--requests" or "--samples" or "--artifacts" or "--profiles" or "--sync-group-size" or "--sync-group-timeout-ms") || i + 1 == args.Length)
            {
                throw new ArgumentException("Usage: --query-compare-load [--requests N] [--samples N] [--artifacts directory] [--profiles comma-separated-names] [--sync-group-size N] [--sync-group-timeout-ms X]");
            }
            string key = args[i],
                value = args[++i];
            if (key == "--artifacts")
            {
                directory = Path.GetFullPath(value);
            }
            else if (key == "--profiles")
            {
                var names = value.Split(',', StringSplitOptions.TrimEntries);
                if (names.Any(string.IsNullOrEmpty) || names
                        .Distinct(StringComparer.Ordinal)
                        .Count() != names.Length)
                {
                    throw new ArgumentException("--profiles requires distinct, nonempty profile names.");
                }
                profiles = [.. names.Select(QueryLoadProfile.Find)];
            }
            else
            {
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
                {
                    throw new ArgumentException(key + " must be positive.");
                }
                if (key == "--requests")
                {
                    requests = number;
                }
                else if (key == "--samples")
                {
                    samples = number;
                }
                else if (key == "--sync-group-size")
                {
                    syncGroupSize = number;
                }
                else
                {
                    syncGroupTimeoutMs = number;
                }
            }
        }
        return new LoadOptions(requests, samples, directory, profiles, syncGroupSize, syncGroupTimeoutMs);
    }

    private sealed record WorkerSample
    (
        int Worker,
        bool Slow,
        long Checksum,
        long[] LatencyTicks,
        long[] FirstRowTicks
    );

    private sealed record Metrics
    (
        long Requests,
        double Seconds,
        double RequestsPerSecond,
        double MeanMs,
        double P50Ms,
        double P95Ms,
        double P99Ms,
        double MaxMs,
        double FirstRowP50Ms,
        double FirstRowP95Ms,
        double FirstRowP99Ms,
        long AllocatedBytes,
        double AllocatedBytesPerRequest,
        int Gen0,
        int Gen1,
        int Gen2,
        long Queries,
        long Syncs,
        long PeerReplyBytes,
        long? ClientFlushes,
        long? ClientBytes,
        long? CopiedRowBytes,
        long? MaxObservedBufferedRowBytesPerConnection,
        double? ShortRequestP99Ms,
        double? SlowRequestP99Ms
    );

    private sealed record Sample(int Number, Metrics Metrics, WorkerSample[] Workers);

    private sealed record Result
    (
        string Driver,
        QueryLoadProfile Workload,
        int? MaxInFlightPerConnection,
        long? RowBudgetBytesPerConnection,
        int? SyncGroupSize,
        int? SyncGroupTimeoutMs,
        int PrewarmedPhysicalConnections,
        string? NpgsqlConnectionString,
        long PreparedPeerReplyBytes,
        Metrics Aggregate,
        Sample[] Samples
    );

    private sealed record LoadOptions
    (
        int Requests,
        int Samples,
        string Directory,
        QueryLoadProfile[] Profiles,
        int SyncGroupSize,
        int SyncGroupTimeoutMs
    );
}