using Mpgsql.Protocol;

namespace Mpgsql.Internal;

internal readonly record struct ResultEvent
(
    int QueryIndex,
    ReadOnlyMemory<RowField> Columns,
    OwnedRow? Row = null,
    string? CommandTag = null,
    bool IsEnd = false,
    bool IsRowSet = false,
    bool IsBorrowedRow = false
);
