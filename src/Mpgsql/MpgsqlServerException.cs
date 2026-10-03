using Mpgsql.Protocol;

namespace Mpgsql;

/// <summary>A group SQL error, surfaced only after its Sync/ReadyForQuery recovery boundary.</summary>
public sealed class MpgsqlServerException : Exception
{
    public DiagnosticMessage Diagnostics { get; }
    public string? SqlState => Diagnostics.SqlState;
    /// <summary>Zero-based query index; null denotes an error at preparation, Close or Sync.</summary>
    public int? QueryIndex { get; }
    public TransactionStatus TransactionStatus { get; }

    internal MpgsqlServerException(DiagnosticMessage diagnostics,
        int? queryIndex,
        TransactionStatus status)
        : base(diagnostics.Message ?? "PostgreSQL rejected the query group.")
        => (Diagnostics, QueryIndex, TransactionStatus) = (diagnostics, queryIndex, status);
}
