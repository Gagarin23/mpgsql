using System.Data;
using System.Data.Common;
using System.Globalization;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Mpgsql.Benchmarks.Comparison;
using Mpgsql.Benchmarks.Queries;
using Npgsql;
using NpgsqlTypes;

namespace Mpgsql.Benchmarks;

// Real PostgreSQL execution through the production DataSource constructors. Startup,
// authentication and the single exclusive connection lease are outside each operation.
// Batch16 is exposed to the paired runner; BDN's parameter matrix covers readers only.
[MemoryDiagnoser, JsonExporterAttribute.Full, Config(typeof(QueryBenchmarkConfig)), IterationTime(150),
 GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory), CategoriesColumn]
public class PostgresAdoBenchmarks
{
    private const int BatchGroups = 32;
    private const int BatchCommands = 16;
    private QueryScenario _scenario = null!;
    private byte[] _buffer = [];
    private MpgsqlDataSource? _source;
    private MpgsqlConnection? _connection;
    private MpgsqlMessageSession? _physicalSession;
    private MpgsqlCommand? _command;
    private NpgsqlDataSource? _nativeSource;
    private NpgsqlConnection? _nativeConnection;
    private NpgsqlCommand? _nativeCommand;
    private MpgsqlBatch[] _batches = [];
    private NpgsqlBatch[] _nativeBatches = [];
    private int _backendPid;
    private int _startupBackendPid;

    [Params("Empty", "OneBigint", "Rows128Columns8", "Rows4096", "Bytea64KiB")]
    public string Case { get; set; } = "OneBigint";
    public bool IsLive => true;
    public int OperationsPerInvocation => Case == "Batch16" ? BatchGroups : 1;
    public int QueriesPerOperation => Case == "Batch16" ? BatchCommands : 1;
    public int SyncsPerOperation => 1;
    public long ExpectedChecksum => Case == "Batch16" ? BatchGroups * 136L : _scenario.Expected(0);
    public int BackendProcessId => _backendPid;
    public string ServerVersion => _connection?.ServerVersion ?? _nativeConnection!.ServerVersion;
    public object Endpoint { get; private set; } = null!;

    [GlobalSetup(Targets = [nameof(ReusedTyped), nameof(FreshTyped), nameof(ReusedObject),
        nameof(ReusedTypedStandard), nameof(FreshTypedStandard)])]
    public async Task Setup()
    {
        var settings = InitializeScenario();
        var builder = new MpgsqlConnectionStringBuilder
        {
            Host = settings.Host,
            Port = settings.Port,
            Username = settings.User,
            Password = settings.Password,
            Database = settings.Database,
            SslMode = MpgsqlSslMode.Disable,
            Timeout = 15,
            CommandTimeout = 0,
            MaxPoolSize = 1
        };
        _source = new MpgsqlDataSource(builder.ConnectionString);
        try
        {
            _connection = await _source.OpenConnectionAsync().ConfigureAwait(false);
            _physicalSession = _connection.Session;
            _startupBackendPid = _physicalSession.BackendKey?.ProcessId
                ?? throw new InvalidDataException("PostgreSQL did not provide BackendKeyData.");
            _backendPid = await ReadBackendPidAsync(_connection).ConfigureAwait(false);
            if (Case == "Batch16")
            {
                PrepareMpgsql();
                Validate(await MpgsqlFacadeBatch().ConfigureAwait(false));
            }
            else
            {
                _command = CreateCommand();
                Validate(await ReusedTyped().ConfigureAwait(false));
                Validate(await FreshTyped().ConfigureAwait(false));
                Validate(await ReusedObject().ConfigureAwait(false));
                Validate(await ReusedTypedStandard().ConfigureAwait(false));
                Validate(await FreshTypedStandard().ConfigureAwait(false));
            }
            await CheckIdleAsync().ConfigureAwait(false);
        }
        catch
        {
            await Cleanup().ConfigureAwait(false);
            throw;
        }
    }

    public Task SetupMpgsql() => Setup();

    [GlobalSetup(Targets = [nameof(NpgsqlReusedTyped), nameof(NpgsqlFreshTyped), nameof(NpgsqlReusedObject),
        nameof(NpgsqlReusedTypedStandard), nameof(NpgsqlFreshTypedStandard)])]
    public async Task SetupNpgsql()
    {
        var settings = InitializeScenario();
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = settings.Host,
            Port = settings.Port,
            Username = settings.User,
            Password = settings.Password,
            Database = settings.Database,
            SslMode = SslMode.Disable,
            GssEncryptionMode = GssEncryptionMode.Disable,
            Pooling = true,
            MaxPoolSize = 1,
            MinPoolSize = 1,
            Multiplexing = false,
            NoResetOnClose = true,
            MaxAutoPrepare = 0,
            Enlist = false,
            Timeout = 15,
            CommandTimeout = 0
        };
        _nativeSource = new NpgsqlDataSourceBuilder(builder.ConnectionString).Build();
        try
        {
            _nativeConnection = await _nativeSource.OpenConnectionAsync().ConfigureAwait(false);
            _startupBackendPid = _nativeConnection.ProcessID;
            _backendPid = await ReadBackendPidAsync(_nativeConnection).ConfigureAwait(false);
            if (Case == "Batch16")
            {
                PrepareNpgsql();
                Validate(await NpgsqlFreshBatch().ConfigureAwait(false));
            }
            else
            {
                _nativeCommand = CreateNativeCommand();
                Validate(await NpgsqlReusedTyped().ConfigureAwait(false));
                Validate(await NpgsqlFreshTyped().ConfigureAwait(false));
                Validate(await NpgsqlReusedObject().ConfigureAwait(false));
            }
            await CheckIdleAsync().ConfigureAwait(false);
        }
        catch
        {
            await Cleanup().ConfigureAwait(false);
            throw;
        }
    }

    private LiveSettings InitializeScenario()
    {
        _scenario = Case == "Batch16" ? QueryScenario.One : QueryScenario.Readers.Single(value => value.Name == Case);
        _buffer = new byte[_scenario.ByteaBytes];
        var settings = LiveSettings.Read();
        Endpoint = new { settings.Host, settings.Port, Username = settings.User, settings.Database, SslMode = "Disable" };
        return settings;
    }

    private MpgsqlCommand CreateCommand()
    {
        var command = _connection!.CreateCommand(_scenario.Sql);
        command.CommandTimeout = 0;
        command.Parameters.Add(new MpgsqlParameter<long>(20U, 1L));
        if (_scenario.ByteaBytes != 0)
            command.Parameters.Add(new MpgsqlParameter<byte[]>(17U, _scenario.Blob));
        return command;
    }

    private NpgsqlCommand CreateNativeCommand()
    {
        var command = new NpgsqlCommand(_scenario.Sql, _nativeConnection) { CommandTimeout = 0 };
        command.Parameters.Add(new NpgsqlParameter<long> { NpgsqlDbType = NpgsqlDbType.Bigint, TypedValue = 1L });
        if (_scenario.ByteaBytes != 0)
            command.Parameters.Add(new NpgsqlParameter<byte[]> { NpgsqlDbType = NpgsqlDbType.Bytea, TypedValue = _scenario.Blob });
        return command;
    }

    [Benchmark, BenchmarkCategory("ReusedTyped")]
    public async Task<long> ReusedTyped()
    {
        await using var reader = await _command!.ExecuteReaderValueTaskAsync().ConfigureAwait(false);
        return await TcpQueryOperations.ConsumeAsync(reader, _scenario, _buffer).ConfigureAwait(false);
    }

    [Benchmark, BenchmarkCategory("FreshTyped")]
    public async Task<long> FreshTyped()
    {
        await using var command = CreateCommand();
        await using var reader = await command.ExecuteReaderValueTaskAsync().ConfigureAwait(false);
        return await TcpQueryOperations.ConsumeAsync(reader, _scenario, _buffer).ConfigureAwait(false);
    }

    [Benchmark, BenchmarkCategory("ReusedTypedStandard")]
    public async Task<long> ReusedTypedStandard()
    {
        await using var reader = await _command!.ExecuteReaderAsync().ConfigureAwait(false);
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
        DbCommand command = _command!;
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        return await ConsumeObjectAsync(reader).ConfigureAwait(false);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("ReusedTyped")]
    public async Task<long> NpgsqlReusedTyped()
    {
        await using var reader = await _nativeCommand!.ExecuteReaderAsync().ConfigureAwait(false);
        return await ConsumeNativeTypedAsync(reader).ConfigureAwait(false);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("FreshTyped")]
    public async Task<long> NpgsqlFreshTyped()
    {
        await using var command = CreateNativeCommand();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        return await ConsumeNativeTypedAsync(reader).ConfigureAwait(false);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("ReusedObject")]
    public async Task<long> NpgsqlReusedObject()
    {
        DbCommand command = _nativeCommand!;
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        return await ConsumeObjectAsync(reader).ConfigureAwait(false);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("ReusedTypedStandard")]
    public Task<long> NpgsqlReusedTypedStandard() => NpgsqlReusedTyped();

    [Benchmark(Baseline = true), BenchmarkCategory("FreshTypedStandard")]
    public Task<long> NpgsqlFreshTypedStandard() => NpgsqlFreshTyped();

    private async Task<long> ConsumeNativeTypedAsync(NpgsqlDataReader reader)
    {
        long sum = 0;
        do
        {
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                for (var column = 0; column < (_scenario.ByteaBytes == 0 ? _scenario.Columns : 1); column++)
                    sum += reader.GetFieldValue<long>(column);
                if (_scenario.ByteaBytes != 0)
                {
                    var length = reader.GetBytes(1, 0, _buffer, 0, _buffer.Length);
                    sum += length + _buffer[0] + _buffer[^1];
                }
            }
        }
        while (await reader.NextResultAsync().ConfigureAwait(false));
        return sum;
    }

    private async Task<long> ConsumeStandardTypedAsync(MpgsqlDataReader reader)
    {
        long sum = 0;
        do
        {
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                for (var column = 0; column < (_scenario.ByteaBytes == 0 ? _scenario.Columns : 1); column++)
                    sum += reader.GetFieldValue<long>(column);
                if (_scenario.ByteaBytes != 0)
                {
                    var length = reader.GetBytes(1, 0, _buffer, 0, _buffer.Length);
                    sum += length + _buffer[0] + _buffer[^1];
                }
            }
        }
        while (await reader.NextResultAsync().ConfigureAwait(false));
        return sum;
    }

    private async Task<long> ConsumeObjectAsync(DbDataReader reader)
    {
        long sum = 0;
        do
        {
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                for (var column = 0; column < (_scenario.ByteaBytes == 0 ? _scenario.Columns : 1); column++)
                    sum += (long)reader.GetValue(column);
                if (_scenario.ByteaBytes != 0)
                {
                    var length = reader.GetBytes(1, 0, _buffer, 0, _buffer.Length);
                    sum += length + _buffer[0] + _buffer[^1];
                }
            }
        }
        while (await reader.NextResultAsync().ConfigureAwait(false));
        return sum;
    }

    // The paired runner calls preparation outside the measured/allocated interval.
    // Reader scenarios are no-ops because its reflection adapter discovers these methods.
    public void PrepareMpgsql()
    {
        if (Case != "Batch16")
            return;
        _batches = new MpgsqlBatch[BatchGroups];
        for (var group = 0; group < BatchGroups; group++)
        {
            var batch = new MpgsqlBatch(_connection!) { Timeout = 0 };
            for (var value = 1; value <= BatchCommands; value++)
            {
                var command = new MpgsqlBatchCommand(QueryScenario.One.Sql);
                command.Parameters.Add(new MpgsqlParameter<long>(20U, value));
                batch.BatchCommands.Add(command);
            }
            _batches[group] = batch;
        }
    }

    public void PrepareNpgsql()
    {
        if (Case != "Batch16")
            return;
        _nativeBatches = new NpgsqlBatch[BatchGroups];
        for (var group = 0; group < BatchGroups; group++)
        {
            var batch = new NpgsqlBatch(_nativeConnection) { Timeout = 0, EnableErrorBarriers = false };
            for (var value = 1; value <= BatchCommands; value++)
            {
                var command = new NpgsqlBatchCommand(QueryScenario.One.Sql);
                command.Parameters.Add(new NpgsqlParameter<long> { NpgsqlDbType = NpgsqlDbType.Bigint, TypedValue = value });
                batch.BatchCommands.Add(command);
            }
            _nativeBatches[group] = batch;
        }
    }

    public async Task<long> MpgsqlFacadeBatch()
    {
        long sum = 0;
        foreach (var batch in _batches)
        {
            await using (batch.ConfigureAwait(false))
            {
                await using var reader = await batch.ExecuteReaderValueTaskAsync().ConfigureAwait(false);
                sum += await TcpQueryOperations.ConsumeAsync(reader, QueryScenario.One, _buffer).ConfigureAwait(false);
            }
        }
        return sum;
    }

    public async Task<long> MpgsqlFacadeBatchStandard()
    {
        long sum = 0;
        foreach (var batch in _batches)
        {
            await using (batch.ConfigureAwait(false))
            {
                DbBatch standardBatch = batch;
                await using var reader = await standardBatch.ExecuteReaderAsync().ConfigureAwait(false);
                do
                {
                    while (await reader.ReadAsync().ConfigureAwait(false))
                        sum += reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
                }
                while (await reader.NextResultAsync().ConfigureAwait(false));
            }
        }
        return sum;
    }

    public async Task<long> NpgsqlFreshBatch()
    {
        long sum = 0;
        foreach (var batch in _nativeBatches)
        {
            await using (batch.ConfigureAwait(false))
            {
                await using var reader = await batch.ExecuteReaderAsync().ConfigureAwait(false);
                sum += await TcpQueryOperations.ConsumeAsync(reader, QueryScenario.One, _buffer).ConfigureAwait(false);
            }
        }
        return sum;
    }

    private void Validate(long checksum)
    {
        if (checksum != ExpectedChecksum)
            throw new InvalidDataException("Live ADO.NET benchmark checksum.");
    }

    // No network operation. The runner may call this between blocks, outside clocks.
    public void CheckIdle()
    {
        var session = _physicalSession;
        if (_connection is not null && (session is null || _connection.State != ConnectionState.Open
            || !ReferenceEquals(session, _connection.Session)
            || !session.IsIdleAndHealthy
            || session.BackendKey?.ProcessId != _startupBackendPid))
            throw new InvalidDataException("Live Mpgsql physical connection is no longer idle and unchanged.");
        if (_nativeConnection is not null && (_nativeConnection.FullState != ConnectionState.Open
            || _nativeConnection.ProcessID != _startupBackendPid))
            throw new InvalidDataException("Live Npgsql physical connection is no longer idle and unchanged.");
    }

    // Actual SQL identity probe is optional and must remain outside timing/allocation windows.
    public async Task CheckIdleAsync()
    {
        CheckIdle();
        var connection = (DbConnection?)_connection ?? _nativeConnection
            ?? throw new InvalidOperationException("Open the live fixture first.");
        if (await ReadBackendPidAsync(connection).ConfigureAwait(false) != _backendPid)
            throw new InvalidDataException("Live PostgreSQL backend identity changed.");
        CheckIdle();
    }

    private static async Task<int> ReadBackendPidAsync(DbConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "select pg_backend_pid()";
        command.CommandTimeout = 0;
        var value = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return value is int pid && pid > 0
            ? pid : throw new InvalidDataException("PostgreSQL backend identity.");
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        foreach (var batch in _batches)
            await batch.DisposeAsync().ConfigureAwait(false);
        foreach (var batch in _nativeBatches)
            await batch.DisposeAsync().ConfigureAwait(false);
        _batches = [];
        _nativeBatches = [];
        if (_command is not null)
            await _command.DisposeAsync().ConfigureAwait(false);
        if (_nativeCommand is not null)
            await _nativeCommand.DisposeAsync().ConfigureAwait(false);
        if (_connection is not null)
            await _connection.DisposeAsync().ConfigureAwait(false);
        if (_nativeConnection is not null)
            await _nativeConnection.DisposeAsync().ConfigureAwait(false);
        if (_source is not null)
            await _source.DisposeAsync().ConfigureAwait(false);
        if (_nativeSource is not null)
            await _nativeSource.DisposeAsync().ConfigureAwait(false);
    }

    // Connection strings/passwords stay private and are never benchmark metadata.
    private sealed record LiveSettings(string Host, int Port, string User, string Password, string Database)
    {
        internal static LiveSettings Read()
        {
            var host = Required("MPGSQL_TEST_HOST");
            var portText = Required("MPGSQL_TEST_PORT");
            if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                || port is < 1 or > 65535)
                throw new InvalidOperationException("MPGSQL_TEST_PORT must be from 1 to 65535.");
            return new LiveSettings(host, port, Required("MPGSQL_TEST_USER"),
                Required("MPGSQL_TEST_PASSWORD", allowEmpty: true), Required("MPGSQL_TEST_DATABASE"));
        }

        private static string Required(string name, bool allowEmpty = false)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (value is null || !allowEmpty && string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException("Set " + name + " to run live ADO.NET benchmarks.");
            return value;
        }
    }
}
