using BenchmarkDotNet.Attributes;
using Mpgsql.Benchmarks.Queries;

namespace Mpgsql.Benchmarks;

[MemoryDiagnoser, JsonExporterAttribute.Full]
[Config(typeof(QueryBenchmarkConfig)), IterationTime(150)]
public class QueryBatchBenchmarks
{
    private QueryCatalog _catalog = null!;
    private QueryPeer _peer = null!;
    [GlobalSetup]
    public async Task Setup()
    {
        _catalog = new([QueryScenario.One], 16);
        _peer = new(_catalog);
        if (await SharedSyncGroup() != 136) throw new InvalidOperationException("Batch checksum.");
    }
    // One reported operation is the entire sixteen-query group, not one query.
    [Benchmark] public Task<long> SharedSyncGroup() => QueryOperations.BatchAsync(_peer, _catalog);
    [GlobalCleanup] public async Task Cleanup() => await _peer.DisposeAsync();
}
