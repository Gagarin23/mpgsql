using System.Data;
using Mpgsql.Benchmarks.Queries;
using Npgsql;
using NpgsqlTypes;

namespace Mpgsql.Benchmarks.Comparison;

internal sealed class TcpNpgsqlFixture : IAsyncDisposable
{
    private readonly int _connections;
    private TcpNpgsqlFixture(
        QueryCatalog catalog, int connections,
        bool multiplexing
    )
    {
        _connections = connections;
        Peer = new TcpQueryPeer(new TcpQueryCatalog(catalog));
        var settings = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = Peer.Port,
            Username = "benchmark",
            Database = "benchmark",
            SslMode = SslMode.Disable,
            GssEncryptionMode = GssEncryptionMode.Disable,
            Pooling = true,
            MaxPoolSize = connections,
            MinPoolSize = connections,
            Multiplexing = multiplexing,
            NoResetOnClose = true,
            MaxAutoPrepare = 0,
            Enlist = false,
            Timeout = 10,
            CommandTimeout = 0
        };
        ConnectionString = settings.ConnectionString;
        var builder = new NpgsqlDataSourceBuilder(ConnectionString);
        builder.ConfigureTypeLoading(options => options.EnableTypeLoading(false));
        Source = builder.Build();
        Commands = new NpgsqlCommand[catalog.Scenarios.Length][];
        Buffers =
        [
            .. catalog
                .Inputs[0]
                .Select(_ => new byte[catalog.Scenarios.Max(x => x.ByteaBytes)])
        ];
        for (var i = 0;
             i < Commands.Length;
             i++)
        {
            Commands[i] = new NpgsqlCommand[catalog.Inputs[i].Length];
            for (var worker = 0;
                 worker < Commands[i].Length;
                 worker++)
            {
                Commands[i][worker] = AddParameters(Source.CreateCommand(catalog.Scenarios[i].Sql), catalog.Scenarios[i], worker);
            }
        }
    }
    internal TcpQueryPeer Peer { get; }
    internal NpgsqlDataSource Source { get; }
    internal NpgsqlCommand[][] Commands { get; }
    internal byte[][] Buffers { get; }
    internal NpgsqlConnection? Connection { get; private set; }
    internal string ConnectionString { get; }
    public async ValueTask DisposeAsync()
    {
        foreach (var commands in Commands)
        foreach (var command in commands)
        {
            await command
                .DisposeAsync()
                .ConfigureAwait(false);
        }
        if (Connection is not null)
        {
            await Connection
                .DisposeAsync()
                .ConfigureAwait(false);
        }
        await Source
            .DisposeAsync()
            .ConfigureAwait(false);
        await Peer
            .DisposeAsync()
            .ConfigureAwait(false);
    }
    internal static NpgsqlCommand AddParameters(
        NpgsqlCommand command, QueryScenario scenario,
        int worker
    )
    {
        command.CommandTimeout = 0;
        command.Parameters.Add
        (
            new NpgsqlParameter<long>
            {
                NpgsqlDbType = NpgsqlDbType.Bigint,
                TypedValue = worker + 1L
            }
        );
        if (scenario.ByteaBytes != 0)
        {
            command.Parameters.Add
            (
                new NpgsqlParameter<byte[]>
                {
                    NpgsqlDbType = NpgsqlDbType.Bytea,
                    TypedValue = scenario.Blob
                }
            );
        }
        return command;
    }
    internal static async Task<TcpNpgsqlFixture> CreateAsync(
        QueryCatalog catalog, int connections = 1,
        bool multiplexing = false, bool exclusive = false
    )
    {
        var fixture = new TcpNpgsqlFixture(catalog, connections, multiplexing);
        var leases = new NpgsqlConnection[connections];
        var transactions = new NpgsqlTransaction[connections];
        try
        {
            for (var i = 0;
                 i < leases.Length;
                 i++)
            {
                leases[i] = await fixture
                    .Source.OpenConnectionAsync()
                    .ConfigureAwait(false);
                // A transaction pins a multiplexed logical lease to a physical connector.
                transactions[i] = await leases[i]
                    .BeginTransactionAsync()
                    .ConfigureAwait(false);
                await using var command = AddParameters(new NpgsqlCommand(catalog.Scenarios[0].Sql, leases[i]), catalog.Scenarios[0], 0);
                await using var reader = await command
                    .ExecuteReaderAsync()
                    .ConfigureAwait(false);
                if (await TcpQueryOperations
                        .ConsumeAsync(reader, catalog.Scenarios[0], fixture.Buffers[0])
                        .ConfigureAwait(false)
                    != catalog
                        .Scenarios[0]
                        .Expected(0))
                {
                    throw new InvalidOperationException("Npgsql physical warmup checksum.");
                }
            }
            for (var i = 0;
                 i < transactions.Length;
                 i++)
            {
                await transactions[i]
                    .DisposeAsync()
                    .ConfigureAwait(false);
            }
            if (exclusive)
            {
                fixture.Connection = leases[0];
                leases[0] = null!;
                foreach (var commands in fixture.Commands)
                foreach (var command in commands)
                {
                    await command
                        .DisposeAsync()
                        .ConfigureAwait(false);
                }
                for (var i = 0;
                     i < fixture.Commands.Length;
                     i++)
                for (var worker = 0;
                     worker < fixture.Commands[i].Length;
                     worker++)
                {
                    fixture
                        .Commands[i][worker] = AddParameters(new NpgsqlCommand(catalog.Scenarios[i].Sql, fixture.Connection), catalog.Scenarios[i], worker);
                }
            }
        }
        catch
        {
            await fixture
                .DisposeAsync()
                .ConfigureAwait(false);
            throw;
        }
        finally
        {
            foreach (var lease in leases)
            {
                if (lease is not null)
                {
                    await lease
                        .DisposeAsync()
                        .ConfigureAwait(false);
                }
            }
        }
        fixture.Peer.CheckHealthy();
        if (fixture.Peer.Counters()
                .Connections != connections)
        {
            throw new InvalidOperationException("Npgsql pool was not fully prewarmed.");
        }
        return fixture;
    }
    internal void CheckIdle()
    {
        Peer.CheckHealthy();
        if (Peer.Counters()
                .Connections != _connections)
        {
            throw new InvalidOperationException("A prewarmed Npgsql connector was replaced.");
        }
        if (Connection is {State: not ConnectionState.Open})
        {
            throw new InvalidOperationException("Npgsql exclusive connection closed.");
        }
    }
}