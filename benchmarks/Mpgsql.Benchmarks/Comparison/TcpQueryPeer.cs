using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Mpgsql.Benchmarks.Queries;

namespace Mpgsql.Benchmarks.Comparison;

internal sealed class TcpQueryPeer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly TcpQueryCatalog _catalog;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Connection> _connections = [];
    private readonly object _gate = new();
    private readonly Task _accept;
    internal int Port { get; }
    internal TcpQueryPeer(TcpQueryCatalog catalog)
    {
        _catalog = catalog;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accept = AcceptAsync();
    }
    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                client.NoDelay = true;
                var connection = new Connection(client, _catalog, _stop.Token);
                lock (_gate) _connections.Add(connection);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
    }
    internal (long Queries, long Syncs, long ReplyBytes, int Connections) Counters()
    {
        lock (_gate) return (_connections.Sum(x => Interlocked.Read(ref x.Queries)),
            _connections.Sum(x => Interlocked.Read(ref x.Syncs)), _connections.Sum(x => Interlocked.Read(ref x.ReplyBytes)), _connections.Count);
    }
    internal void CheckHealthy()
    {
        if (_accept.IsFaulted) _accept.GetAwaiter().GetResult();
        lock (_gate) foreach (var connection in _connections) connection.ThrowIfFailed();
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _listener.Stop();
        await _accept.ConfigureAwait(false);
        Connection[] connections;
        lock (_gate) connections = [.. _connections];
        foreach (var connection in connections) await connection.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }

    private sealed class Connection : IAsyncDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly PipeReader _input;
        private readonly PipeWriter _output;
        private readonly CancellationToken _stop;
        private readonly TcpQueryProtocol _protocol;
        private readonly Channel<ReadOnlyMemory<byte>> _replies = Channel.CreateBounded<ReadOnlyMemory<byte>>(
            new BoundedChannelOptions(256) { SingleReader = true, SingleWriter = true, AllowSynchronousContinuations = false });
        private readonly Task _read, _write;
        private Exception? _failure;
        internal long Queries, Syncs, ReplyBytes;
        internal Connection(TcpClient client, TcpQueryCatalog catalog, CancellationToken stop)
        {
            _client = client; _stream = client.GetStream(); _stop = stop;
            _input = PipeReader.Create(_stream, new(leaveOpen: true));
            _output = PipeWriter.Create(_stream, new(leaveOpen: true));
            _protocol = new(catalog);
            _read = ReadAsync(); _write = WriteAsync();
        }
        private async Task ReadAsync()
        {
            Exception? failure = null;
            try
            {
                byte[] length = new byte[4];
                await _stream.ReadExactlyAsync(length, _stop).ConfigureAwait(false);
                int size = BinaryPrimitives.ReadInt32BigEndian(length);
                if (size is < 8 or > 16384) throw new InvalidDataException("Startup length.");
                byte[] startup = new byte[size - 4];
                await _stream.ReadExactlyAsync(startup, _stop).ConfigureAwait(false);
                if (BinaryPrimitives.ReadInt32BigEndian(startup) != 196608 || startup[^1] != 0)
                    throw new InvalidDataException("Only protocol 3.0 Startup is supported.");
                await _replies.Writer.WriteAsync(TcpQueryCatalog.StartupReply, _stop).ConfigureAwait(false);
                while (true)
                {
                    var read = await _input.ReadAsync(_stop).ConfigureAwait(false);
                    var remaining = read.Buffer;
                    try
                    {
                        while (FrontendFrameParser.TryRead(ref remaining, out byte tag, out var payload, out _))
                        {
                            var response = _protocol.Process(tag, payload);
                            Interlocked.Exchange(ref Queries, _protocol.Queries);
                            Interlocked.Exchange(ref Syncs, _protocol.Syncs);
                            if (!response.IsEmpty) await _replies.Writer.WriteAsync(response, _stop).ConfigureAwait(false);
                            if (_protocol.Terminated) return;
                        }
                        if (read.IsCompleted)
                        {
                            if (!remaining.IsEmpty) throw new InvalidDataException("Truncated TCP frontend.");
                            return;
                        }
                    }
                    finally { _input.AdvanceTo(remaining.Start, read.Buffer.End); }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (IOException) when (_stop.IsCancellationRequested) { }
            catch (Exception error) { failure = error; Interlocked.CompareExchange(ref _failure, error, null); _client.Dispose(); }
            finally { _replies.Writer.TryComplete(failure); await _input.CompleteAsync(failure).ConfigureAwait(false); }
        }
        private async Task WriteAsync()
        {
            Exception? failure = null;
            try
            {
                while (await _replies.Reader.WaitToReadAsync(_stop).ConfigureAwait(false))
                {
                    while (_replies.Reader.TryRead(out var response))
                    {
                        _output.Write(response.Span);
                        Interlocked.Add(ref ReplyBytes, response.Length);
                    }
                    var flush = await _output.FlushAsync(_stop).ConfigureAwait(false);
                    if (flush.IsCompleted) return;
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (IOException) when (_stop.IsCancellationRequested) { }
            catch (Exception error) { failure = error; Interlocked.CompareExchange(ref _failure, error, null); _client.Dispose(); }
            finally { await _output.CompleteAsync(failure).ConfigureAwait(false); }
        }
        internal void ThrowIfFailed()
        {
            if (_failure is { } error) throw new InvalidOperationException("TCP peer failed.", error);
            if (_read.IsFaulted) _read.GetAwaiter().GetResult();
            if (_write.IsFaulted) _write.GetAwaiter().GetResult();
        }
        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await Task.WhenAll(_read, _write).ConfigureAwait(false);
            ThrowIfFailed();
        }
    }
}
