using Mpgsql.Protocol;

namespace Mpgsql;

/// <summary>Received PostgreSQL diagnostics, either at ReadyForQuery or terminal session failure.</summary>
public sealed class MpgsqlServerException : Exception
{
    public DiagnosticMessage Diagnostics { get; }
    public string? SqlState => Diagnostics.SqlState;
    /// <summary>Zero-based query index; null when no query can be attributed, including session failure.</summary>
    public int? QueryIndex { get; }
    /// <summary>Status confirmed by ReadyForQuery; null if the session failed before that boundary.</summary>
    public TransactionStatus? TransactionStatus { get; }

    internal MpgsqlServerException(DiagnosticMessage diagnostics,
        int? queryIndex,
        TransactionStatus? status,
        Exception? innerException = null)
        : base(diagnostics.Message ?? "PostgreSQL reported an error.", innerException)
        => (Diagnostics, QueryIndex, TransactionStatus) = (diagnostics, queryIndex, status);
}
