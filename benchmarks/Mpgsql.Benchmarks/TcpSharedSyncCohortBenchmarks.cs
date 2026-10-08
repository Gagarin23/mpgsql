using BenchmarkDotNet.Attributes;
using Mpgsql.Benchmarks.Comparison;
using Mpgsql.Benchmarks.Queries;
using Npgsql;
using NpgsqlTypes;

namespace Mpgsql.Benchmarks;

// Complete cohorts have identical SQL/parameters/transcripts and exactly one common Sync.
// Mpgsql routes to separate DataSource readers; Npgsql exposes one native Batch reader.
[MemoryDiagnoser, JsonExporterAttribute.Full, Config(typeof(QueryBenchmarkConfig)), IterationTime(150)]
public class TcpSharedSyncCohortBenchmarks
{
    private NpgsqlBatch? _batch;
    private QueryCatalog _catalog = null!;
    private TcpMpgsqlFixture? _m;
    private TcpNpgsqlFixture? _n;
    [Params(8, 16)]
    public int RequestsPerGroup { get; set; } = 8;
    private TcpQueryPeer Peer => _m?.Peer ?? _n!.Peer;

    [GlobalSetup(Target = nameof(MpgsqlDataSourceCohort))]
    public async Task SetupMpgsql()
    {
        _catalog = new QueryCatalog([QueryScenario.One], RequestsPerGroup);
        _m = await TcpMpgsqlFixture.CreateAsync
        (
            _catalog, inFlight: RequestsPerGroup,
            syncGroupSize: RequestsPerGroup, syncTimeoutMs: 1000
        );
    }
    [GlobalSetup(Target = nameof(NpgsqlNativeBatch))]
    public async Task SetupNpgsql()
    {
        _catalog = new QueryCatalog([QueryScenario.One], RequestsPerGroup);
        _n = await TcpNpgsqlFixture.CreateAsync(_catalog, exclusive: true);
        _batch = new NpgsqlBatch(_n.Connection)
        {
            Timeout = 0,
            EnableErrorBarriers = false
        };
        for (var worker = 0;
             worker < RequestsPerGroup;
             worker++)
        {
            var command = new NpgsqlBatchCommand(QueryScenario.One.Sql);
            command.Parameters.Add
            (
                new NpgsqlParameter<long>
                {
                    NpgsqlDbType = NpgsqlDbType.Bigint,
                    TypedValue = worker + 1L
                }
            );
            _batch.BatchCommands.Add(command);
        }
    }
    [Benchmark(Baseline = true)]
    public async Task<long> MpgsqlDataSourceCohort()
    {
        var requests = new Task<long>[RequestsPerGroup];
        for (var worker = 0;
             worker < requests.Length;
             worker++)
        {
            requests[worker] = TcpQueryOperations.MpgsqlAsync(_m!, QueryScenario.One, worker);
        }
        var values = await Task
            .WhenAll(requests)
            .ConfigureAwait(false);
        long sum = 0;
        foreach (var value in values)
        {
            sum += value;
        }
        return sum;
    }
    [Benchmark]
    public async Task<long> NpgsqlNativeBatch()
    {
        await using var reader = await _batch!
            .ExecuteReaderAsync()
            .ConfigureAwait(false);
        return await TcpQueryOperations
            .ConsumeAsync(reader, QueryScenario.One, _n!.Buffers[0])
            .ConfigureAwait(false);
    }
    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_batch is not null)
        {
            await _batch.DisposeAsync();
        }
        if (_m is not null)
        {
            _m.CheckIdle();
            await _m.DisposeAsync();
        }
        if (_n is not null)
        {
            _n.CheckIdle();
            await _n.DisposeAsync();
        }
    }
    internal static async Task VerifyAsync()
    {
        foreach (var size in new[]
                 {
                     8,
                     16
                 })
        foreach (var native in new[]
                 {
                     false,
                     true
                 })
        {
            var benchmark = new TcpSharedSyncCohortBenchmarks
            {
                RequestsPerGroup = size
            };
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
                var before = benchmark.Peer.Counters();
                for (var repeat = 0;
                     repeat < 2;
                     repeat++)
                {
                    var sum = native ? await benchmark.NpgsqlNativeBatch() : await benchmark.MpgsqlDataSourceCohort();
                    TcpComparisonVerification.Check(sum == size * (size + 1L) / 2, "Shared cohort checksum");
                }
                var after = benchmark.Peer.Counters();
                TcpComparisonVerification.Check
                (
                    after.Queries - before.Queries == size * 2 && after.Syncs - before.Syncs == 2,
                    "Exactly one Sync per matching shared cohort"
                );
            }
            finally { await benchmark.Cleanup(); }
        }
        Console.WriteLine("PASS shared cohorts: N=8/16, Mpgsql DataSource / native Npgsql Batch, exact common boundaries");
    }
}