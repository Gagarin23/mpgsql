using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net.Sockets;
using Mpgsql.Protocol;

namespace Mpgsql.IntegrationTests;

// Only the test harness establishes/authenticates transports and implements the cancel channel.
internal sealed class UpperApiTestSource : IAsyncDisposable
{
    private readonly ConcurrentDictionary<MpgsqlMessageSession, MpgsqlPreparedStatement> _prepared = new ConcurrentDictionary<MpgsqlMessageSession, MpgsqlPreparedStatement>();
    private readonly ConcurrentDictionary<MpgsqlMessageSession, TestConnection> _transports = new ConcurrentDictionary<MpgsqlMessageSession, TestConnection>();
    internal int CancelRequests;
    internal int FactoryCalls;

    internal UpperApiTestSource(string host, int port,
        string user, string password,
        string database,
        int maxConnections = 2, bool prepare = false,
        int syncGroupSize = 1, TimeSpan? syncGroupTimeout = null)
    {
        Func<CancellationToken, ValueTask<MpgsqlMessageSession>> factory = async token =>
        {
            var transport = await Task.Run(() => TestConnection.Open(host, port, user, password, database), token);
            var session = new MpgsqlMessageSession(PipeReader.Create(transport.CopyStream, new StreamPipeReaderOptions(leaveOpen: false)),
                PipeWriter.Create(transport.CopyStream, new StreamPipeWriterOptions(leaveOpen: false)));
            _transports[session] = transport;
            Interlocked.Increment(ref FactoryCalls);
            if (prepare)
            {
                var statement = session.CreatePreparedStatement("select $1::bigint + 1", new uint[] {20});
                await using var group = session.CreateBatch(token);
                await group.SendPrepareAsync(statement);
                await group.SendSyncAsync();
                await statement.Prepared;
                await group.Completion;
                _prepared[session] = statement;
            }
            return session;
        };
        Func<MpgsqlMessageSession, CancellationToken, ValueTask> cancel = async (session, token) =>
        {
            var key = _transports[session].BackendKey;
            using var channel = new TcpClient {NoDelay = true};
            await channel.ConnectAsync(host, port, token);
            var bytes = new ArrayBufferWriter<byte>();
            FrontendMessage.CancelRequest(key.ProcessId, key.SecretKey).Write(bytes);
            await channel.GetStream().WriteAsync(bytes.WrittenMemory, token);
            var reply = new byte[1];
            if (await channel.GetStream().ReadAsync(reply, token) != 0)
            {
                throw new InvalidDataException("CancelRequest channel returned unexpected data.");
            }
            Interlocked.Increment(ref CancelRequests);
        };
        Source = new MpgsqlMultiplexingDataSource(factory, new MpgsqlMultiplexingOptions
        {
            // The session-mode fixtures have two backend slots. Reserve one for ADO.NET
            // when this harness exercises both independent pools in the same scope.
            MaxConnections = Math.Max(1, maxConnections - 1), MaxInFlightPerConnection = 8,
            SyncGroupSize = syncGroupSize, SyncGroupTimeout = syncGroupTimeout ?? TimeSpan.FromMilliseconds(1),
            MaxBufferedRowBytesPerConnection = 64 * 1024
        });
        ClientSource = new MpgsqlDataSource(factory, cancel, new MpgsqlDataSourceOptions {MaxConnections = maxConnections, MaxBufferedRowBytesPerConnection = 64 * 1024});
    }
    internal MpgsqlMultiplexingDataSource Source { get; }
    internal MpgsqlDataSource ClientSource { get; }
    internal MpgsqlMessageSession[] Sessions => [.. _transports.Keys];

    public async ValueTask DisposeAsync()
    {
        await Source.DisposeAsync();
        await ClientSource.DisposeAsync();
        foreach (var transport in _transports.Values) transport.Dispose();
    }
    internal MpgsqlPreparedStatement Statement(MpgsqlMessageSession session)
    {
        return _prepared[session];
    }
}