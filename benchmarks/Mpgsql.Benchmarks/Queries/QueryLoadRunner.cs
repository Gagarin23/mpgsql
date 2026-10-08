using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Mpgsql.Benchmarks.Queries;

internal static class QueryLoadRunner
{
    private const int WarmupRequests = 256;
    private sealed record WorkerSample(int Worker, bool Slow, long Checksum, long[] LatencyTicks, long[] FirstRowTicks);
    private sealed record Metrics(long Requests, double Seconds, double RequestsPerSecond,
        double MeanMs, double P50Ms, double P95Ms, double P99Ms, double MaxMs,
        double FirstRowP50Ms, double FirstRowP95Ms, double FirstRowP99Ms,
        long AllocatedBytes, double AllocatedBytesPerRequest, int Gen0, int Gen1, int Gen2,
        long ClientFlushes, long ClientBytes, long CopiedRowBytes, long MaxObservedBufferedRowBytesPerConnection,
        double? ShortRequestP99Ms, double? SlowRequestP99Ms);
    private sealed record Sample(int Number, Metrics Metrics, WorkerSample[] Workers);
    private sealed record ProfileResult(QueryLoadProfile Profile, long RowBudgetBytes, long PreparedPeerReplyBytes,
        int PrewarmedSessions, Metrics Aggregate, Sample[] Samples);

    internal static async Task RunAsync(string[] args)
    {
        var options = Options(args);
        Directory.CreateDirectory(options.Directory);
        object metadata = QueryRunMetadata.Capture();
        var results = new List<ProfileResult>();
        Console.WriteLine($"Query Pipe load: {options.Requests} requests/sample, {options.Samples} samples/profile; {WarmupRequests} warmups; closed-loop; whole-process allocations.");
        foreach (var profile in QueryLoadProfile.All)
        {
            var catalog = profile.CreateCatalog();
            await using var fixture = await QuerySourceFixture.CreateAsync(catalog, profile.Connections,
                profile.InFlight, profile.RowBudget).ConfigureAwait(false);
            _ = await MeasureAsync(fixture, profile, WarmupRequests, 0).ConfigureAwait(false);
            var samples = new Sample[options.Samples];
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = await MeasureAsync(fixture, profile, options.Requests, i + 1).ConfigureAwait(false);
                Console.WriteLine(FormattableString.Invariant($"{profile.Name} sample {i + 1}: {samples[i].Metrics.RequestsPerSecond:F0} req/s; p99 {samples[i].Metrics.P99Ms:F3} ms; {samples[i].Metrics.AllocatedBytesPerRequest:F1} B/request"));
            }
            var combined = Aggregate(samples);
            results.Add(new(profile, profile.RowBudget, catalog.PreparedReplyBytes, fixture.Peers.Length, combined, samples));
            // Preserve completed profiles if a later profile fails; success is still determined by the exit code.
            Save(options.Directory, metadata, options, results, completed: false);
        }
        Save(options.Directory, metadata, options, results, completed: true);
        Console.WriteLine("Query load artifacts: " + options.Directory);
    }

    private static async Task<Sample> MeasureAsync(QuerySourceFixture fixture, QueryLoadProfile profile,
        int requests, int number)
    {
        fixture.CheckIdle();
        fixture.ResetObservation();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new Task<WorkerSample>[profile.Callers];
        for (int worker = 0; worker < pending.Length; worker++)
        {
            int count = worker >= requests ? 0 : (requests - 1 - worker) / profile.Callers + 1;
            long[] latencies = new long[count], firstRows = new long[count];
            pending[worker] = WorkAsync(worker, latencies, firstRows);
        }
        var before = Counters(fixture);
        long allocated = GC.GetTotalAllocatedBytes(precise: true);
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        long start = Stopwatch.GetTimestamp();
        gate.SetResult();
        WorkerSample[] workers;
        try { workers = await Task.WhenAll(pending).WaitAsync(TimeSpan.FromMinutes(5)).ConfigureAwait(false); }
        catch
        {
            await fixture.Source.DisposeAsync().ConfigureAwait(false);
            try { await Task.WhenAll(pending).ConfigureAwait(false); } catch { }
            throw;
        }
        long end = Stopwatch.GetTimestamp();
        allocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;
        g0 = GC.CollectionCount(0) - g0; g1 = GC.CollectionCount(1) - g1; g2 = GC.CollectionCount(2) - g2;
        var after = Counters(fixture);
        fixture.CheckIdle();
        QueryBenchmarkVerification.Check(after.Queries - before.Queries == requests && after.Syncs - before.Syncs == requests,
            "Load query/Sync counts");
        var metrics = Summarize(workers, (double)(end - start) / Stopwatch.Frequency, allocated, g0, g1, g2,
            after.Flushes - before.Flushes, after.Bytes - before.Bytes, after.Copied - before.Copied,
            fixture.MaxObservedBufferedRowBytes);
        return new(number, metrics, workers);

        async Task<WorkerSample> WorkAsync(int worker, long[] latencies, long[] firstRows)
        {
            bool slow = profile.Mixed && worker % 8 == 0;
            var scenario = slow ? QueryScenario.Slow : QueryScenario.One;
            long expected = scenario.Expected(worker), checksum = 0;
            await gate.Task.ConfigureAwait(false);
            for (int i = 0; i < latencies.Length; i++)
            {
                var timing = await QueryOperations.TimedAsync(fixture, scenario, slow ? 1 : 0, worker, slow).ConfigureAwait(false);
                latencies[i] = timing.LatencyTicks;
                firstRows[i] = timing.FirstRowTicks;
                checksum += timing.Checksum;
            }
            QueryBenchmarkVerification.Check(checksum == expected * latencies.Length, "Load worker checksum");
            return new(worker, slow, checksum, latencies, firstRows);
        }
    }

    private static (long Flushes, long Bytes, long Copied, long Queries, long Syncs) Counters(QuerySourceFixture fixture)
    {
        long flushes = 0, bytes = 0, copied = 0, queries = 0, syncs = 0;
        foreach (var peer in fixture.Peers)
        {
            flushes += peer.ClientWriter.Flushes; bytes += peer.ClientWriter.Bytes;
            copied += peer.Session.CopiedRowBytes; queries += peer.Queries; syncs += peer.Syncs;
        }
        return (flushes, bytes, copied, queries, syncs);
    }

    private static Metrics Summarize(WorkerSample[] workers, double seconds, long allocated,
        int g0, int g1, int g2, long flushes, long bytes, long copied, long observed)
    {
        long[] latencies = [.. workers.SelectMany(x => x.LatencyTicks)];
        long[] firstRows = [.. workers.SelectMany(x => x.FirstRowTicks)];
        Array.Sort(latencies); Array.Sort(firstRows);
        double unit = 1000.0 / Stopwatch.Frequency;
        double mean = latencies.Average(x => (double)x) * unit;
        return new(latencies.LongLength, seconds, latencies.Length / seconds,
            mean, Rank(latencies, .50) * unit, Rank(latencies, .95) * unit, Rank(latencies, .99) * unit, latencies[^1] * unit,
            Rank(firstRows, .50) * unit, Rank(firstRows, .95) * unit, Rank(firstRows, .99) * unit,
            allocated, (double)allocated / latencies.Length, g0, g1, g2, flushes, bytes, copied, observed,
            RoleP99(false), RoleP99(true));

        double? RoleP99(bool slow)
        {
            long[] values = [.. workers.Where(x => x.Slow == slow).SelectMany(x => x.LatencyTicks)];
            if (values.Length == 0) return null;
            Array.Sort(values);
            return Rank(values, .99) * unit;
        }
    }

    internal static long Rank(long[] sorted, double probability)
    {
        if (sorted.Length == 0 || probability is <= 0 or > 1 || double.IsNaN(probability))
            throw new ArgumentOutOfRangeException(nameof(probability));
        return sorted[(int)Math.Ceiling(probability * sorted.Length) - 1];
    }

    private static Metrics Aggregate(Sample[] samples)
    {
        var values = samples.Select(x => x.Metrics).ToArray();
        return Summarize([.. samples.SelectMany(x => x.Workers)], values.Sum(x => x.Seconds), values.Sum(x => x.AllocatedBytes),
            values.Sum(x => x.Gen0), values.Sum(x => x.Gen1), values.Sum(x => x.Gen2), values.Sum(x => x.ClientFlushes),
            values.Sum(x => x.ClientBytes), values.Sum(x => x.CopiedRowBytes), values.Max(x => x.MaxObservedBufferedRowBytesPerConnection));
    }

    private static void Save(string directory, object metadata, (int Requests, int Samples, string Directory) options,
        List<ProfileResult> results, bool completed)
    {
        var document = new {SchemaVersion = 1, Completed = completed, Metadata = metadata,
            Settings = new {options.Requests, options.Samples, WarmupRequests, SlowReaderDelayMs = 1, SlowReaderEveryRows = 8}, Profiles = results};
        File.WriteAllText(Path.Combine(directory, "load-results.json"), JsonSerializer.Serialize(document, new JsonSerializerOptions {WriteIndented = true}));
        var csv = new StringBuilder("Profile,Sample,Requests,Seconds,RequestsPerSecond,MeanMs,P50Ms,P95Ms,P99Ms,MaxMs,FirstRowP50Ms,FirstRowP95Ms,FirstRowP99Ms,AllocatedBytes,AllocatedBytesPerRequest,Gen0,Gen1,Gen2,ClientFlushes,ClientBytes,CopiedRowBytes,MaxObservedBufferedRowBytesPerConnection,ShortRequestP99Ms,SlowRequestP99Ms\n");
        var markdown = new StringBuilder("# Query/DataSource Pipe load baseline\n\nAllocation scope: whole process (driver, peer, timed runner). Sessions and reply templates are prewarmed. Closed-loop workers; no fixed arrival rate or coordinated-omission correction. Buffered bytes are sampled per-connection payload reservations, not physical memory or a guaranteed peak. Slow-reader timings include Task.Delay and OS scheduling.\n\n");
        markdown.AppendLine(completed ? "All profiles completed.\n" : "Partial run: not all profiles completed.\n");
        markdown.AppendLine("| Profile | Requests/s | p50 ms | p95 ms | p99 ms | Short p99 ms | Slow p99 ms | B/request | Flush/request | Copied B/request | Max observed buffered B |\n| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var result in results)
        {
            WriteCsv(result.Profile.Name, "aggregate", result.Aggregate);
            foreach (var sample in result.Samples) WriteCsv(result.Profile.Name, sample.Number.ToString(CultureInfo.InvariantCulture), sample.Metrics);
            var m = result.Aggregate;
            markdown.AppendLine(FormattableString.Invariant($"| {result.Profile.Name} | {m.RequestsPerSecond:F0} | {m.P50Ms:F3} | {m.P95Ms:F3} | {m.P99Ms:F3} | {m.ShortRequestP99Ms:F3} | {m.SlowRequestP99Ms:F3} | {m.AllocatedBytesPerRequest:F1} | {(double)m.ClientFlushes / m.Requests:F2} | {(double)m.CopiedRowBytes / m.Requests:F1} | {m.MaxObservedBufferedRowBytesPerConnection} |"));
        }
        File.WriteAllText(Path.Combine(directory, "load-results.csv"), csv.ToString());
        File.WriteAllText(Path.Combine(directory, "load-report.md"), markdown.ToString());
        void WriteCsv(string profile, string number, Metrics m)
            => csv.AppendLine(FormattableString.Invariant($"{profile},{number},{m.Requests},{m.Seconds:F6},{m.RequestsPerSecond:F6},{m.MeanMs:F6},{m.P50Ms:F6},{m.P95Ms:F6},{m.P99Ms:F6},{m.MaxMs:F6},{m.FirstRowP50Ms:F6},{m.FirstRowP95Ms:F6},{m.FirstRowP99Ms:F6},{m.AllocatedBytes},{m.AllocatedBytesPerRequest:F6},{m.Gen0},{m.Gen1},{m.Gen2},{m.ClientFlushes},{m.ClientBytes},{m.CopiedRowBytes},{m.MaxObservedBufferedRowBytesPerConnection},{m.ShortRequestP99Ms:F6},{m.SlowRequestP99Ms:F6}"));
    }

    private static (int Requests, int Samples, string Directory) Options(string[] args)
    {
        int requests = 4096, samples = 5;
        string directory = Path.Combine(QueryRunMetadata.RepositoryRoot(), "artifacts", "query-path");
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--query-load") continue;
            if (args[i] is not ("--requests" or "--samples" or "--artifacts") || i + 1 == args.Length)
                throw new ArgumentException("Usage: --query-load [--requests N] [--samples N] [--artifacts directory]");
            string key = args[i], value = args[++i];
            if (key == "--artifacts") directory = Path.GetFullPath(value);
            else
            {
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number <= 0)
                    throw new ArgumentException(key + " must be a positive integer.");
                if (key == "--requests") requests = number; else samples = number;
            }
        }
        return (requests, samples, directory);
    }
}
