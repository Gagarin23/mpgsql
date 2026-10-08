using System.Data.Common;
using BenchmarkDotNet.Attributes;
using Mpgsql.Benchmarks.Comparison;
using Mpgsql.Benchmarks.Queries;

namespace Mpgsql.Benchmarks;

[MemoryDiagnoser, JsonExporterAttribute.Full, Config(typeof(QueryBenchmarkConfig)), IterationTime(150)]
public class TcpAdoReaderBenchmarks
{
    private QueryCatalog _catalog = null!;
    private MpgsqlCommand _command = null!;
    private MpgsqlConnection _connection = null!;
    private TcpMpgsqlFixture _fixture = null!;
    private QueryScenario _scenario = null!;
    [Params("Empty", "OneBigint", "Rows128Columns8", "Rows4096", "Bytea64KiB")]
    public string Case { get; set; } = "OneBigint";
    [GlobalSetup]
    public async Task Setup()
    {
        _scenario = QueryScenario.Find(Case);
        _catalog = new QueryCatalog([_scenario], 1);
        _fixture = await TcpMpgsqlFixture.CreateAsync(_catalog, multiplexing: false);
        _connection = await _fixture.OpenClientConnectionAsync();
        _command = CreateCommand();
        var actual = await ReusedTyped();
        if (actual != _scenario.Expected(0))
        {
            throw new InvalidOperationException("ADO.NET checksum.");
        }
    }
    private MpgsqlCommand CreateCommand()
    {
        var command = _connection.CreateCommand(_scenario.Sql);
        foreach (var value in _catalog.Inputs[0][0]) command.Parameters.Add(MpgsqlParameter.FromValue(value));
        return command;
    }
    [Benchmark(Baseline = true)]
    public async Task<long> ReusedTyped()
    {
        await using var reader = await _command.ExecuteReaderValueTaskAsync();
        return await TcpQueryOperations.ConsumeAsync(reader, _scenario, _fixture.Buffers[0]);
    }
    [Benchmark]
    public async Task<long> FreshTyped()
    {
        await using var command = CreateCommand();
        await using var reader = await command.ExecuteReaderValueTaskAsync();
        return await TcpQueryOperations.ConsumeAsync(reader, _scenario, _fixture.Buffers[0]);
    }
    [Benchmark]
    public async Task<long> ReusedObject()
    {
        DbCommand command = _command;
        await using var reader = await command.ExecuteReaderAsync();
        long sum = 0;
        do
        {
            while (await reader.ReadAsync())
            {
                for (var i = 0; i < (_scenario.ByteaBytes == 0 ? _scenario.Columns : 1); i++)
                {
                    sum += (long)reader.GetValue(i);
                }
                if (_scenario.ByteaBytes != 0)
                {
                    var length = (int)reader.GetBytes(1, 0, _fixture.Buffers[0], 0, _scenario.ByteaBytes);
                    sum += length + _fixture.Buffers[0][0] + _fixture.Buffers[0][_scenario.ByteaBytes - 1];
                }
            }
        } while (await reader.NextResultAsync());
        return sum;
    }
    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _command.DisposeAsync();
        await _connection.DisposeAsync();
        _fixture.CheckIdle();
        await _fixture.DisposeAsync();
    }
}