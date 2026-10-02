namespace Mpgsql.Protocol;

/// <summary>An opaque body, also used for COPY, GSS and SASL continuation bytes.</summary>
public readonly struct RawFrontendMessage : IFrontendMessage<RawFrontendMessage>
{
    public int GetByteCount() => FrontendMessageWriter.GetByteCount(in this);
    public int Write(Span<byte> destination) => FrontendMessageWriter.Write(in this,
        destination);
    public void Write(System.Buffers.IBufferWriter<byte> destination) => FrontendMessageWriter.Write(in this,
        destination);

    private readonly ReadOnlyMemory<byte> _payload;
    private readonly int _byteCount;
    public byte? Type { get; }
    public FrontendMessageKind Kind { get; }
    internal RawFrontendMessage(byte? type,
        ReadOnlyMemory<byte> payload,
        FrontendMessageKind kind)
    {
        Type = type;
        Kind = kind;
        _payload = payload;
        _byteCount = FrontendSize.Packet(payload.Length,
            type.HasValue);
    }
    static byte? IFrontendMessage<RawFrontendMessage>.GetMessageType(in RawFrontendMessage message) => message.Type;
    static int IFrontendMessage<RawFrontendMessage>.GetByteCount(in RawFrontendMessage message) => FrontendSize.Initialized(message._byteCount);
    static void IFrontendMessage<RawFrontendMessage>.WritePayload(in RawFrontendMessage message,
        Span<byte> destination) => message._payload.Span.CopyTo(destination);
}

/// <summary>A Query, Password or CopyFail string with a prepared UTF-8 byte count.</summary>
public readonly struct TextMessage : IFrontendMessage<TextMessage>
{
    public int GetByteCount() => FrontendMessageWriter.GetByteCount(in this);
    public int Write(Span<byte> destination) => FrontendMessageWriter.Write(in this,
        destination);
    public void Write(System.Buffers.IBufferWriter<byte> destination) => FrontendMessageWriter.Write(in this,
        destination);

    private readonly string _text;
    private readonly int _byteCount;
    private readonly byte _type;
    public byte? Type => _type;
    public FrontendMessageKind Kind { get; }
    internal TextMessage(byte type,
        string text,
        FrontendMessageKind kind)
    {
        _byteCount = FrontendSize.Packet(WireEncoding.CStringLength(text));
        _type = type;
        _text = text;
        Kind = kind;
    }
    static byte? IFrontendMessage<TextMessage>.GetMessageType(in TextMessage message) => message.Type;
    static int IFrontendMessage<TextMessage>.GetByteCount(in TextMessage message) => FrontendSize.Initialized(message._byteCount);
    static void IFrontendMessage<TextMessage>.WritePayload(in TextMessage message,
        Span<byte> destination)
    {
        var writer = new WireWriter(destination);
        writer.CString(message._text);
    }
}

/// <summary>A Flush, Sync, Terminate or CopyDone packet with no body.</summary>
public readonly struct EmptyMessage : IFrontendMessage<EmptyMessage>
{
    public int GetByteCount() => FrontendMessageWriter.GetByteCount(in this);
    public int Write(Span<byte> destination) => FrontendMessageWriter.Write(in this,
        destination);
    public void Write(System.Buffers.IBufferWriter<byte> destination) => FrontendMessageWriter.Write(in this,
        destination);

    private readonly byte _type;
    public byte? Type => _type;
    public FrontendMessageKind Kind { get; }
    internal EmptyMessage(byte type,
        FrontendMessageKind kind)
    {
        _type = type;
        Kind = kind;
    }
    static byte? IFrontendMessage<EmptyMessage>.GetMessageType(in EmptyMessage message) => message.Type;
    static int IFrontendMessage<EmptyMessage>.GetByteCount(in EmptyMessage message) => FrontendSize.Initialized(message._type == 0 ? 0 : 5);
    static void IFrontendMessage<EmptyMessage>.WritePayload(in EmptyMessage message,
        Span<byte> destination) { }
}

/// <summary>A Describe or Close command; the target selects statement or portal.</summary>
public readonly struct TargetMessage : IFrontendMessage<TargetMessage>
{
    public int GetByteCount() => FrontendMessageWriter.GetByteCount(in this);
    public int Write(Span<byte> destination) => FrontendMessageWriter.Write(in this,
        destination);
    public void Write(System.Buffers.IBufferWriter<byte> destination) => FrontendMessageWriter.Write(in this,
        destination);

    private readonly byte _type;
    private readonly StatementOrPortal _target;
    private readonly string _name;
    private readonly int _byteCount;
    public byte? Type => _type;
    public FrontendMessageKind Kind { get; }
    internal TargetMessage(byte type,
        StatementOrPortal target,
        string name,
        FrontendMessageKind kind)
    {
        if (target is not (StatementOrPortal.Statement or StatementOrPortal.Portal))
        {
            throw new ArgumentOutOfRangeException(nameof(target));
        }
        _byteCount = FrontendSize.Packet(checked(1 + WireEncoding.CStringLength(name)));
        _type = type;
        _target = target;
        _name = name;
        Kind = kind;
    }
    static byte? IFrontendMessage<TargetMessage>.GetMessageType(in TargetMessage message) => message.Type;
    static int IFrontendMessage<TargetMessage>.GetByteCount(in TargetMessage message) => FrontendSize.Initialized(message._byteCount);
    static void IFrontendMessage<TargetMessage>.WritePayload(in TargetMessage message,
        Span<byte> destination)
    {
        var writer = new WireWriter(destination);
        writer.Byte((byte)message._target);
        writer.CString(message._name);
    }
}

public readonly struct ExecuteMessage : IFrontendMessage<ExecuteMessage>
{
    public int GetByteCount() => FrontendMessageWriter.GetByteCount(in this);
    public int Write(Span<byte> destination) => FrontendMessageWriter.Write(in this,
        destination);
    public void Write(System.Buffers.IBufferWriter<byte> destination) => FrontendMessageWriter.Write(in this,
        destination);

    private readonly string _portal;
    private readonly int _maxRows;
    private readonly int _byteCount;
    public byte? Type => (byte)'E';
    public FrontendMessageKind Kind => FrontendMessageKind.Execute;
    internal ExecuteMessage(string portal,
        int maxRows)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxRows);
        _byteCount = FrontendSize.Packet(checked(WireEncoding.CStringLength(portal) + 4));
        _portal = portal;
        _maxRows = maxRows;
    }
    static byte? IFrontendMessage<ExecuteMessage>.GetMessageType(in ExecuteMessage message) => (byte)'E';
    static int IFrontendMessage<ExecuteMessage>.GetByteCount(in ExecuteMessage message) => FrontendSize.Initialized(message._byteCount);
    static void IFrontendMessage<ExecuteMessage>.WritePayload(in ExecuteMessage message,
        Span<byte> destination)
    {
        var writer = new WireWriter(destination);
        writer.CString(message._portal);
        writer.Int32(message._maxRows);
    }
}