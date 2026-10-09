using System.IO.Pipelines;
using Mpgsql.Internal;
using Mpgsql.Protocol;

namespace Mpgsql;

public sealed partial class MpgsqlMessageSession
{
    // The measured 32 KiB setting amortizes receive bursts for larger row sets.
    // Keep the existing segment-allocation threshold; ReadAsync returns after one stream read.
    internal const int DefaultReadBufferSize = 32 * 1024;
    private readonly SocketTransport? _transport;
    public BackendKeyData? BackendKey => _transport?.BackendKey;

    public static async ValueTask<MpgsqlMessageSession> OpenAsync(MpgsqlSessionOptions options, CancellationToken cancellationToken = default)
        => await OpenCoreAsync(options, false, cancellationToken).ConfigureAwait(false);

    internal static ValueTask<MpgsqlMessageSession> OpenAdoAsync(MpgsqlSessionOptions options, CancellationToken cancellationToken = default)
        => OpenCoreAsync(options, true, cancellationToken);

    private static async ValueTask<MpgsqlMessageSession> OpenCoreAsync(MpgsqlSessionOptions options, bool adoSession, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (options.ConnectTimeout != TimeSpan.Zero)
        {
            deadline.CancelAfter(options.ConnectTimeout);
        }
        SocketTransport? transport = null;
        try
        {
            transport = await SocketTransport
                .ConnectAsync(options, deadline.Token)
                .ConfigureAwait(false);
            await transport
                .StartupAsync(deadline.Token)
                .ConfigureAwait(false);
            return new MpgsqlMessageSession(PipeReader.Create(transport.Stream,
                new StreamPipeReaderOptions(bufferSize: DefaultReadBufferSize, leaveOpen: true)),
                PipeWriter.Create(transport.Stream, new StreamPipeWriterOptions(leaveOpen: true)), default, transport, adoSession);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            transport?.Dispose();
            throw new TimeoutException("Opening the PostgreSQL session timed out.", error);
        }
        catch
        {
            transport?.Dispose();
            throw;
        }
    }

    public ValueTask SendCancelRequestAsync(CancellationToken cancellationToken = default)
    {
        return (_transport ?? throw new NotSupportedException("Externally authenticated sessions require an external cancellation sender.")).CancelAsync(cancellationToken);
    }
}
