using BenchmarkDotNet.Attributes;
using Mpgsql.Benchmarks.Comparison;
using Mpgsql.Benchmarks.Queries;
using Npgsql;

namespace Mpgsql.Benchmarks;

[MemoryDiagnoser, JsonExporterAttribute.Full]
[Config(typeof(QueryBenchmarkConfig)), IterationTime(150)]
public class TcpBatchComparisonBenchmarks
{
    private QueryCatalog _catalog = null!;
    private TcpMpgsqlFixture? _m;
    private TcpNpgsqlFixture? _n;
    private NpgsqlBatch? _batch;
    internal TcpQueryPeer Peer => _m?.Peer ?? _n!.Peer;
    internal void CheckIdle() { _m?.CheckIdle(); _n?.CheckIdle(); }
    internal NpgsqlBatch NativeBatch => _batch!;
    [GlobalSetup(Target = nameof(MpgsqlSharedSync))]
    public async Task SetupMpgsql() { _catalog = new([QueryScenario.One], 16); _m = await TcpMpgsqlFixture.CreateAsync(_catalog); }
    [GlobalSetup(Target = nameof(NpgsqlBatch))]
    public async Task SetupNpgsql()
    {
        _catalog = new([QueryScenario.One], 16); _n = await TcpNpgsqlFixture.CreateAsync(_catalog, exclusive: true);
        _batch = new(_n.Connection) { Timeout = 0, EnableErrorBarriers = false };
        for (int worker = 0; worker < 16; worker++)
        {
            var command = new NpgsqlBatchCommand(QueryScenario.One.Sql);
            command.Parameters.Add(new NpgsqlParameter<long> { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint, TypedValue = worker + 1L });
            _batch.BatchCommands.Add(command);
        }
    }
    // One operation is the complete sixteen-query group with a single Sync.
    [Benchmark(Baseline = true)]
    public async Task<long> MpgsqlSharedSync()
    {
        await using var batch = _m!.Transports[0].Session.CreateBatch();
        var sends = new Task[17];
        for (int i = 0; i < 16; i++) sends[i] = batch.SendQueryAsync(QueryScenario.One.Sql, _catalog.Inputs[0][i]).AsTask();
        sends[16] = batch.SendSyncAsync().AsTask();
        await Task.WhenAll(sends).ConfigureAwait(false);
        await using var reader = await batch.ReadResultsAsync().ConfigureAwait(false);
        long sum = await TcpQueryOperations.ConsumeAsync(reader, QueryScenario.One, _m.Buffers[0]).ConfigureAwait(false);
        await batch.Completion.ConfigureAwait(false);
        return sum;
    }
    [Benchmark]
    public async Task<long> NpgsqlBatch()
    {
        await using var reader = await _batch!.ExecuteReaderAsync().ConfigureAwait(false);
        return await TcpQueryOperations.ConsumeAsync(reader, QueryScenario.One, _n!.Buffers[0]).ConfigureAwait(false);
    }
    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_batch is not null) await _batch.DisposeAsync();
        if (_m is not null) { _m.CheckIdle(); await _m.DisposeAsync(); }
        if (_n is not null) { _n.CheckIdle(); await _n.DisposeAsync(); }
    }
}
