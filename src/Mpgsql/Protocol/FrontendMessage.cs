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
        return new(type,
            payload,
            FrontendMessageKind.Raw);
    }

    /// <summary>The untagged payload starts with its protocol/request code.</summary>
    public static RawFrontendMessage RawStartup(ReadOnlyMemory<byte> payload) => new(null,
        payload,
        FrontendMessageKind.Raw);
    public static StartupMessage Startup(string user,
        string? database = null) =>
        Startup(database is null
            ? new KeyValuePair<string, string>[]
            {
                new("user",
                    user)
            }
            : new KeyValuePair<string, string>[]
            {
                new("user",
                    user),
                new("database",
                    database)
            });
    public static StartupMessage Startup(ReadOnlyMemory<KeyValuePair<string, string>> parameters) => new(parameters);
    public static EncryptionRequestMessage SslRequest() => new(80877103,
        FrontendMessageKind.SslRequest);
    public static EncryptionRequestMessage GssEncRequest() => new(80877104,
        FrontendMessageKind.GssEncRequest);
    public static CancelRequestMessage CancelRequest(int processId,
        int secretKey) => new(processId,
        secretKey);
    public static TextMessage Query(string query) => new((byte)'Q',
        query,
        FrontendMessageKind.Query);
    public static ParseMessage Parse(string query,
        string statement = "",
        ReadOnlyMemory<uint> parameterTypes = default) => new(query,
        statement,
        parameterTypes);
    public static BindMessage Bind(
        string portal = "",
        string statement = "",
        ReadOnlyMemory<ReadOnlyMemory<byte>?> parameters = default,
        ReadOnlyMemory<FormatCode> parameterFormats = default,
        ReadOnlyMemory<FormatCode> resultFormats = default) =>
        new(portal,
            statement,
            parameters,
            parameterFormats,
            resultFormats);
    public static TargetMessage Describe(StatementOrPortal target,
        string name = "") => new((byte)'D',
        target,
        name,
        FrontendMessageKind.Describe);
    public static TargetMessage Close(StatementOrPortal target,
        string name = "") => new((byte)'C',
        target,
        name,
        FrontendMessageKind.Close);
    public static ExecuteMessage Execute(string portal = "",
        int maxRows = 0) => new(portal,
        maxRows);
    public static EmptyMessage Flush() => new((byte)'H',
        FrontendMessageKind.Flush);
    public static EmptyMessage Sync() => new((byte)'S',
        FrontendMessageKind.Sync);
    public static EmptyMessage Terminate() => new((byte)'X',
        FrontendMessageKind.Terminate);
    public static TextMessage Password(string password) => new((byte)'p',
        password,
        FrontendMessageKind.Password);
    public static RawFrontendMessage GssResponse(ReadOnlyMemory<byte> data) => new((byte)'p',
        data,
        FrontendMessageKind.GssResponse);
    public static SaslInitialResponseMessage SaslInitialResponse(string mechanism,
        ReadOnlyMemory<byte>? initialResponse = null) => new(mechanism,
        initialResponse);
    public static RawFrontendMessage SaslResponse(ReadOnlyMemory<byte> data) => new((byte)'p',
        data,
        FrontendMessageKind.SaslResponse);
    public static RawFrontendMessage CopyData(ReadOnlyMemory<byte> data) => new((byte)'d',
        data,
        FrontendMessageKind.CopyData);
    public static EmptyMessage CopyDone() => new((byte)'c',
        FrontendMessageKind.CopyDone);
    public static TextMessage CopyFail(string reason) => new((byte)'f',
        reason,
        FrontendMessageKind.CopyFail);
    public static FunctionCallMessage FunctionCall(
        uint functionOid,
        ReadOnlyMemory<ReadOnlyMemory<byte>?> arguments = default,
        ReadOnlyMemory<FormatCode> argumentFormats = default,
        FormatCode resultFormat = FormatCode.Text) =>
        new(functionOid,
            arguments,
            argumentFormats,
            resultFormat);
}