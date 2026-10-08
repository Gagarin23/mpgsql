using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Mpgsql.Internal;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class StartupAuthenticationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static byte[] Auth(int method, byte[]? data = null)
    {
        var bytes = new byte[4 + (data?.Length ?? 0)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, method);
        data?.CopyTo(bytes, 4);
        return Packet('R', bytes);
    }
    private static byte[] Key()
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(bytes, 123);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4), 456);
        return Packet('K', bytes);
    }
    private static byte[] Startup()
    {
        var body = Encoding.UTF8.GetBytes("user\0test\0database\0test\0client_encoding\0UTF8\0application_name\0Mpgsql\0\0");
        var bytes = new byte[body.Length + 8];
        BinaryPrimitives.WriteInt32BigEndian(bytes, bytes.Length);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4), 196608);
        body.CopyTo(bytes, 8);
        return bytes;
    }
    [Theory, InlineData(1), InlineData(1024)]
    public async Task StartupRetainsKeyParametersAndUnreadFrames(int fragment)
    {
        var tail = Packet('N', "SNOTICE\0Mafter startup\0\0"u8.ToArray());
        using var stream = new Transcript(Join(Auth(0), Key(), Packet('S', "client_encoding\0UTF8\0"u8.ToArray()), Ready(), tail), fragment);
        using var transport = new SocketTransport(new TcpClient(), stream, new MpgsqlSessionOptions {Username = "test", SslMode = MpgsqlSslMode.Disable});
        await transport.StartupAsync(Token);
        Assert.Equal(Startup(), stream.Written.ToArray());
        Assert.Equal(123, transport.BackendKey!.Value.ProcessId);
        Assert.Equal("UTF8", transport.Parameters["client_encoding"]);
        var remaining = new byte[tail.Length];
        await stream.ReadExactlyAsync(remaining, Token);
        Assert.Equal(tail, remaining);
    }
    [Theory, InlineData(false), InlineData(true)]
    public async Task PasswordAuthenticationWritesCompletePayload(bool md5)
    {
        byte[] salt = [1, 2, 3, 4];
        var password = "pаss😀";
        using var stream = new Transcript(Join(Auth(md5 ? 5 : 3, md5 ? salt : null), Auth(0), Key(), Ready()), 1);
        using var transport = new SocketTransport(new TcpClient(), stream, new MpgsqlSessionOptions {Username = "test", Password = password, SslMode = MpgsqlSslMode.Disable});
        await transport.StartupAsync(Token);
        var response = password;
        if (md5)
        {
            var digest = Encoding.ASCII.GetBytes(Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(password + "test"))));
            response = "md5" + Convert.ToHexStringLower(MD5.HashData([.. digest, .. salt]));
        }
        Assert.Equal(Join(Startup(), Packet('p', Encoding.UTF8.GetBytes(response + '\0'))), stream.Written.ToArray());
    }
    [Theory, InlineData("I\u00ADX", "IX"), InlineData("\u00AA", "a"), InlineData("\u2168", "IX"), InlineData("x\u00A0y", "x y"), InlineData("x\u200By", "x y"), InlineData("\u00AD", "\u00AD"), InlineData("\u0340", "\u0340"),
     InlineData("\u1D2C", "\u1D2C"), InlineData("a\u0007", "a\u0007"), InlineData("\u0627x\u0627", "\u0627x\u0627"), InlineData("Я😀", "Я😀")]
    public void UnicodePasswordUsesPostgresqlSaslprepFallback(string password, string normalized)
    {
        Assert.Equal(normalized, SaslPrep.Normalize(password));
    }
    [Theory, InlineData("r=other,s=AAAA,i=4096"), InlineData("r=nonceServer,s=AAAA,i=0"), InlineData("r=nonceServer,s=AAAA,i=4096,m=required"), InlineData("r=nonceServer,r=nonceServer,s=AAAA,i=4096")]
    public void ScramRejectsInvalidChallenge(string challenge)
    {
        Assert.Throws<InvalidDataException>(() => new ScramAuthentication("nonce").Continue(challenge, "password"));
    }
    [Fact]
    public void ScramRejectsIncorrectServerSignature()
    {
        var scram = new ScramAuthentication("nonce");
        scram.Continue("r=nonceServer,s=AAAA,i=4096", "password");
        Assert.Throws<InvalidDataException>(() => scram.Verify("v=" + Convert.ToBase64String(new byte[32])));
        Assert.False(scram.Completed);
    }
    [Fact]
    public void ScramMatchesIndependentSha256ReferenceAndRejectsRepeatedChallenge()
    {
        var scram = new ScramAuthentication("nonce");
        Assert.Equal("n,,n=,r=nonce"u8.ToArray(), scram.First());
        Assert.Equal("c=biws,r=nonceServer,p=vA58x2pSRSMXykJYvUI5yVPRE6oqqEi0iub4OsBB8jI="u8.ToArray(),
            scram.Continue("r=nonceServer,s=AAAA,i=4096", "x\u200By"));
        scram.Verify("v=nHBKVuAzTDh+0UnVR5PDxsBTqlzg1McqxNNpypmHuJ0=");
        Assert.True(scram.Completed);
        Assert.Throws<InvalidDataException>(() => scram.Continue("r=nonceServer,s=AAAA,i=4096", "password"));
    }

    private sealed class Transcript(byte[] input, int fragment) : Stream
    {
        internal readonly MemoryStream Written = new MemoryStream();
        private readonly MemoryStream _read = new MemoryStream(input, false);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset,
            int count)
        {
            return _read.Read(buffer, offset, Math.Min(count, fragment));
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<int>(_read.Read(buffer.Span[..Math.Min(buffer.Length, fragment)]));
        }
        public override void Write(byte[] buffer, int offset,
            int count)
        {
            Written.Write(buffer, offset, count);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Written.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }
        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }
    }
}