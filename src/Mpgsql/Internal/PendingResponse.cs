namespace Mpgsql.Internal;

// Only published operations enter the response FIFO. Administrative operations have no query index.
internal readonly record struct PendingResponse(
    MessageOperationKind Kind,
    MpgsqlPreparedStatement? Statement,
    int? QueryIndex);
