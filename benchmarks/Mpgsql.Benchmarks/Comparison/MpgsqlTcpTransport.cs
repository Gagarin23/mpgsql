using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using Mpgsql.Benchmarks.Queries;
using Mpgsql.Protocol;

namespace Mpgsql.Benchmarks.Comparison;

internal sealed class MpgsqlTcpTransport : IAsyncDisposable
{
    private readonly TcpClient _client;
    private MpgsqlTcpTransport(TcpClient client)
    {
        _client = client;
        var stream = client.GetStream();
        Writer = new CountingPipeWriter(PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true)));
        Session = new MpgsqlMessageSession(PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true)), Writer);
    }
    internal MpgsqlMessageSession Session { get; }
    internal CountingPipeWriter Writer { get; }
    public async ValueTask DisposeAsync()
    {
        await Session.DisposeAsync().ConfigureAwait(false);
        _client.Dispose();
    }
    internal static async Task<MpgsqlTcpTransport> OpenAsync(int port, CancellationToken token = default)
    {
        var client = new TcpClient {NoDelay = true};
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, token).ConfigureAwait(false);
            var stream = client.GetStream();
            var startup = new ArrayBufferWriter<byte>();
            FrontendMessage.Startup("benchmark", "benchmark").Write(startup);
            await stream.WriteAsync(startup.WrittenMemory, token).ConfigureAwait(false);
            var header = new byte[5];
            while (true)
            {
                await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
                var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
                if (length is < 4 or > 16384)
                {
                    throw new InvalidDataException("Startup backend length.");
                }
                var payload = new byte[length - 4];
                await stream.ReadExactlyAsync(payload, token).ConfigureAwait(false);
                if (header[0] == 'Z')
                {
                    if (payload.Length != 1 || payload[0] != 'I')
                    {
                        throw new InvalidDataException("Startup ReadyForQuery.");
                    }
                    break;
                }
                if (header[0] == 'R' && (payload.Length != 4 || BinaryPrimitives.ReadInt32BigEndian(payload) != 0))
                {
                    throw new InvalidDataException("Expected AuthenticationOk.");
                }
                if (header[0] == 'E')
                {
                    throw new InvalidDataException("Startup ErrorResponse.");
                }
            }
            return new MpgsqlTcpTransport(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}