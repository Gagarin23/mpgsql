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
    internal MpgsqlMessageSession Session { get; }
    internal CountingPipeWriter Writer { get; }
    private MpgsqlTcpTransport(TcpClient client)
    {
        _client = client;
        var stream = client.GetStream();
        Writer = new(PipeWriter.Create(stream, new(leaveOpen: true)));
        Session = new(PipeReader.Create(stream, new(leaveOpen: true)), Writer);
    }
    internal static async Task<MpgsqlTcpTransport> OpenAsync(int port, CancellationToken token = default)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, token).ConfigureAwait(false);
            var stream = client.GetStream();
            var startup = new ArrayBufferWriter<byte>();
            FrontendMessage.Startup("benchmark", "benchmark").Write(startup);
            await stream.WriteAsync(startup.WrittenMemory, token).ConfigureAwait(false);
            byte[] header = new byte[5];
            while (true)
            {
                await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
                int length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
                if (length is < 4 or > 16384) throw new InvalidDataException("Startup backend length.");
                byte[] payload = new byte[length - 4];
                await stream.ReadExactlyAsync(payload, token).ConfigureAwait(false);
                if (header[0] == 'Z')
                {
                    if (payload.Length != 1 || payload[0] != 'I') throw new InvalidDataException("Startup ReadyForQuery.");
                    break;
                }
                if (header[0] == 'R' && (payload.Length != 4 || BinaryPrimitives.ReadInt32BigEndian(payload) != 0))
                    throw new InvalidDataException("Expected AuthenticationOk.");
                if (header[0] == 'E') throw new InvalidDataException("Startup ErrorResponse.");
            }
            return new(client);
        }
        catch { client.Dispose(); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        await Session.DisposeAsync().ConfigureAwait(false);
        _client.Dispose();
    }
}
