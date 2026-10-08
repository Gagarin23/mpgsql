using System.Buffers;

namespace Mpgsql.Protocol;

public readonly struct StartupMessage : IFrontendMessage<StartupMessage>
{
    public int GetByteCount()
    {
        return FrontendMessageWriter.GetByteCount(in this);
    }
    public int Write(Span<byte> destination)
    {
        return FrontendMessageWriter.Write(in this,
            destination);
    }
    public void Write(IBufferWriter<byte> destination)
    {
        FrontendMessageWriter.Write(in this,
            destination);
    }

    private readonly ReadOnlyMemory<KeyValuePair<string, string>> _parameters;
    private readonly int _byteCount;
    private readonly bool _appendEncoding;
    public byte? Type => null;
    public FrontendMessageKind Kind => FrontendMessageKind.Startup;

    internal StartupMessage(ReadOnlyMemory<KeyValuePair<string, string>> parameters)
    {
        var hasUser = false;
        var hasEncoding = false;
        var size = 5; // Protocol version and the final NUL after the name/value pairs.
        var pairs = parameters.Span;
        for (var i = 0; i < pairs.Length; i++)
        {
            var pair = pairs[i];
            if (string.IsNullOrEmpty(pair.Key))
            {
                throw new ArgumentException("A startup parameter name cannot be empty.");
            }
            for (var j = 0; j < i; j++)
            {
                if (pairs[j].Key == pair.Key)
                {
                    throw new ArgumentException($"Duplicate startup parameter: {pair.Key}.");
                }
            }
            size = checked(size + WireEncoding.CStringLength(pair.Key) + WireEncoding.CStringLength(pair.Value));
            if (pair.Key == "user")
            {
                hasUser = !string.IsNullOrEmpty(pair.Value);
            }
            if (pair.Key == "client_encoding")
            {
                if (!string.Equals(pair.Value,
                        "UTF8",
                        StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(pair.Value,
                        "UTF-8",
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("The message codec requires client_encoding=UTF8.");
                }
                hasEncoding = true;
            }
        }
        if (!hasUser)
        {
            throw new ArgumentException("The startup message requires a nonempty user parameter.");
        }
        if (!hasEncoding)
        {
            size = checked(size + WireEncoding.CStringLength("client_encoding") + WireEncoding.CStringLength("UTF8"));
        }
        _byteCount = FrontendSize.Packet(size,
            false);
        _parameters = parameters;
        _appendEncoding = !hasEncoding;
    }

    static byte? IFrontendMessage<StartupMessage>.GetMessageType(in StartupMessage message)
    {
        return null;
    }
    static int IFrontendMessage<StartupMessage>.GetByteCount(in StartupMessage message)
    {
        return FrontendSize.Initialized(message._byteCount);
    }
    static void IFrontendMessage<StartupMessage>.WritePayload(in StartupMessage message,
        Span<byte> destination)
    {
        var writer = new WireWriter(destination);
        writer.Int32(FrontendMessage.ProtocolVersion);
        foreach (var pair in message._parameters.Span)
        {
            writer.CString(pair.Key);
            writer.CString(pair.Value);
        }
        if (message._appendEncoding)
        {
            writer.CString("client_encoding");
            writer.CString("UTF8");
        }
        writer.Byte(0);
    }
}

/// <summary>An untagged SSLRequest or GSSENCRequest.</summary>
public readonly struct EncryptionRequestMessage : IFrontendMessage<EncryptionRequestMessage>
{
    public int GetByteCount()
    {
        return FrontendMessageWriter.GetByteCount(in this);
    }
    public int Write(Span<byte> destination)
    {
        return FrontendMessageWriter.Write(in this,
            destination);
    }
    public void Write(IBufferWriter<byte> destination)
    {
        FrontendMessageWriter.Write(in this,
            destination);
    }

    private readonly int _code;
    public byte? Type => null;
    public FrontendMessageKind Kind { get; }
    internal EncryptionRequestMessage(int code,
        FrontendMessageKind kind)
    {
        _code = code;
        Kind = kind;
    }
    static byte? IFrontendMessage<EncryptionRequestMessage>.GetMessageType(in EncryptionRequestMessage message)
    {
        return null;
    }
    static int IFrontendMessage<EncryptionRequestMessage>.GetByteCount(in EncryptionRequestMessage message)
    {
        return FrontendSize.Initialized(message._code == 0 ? 0 : 8);
    }
    static void IFrontendMessage<EncryptionRequestMessage>.WritePayload(in EncryptionRequestMessage message,
        Span<byte> destination)
    {
        var writer = new WireWriter(destination);
        writer.Int32(message._code);
    }
}

public readonly struct CancelRequestMessage : IFrontendMessage<CancelRequestMessage>
{
    public int GetByteCount()
    {
        return FrontendMessageWriter.GetByteCount(in this);
    }
    public int Write(Span<byte> destination)
    {
        return FrontendMessageWriter.Write(in this,
            destination);
    }
    public void Write(IBufferWriter<byte> destination)
    {
        FrontendMessageWriter.Write(in this,
            destination);
    }

    private readonly int _processId;
    private readonly int _secretKey;
    private readonly bool _initialized;
    public byte? Type => null;
    public FrontendMessageKind Kind => FrontendMessageKind.CancelRequest;
    internal CancelRequestMessage(int processId,
        int secretKey)
    {
        _processId = processId;
        _secretKey = secretKey;
        _initialized = true;
    }
    static byte? IFrontendMessage<CancelRequestMessage>.GetMessageType(in CancelRequestMessage message)
    {
        return null;
    }
    static int IFrontendMessage<CancelRequestMessage>.GetByteCount(in CancelRequestMessage message)
    {
        return FrontendSize.Initialized(message._initialized ? 16 : 0);
    }
    static void IFrontendMessage<CancelRequestMessage>.WritePayload(in CancelRequestMessage message,
        Span<byte> destination)
    {
        var writer = new WireWriter(destination);
        writer.Int32(80877102);
        writer.Int32(message._processId);
        writer.Int32(message._secretKey);
    }
}

public readonly struct SaslInitialResponseMessage : IFrontendMessage<SaslInitialResponseMessage>
{
    public int GetByteCount()
    {
        return FrontendMessageWriter.GetByteCount(in this);
    }
    public int Write(Span<byte> destination)
    {
        return FrontendMessageWriter.Write(in this,
            destination);
    }
    public void Write(IBufferWriter<byte> destination)
    {
        FrontendMessageWriter.Write(in this,
            destination);
    }

    private readonly string _mechanism;
    private readonly ReadOnlyMemory<byte>? _response;
    private readonly int _byteCount;
    public byte? Type => (byte)'p';
    public FrontendMessageKind Kind => FrontendMessageKind.SaslInitialResponse;
    internal SaslInitialResponseMessage(string mechanism,
        ReadOnlyMemory<byte>? response)
    {
        if (string.IsNullOrEmpty(mechanism))
        {
            throw new ArgumentException("A SASL mechanism name is required.");
        }
        _byteCount = FrontendSize.Packet(checked(WireEncoding.CStringLength(mechanism) + 4 + response.GetValueOrDefault().Length));
        _mechanism = mechanism;
        _response = response;
    }
    static byte? IFrontendMessage<SaslInitialResponseMessage>.GetMessageType(in SaslInitialResponseMessage message)
    {
        return (byte)'p';
    }
    static int IFrontendMessage<SaslInitialResponseMessage>.GetByteCount(in SaslInitialResponseMessage message)
    {
        return FrontendSize.Initialized(message._byteCount);
    }
    static void IFrontendMessage<SaslInitialResponseMessage>.WritePayload(in SaslInitialResponseMessage message,
        Span<byte> destination)
    {
        var writer = new WireWriter(destination);
        writer.CString(message._mechanism);
        writer.Value(message._response);
    }
}