using System.Buffers;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Mpgsql.Protocol;

namespace Mpgsql.IntegrationTests;

// A synchronous test transport only; connection scheduling/authentication are not library APIs.
internal sealed class TestConnection : IDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly ArrayBufferWriter<byte> _output = new();
    private readonly ReadOnlySequence<byte>?[] _rowStorage = new ReadOnlySequence<byte>?[8];
    private readonly Dictionary<string, string> _parameters = new();
    public List<BackendMessage> AsynchronousMessages { get; } = [];
    public string ServerVersion => _parameters["server_version"];
    public string Authentication { get; private set; } = "trust";
    internal BackendKeyData BackendKey { get; private set; }
    internal Stream CopyStream => _stream;
    internal BackendMessage Receive() => Read(out _);

    private TestConnection(string host,
        int port)
    {
        _client = new TcpClient {NoDelay = true};
        try
        {
            _client.ConnectAsync(host,
                port).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            _stream = _client.GetStream();
            _stream.ReadTimeout = 10_000;
            _stream.WriteTimeout = 10_000;
        }
        catch
        {
            _client.Dispose();
            throw;
        }
    }

    public static TestConnection Open(string host,
        int port,
        string user,
        string password,
        string database)
    {
        var connection = new TestConnection(host,
            port);
        try
        {
            connection.Startup(user,
                password,
                database);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public void Append<T>(T message) where T : struct, IFrontendMessage<T> => FrontendMessageWriter.Write(in message,
        _output);

    public void Flush()
    {
        _stream.Write(_output.WrittenSpan);
        _output.Clear();
    }

    public void Send<T>(T message) where T : struct, IFrontendMessage<T>
    {
        Append(message);
        Flush();
    }

    public BackendMessage Expect(BackendMessageKind kind) => Expect(kind,
        out _);

    public BackendMessage ExpectCopyDataOrDone()
    {
        while (true)
        {
            var message = Read(out _);
            if (message.IsAsynchronous)
            {
                Record(message);
                continue;
            }
            if (message.Kind is BackendMessageKind.CopyData or BackendMessageKind.CopyDone)
            {
                return message;
            }
            if (message.Kind == BackendMessageKind.ErrorResponse)
            {
                throw new ServerFailure(message.GetDiagnostics());
            }
            throw new InvalidDataException($"Unexpected COPY message: {message.Kind}.");
        }
    }

    public BackendMessage Expect(BackendMessageKind kind,
        out IndexedDataRow row)
    {
        while (true)
        {
            var message = Read(out row);
            if (message.IsAsynchronous)
            {
                Record(message);
                continue;
            }
            if (message.Kind != kind)
            {
                if (message.Kind == BackendMessageKind.ErrorResponse)
                {
                    throw new ServerFailure(message.GetDiagnostics());
                }
                throw new InvalidDataException($"Expected {kind}, received {message.Kind}.");
            }
            return message;
        }
    }

    public List<BackendMessage> Query(string sql)
    {
        Send(FrontendMessage.Query(sql));
        var messages = new List<BackendMessage>();
        while (true)
        {
            var message = Read(out _);
            if (message.IsAsynchronous)
            {
                Record(message);
            }
            else if (message.Kind == BackendMessageKind.ErrorResponse)
            {
                throw new ServerFailure(message.GetDiagnostics());
            }
            else
            {
                messages.Add(message);
            }
            if (message.Kind == BackendMessageKind.ReadyForQuery)
            {
                return messages;
            }
        }
    }

    private BackendMessage Read(out IndexedDataRow row)
    {
        Span<byte> header = stackalloc byte[5];
        _stream.ReadExactly(header);
        int length = BinaryPrimitives.ReadInt32BigEndian(header[1..]);
        if (length < 4 || length > BackendMessageReader.DefaultMaxMessageLength)
        {
            throw new InvalidDataException("Invalid live server packet length.");
        }
        byte[] packet = new byte[length + 1];
        header.CopyTo(packet);
        _stream.ReadExactly(packet.AsSpan(5));
        var input = new ReadOnlySequence<byte>(packet);
        if (!BackendMessageReader.TryRead(ref input,
                _rowStorage,
                out var message,
                out row) || !input.IsEmpty)
        {
            throw new InvalidDataException("The codec did not consume the complete live server packet.");
        }
        return message;
    }

    private void Record(BackendMessage message)
    {
        AsynchronousMessages.Add(message);
        if (message.Kind == BackendMessageKind.ParameterStatus)
        {
            var parameter = message.GetParameterStatus();
            _parameters[parameter.Name] = parameter.Value;
        }
    }

    private void Startup(string user,
        string password,
        string database)
    {
        Send(FrontendMessage.Startup(user,
            database));
        TestScram? scram = null;
        bool authenticated = false;
        while (true)
        {
            var message = Read(out _);
            if (message.IsAsynchronous)
            {
                Record(message);
                continue;
            }
            if (message.Kind == BackendMessageKind.ErrorResponse)
            {
                throw new ServerFailure(message.GetDiagnostics());
            }
            if (message.Kind == BackendMessageKind.Authentication)
            {
                var request = message.GetAuthentication();
                switch (request.Method)
                {
                    case AuthenticationMethod.Ok:
                        if (scram is not null && !scram.Completed)
                        {
                            throw new InvalidDataException("SCRAM completed without a verified server signature.");
                        }
                        authenticated = true;
                        break;
                    case AuthenticationMethod.CleartextPassword:
                        Authentication = "cleartext";
                        Send(FrontendMessage.Password(password));
                        break;
                    case AuthenticationMethod.Md5Password:
                        Authentication = "MD5";
                        byte[] inner = Encoding.ASCII.GetBytes(Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(password + user))));
                        byte[] salted = [.. inner, .. request.Data.ToArray()];
                        Send(FrontendMessage.Password("md5" + Convert.ToHexStringLower(MD5.HashData(salted))));
                        break;
                    case AuthenticationMethod.Sasl:
                        if (!request.Mechanisms.Span.Contains("SCRAM-SHA-256"))
                        {
                            throw new NotSupportedException("The live-test helper requires SCRAM-SHA-256.");
                        }
                        Authentication = "SCRAM-SHA-256";
                        scram = new TestScram(user);
                        Send(FrontendMessage.SaslInitialResponse("SCRAM-SHA-256",
                            scram.First()));
                        break;
                    case AuthenticationMethod.SaslContinue:
                        Send(FrontendMessage.SaslResponse((scram ?? throw new InvalidDataException()).Continue(
                            Encoding.UTF8.GetString(request.Data.ToArray()),
                            password)));
                        break;
                    case AuthenticationMethod.SaslFinal:
                        (scram ?? throw new InvalidDataException()).Verify(Encoding.UTF8.GetString(request.Data.ToArray()));
                        break;
                    default:
                        throw new NotSupportedException($"Unsupported live-test authentication: {request.Method}.");
                }
            }
            else if (message.Kind == BackendMessageKind.BackendKeyData)
            {
                BackendKey = message.GetBackendKeyData();
                if (!authenticated || BackendKey.ProcessId <= 0)
                {
                    throw new InvalidDataException("Invalid startup BackendKeyData.");
                }
            }
            else if (message.Kind == BackendMessageKind.ReadyForQuery)
            {
                if (!authenticated || message.GetTransactionStatus() != TransactionStatus.Idle)
                {
                    throw new InvalidDataException("Invalid startup ReadyForQuery boundary.");
                }
                return;
            }
            else
            {
                throw new InvalidDataException($"Unexpected startup message: {message.Kind}.");
            }
        }
    }

    public void Dispose() => _client.Dispose();
}
