using BenchmarkDotNet.Attributes;
using Mpgsql.Benchmarks.Comparison;
using Mpgsql.Benchmarks.Queries;

namespace Mpgsql.Benchmarks;

[MemoryDiagnoser, JsonExporterAttribute.Full]
[Config(typeof(QueryBenchmarkConfig)), IterationTime(150)]
public class TcpReaderComparisonBenchmarks
{
    [Params("Empty", "OneBigint", "Rows128Columns8", "Rows4096", "Bytea64KiB")]
    public string Case { get; set; } = "OneBigint";
    private QueryScenario _scenario = null!;
    private QueryCatalog _catalog = null!;
    private TcpMpgsqlFixture? _m;
    private TcpNpgsqlFixture? _n;
    private void Initialize() { _scenario = QueryScenario.Find(Case); _catalog = new([_scenario], 1); }
    [GlobalSetup(Targets = [nameof(MpgsqlRaw), nameof(MpgsqlDataSource)])]
    public async Task SetupMpgsql() { Initialize(); _m = await TcpMpgsqlFixture.CreateAsync(_catalog); }
    [GlobalSetup(Target = nameof(NpgsqlConnection))]
    public async Task SetupConnection() { Initialize(); _n = await TcpNpgsqlFixture.CreateAsync(_catalog, exclusive: true); }
    [GlobalSetup(Target = nameof(NpgsqlPool))]
    public async Task SetupPool() { Initialize(); _n = await TcpNpgsqlFixture.CreateAsync(_catalog); }
    [GlobalSetup(Target = nameof(NpgsqlMultiplexed))]
    public async Task SetupMultiplexed() { Initialize(); _n = await TcpNpgsqlFixture.CreateAsync(_catalog, multiplexing: true); }
    [Benchmark(Baseline = true)] public Task<long> MpgsqlRaw() => TcpQueryOperations.RawAsync(_m!.Transports[0], _catalog, _scenario, _m.Buffers[0]);
    [Benchmark] public Task<long> MpgsqlDataSource() => TcpQueryOperations.MpgsqlAsync(_m!, _scenario);
    [Benchmark] public Task<long> NpgsqlConnection() => TcpQueryOperations.NpgsqlAsync(_n!, _catalog, _scenario);
    [Benchmark] public Task<long> NpgsqlPool() => TcpQueryOperations.NpgsqlAsync(_n!, _catalog, _scenario);
    [Benchmark] public Task<long> NpgsqlMultiplexed() => TcpQueryOperations.NpgsqlAsync(_n!, _catalog, _scenario);
    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_m is not null) { _m.CheckIdle(); await _m.DisposeAsync(); }
        if (_n is not null) { _n.CheckIdle(); await _n.DisposeAsync(); }
    }
}
