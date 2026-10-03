namespace Mpgsql.Internal;

internal enum MessageOperationKind : byte
{
    Sync,
    Query,
    Prepare,
    PreparedQuery,
    Close
}
