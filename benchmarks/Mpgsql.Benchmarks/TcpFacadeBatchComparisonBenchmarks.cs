using BenchmarkDotNet.Attributes;
using Mpgsql.Benchmarks.Comparison;
using Mpgsql.Benchmarks.Queries;
using Npgsql;
using NpgsqlTypes;

namespace Mpgsql.Benchmarks;

// Both APIs execute fresh command groups. Their caller construction happens in iteration setup.
[MemoryDiagnoser, JsonExporterAttribute.Full, Config(typeof(QueryBenchmarkConfig)), InvocationCount(1)]
public class TcpFacadeBatchComparisonBenchmarks
{
    internal const int GroupsPerIteration = 32;
    internal const int QueriesPerGroup = 16;
    private QueryCatalog _catalog = null!;
    private MpgsqlConnection? _connection;
    private TcpMpgsqlFixture? _m;
    private TcpNpgsqlFixture? _n;
    internal int PreparedGroupsPerIteration { get; set; } = GroupsPerIteration;
    internal MpgsqlBatch[] MpgsqlBatches { get; private set; } = [];
    internal NpgsqlBatch[] NpgsqlBatches { get; private set; } = [];
    internal TcpQueryPeer Peer => _m?.Peer ?? _n!.Peer;
    internal void CheckIdle()
    {
        _m?.CheckIdle();
        _n?.CheckIdle();
    }
    // Diagnostic runs release completed caller groups before their final heap snapshot.
    internal void ReleaseCompletedGroups()
    {
        MpgsqlBatches = [];
        NpgsqlBatches = [];
    }

    [GlobalSetup(Target = nameof(MpgsqlFacadeBatch))]
    public async Task SetupMpgsql()
    {
        _catalog = new QueryCatalog([QueryScenario.One], QueriesPerGroup);
        _m = await TcpMpgsqlFixture.CreateAsync(_catalog, multiplexing: false);
        _connection = await _m.OpenClientConnectionAsync();
    }

    [GlobalSetup(Target = nameof(NpgsqlFreshBatch))]
    public async Task SetupNpgsql()
    {
        _catalog = new QueryCatalog([QueryScenario.One], QueriesPerGroup);
        _n = await TcpNpgsqlFixture.CreateAsync(_catalog, exclusive: true);
    }

    [IterationSetup(Target = nameof(MpgsqlFacadeBatch))]
    public void PrepareMpgsql()
    {
        MpgsqlBatches = new MpgsqlBatch[PreparedGroupsPerIteration];
        for (var group = 0; group < MpgsqlBatches.Length; group++)
        {
            var batch = _connection!.CreateBatch();
            for (var worker = 0; worker < QueriesPerGroup; worker++)
            {
                var command = new MpgsqlBatchCommand(QueryScenario.One.Sql);
                foreach (var parameter in _catalog.Inputs[0][worker]) command.Parameters.Add(parameter);
                batch.BatchCommands.Add(command);
            }
            MpgsqlBatches[group] = batch;
        }
    }

    [IterationSetup(Target = nameof(NpgsqlFreshBatch))]
    public void PrepareNpgsql()
    {
        NpgsqlBatches = new NpgsqlBatch[PreparedGroupsPerIteration];
        for (var group = 0; group < NpgsqlBatches.Length; group++)
        {
            var batch = new NpgsqlBatch(_n!.Connection) {Timeout = 0, EnableErrorBarriers = false};
            for (var worker = 0; worker < QueriesPerGroup; worker++)
            {
                var command = new NpgsqlBatchCommand(QueryScenario.One.Sql);
                command.Parameters.Add(new NpgsqlParameter<long> {NpgsqlDbType = NpgsqlDbType.Bigint, TypedValue = worker + 1L});
                batch.BatchCommands.Add(command);
            }
            NpgsqlBatches[group] = batch;
        }
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = GroupsPerIteration)]
    public async Task<long> MpgsqlFacadeBatch()
    {
        long sum = 0;
        foreach (var batch in MpgsqlBatches)
        {
            await using (batch.ConfigureAwait(false))
            {
                await using var reader = await batch.ExecuteReaderValueTaskAsync().ConfigureAwait(false);
                sum += await TcpQueryOperations.ConsumeAsync(reader, QueryScenario.One, _m!.Buffers[0]).ConfigureAwait(false);
            }
        }
        return sum;
    }

    [Benchmark(OperationsPerInvoke = GroupsPerIteration)]
    public async Task<long> NpgsqlFreshBatch()
    {
        long sum = 0;
        foreach (var batch in NpgsqlBatches)
        {
            await using (batch.ConfigureAwait(false))
            {
                await using var reader = await batch.ExecuteReaderAsync().ConfigureAwait(false);
                sum += await TcpQueryOperations.ConsumeAsync(reader, QueryScenario.One, _n!.Buffers[0]).ConfigureAwait(false);
            }
        }
        return sum;
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        foreach (var batch in MpgsqlBatches) await batch.DisposeAsync();
        foreach (var batch in NpgsqlBatches) await batch.DisposeAsync();
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
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
}