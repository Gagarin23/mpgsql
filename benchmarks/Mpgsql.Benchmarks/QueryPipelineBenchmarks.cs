using BenchmarkDotNet.Attributes;
using Mpgsql.Benchmarks.Queries;

namespace Mpgsql.Benchmarks;

[MemoryDiagnoser, JsonExporterAttribute.Full, Config(typeof(QueryBenchmarkConfig)), IterationTime(150)]
public class QueryPipelineBenchmarks
{
    private QueryCatalog _catalog = null!;
    private QueryPeer _raw = null!;
    private QueryScenario _scenario = null!;
    private QuerySourceFixture _source = null!;
    [Params("Empty", "OneBigint", "Rows128Columns8", "Rows4096", "Bytea64KiB")]
    public string Case { get; set; } = "OneBigint";
    [Params(0, 4096)]
    public int ForcedReplyChunk { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _scenario = QueryScenario.Find(Case);
        _catalog = new QueryCatalog([_scenario], 1);
        _raw = new QueryPeer(_catalog, ForcedReplyChunk);
        _source = await QuerySourceFixture.CreateAsync(_catalog, chunk: ForcedReplyChunk);
        var expected = _scenario.Expected(0);
        if (await RawSession() != expected || await DataSource() != expected)
        {
            throw new InvalidOperationException("Reader benchmark checksum mismatch.");
        }
        _source.CheckIdle();
    }
    [Benchmark(Baseline = true)]
    public Task<long> RawSession()
    {
        return QueryOperations.RawAsync(_raw, _catalog, _scenario);
    }
    [Benchmark]
    public Task<long> DataSource()
    {
        return QueryOperations.DataSourceAsync(_source, _scenario);
    }
    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _raw.DisposeAsync();
        await _source.DisposeAsync();
    }
}