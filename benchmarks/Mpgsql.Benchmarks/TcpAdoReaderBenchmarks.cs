using System.Data.Common;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Mpgsql.Benchmarks.Comparison;
using Mpgsql.Benchmarks.Queries;
using Npgsql;

namespace Mpgsql.Benchmarks;

[MemoryDiagnoser, JsonExporterAttribute.Full, Config(typeof(QueryBenchmarkConfig)), IterationTime(150),
 GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory), CategoriesColumn]
public class TcpAdoReaderBenchmarks
{
    private QueryCatalog _catalog = null!;
    private MpgsqlCommand _command = null!;
    private MpgsqlConnection _connection = null!;
    private TcpMpgsqlFixture? _fixture;
    private NpgsqlCommand _nativeCommand = null!;
    private TcpNpgsqlFixture? _nativeFixture;
    private QueryScenario _scenario = null!;
    [Params("Empty", "OneBigint", "Rows128Columns8", "Rows4096", "Bytea64KiB")]
    public string Case { get; set; } = "OneBigint";
    // Zero uses each driver's production default; explicit values match read buffers.
    // The paired sweep sets this before setup and changes only the read buffer.
    public int ReadBufferSize { get; set; }
    // Legacy/diagnostic methods keep counters; canonical ADO acceptance disables them.
    public bool InstrumentTransport { get; set; } = true;
    // Optional synthetic peer policy; canonical comparisons keep the original default.
    public bool CoalesceReplies { get; set; }
    internal TcpQueryPeer Peer => _fixture?.Peer ?? _nativeFixture!.Peer;
    internal void CheckIdle()
    {
        _fixture?.CheckIdle();
        _nativeFixture?.CheckIdle();
    }
    [GlobalSetup(Targets = [nameof(ReusedTyped), nameof(FreshTyped), nameof(ReusedObject), nameof(ReusedTypedStandard), nameof(FreshTypedStandard)])]
    public async Task Setup()
    {
        _scenario = QueryScenario.Find(Case);
        _catalog = new QueryCatalog([_scenario], 1);
        _fixture = await TcpMpgsqlFixture.CreateAsync(_catalog, multiplexing: false, readBufferSize: ReadBufferSize,
            instrumentTransport: InstrumentTransport, coalesceReplies: CoalesceReplies);
        _connection = await _fixture.OpenClientConnectionAsync();
        _command = CreateCommand();
        Validate(await ReusedTyped());
        Validate(await FreshTyped());
        Validate(await ReusedObject());
        _fixture.CheckIdle();
    }
    private MpgsqlCommand CreateCommand()
    {
        var command = _connection.CreateCommand(_scenario.Sql);
        command.Parameters.Add(new MpgsqlParameter<long>(20U, 1L));
        if (_scenario.ByteaBytes != 0)
        {
            command.Parameters.Add(new MpgsqlParameter<byte[]>(17U, _scenario.Blob));
        }
        return command;
    }
    [GlobalSetup(Targets = [nameof(NpgsqlReusedTyped), nameof(NpgsqlFreshTyped), nameof(NpgsqlReusedObject)])]
    public async Task SetupNpgsql()
    {
        _scenario = QueryScenario.Find(Case);
        _catalog = new QueryCatalog([_scenario], 1);
        _nativeFixture = await TcpNpgsqlFixture.CreateAsync(_catalog, exclusive: true, readBufferSize: ReadBufferSize,
            coalesceReplies: CoalesceReplies);
        _nativeCommand = _nativeFixture.Commands[0][0];
        Validate(await NpgsqlReusedTyped());
        Validate(await NpgsqlFreshTyped());
        Validate(await NpgsqlReusedObject());
        _nativeFixture.CheckIdle();
    }
    private void Validate(long checksum)
    {
        if (checksum != _scenario.Expected(0))
        {
            throw new InvalidOperationException("ADO.NET comparison checksum.");
        }
    }
    [Benchmark, BenchmarkCategory("ReusedTyped")]
    public async Task<long> ReusedTyped()
    {
        await using var reader = await _command.ExecuteReaderValueTaskAsync();
        return await TcpQueryOperations.ConsumeAsync(reader, _scenario, _fixture!.Buffers[0]);
    }
    [Benchmark, BenchmarkCategory("FreshTyped")]
    public async Task<long> FreshTyped()
    {
        await using var command = CreateCommand();
        await using var reader = await command.ExecuteReaderValueTaskAsync();
        return await TcpQueryOperations.ConsumeAsync(reader, _scenario, _fixture!.Buffers[0]);
    }
    [Benchmark, BenchmarkCategory("ReusedTypedStandard")]
    public async Task<long> ReusedTypedStandard()
    {
        await using var reader = await _command.ExecuteReaderAsync().ConfigureAwait(false);
        return await ConsumeStandardTypedAsync(reader).ConfigureAwait(false);
    }
    [Benchmark, BenchmarkCategory("FreshTypedStandard")]
    public async Task<long> FreshTypedStandard()
    {
        await using var command = CreateCommand();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        return await ConsumeStandardTypedAsync(reader).ConfigureAwait(false);
    }
    [Benchmark, BenchmarkCategory("ReusedObject")]
    public async Task<long> ReusedObject()
    {
        DbCommand command = _command;
        await using var reader = await command.ExecuteReaderAsync();
        return await ConsumeObjectAsync(reader, _fixture!.Buffers[0]);
    }
    [Benchmark(Baseline = true), BenchmarkCategory("ReusedTyped")]
    public async Task<long> NpgsqlReusedTyped()
    {
        await using var reader = await _nativeCommand.ExecuteReaderAsync();
        return await ConsumeNativeTypedAsync(reader);
    }
    [Benchmark(Baseline = true), BenchmarkCategory("FreshTyped")]
    public async Task<long> NpgsqlFreshTyped()
    {
        await using var command = TcpNpgsqlFixture.AddParameters(
            new NpgsqlCommand(_scenario.Sql, _nativeFixture!.Connection), _scenario, 0);
        await using var reader = await command.ExecuteReaderAsync();
        return await ConsumeNativeTypedAsync(reader);
    }
    [Benchmark(Baseline = true), BenchmarkCategory("ReusedObject")]
    public async Task<long> NpgsqlReusedObject()
    {
        DbCommand command = _nativeCommand;
        await using var reader = await command.ExecuteReaderAsync();
        return await ConsumeObjectAsync(reader, _nativeFixture!.Buffers[0]);
    }
    private async Task<long> ConsumeNativeTypedAsync(NpgsqlDataReader reader)
    {
        var buffer = _nativeFixture!.Buffers[0];
        long sum = 0;
        do
        {
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                for (var i = 0;
                     i < (_scenario.ByteaBytes == 0 ? _scenario.Columns : 1);
                     i++)
                {
                    sum += reader.GetFieldValue<long>(i);
                }
                if (_scenario.ByteaBytes != 0)
                {
                    var length = reader.GetBytes(1, 0, buffer, 0, _scenario.ByteaBytes);
                    sum += length + buffer[0] + buffer[_scenario.ByteaBytes - 1];
                }
            }
        }
        while (await reader.NextResultAsync().ConfigureAwait(false));
        return sum;
    }
    private async Task<long> ConsumeStandardTypedAsync(MpgsqlDataReader reader)
    {
        var buffer = _fixture!.Buffers[0];
        long sum = 0;
        do
        {
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                for (var i = 0; i < (_scenario.ByteaBytes == 0 ? _scenario.Columns : 1); i++)
                {
                    sum += reader.GetFieldValue<long>(i);
                }
                if (_scenario.ByteaBytes != 0)
                {
                    var length = reader.GetBytes(1, 0, buffer, 0, _scenario.ByteaBytes);
                    sum += length + buffer[0] + buffer[_scenario.ByteaBytes - 1];
                }
            }
        }
        while (await reader.NextResultAsync().ConfigureAwait(false));
        return sum;
    }
    private async Task<long> ConsumeObjectAsync(DbDataReader reader, byte[] buffer)
    {
        long sum = 0;
        do
        {
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                for (var i = 0;
                     i < (_scenario.ByteaBytes == 0 ? _scenario.Columns : 1);
                     i++)
                {
                    sum += (long)reader.GetValue(i);
                }
                if (_scenario.ByteaBytes != 0)
                {
                    var length = reader.GetBytes(1, 0, buffer, 0, _scenario.ByteaBytes);
                    sum += length + buffer[0] + buffer[_scenario.ByteaBytes - 1];
                }
            }
        }
        while (await reader.NextResultAsync().ConfigureAwait(false));
        return sum;
    }
    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_fixture is not null)
        {
            await _command.DisposeAsync();
            await _connection.DisposeAsync();
            _fixture.CheckIdle();
            await _fixture.DisposeAsync();
        }
        if (_nativeFixture is not null)
        {
            _nativeFixture.CheckIdle();
            await _nativeFixture.DisposeAsync();
        }
    }
}
