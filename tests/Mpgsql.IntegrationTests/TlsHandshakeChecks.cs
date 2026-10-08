using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Mpgsql.IntegrationTests;

internal static class TlsHandshakeChecks
{
    internal static async Task RunAsync()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Mpgsql test CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var root = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var leafKey = RSA.Create(2048);
        var leafRequest = new CertificateRequest("CN=localhost", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        leafRequest.CertificateExtensions.Add(san.Build());
        leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection {new Oid("1.3.6.1.5.5.7.3.1")}, true));
        using var issued = leafRequest.Create(root, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1), new byte[] {1});
        using var ephemeral = issued.CopyWithPrivateKey(leafKey);
        // Schannel needs an imported key container; Dispose removes this temporary key.
        using var leaf = X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, root.ExportCertificatePem());
        try
        {
            await CheckAsync(MpgsqlSslMode.VerifyFull, "localhost", path, leaf, true);
            await CheckAsync(MpgsqlSslMode.VerifyFull, "127.0.0.1", path, leaf, false);
            await CheckAsync(MpgsqlSslMode.VerifyFull, "localhost", null, leaf, false);
            await CheckAsync(MpgsqlSslMode.VerifyCA, "127.0.0.1", path, leaf, true);
            await CheckAsync(MpgsqlSslMode.Require, "127.0.0.1", null, leaf, true);
        }
        finally { File.Delete(path); }
        Console.WriteLine("PASS native TLS: complete SSLRequest/startup/CancelRequest, custom CA, VerifyFull hostname and trust rejection, VerifyCA, Require.");
    }
    private static async Task CheckAsync(MpgsqlSslMode mode, string host,
        string? root, X509Certificate2 certificate,
        bool succeeds)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = deadline.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = ServeAsync();
        var settings = new MpgsqlConnectionStringBuilder {Host = host, Port = port, Username = "tls", SslMode = mode, RootCertificate = root};
        await using var connection = new MpgsqlConnection(settings.ConnectionString);
        try
        {
            await connection.OpenAsync(token);
            if (!succeeds)
            {
                throw new InvalidDataException("TLS incorrectly accepted the certificate.");
            }
            await connection.Session.SendCancelRequestAsync(token);
        }
        catch (MpgsqlException error) when (!succeeds && error.InnerException is AuthenticationException) { }
        catch (Exception clientError)
        {
            try { await server.WaitAsync(token); }
            catch (Exception serverError) { throw new AggregateException("TLS client/server handshake diagnostics.", clientError, serverError); }
            throw;
        }
        finally { await connection.DisposeAsync(); }
        await server.WaitAsync(token);

        async Task ServeAsync()
        {
            using var client = await listener.AcceptTcpClientAsync(token);
            using var stream = client.GetStream();
            var request = new byte[8];
            await stream.ReadExactlyAsync(request, token);
            if (!request.AsSpan().SequenceEqual(new byte[] {0, 0, 0, 8, 4, 210, 22, 47}))
            {
                throw new InvalidDataException("SSLRequest bytes.");
            }
            await stream.WriteAsync(new[] {(byte)'S'}, token);
            using var ssl = new SslStream(stream, true);
            try { await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {ServerCertificate = certificate}, token); }
            catch (Exception error) when (!succeeds && error is IOException or AuthenticationException) { return; }
            if (!succeeds)
            {
                return;
            }
            var length = new byte[4];
            await ssl.ReadExactlyAsync(length, token);
            var startup = new byte[BinaryPrimitives.ReadInt32BigEndian(length) - 4];
            await ssl.ReadExactlyAsync(startup, token);
            byte[] expected = [0, 3, 0, 0, .. Encoding.UTF8.GetBytes("user\0tls\0database\0tls\0client_encoding\0UTF8\0application_name\0Mpgsql\0\0")];
            if (!startup.AsSpan().SequenceEqual(expected))
            {
                throw new InvalidDataException("TLS startup bytes.");
            }
            byte[] response = [82, 0, 0, 0, 8, 0, 0, 0, 0, 75, 0, 0, 0, 12, 0, 0, 0, 1, 0, 0, 0, 2, 90, 0, 0, 0, 5, 73];
            foreach (var value in response) await ssl.WriteAsync(new[] {value}, token);
            using (var cancelClient = await listener.AcceptTcpClientAsync(token))
            using (var cancelStream = cancelClient.GetStream())
            {
                await cancelStream.ReadExactlyAsync(request, token);
                if (!request.AsSpan().SequenceEqual(new byte[] {0, 0, 0, 8, 4, 210, 22, 47}))
                {
                    throw new InvalidDataException("Cancel SSLRequest bytes.");
                }
                await cancelStream.WriteAsync(new[] {(byte)'S'}, token);
                using var cancelTls = new SslStream(cancelStream, true);
                await cancelTls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {ServerCertificate = certificate}, token);
                var cancel = new byte[16];
                await cancelTls.ReadExactlyAsync(cancel, token);
                if (!cancel.AsSpan().SequenceEqual(new byte[] {0, 0, 0, 16, 4, 210, 22, 46, 0, 0, 0, 1, 0, 0, 0, 2}))
                {
                    throw new InvalidDataException("CancelRequest bytes.");
                }
            }
            var end = new byte[1];
            _ = await ssl.ReadAsync(end, token);
        }
    }
}