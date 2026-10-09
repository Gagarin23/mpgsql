using System.Data.Common;
using BenchmarkDotNet.Attributes;
using Mpgsql.Benchmarks.Comparison;
using Mpgsql.Benchmarks.Queries;
using Npgsql;
using NpgsqlTypes;

namespace Mpgsql.Benchmarks;

// BDN reports one complete invocation. The paired runner additionally normalizes
// Mutation32/ReusedBatch16 by their 32 logical executions per invocation.
[MemoryDiagnoser, JsonExporterAttribute.Full, Config(typeof(QueryBenchmarkConfig))]
public class TcpAdoExecutionBenchmarks
{
    private enum ExecutionCase { ParameterMutation32, ScalarEmpty, ScalarOneBigint, ScalarNull, ScalarRows128, ReusedBatch16, NonQueryZero }
    private ExecutionCase _case;
    private TcpMpgsqlFixture? _fixture;
    private TcpNpgsqlFixture? _nativeFixture;
    private MpgsqlConnection? _connection;
    private MpgsqlCommand? _command;
    private NpgsqlCommand? _nativeCommand;
    private MpgsqlParameter<long>? _parameter;
    private NpgsqlParameter<long>? _nativeParameter;
    private MpgsqlBatch? _batch;
    private NpgsqlBatch? _nativeBatch;

    [Params("ParameterMutation32", "ScalarEmpty", "ScalarOneBigint", "ScalarNull", "ScalarRows128", "ReusedBatch16", "NonQueryZero")]
    public string Case { get; set; } = "ScalarOneBigint";
    public bool InstrumentTransport { get; set; }
    public int OperationsPerInvocation => _case is ExecutionCase.ParameterMutation32 or ExecutionCase.ReusedBatch16 ? 32 : 1;
    public int QueriesPerOperation => _case == ExecutionCase.ReusedBatch16 ? 16 : 1;
    public int SyncsPerOperation => 1;
    public long ExpectedChecksum => _case switch
    {
        ExecutionCase.ParameterMutation32 => 528,
        ExecutionCase.ReusedBatch16 => 4352,
        ExecutionCase.ScalarEmpty => -2,
        ExecutionCase.ScalarNull => -1,
        ExecutionCase.NonQueryZero => 0,
        _ => 1
    };
    internal TcpQueryPeer Peer => _fixture?.Peer ?? _nativeFixture!.Peer;
    internal void CheckIdle()
    {
        _fixture?.CheckIdle();
        _nativeFixture?.CheckIdle();
    }
    private QueryScenario Scenario => _case switch
    {
        ExecutionCase.ScalarEmpty => QueryScenario.Empty,
        ExecutionCase.ScalarNull => QueryScenario.NullValue,
        ExecutionCase.ScalarRows128 => QueryScenario.ScalarMany,
        ExecutionCase.NonQueryZero => QueryScenario.NonQuery,
        _ => QueryScenario.One
    };
    private void SelectCase()
    {
        if (!Enum.TryParse(Case, out _case) || !Enum.IsDefined(_case) || !string.Equals(_case.ToString(), Case, StringComparison.Ordinal))
            throw new ArgumentException("Unknown ADO execution case: " + Case);
    }

    [GlobalSetup(Target = nameof(MpgsqlExecute))]
    public async Task SetupMpgsql()
    {
        SelectCase();
        var catalog = new QueryCatalog([Scenario], 32);
        _fixture = await TcpMpgsqlFixture.CreateAsync(catalog, multiplexing: false, instrumentTransport: InstrumentTransport).ConfigureAwait(false);
        _connection = await _fixture.OpenClientConnectionAsync().ConfigureAwait(false);
        if (_case == ExecutionCase.ReusedBatch16)
        {
            _batch = _connection.CreateBatch();
            for (var value = 1; value <= 16; value++)
            {
                var command = new MpgsqlBatchCommand(QueryScenario.One.Sql);
                command.Parameters.Add(new MpgsqlParameter<long>(20U, value));
                _batch.BatchCommands.Add(command);
            }
        }
        else
        {
            _command = _connection.CreateCommand(Scenario.Sql);
            _parameter = new MpgsqlParameter<long>(20U, 1L);
            _command.Parameters.Add(_parameter);
        }
    }

    [GlobalSetup(Target = nameof(NpgsqlExecute))]
    public async Task SetupNpgsql()
    {
        SelectCase();
        _nativeFixture = await TcpNpgsqlFixture.CreateAsync(new QueryCatalog([Scenario], 32), exclusive: true).ConfigureAwait(false);
        if (_case == ExecutionCase.ReusedBatch16)
        {
            _nativeBatch = new NpgsqlBatch(_nativeFixture.Connection) { Timeout = 0, EnableErrorBarriers = false };
            for (var value = 1; value <= 16; value++)
            {
                var command = new NpgsqlBatchCommand(QueryScenario.One.Sql);
                command.Parameters.Add(new NpgsqlParameter<long> { NpgsqlDbType = NpgsqlDbType.Bigint, TypedValue = value });
                _nativeBatch.BatchCommands.Add(command);
            }
        }
        else
        {
            _nativeCommand = new NpgsqlCommand(Scenario.Sql, _nativeFixture.Connection) { CommandTimeout = 0 };
            _nativeParameter = new NpgsqlParameter<long> { NpgsqlDbType = NpgsqlDbType.Bigint, TypedValue = 1L };
            _nativeCommand.Parameters.Add(_nativeParameter);
        }
    }

    [Benchmark]
    public async Task<long> MpgsqlExecute()
    {
        if (_case == ExecutionCase.ParameterMutation32)
        {
            long sum = 0;
            for (var value = 1; value <= 32; value++)
            {
                _parameter!.TypedValue = value;
                await using var reader = await _command!.ExecuteReaderAsync().ConfigureAwait(false);
                sum += await ConsumeAsync(reader).ConfigureAwait(false);
            }
            return sum;
        }
        if (_case == ExecutionCase.ReusedBatch16)
        {
            long sum = 0;
            for (var iteration = 0; iteration < 32; iteration++)
            {
                await using var reader = await _batch!.ExecuteReaderAsync().ConfigureAwait(false);
                sum += await ConsumeAsync(reader).ConfigureAwait(false);
            }
            return sum;
        }
        DbCommand command = _command!;
        return _case == ExecutionCase.NonQueryZero
            ? await command.ExecuteNonQueryAsync().ConfigureAwait(false)
            : ScalarChecksum(await command.ExecuteScalarAsync().ConfigureAwait(false));
    }

    [Benchmark(Baseline = true)]
    public async Task<long> NpgsqlExecute()
    {
        if (_case == ExecutionCase.ParameterMutation32)
        {
            long sum = 0;
            for (var value = 1; value <= 32; value++)
            {
                _nativeParameter!.TypedValue = value;
                await using var reader = await _nativeCommand!.ExecuteReaderAsync().ConfigureAwait(false);
                sum += await ConsumeAsync(reader).ConfigureAwait(false);
            }
            return sum;
        }
        if (_case == ExecutionCase.ReusedBatch16)
        {
            long sum = 0;
            for (var iteration = 0; iteration < 32; iteration++)
            {
                await using var reader = await _nativeBatch!.ExecuteReaderAsync().ConfigureAwait(false);
                sum += await ConsumeAsync(reader).ConfigureAwait(false);
            }
            return sum;
        }
        DbCommand command = _nativeCommand!;
        return _case == ExecutionCase.NonQueryZero
            ? await command.ExecuteNonQueryAsync().ConfigureAwait(false)
            : ScalarChecksum(await command.ExecuteScalarAsync().ConfigureAwait(false));
    }

    private static long ScalarChecksum(object? value) => value switch
    {
        null => -2,
        DBNull => -1,
        long integer => integer,
        _ => throw new InvalidDataException("Expected null, DBNull.Value or an Int64 scalar.")
    };
    private static async Task<long> ConsumeAsync(DbDataReader reader)
    {
        long sum = 0;
        do
        {
            while (await reader.ReadAsync().ConfigureAwait(false))
                sum += reader.GetFieldValue<long>(0);
        }
        while (await reader.NextResultAsync().ConfigureAwait(false));
        return sum;
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_batch is not null) await _batch.DisposeAsync().ConfigureAwait(false);
        if (_nativeBatch is not null) await _nativeBatch.DisposeAsync().ConfigureAwait(false);
        if (_command is not null) await _command.DisposeAsync().ConfigureAwait(false);
        if (_nativeCommand is not null) await _nativeCommand.DisposeAsync().ConfigureAwait(false);
        if (_connection is not null) await _connection.DisposeAsync().ConfigureAwait(false);
        if (_fixture is not null)
        {
            _fixture.CheckIdle();
            await _fixture.DisposeAsync().ConfigureAwait(false);
        }
        if (_nativeFixture is not null)
        {
            _nativeFixture.CheckIdle();
            await _nativeFixture.DisposeAsync().ConfigureAwait(false);
        }
    }
}
