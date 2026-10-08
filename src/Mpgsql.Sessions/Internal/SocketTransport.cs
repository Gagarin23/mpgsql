using System.Buffers;
using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Mpgsql.Protocol;

namespace Mpgsql.Internal;

internal sealed class SocketTransport(TcpClient client, Stream stream, MpgsqlSessionOptions options) : IDisposable
{
    internal Stream Stream { get; } = stream;
    internal BackendKeyData? BackendKey { get; private set; }
    internal Dictionary<string, string> Parameters { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
    internal List<DiagnosticMessage> Notices { get; } = [];

    public void Dispose()
    {
        Stream.Dispose();
        client.Dispose();
    }

    internal static async ValueTask<SocketTransport> ConnectAsync(MpgsqlSessionOptions options, CancellationToken token)
    {
        var client = new TcpClient
        {
            NoDelay = true
        };
        try
        {
            await client
                .ConnectAsync(options.Host, options.Port, token)
                .ConfigureAwait(false);
            Stream stream = client.GetStream();
            if (options.SslMode != MpgsqlSslMode.Disable)
            {
                await WriteAsync(stream, FrontendMessage.SslRequest(), token)
                    .ConfigureAwait(false);
                var response = new byte[1];
                await stream
                    .ReadExactlyAsync(response, token)
                    .ConfigureAwait(false);
                if (response[0] != 'S')
                {
                    throw new AuthenticationException("The server did not accept TLS.");
                }
                var root = options.RootCertificate is null ? null : X509Certificate2.CreateFromPem(File.ReadAllText(options.RootCertificate));
                try
                {
                    var ssl = new SslStream
                    (
                        stream, false, (
                            _, certificate,
                            _, errors
                        ) => ValidateCertificate(options.SslMode, certificate, errors, root)
                    );
                    try
                    {
                        await ssl
                            .AuthenticateAsClientAsync
                            (
                                new SslClientAuthenticationOptions
                                {
                                    TargetHost = options.Host,
                                    AllowRenegotiation = false
                                }, token
                            )
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        ssl.Dispose();
                        throw;
                    }
                    stream = ssl;
                }
                finally { root?.Dispose(); }
            }
            return new SocketTransport(client, stream, options);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    internal static bool ValidateCertificate(
        MpgsqlSslMode mode, X509Certificate? certificate,
        SslPolicyErrors errors, X509Certificate2? root
    )
    {
        if (mode == MpgsqlSslMode.Require)
        {
            return true;
        }
        if (certificate is null)
        {
            return false;
        }
        if (mode == MpgsqlSslMode.VerifyFull && (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
        {
            return false;
        }
        if (root is null)
        {
            return (errors & ~SslPolicyErrors.RemoteCertificateNameMismatch) == SslPolicyErrors.None;
        }
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        using var leaf = new X509Certificate2(certificate);
        return chain.Build(leaf);
    }

    internal async ValueTask StartupAsync(CancellationToken token)
    {
        await WriteAsync
            (
                Stream, FrontendMessage.Startup
                (
                    new[]
                    {
                        new KeyValuePair<string, string>("user", options.Username),
                        new KeyValuePair<string, string>("database", options.Database ?? options.Username),
                        new KeyValuePair<string, string>("client_encoding", "UTF8"),
                        new KeyValuePair<string, string>("application_name", options.ApplicationName)
                    }
                ), token
            )
            .ConfigureAwait(false);
        var authenticated = false;
        ScramAuthentication? scram = null;
        var passwordSent = false;
        while (true)
        {
            // Exact reads deliberately avoid startup read-ahead: no frame tail is lost when
            // the authenticated stream is handed to PipeReader.
            var frame = await ReadFrameAsync(Stream, token)
                .ConfigureAwait(false);
            var input = new ReadOnlySequence<byte>(frame);
            if (!BackendMessageReader.TryRead(ref input, out var message))
            {
                throw new InvalidDataException("Incomplete startup frame.");
            }
            switch (message.Kind)
            {
                case BackendMessageKind.Authentication:
                    var auth = message.GetAuthentication();
                    switch (auth.Method)
                    {
                        case AuthenticationMethod.Ok:
                            if (authenticated || scram is not null && !scram.Completed)
                            {
                                throw new InvalidDataException("Invalid AuthenticationOk.");
                            }
                            authenticated = true;
                            break;
                        case AuthenticationMethod.CleartextPassword:
                        case AuthenticationMethod.Md5Password:
                            if (authenticated || passwordSent || scram is not null)
                            {
                                throw new InvalidDataException("Repeated password challenge.");
                            }
                            var password = options.Password ?? throw new AuthenticationException("The server requires a password.");
                            if (auth.Method == AuthenticationMethod.Md5Password)
                            {
                                var first = Encoding.ASCII.GetBytes(Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(password + options.Username))));
                                password = "md5" + Convert.ToHexStringLower(MD5.HashData([.. first, .. auth.Data.ToArray()]));
                            }
                            await WriteAsync(Stream, FrontendMessage.Password(password), token)
                                .ConfigureAwait(false);
                            passwordSent = true;
                            break;
                        case AuthenticationMethod.Sasl:
                            if (authenticated || passwordSent || scram is not null)
                            {
                                throw new InvalidDataException("Repeated SASL challenge.");
                            }
                            if (!auth.Mechanisms.Span.Contains("SCRAM-SHA-256"))
                            {
                                throw new NotSupportedException("Only SCRAM-SHA-256 SASL is supported.");
                            }
                            scram = new ScramAuthentication();
                            await WriteAsync(Stream, FrontendMessage.SaslInitialResponse("SCRAM-SHA-256", scram.First()), token)
                                .ConfigureAwait(false);
                            break;
                        case AuthenticationMethod.SaslContinue:
                            await WriteAsync
                                (
                                    Stream,
                                    FrontendMessage.SaslResponse
                                    (
                                        (scram ?? throw new InvalidDataException("Unexpected SASL continuation.")).Continue
                                        (
                                            Encoding.UTF8.GetString(auth.Data.ToArray()),
                                            options.Password ?? throw new AuthenticationException("The server requires a password.")
                                        )
                                    ), token
                                )
                                .ConfigureAwait(false); break;
                        case AuthenticationMethod.SaslFinal:
                            (scram ?? throw new InvalidDataException("Unexpected SASL final.")).Verify(Encoding.UTF8.GetString(auth.Data.ToArray())); break;
                        default: throw new NotSupportedException($"Authentication {auth.Method} is not supported.");
                    }
                    break;
                case BackendMessageKind.ParameterStatus:
                    var status = message.GetParameterStatus();
                    Parameters[status.Name] = status.Value;
                    break;
                case BackendMessageKind.NoticeResponse: Notices.Add(message.GetDiagnostics()); break;
                case BackendMessageKind.BackendKeyData:
                    if (!authenticated || BackendKey is not null)
                    {
                        throw new InvalidDataException("Invalid startup BackendKeyData.");
                    }
                    BackendKey = message.GetBackendKeyData();
                    if (BackendKey.Value.ProcessId <= 0)
                    {
                        throw new InvalidDataException("Invalid backend process identifier.");
                    }
                    break;
                case BackendMessageKind.ErrorResponse: throw new MpgsqlServerException(message.GetDiagnostics(), null, null);
                case BackendMessageKind.ReadyForQuery:
                    if (!authenticated || message.GetTransactionStatus() != TransactionStatus.Idle)
                    {
                        throw new InvalidDataException("Invalid startup ReadyForQuery.");
                    }
                    if (Parameters.TryGetValue("client_encoding", out var encoding) && encoding != "UTF8")
                    {
                        throw new NotSupportedException("Mpgsql requires UTF8 client encoding.");
                    }
                    return;
                default: throw new InvalidDataException($"Unexpected startup message {message.Kind}.");
            }
        }
    }

    internal async ValueTask CancelAsync(CancellationToken token)
    {
        var key = BackendKey ?? throw new InvalidOperationException("The server did not provide BackendKeyData.");
        using var channel = await ConnectAsync(options, token)
            .ConfigureAwait(false);
        await WriteAsync(channel.Stream, FrontendMessage.CancelRequest(key.ProcessId, key.SecretKey), token)
            .ConfigureAwait(false);
        var reply = new byte[1];
        if (await channel
                .Stream.ReadAsync(reply, token)
                .ConfigureAwait(false) != 0)
        {
            throw new InvalidDataException("Unexpected CancelRequest response.");
        }
    }

    private static async ValueTask<byte[]> ReadFrameAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[5];
        await stream
            .ReadExactlyAsync(header, token)
            .ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
        if (length is < 4 or > 16 * 1024 * 1024)
        {
            throw new InvalidDataException("Invalid startup frame length.");
        }
        var frame = new byte[length + 1];
        header.CopyTo(frame, 0);
        await stream
            .ReadExactlyAsync(frame.AsMemory(5), token)
            .ConfigureAwait(false);
        return frame;
    }

    private static async ValueTask WriteAsync<T>(
        Stream stream, T message,
        CancellationToken token
    ) where T : struct, IFrontendMessage<T>
    {
        var bytes = new byte[FrontendMessageWriter.GetByteCount(message)];
        FrontendMessageWriter.Write(message, bytes);
        await stream
            .WriteAsync(bytes, token)
            .ConfigureAwait(false);
    }
}