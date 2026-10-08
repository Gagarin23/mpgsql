namespace Mpgsql.Protocol;

/// <summary>Factories for typed, validated frontend protocol 3.0 messages.</summary>
/// <remarks>Memory arguments are borrowed. Keep metadata unchanged until writing completes.</remarks>
public static class FrontendMessage
{
    public const int ProtocolVersion = 3 << 16;

    /// <summary>The payload excludes the tag and length; body validation is left to the caller.</summary>
    public static RawFrontendMessage Raw(byte type,
        ReadOnlyMemory<byte> payload)
    {
        ArgumentOutOfRangeException.ThrowIfZero(type);
        return new RawFrontendMessage(type,
            payload,
            FrontendMessageKind.Raw);
    }

    /// <summary>The untagged payload starts with its protocol/request code.</summary>
    public static RawFrontendMessage RawStartup(ReadOnlyMemory<byte> payload)
    {
        return new RawFrontendMessage(null,
            payload,
            FrontendMessageKind.Raw);
    }
    public static StartupMessage Startup(string user,
        string? database = null)
    {
        return Startup(database is null
            ? new[]
            {
                new KeyValuePair<string, string>("user", user)
            }
            : new[]
            {
                new KeyValuePair<string, string>("user", user),
                new KeyValuePair<string, string>("database", database)
            });
    }
    public static StartupMessage Startup(ReadOnlyMemory<KeyValuePair<string, string>> parameters)
    {
        return new StartupMessage(parameters);
    }
    public static EncryptionRequestMessage SslRequest()
    {
        return new EncryptionRequestMessage(80877103,
            FrontendMessageKind.SslRequest);
    }
    public static EncryptionRequestMessage GssEncRequest()
    {
        return new EncryptionRequestMessage(80877104,
            FrontendMessageKind.GssEncRequest);
    }
    public static CancelRequestMessage CancelRequest(int processId,
        int secretKey)
    {
        return new CancelRequestMessage(processId,
            secretKey);
    }
    public static TextMessage Query(string query)
    {
        return new TextMessage((byte)'Q',
            query,
            FrontendMessageKind.Query);
    }
    public static ParseMessage Parse(string query,
        string statement = "",
        ReadOnlyMemory<uint> parameterTypes = default)
    {
        return new ParseMessage(query,
            statement,
            parameterTypes);
    }
    public static BindMessage Bind(
        string portal = "",
        string statement = "",
        ReadOnlyMemory<ReadOnlyMemory<byte>?> parameters = default,
        ReadOnlyMemory<FormatCode> parameterFormats = default,
        ReadOnlyMemory<FormatCode> resultFormats = default)
    {
        return new BindMessage(portal,
            statement,
            parameters,
            parameterFormats,
            resultFormats);
    }
    public static TargetMessage Describe(StatementOrPortal target,
        string name = "")
    {
        return new TargetMessage((byte)'D',
            target,
            name,
            FrontendMessageKind.Describe);
    }
    public static TargetMessage Close(StatementOrPortal target,
        string name = "")
    {
        return new TargetMessage((byte)'C',
            target,
            name,
            FrontendMessageKind.Close);
    }
    public static ExecuteMessage Execute(string portal = "",
        int maxRows = 0)
    {
        return new ExecuteMessage(portal,
            maxRows);
    }
    public static EmptyMessage Flush()
    {
        return new EmptyMessage((byte)'H',
            FrontendMessageKind.Flush);
    }
    public static EmptyMessage Sync()
    {
        return new EmptyMessage((byte)'S',
            FrontendMessageKind.Sync);
    }
    public static EmptyMessage Terminate()
    {
        return new EmptyMessage((byte)'X',
            FrontendMessageKind.Terminate);
    }
    public static TextMessage Password(string password)
    {
        return new TextMessage((byte)'p',
            password,
            FrontendMessageKind.Password);
    }
    public static RawFrontendMessage GssResponse(ReadOnlyMemory<byte> data)
    {
        return new RawFrontendMessage((byte)'p',
            data,
            FrontendMessageKind.GssResponse);
    }
    public static SaslInitialResponseMessage SaslInitialResponse(string mechanism,
        ReadOnlyMemory<byte>? initialResponse = null)
    {
        return new SaslInitialResponseMessage(mechanism,
            initialResponse);
    }
    public static RawFrontendMessage SaslResponse(ReadOnlyMemory<byte> data)
    {
        return new RawFrontendMessage((byte)'p',
            data,
            FrontendMessageKind.SaslResponse);
    }
    public static RawFrontendMessage CopyData(ReadOnlyMemory<byte> data)
    {
        return new RawFrontendMessage((byte)'d',
            data,
            FrontendMessageKind.CopyData);
    }
    public static EmptyMessage CopyDone()
    {
        return new EmptyMessage((byte)'c',
            FrontendMessageKind.CopyDone);
    }
    public static TextMessage CopyFail(string reason)
    {
        return new TextMessage((byte)'f',
            reason,
            FrontendMessageKind.CopyFail);
    }
    public static FunctionCallMessage FunctionCall(
        uint functionOid,
        ReadOnlyMemory<ReadOnlyMemory<byte>?> arguments = default,
        ReadOnlyMemory<FormatCode> argumentFormats = default,
        FormatCode resultFormat = FormatCode.Text)
    {
        return new FunctionCallMessage(functionOid,
            arguments,
            argumentFormats,
            resultFormat);
    }
}