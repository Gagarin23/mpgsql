using System.Data;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Mpgsql.Benchmarks.Comparison;
using Mpgsql.Benchmarks.Queries;
using Npgsql;

namespace Mpgsql.Benchmarks;

// A separate destination-filling workload. The existing reader comparisons only
// accumulate values, so they are not baselines for these bulk operations.
[MemoryDiagnoser, JsonExporterAttribute.Full, Config(typeof(QueryBenchmarkConfig)), IterationTime(150),
 GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory), CategoriesColumn]
public class TcpAdoBulkReaderBenchmarks
{
    private TcpMpgsqlFixture? _fixture;
    private MpgsqlConnection? _connection;
    private MpgsqlCommand _command = null!;
    private TcpNpgsqlFixture? _nativeFixture;
    private NpgsqlCommand _nativeCommand = null!;
    private QueryScenario _scenario = null!;
    private long[] _columns = null!;
    private BigintRecord[] _records = null!;
    private readonly long[] _columnProbe = new long[1];
    private readonly BigintRecord[] _recordProbe = new BigintRecord[1];
    private int _capacity;

    [Params(64, 256, 4096, 65536)]
    public int Rows { get; set; } = 4096;

    // Zero is the full-result capacity for the selected row count.
    [Params(256, 4096, 0)]
    public int Capacity { get; set; } = 256;

    public int EffectiveCapacity => _capacity;
    public int OperationsPerInvocation => 1;
    public int QueriesPerOperation => 1;
    public long ExpectedChecksum => (long)Rows * (Rows + 1) / 2;

    [GlobalSetup(Targets = [nameof(ColumnBulk), nameof(ColumnRows), nameof(RecordsBulk), nameof(RecordsRows)])]
    public async Task SetupMpgsql()
    {
        InitializeDestination();
        _fixture = await TcpMpgsqlFixture.CreateAsync(new QueryCatalog([_scenario], 1), multiplexing: false,
            instrumentTransport: false, coalesceReplies: false).ConfigureAwait(false);
        _connection = await _fixture.OpenClientConnectionAsync().ConfigureAwait(false);
        _command = _connection.CreateCommand(_scenario.Sql);
        _command.CommandTimeout = 0;
        _command.Parameters.Add(new MpgsqlParameter<long>(20U, 1L));

        await ValidateExecutionAsync(ColumnBulk, records: false).ConfigureAwait(false);
        await ValidateExecutionAsync(ColumnRows, records: false).ConfigureAwait(false);
        await ValidateExecutionAsync(RecordsBulk, records: true).ConfigureAwait(false);
        await ValidateExecutionAsync(RecordsRows, records: true).ConfigureAwait(false);
        WriteMetadata("Mpgsql");
    }

    [GlobalSetup(Targets = [nameof(NpgsqlColumnRows), nameof(NpgsqlRecordsRows)])]
    public async Task SetupNpgsql()
    {
        InitializeDestination();
        _nativeFixture = await TcpNpgsqlFixture.CreateAsync(new QueryCatalog([_scenario], 1), exclusive: true,
            coalesceReplies: false).ConfigureAwait(false);
        _nativeCommand = _nativeFixture.Commands[0][0];

        await ValidateExecutionAsync(NpgsqlColumnRows, records: false).ConfigureAwait(false);
        await ValidateExecutionAsync(NpgsqlRecordsRows, records: true).ConfigureAwait(false);
        WriteMetadata("Npgsql");
    }

    private void InitializeDestination()
    {
        if (Rows is not (64 or 256 or 4096 or 65536))
            throw new ArgumentOutOfRangeException(nameof(Rows));
        if (Capacity is not (0 or 256 or 4096))
            throw new ArgumentOutOfRangeException(nameof(Capacity));

        _capacity = Capacity == 0 ? Rows : Math.Min(Capacity, Rows);
        // This standalone transcript does not change the shared scenario catalog.
        _scenario = new QueryScenario($"BulkRows{Rows}",
            $"select $1::bigint+i-1 from generate_series(1,{Rows}) i", Rows);
        _columns = new long[Rows];
        _records = new BigintRecord[Rows];
    }

    [Benchmark, BenchmarkCategory("Column")]
    public async Task<long> ColumnBulk()
    {
        await using var reader = await _command.ExecuteReaderValueTaskAsync().ConfigureAwait(false);
        var offset = 0;
        while (offset < _columns.Length)
        {
            var destination = _columns.AsMemory(offset, Math.Min(_capacity, _columns.Length - offset));
            var count = await reader.ReadColumnAsync<long>(0, destination).ConfigureAwait(false);
            RequireProgress(count, destination.Length);
            offset += count;
        }
        // A full destination is not an EOF check. Include the terminal movement
        // with nonempty reusable memory, then consume the ReadyForQuery boundary.
        if (await reader.ReadColumnAsync<long>(0, _columnProbe.AsMemory()).ConfigureAwait(false) != 0)
            throw new InvalidOperationException("Unexpected row after the bulk column destination.");
        if (await reader.NextResultValueTaskAsync().ConfigureAwait(false))
            throw new InvalidOperationException("Unexpected bulk column result set.");
        return ColumnChecksum();
    }

    [Benchmark, BenchmarkCategory("Column")]
    public async Task<long> ColumnRows()
    {
        await using var reader = await _command.ExecuteReaderValueTaskAsync().ConfigureAwait(false);
        var offset = 0;
        while (offset < _columns.Length)
        {
            var destination = _columns.AsMemory(offset, Math.Min(_capacity, _columns.Length - offset));
            for (var i = 0; i < destination.Length; i++)
            {
                if (!await reader.ReadValueTaskAsync().ConfigureAwait(false))
                    throw new InvalidOperationException("Incomplete row-by-row column destination.");
                destination.Span[i] = reader.GetFieldValue<long>(0);
            }
            offset += destination.Length;
        }
        if (await reader.ReadValueTaskAsync().ConfigureAwait(false))
            throw new InvalidOperationException("Unexpected row after the row-by-row column destination.");
        if (await reader.NextResultValueTaskAsync().ConfigureAwait(false))
            throw new InvalidOperationException("Unexpected row-by-row column result set.");
        return ColumnChecksum();
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Column")]
    public async Task<long> NpgsqlColumnRows()
    {
        await using var reader = await _nativeCommand.ExecuteReaderAsync().ConfigureAwait(false);
        var offset = 0;
        while (offset < _columns.Length)
        {
            var destination = _columns.AsMemory(offset, Math.Min(_capacity, _columns.Length - offset));
            for (var i = 0; i < destination.Length; i++)
            {
                if (!await reader.ReadAsync().ConfigureAwait(false))
                    throw new InvalidOperationException("Incomplete native row-by-row column destination.");
                destination.Span[i] = reader.GetFieldValue<long>(0);
            }
            offset += destination.Length;
        }
        if (await reader.ReadAsync().ConfigureAwait(false))
            throw new InvalidOperationException("Unexpected row after the native column destination.");
        if (await reader.NextResultAsync().ConfigureAwait(false))
            throw new InvalidOperationException("Unexpected native column result set.");
        return ColumnChecksum();
    }

    [Benchmark, BenchmarkCategory("Records")]
    public async Task<long> RecordsBulk()
    {
        await using var reader = await _command.ExecuteReaderValueTaskAsync().ConfigureAwait(false);
        var mapper = default(BigintRecordMapper);
        var offset = 0;
        while (offset < _records.Length)
        {
            var destination = _records.AsMemory(offset, Math.Min(_capacity, _records.Length - offset));
            var count = await reader.ReadRowsAsync<BigintRecord, BigintRecordMapper>(destination, mapper)
                .ConfigureAwait(false);
            RequireProgress(count, destination.Length);
            offset += count;
        }
        if (await reader.ReadRowsAsync<BigintRecord, BigintRecordMapper>(_recordProbe.AsMemory(), mapper)
                .ConfigureAwait(false) != 0)
            throw new InvalidOperationException("Unexpected row after the bulk record destination.");
        if (await reader.NextResultValueTaskAsync().ConfigureAwait(false))
            throw new InvalidOperationException("Unexpected bulk record result set.");
        return RecordChecksum();
    }

    [Benchmark, BenchmarkCategory("Records")]
    public async Task<long> RecordsRows()
    {
        await using var reader = await _command.ExecuteReaderValueTaskAsync().ConfigureAwait(false);
        var offset = 0;
        while (offset < _records.Length)
        {
            var destination = _records.AsMemory(offset, Math.Min(_capacity, _records.Length - offset));
            for (var i = 0; i < destination.Length; i++)
            {
                if (!await reader.ReadValueTaskAsync().ConfigureAwait(false))
                    throw new InvalidOperationException("Incomplete row-by-row record destination.");
                destination.Span[i] = new BigintRecord(reader.GetFieldValue<long>(0));
            }
            offset += destination.Length;
        }
        if (await reader.ReadValueTaskAsync().ConfigureAwait(false))
            throw new InvalidOperationException("Unexpected row after the row-by-row record destination.");
        if (await reader.NextResultValueTaskAsync().ConfigureAwait(false))
            throw new InvalidOperationException("Unexpected row-by-row record result set.");
        return RecordChecksum();
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Records")]
    public async Task<long> NpgsqlRecordsRows()
    {
        await using var reader = await _nativeCommand.ExecuteReaderAsync().ConfigureAwait(false);
        var offset = 0;
        while (offset < _records.Length)
        {
            var destination = _records.AsMemory(offset, Math.Min(_capacity, _records.Length - offset));
            for (var i = 0; i < destination.Length; i++)
            {
                if (!await reader.ReadAsync().ConfigureAwait(false))
                    throw new InvalidOperationException("Incomplete native row-by-row record destination.");
                destination.Span[i] = new BigintRecord(reader.GetFieldValue<long>(0));
            }
            offset += destination.Length;
        }
        if (await reader.ReadAsync().ConfigureAwait(false))
            throw new InvalidOperationException("Unexpected row after the native record destination.");
        if (await reader.NextResultAsync().ConfigureAwait(false))
            throw new InvalidOperationException("Unexpected native record result set.");
        return RecordChecksum();
    }

    private static void RequireProgress(int count, int capacity)
    {
        if (count <= 0 || count > capacity)
            throw new InvalidOperationException("Incomplete or invalid bulk destination count.");
    }

    // The same post-fill traversal is timed in every method of its category.
    // It observes every destination element without folding checksum work into
    // only the row-by-row paths or adding any driver payload-validation pass.
    private long ColumnChecksum()
    {
        long sum = 0;
        foreach (var value in _columns)
            sum += value;
        if (sum != ExpectedChecksum)
            throw new InvalidOperationException("Bulk comparison column checksum changed.");
        return sum;
    }

    private long RecordChecksum()
    {
        long sum = 0;
        foreach (var value in _records)
            sum += value.Value;
        if (sum != ExpectedChecksum)
            throw new InvalidOperationException("Bulk comparison record checksum changed.");
        return sum;
    }

    private async Task ValidateExecutionAsync(Func<Task<long>> execute, bool records)
    {
        if (records)
            Array.Fill(_records, new BigintRecord(long.MinValue));
        else
            Array.Fill(_columns, long.MinValue);

        var peer = _fixture?.Peer ?? _nativeFixture!.Peer;
        var before = peer.Counters();
        if (await execute().ConfigureAwait(false) != ExpectedChecksum)
            throw new InvalidOperationException("Bulk comparison checksum.");
        for (var i = 0; i < Rows; i++)
        {
            if ((records ? _records[i].Value : _columns[i]) != i + 1L)
                throw new InvalidOperationException("Bulk comparison destination value or order.");
        }
        CheckIdle();
        var after = peer.Counters();
        if (after.Queries - before.Queries != 1 || after.Syncs - before.Syncs != 1)
            throw new InvalidOperationException("Bulk comparison requires one query and one Sync per invocation.");
    }

    public void CheckIdle()
    {
        _fixture?.CheckIdle();
        if (_connection is not null)
        {
            lock (_connection.Gate)
                _connection.CheckAvailable();
            if (!_connection.Session.IsIdleAndHealthy)
                throw new InvalidOperationException("Bulk comparison Mpgsql session is not healthy and idle.");
        }
        _nativeFixture?.CheckIdle();
        if (_nativeFixture is not null && _nativeFixture.Connection!.FullState != ConnectionState.Open)
            throw new InvalidOperationException("Bulk comparison retains an active native operation.");
        var counters = (_fixture?.Peer ?? _nativeFixture!.Peer).Counters();
        if (counters.Connections != 1 || counters.Queries != counters.Syncs)
            throw new InvalidOperationException("Bulk comparison connection or Sync count changed.");
    }

    private void WriteMetadata(string provider)
    {
        var nativeAssembly = typeof(NpgsqlConnection).Assembly;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Benchmark = nameof(TcpAdoBulkReaderBenchmarks),
            Provider = provider,
            Rows,
            Capacity,
            EffectiveCapacity,
            ExpectedChecksum,
            LogicalValueBytesPerResult = 8L * Rows,
            DataRowWireBytesPerResult = 19L * Rows,
            OperationsPerInvocation,
            QueriesPerOperation,
            SyncsPerOperation = 1,
            ReadBufferSize = _fixture?.ReadBufferSize ?? _nativeFixture!.ReadBufferSize,
            InstrumentTransport = false,
            CoalesceReplies = false,
            Runtime = RuntimeInformation.FrameworkDescription,
            NpgsqlPackageVersion = nativeAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            AssembliesSha256 = new Dictionary<string, string>
            {
                ["Mpgsql.Protocol"] = AssemblyHash(typeof(TypeOid).Assembly),
                ["Mpgsql.Sessions"] = AssemblyHash(typeof(MpgsqlMessageSession).Assembly),
                ["Mpgsql"] = AssemblyHash(typeof(MpgsqlDataReader).Assembly),
                ["Mpgsql.Benchmarks"] = AssemblyHash(typeof(TcpAdoBulkReaderBenchmarks).Assembly),
                ["Npgsql"] = AssemblyHash(nativeAssembly)
            },
            Scope = "Sequential loopback TCP, fixed binary transcript; exclusive preopened connection and reused command. " +
                "Complete destination fill, common checksum traversal, EOF/NextResult and asynchronous reader disposal are timed. " +
                "Mpgsql uses ValueTask execution/movement/bulk APIs; Npgsql uses Task execution/movement."
        }));
    }

    private static string AssemblyHash(Assembly assembly)
    {
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location)));
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_fixture is not null)
        {
            try
            {
                CheckIdle();
            }
            finally
            {
                if (_command is not null)
                    await _command.DisposeAsync().ConfigureAwait(false);
                if (_connection is not null)
                    await _connection.DisposeAsync().ConfigureAwait(false);
                await _fixture.DisposeAsync().ConfigureAwait(false);
            }
        }
        if (_nativeFixture is not null)
        {
            try
            {
                CheckIdle();
            }
            finally
            {
                await _nativeFixture.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private readonly record struct BigintRecord(long Value);

    private readonly struct BigintRecordMapper : IMpgsqlRowMapper<BigintRecord>
    {
        public BigintRecord Read(MpgsqlRow row) => new(row.GetFieldValue<long>(0));
    }
}
