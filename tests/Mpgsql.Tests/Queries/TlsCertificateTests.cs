using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Mpgsql.Internal;

namespace Mpgsql.Tests.Queries;

public sealed class TlsCertificateTests
{
    [Theory, InlineData(MpgsqlSslMode.Require, true), InlineData(MpgsqlSslMode.VerifyCA, true), InlineData(MpgsqlSslMode.VerifyFull, false)]
    public void ModesEnforceDifferentHostnamePolicies(MpgsqlSslMode mode, bool expected)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        Assert.Equal(expected, SocketTransport.ValidateCertificate(mode, certificate, SslPolicyErrors.RemoteCertificateNameMismatch, null));
    }
    [Theory, InlineData(true), InlineData(false)]
    public void CustomRootEnforcesServerAuthenticationPurpose(bool serverPurpose)
    {
        using var key = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=Mpgsql test root", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var leafKey = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection {new Oid(serverPurpose ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2")}, true));
        using var leaf = request.Create(root, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1), new byte[] {1});
        Assert.Equal(serverPurpose, SocketTransport.ValidateCertificate(MpgsqlSslMode.VerifyFull, leaf, SslPolicyErrors.RemoteCertificateChainErrors, root));
        Assert.False(SocketTransport.ValidateCertificate(MpgsqlSslMode.VerifyFull, leaf, SslPolicyErrors.RemoteCertificateChainErrors, null));
    }
}