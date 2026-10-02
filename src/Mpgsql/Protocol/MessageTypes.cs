namespace Mpgsql.Protocol;

public enum FormatCode : short
{
    Text = 0,
    Binary = 1
}

public enum StatementOrPortal : byte
{
    Statement = (byte)'S',
    Portal = (byte)'P'
}

public enum TransactionStatus : byte
{
    Idle = (byte)'I',
    InTransaction = (byte)'T',
    FailedTransaction = (byte)'E'
}

public enum AuthenticationMethod
{
    Ok = 0,
    KerberosV5 = 2,
    CleartextPassword = 3,
    Md5Password = 5,
    Gss = 7,
    GssContinue = 8,
    Sspi = 9,
    Sasl = 10,
    SaslContinue = 11,
    SaslFinal = 12
}

public enum FrontendMessageKind
{
    Undefined,
    Raw,
    Startup,
    SslRequest,
    GssEncRequest,
    CancelRequest,
    Query,
    Parse,
    Bind,
    Describe,
    Execute,
    Close,
    Flush,
    Sync,
    Terminate,
    Password,
    GssResponse,
    SaslInitialResponse,
    SaslResponse,
    CopyData,
    CopyDone,
    CopyFail,
    FunctionCall
}

public enum BackendMessageKind : byte
{
    Unknown = 0,
    Authentication = (byte)'R',
    BackendKeyData = (byte)'K',
    ParseComplete = (byte)'1',
    BindComplete = (byte)'2',
    CloseComplete = (byte)'3',
    CommandComplete = (byte)'C',
    CopyData = (byte)'d',
    CopyDone = (byte)'c',
    CopyInResponse = (byte)'G',
    CopyOutResponse = (byte)'H',
    CopyBothResponse = (byte)'W',
    DataRow = (byte)'D',
    EmptyQueryResponse = (byte)'I',
    ErrorResponse = (byte)'E',
    FunctionCallResponse = (byte)'V',
    NegotiateProtocolVersion = (byte)'v',
    NoData = (byte)'n',
    NoticeResponse = (byte)'N',
    NotificationResponse = (byte)'A',
    ParameterDescription = (byte)'t',
    ParameterStatus = (byte)'S',
    PortalSuspended = (byte)'s',
    ReadyForQuery = (byte)'Z',
    RowDescription = (byte)'T'
}

public enum EncryptionRequestKind
{
    Ssl,
    Gss
}