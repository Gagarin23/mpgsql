using BenchmarkDotNet.Attributes;
using Mpgsql.Benchmarks.Queries;

namespace Mpgsql.Benchmarks;

[MemoryDiagnoser, JsonExporterAttribute.Full, Config(typeof(QueryBenchmarkConfig)), IterationTime(150)]
public class DataSourceConsumptionBenchmarks
{
    private QuerySourceFixture _fixture = null!;
    private MpgsqlParameterValue[] _parameters = [];
    private QueryScenario _scenario = null!;
    [Params("ScalarEmpty", "ScalarNull", "ScalarOne", "ScalarRows128", "NonQuery", "ReturningRows128", "EarlyDispose4096")]
    public string Case { get; set; } = "ScalarOne";
    [GlobalSetup]
    public async Task Setup()
    {
        _scenario = Case switch
        {
            "ScalarEmpty"      => QueryScenario.Empty, "ScalarNull"          => QueryScenario.NullValue,
            "ScalarOne"        => QueryScenario.One, "ScalarRows128"         => QueryScenario.ScalarMany,
            "NonQuery"         => QueryScenario.NonQuery, "ReturningRows128" => QueryScenario.ReturningRows,
            "EarlyDispose4096" => QueryScenario.Many, _                      => throw new ArgumentException("Unknown consumption case.")
        };
        _fixture = await QuerySourceFixture.CreateAsync(new QueryCatalog([_scenario], 1));
        _parameters = _fixture.Catalog.Inputs[0][0];
        long expected = Case switch {"ScalarEmpty" => -2, "ScalarNull" => -1, "NonQuery" => 0, "ReturningRows128" => 128, _ => 1};
        if (await Consume() != expected)
        {
            throw new InvalidOperationException("Consumption benchmark result mismatch.");
        }
        _fixture.CheckIdle();
    }
    [Benchmark]
    public async Task<long> Consume()
    {
        if (Case is "NonQuery" or "ReturningRows128")
        {
            return await _fixture.Source.ExecuteNonQueryAsync(_scenario.Sql, _parameters).ConfigureAwait(false);
        }
        if (Case == "EarlyDispose4096")
        {
            await using var reader = await _fixture.Source.ExecuteReaderAsync(_scenario.Sql, _parameters).ConfigureAwait(false);
            return await reader.ReadAsync().ConfigureAwait(false) ? reader.GetInt64(0)!.Value : 0;
        }
        var value = await _fixture.Source.ExecuteScalarAsync<long>(_scenario.Sql, _parameters).ConfigureAwait(false);
        return !value.HasRow ? -2 : value.IsNull ? -1 : value.Value;
    }
    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _fixture.DisposeAsync();
    }
}