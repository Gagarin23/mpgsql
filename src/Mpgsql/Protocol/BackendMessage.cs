using System.Buffers;

namespace Mpgsql.Protocol;

/// <summary>A structurally validated backend frame with typed accessors and the original wire body.</summary>
/// <remarks>
/// Payload, DataRow values, COPY data, authentication data and function results borrow the input buffer.
/// Consume them before PipeReader.AdvanceTo releases that buffer. Metadata accessors allocate strings
/// and arrays on demand; call them once and retain their results when needed. Unknown tags are retained
/// as Kind=Unknown; deciding whether they are legal belongs to the connection protocol state machine.
/// UTF-8 is decoded by string accessors, without a separate content-validation pass over the frame.
/// </remarks>
public readonly struct BackendMessage
{
    private readonly int _rowCount;
    public byte Type { get; }
    public BackendMessageKind Kind { get; }
    public ReadOnlySequence<byte> Payload { get; }

    /// <summary>Messages routed independently of the current command's response.</summary>
    public bool IsAsynchronous => Kind is BackendMessageKind.NoticeResponse or
        BackendMessageKind.ParameterStatus or BackendMessageKind.NotificationResponse;

    internal BackendMessage(byte type,
        BackendMessageKind kind,
        ReadOnlySequence<byte> payload,
        int rowCount)
    {
        Type = type;
        Kind = kind;
        Payload = payload;
        _rowCount = rowCount;
    }

    public AuthenticationRequest GetAuthentication()
    {
        RequireKind(BackendMessageKind.Authentication);
        var reader = new WireReader(Payload);
        var method = (AuthenticationMethod)reader.Int32();
        if (method != AuthenticationMethod.Sasl)
        {
            return new(method,
                reader.Rest(),
                default);
        }

        var mechanisms = new List<string>();
        ReadOnlySequence<byte> name;
        while (!(name = reader.CStringBytes()).IsEmpty)
            mechanisms.Add(WireEncoding.Decode(name));
        return new(method,
            default,
            mechanisms.ToArray());
    }

    public BackendKeyData GetBackendKeyData()
    {
        RequireKind(BackendMessageKind.BackendKeyData);
        var reader = new WireReader(Payload);
        return new(reader.Int32(),
            reader.Int32());
    }

    public string GetCommandTag()
    {
        RequireKind(BackendMessageKind.CommandComplete);
        var reader = new WireReader(Payload);
        return reader.CString();
    }

    public TransactionStatus GetTransactionStatus()
    {
        RequireKind(BackendMessageKind.ReadyForQuery);
        var reader = new WireReader(Payload);
        return (TransactionStatus)reader.Byte();
    }

    public ParameterStatus GetParameterStatus()
    {
        RequireKind(BackendMessageKind.ParameterStatus);
        var reader = new WireReader(Payload);
        return new(reader.CString(),
            reader.CString());
    }

    public NotificationResponse GetNotification()
    {
        RequireKind(BackendMessageKind.NotificationResponse);
        var reader = new WireReader(Payload);
        return new(reader.Int32(),
            reader.CString(),
            reader.CString());
    }

    public ReadOnlyMemory<uint> GetParameterDescription()
    {
        RequireKind(BackendMessageKind.ParameterDescription);
        var reader = new WireReader(Payload);
        var types = new uint[reader.Count()];
        for (int i = 0; i < types.Length; i++)
            types[i] = reader.UInt32();
        return types;
    }

    public ReadOnlyMemory<RowField> GetRowDescription()
    {
        RequireKind(BackendMessageKind.RowDescription);
        var reader = new WireReader(Payload);
        var fields = new RowField[reader.Count()];
        for (int i = 0; i < fields.Length; i++)
            fields[i] = new(
                reader.CString(),
                reader.UInt32(),
                reader.Int16(),
                reader.UInt32(),
                reader.Int16(),
                reader.Int32(),
                reader.Format());
        return fields;
    }

    public DataRow GetDataRow()
    {
        RequireKind(BackendMessageKind.DataRow);
        return new(_rowCount,
            Payload.Slice(2));
    }

    public ReadOnlySequence<byte>? GetFunctionCallResult()
    {
        RequireKind(BackendMessageKind.FunctionCallResponse);
        var reader = new WireReader(Payload);
        return reader.Value();
    }

    public ReadOnlySequence<byte> GetCopyData()
    {
        RequireKind(BackendMessageKind.CopyData);
        return Payload;
    }

    public CopyResponse GetCopyResponse()
    {
        if (Kind is not (BackendMessageKind.CopyInResponse or BackendMessageKind.CopyOutResponse or BackendMessageKind.CopyBothResponse))
        {
            throw new InvalidOperationException("The message is not a COPY response.");
        }
        var reader = new WireReader(Payload);
        var format = (FormatCode)reader.Byte();
        var columns = new FormatCode[reader.Count()];
        for (int i = 0; i < columns.Length; i++)
            columns[i] = reader.Format();
        return new(format,
            columns);
    }

    public DiagnosticMessage GetDiagnostics()
    {
        if (Kind is not (BackendMessageKind.ErrorResponse or BackendMessageKind.NoticeResponse))
        {
            throw new InvalidOperationException("The message is not an error or notice.");
        }
        var reader = new WireReader(Payload);
        var fields = new List<DiagnosticField>();
        byte code;
        while ((code = reader.Byte()) != 0)
            fields.Add(new(code,
                reader.CString()));
        return new(fields.ToArray());
    }

    public ProtocolVersionNegotiation GetProtocolVersionNegotiation()
    {
        RequireKind(BackendMessageKind.NegotiateProtocolVersion);
        var reader = new WireReader(Payload);
        int minorVersion = reader.Int32();
        var options = new string[reader.Int32()];
        for (int i = 0; i < options.Length; i++)
            options[i] = reader.CString();
        return new(minorVersion,
            options);
    }

    private void RequireKind(BackendMessageKind kind)
    {
        if (Kind != kind)
        {
            throw new InvalidOperationException($"Expected {kind}, received {Kind}.");
        }
    }
}
