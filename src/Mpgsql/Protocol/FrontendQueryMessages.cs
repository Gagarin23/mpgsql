using System.Buffers;

namespace Mpgsql.Protocol;

public readonly struct ParseMessage : IFrontendMessage<ParseMessage>
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

    private readonly string _statement;
    private readonly string _query;
    private readonly ReadOnlyMemory<uint> _parameterTypes;
    private readonly int _byteCount;
    public byte? Type => (byte)'P';
    public FrontendMessageKind Kind => FrontendMessageKind.Parse;

    internal ParseMessage(string query,
        string statement,
        ReadOnlyMemory<uint> parameterTypes)
    {
        FrontendSize.Count(parameterTypes.Length);
        _byteCount = FrontendSize.Packet(checked(WireEncoding.CStringLength(statement) +
                                                 WireEncoding.CStringLength(query) + 2 + 4 * parameterTypes.Length));
        _statement = statement;
        _query = query;
        _parameterTypes = parameterTypes;
    }

    static byte? IFrontendMessage<ParseMessage>.GetMessageType(in ParseMessage message)
    {
        return (byte)'P';
    }
    static int IFrontendMessage<ParseMessage>.GetByteCount(in ParseMessage message)
    {
        return FrontendSize.Initialized(message._byteCount);
    }
    static void IFrontendMessage<ParseMessage>.WritePayload(in ParseMessage message,
        Span<byte> destination)
    {
        var writer = new WireWriter(destination);
        writer.CString(message._statement);
        writer.CString(message._query);
        writer.Count(message._parameterTypes.Length);
        foreach (var oid in message._parameterTypes.Span)
            writer.UInt32(oid);
    }
}

/// <summary>Parameter memory is nullable: null means SQL NULL; empty memory means an empty value.</summary>
public readonly struct BindMessage : IFrontendMessage<BindMessage>
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

    private readonly string _portal;
    private readonly string _statement;
    private readonly ReadOnlyMemory<ReadOnlyMemory<byte>?> _parameters;
    private readonly ReadOnlyMemory<FormatCode> _parameterFormats;
    private readonly ReadOnlyMemory<FormatCode> _resultFormats;
    private readonly int _byteCount;
    public byte? Type => (byte)'B';
    public FrontendMessageKind Kind => FrontendMessageKind.Bind;

    internal BindMessage(string portal,
        string statement,
        ReadOnlyMemory<ReadOnlyMemory<byte>?> parameters,
        ReadOnlyMemory<FormatCode> parameterFormats,
        ReadOnlyMemory<FormatCode> resultFormats)
    {
        FrontendSize.ParameterFormats(parameterFormats.Length,
            parameters.Length);
        _byteCount = FrontendSize.Packet(checked(WireEncoding.CStringLength(portal) +
                                                 WireEncoding.CStringLength(statement) + FrontendSize.Formats(parameterFormats.Span) +
                                                 FrontendSize.Values(parameters.Span) + FrontendSize.Formats(resultFormats.Span)));
        _portal = portal;
        _statement = statement;
        _parameters = parameters;
        _parameterFormats = parameterFormats;
        _resultFormats = resultFormats;
    }

    static byte? IFrontendMessage<BindMessage>.GetMessageType(in BindMessage message)
    {
        return (byte)'B';
    }
    static int IFrontendMessage<BindMessage>.GetByteCount(in BindMessage message)
    {
        return FrontendSize.Initialized(message._byteCount);
    }
    static void IFrontendMessage<BindMessage>.WritePayload(in BindMessage message,
        Span<byte> destination)
    {
        var writer = new WireWriter(destination);
        writer.CString(message._portal);
        writer.CString(message._statement);
        writer.Formats(message._parameterFormats.Span);
        writer.Values(message._parameters.Span);
        writer.Formats(message._resultFormats.Span);
    }
}

public readonly struct FunctionCallMessage : IFrontendMessage<FunctionCallMessage>
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

    private readonly uint _functionOid;
    private readonly ReadOnlyMemory<ReadOnlyMemory<byte>?> _arguments;
    private readonly ReadOnlyMemory<FormatCode> _argumentFormats;
    private readonly FormatCode _resultFormat;
    private readonly int _byteCount;
    public byte? Type => (byte)'F';
    public FrontendMessageKind Kind => FrontendMessageKind.FunctionCall;

    internal FunctionCallMessage(uint functionOid,
        ReadOnlyMemory<ReadOnlyMemory<byte>?> arguments,
        ReadOnlyMemory<FormatCode> argumentFormats,
        FormatCode resultFormat)
    {
        FrontendSize.ParameterFormats(argumentFormats.Length,
            arguments.Length);
        FrontendSize.Format(resultFormat);
        _byteCount = FrontendSize.Packet(checked(4 + FrontendSize.Formats(argumentFormats.Span) +
                                                 FrontendSize.Values(arguments.Span) + 2));
        _functionOid = functionOid;
        _arguments = arguments;
        _argumentFormats = argumentFormats;
        _resultFormat = resultFormat;
    }

    static byte? IFrontendMessage<FunctionCallMessage>.GetMessageType(in FunctionCallMessage message)
    {
        return (byte)'F';
    }
    static int IFrontendMessage<FunctionCallMessage>.GetByteCount(in FunctionCallMessage message)
    {
        return FrontendSize.Initialized(message._byteCount);
    }
    static void IFrontendMessage<FunctionCallMessage>.WritePayload(in FunctionCallMessage message,
        Span<byte> destination)
    {
        var writer = new WireWriter(destination);
        writer.UInt32(message._functionOid);
        writer.Formats(message._argumentFormats.Span);
        writer.Values(message._arguments.Span);
        writer.Int16((short)message._resultFormat);
    }
}