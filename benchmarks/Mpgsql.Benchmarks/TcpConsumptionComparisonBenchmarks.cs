using BenchmarkDotNet.Attributes;
using Mpgsql.Benchmarks.Comparison;
using Mpgsql.Benchmarks.Queries;

namespace Mpgsql.Benchmarks;

[MemoryDiagnoser, JsonExporterAttribute.Full, Config(typeof(QueryBenchmarkConfig)), IterationTime(150)]
public class TcpConsumptionComparisonBenchmarks
{
    private QueryCatalog _catalog = null!;
    private TcpMpgsqlFixture? _m;
    private TcpNpgsqlFixture? _n;
    private QueryScenario _scenario = null!;
    [Params("ScalarEmpty", "ScalarNull", "ScalarOne", "ScalarRows128", "NonQuery", "ReturningRows128", "EarlyDispose4096")]
    public string Case { get; set; } = "ScalarOne";
    private void Initialize()
    {
        _scenario = Case switch
        {
            "ScalarEmpty"      => QueryScenario.Empty,
            "ScalarNull"       => QueryScenario.NullValue,
            "ScalarOne"        => QueryScenario.One,
            "ScalarRows128"    => QueryScenario.ScalarMany,
            "NonQuery"         => QueryScenario.NonQuery,
            "ReturningRows128" => QueryScenario.ReturningRows,
            "EarlyDispose4096" => QueryScenario.Many,
            _                  => throw new ArgumentException("Unknown consumption case.")
        };
        _catalog = new QueryCatalog([_scenario], 1);
    }
    [GlobalSetup(Target = nameof(MpgsqlDataSource))]
    public async Task SetupMpgsql()
    {
        Initialize();
        _m = await TcpMpgsqlFixture.CreateAsync(_catalog);
    }
    [GlobalSetup(Target = nameof(NpgsqlPool))]
    public async Task SetupPool()
    {
        Initialize();
        _n = await TcpNpgsqlFixture.CreateAsync(_catalog);
    }
    [GlobalSetup(Target = nameof(NpgsqlMultiplexed))]
    public async Task SetupMultiplexed()
    {
        Initialize();
        _n = await TcpNpgsqlFixture.CreateAsync(_catalog, multiplexing: true);
    }
    [Benchmark(Baseline = true)]
    public async Task<long> MpgsqlDataSource()
    {
        var parameters = _catalog
            .Inputs[0][0];
        if (Case is "NonQuery" or "ReturningRows128")
        {
            return await _m!
                .Source.ExecuteNonQueryAsync(_scenario.Sql, parameters)
                .ConfigureAwait(false);
        }
        if (Case == "EarlyDispose4096")
        {
            await using var reader = await _m!
                .Source.ExecuteReaderAsync(_scenario.Sql, parameters)
                .ConfigureAwait(false);
            return await reader
                .ReadAsync()
                .ConfigureAwait(false)
                ? reader.GetInt64(0)!.Value
                : 0;
        }
        var value = await _m!
            .Source.ExecuteScalarAsync<long>(_scenario.Sql, parameters)
            .ConfigureAwait(false);
        return !value.HasRow ? -2 : value.IsNull ? -1 : value.Value;
    }
    [Benchmark]
    public Task<long> NpgsqlPool()
    {
        return ConsumeNpgsql();
    }
    [Benchmark]
    public Task<long> NpgsqlMultiplexed()
    {
        return ConsumeNpgsql();
    }
    private async Task<long> ConsumeNpgsql()
    {
        var command = _n!
            .Commands[0][0];
        if (Case is "NonQuery" or "ReturningRows128")
        {
            return await command
                .ExecuteNonQueryAsync()
                .ConfigureAwait(false);
        }
        if (Case == "EarlyDispose4096")
        {
            await using var reader = await command
                .ExecuteReaderAsync()
                .ConfigureAwait(false);
            return await reader
                .ReadAsync()
                .ConfigureAwait(false)
                ? reader.GetInt64(0)
                : 0;
        }
        var value = await command
            .ExecuteScalarAsync()
            .ConfigureAwait(false);
        return value is null ? -2 : value is DBNull ? -1 : (long)value;
    }
    [GlobalCleanup]
    public async Task Cleanup()
    {
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