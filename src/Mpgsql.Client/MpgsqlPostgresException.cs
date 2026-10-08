using Mpgsql.Protocol;

namespace Mpgsql;

public sealed class MpgsqlPostgresException : MpgsqlException
{
    internal MpgsqlPostgresException(MpgsqlServerException error) : base(error.Message, error)
    {
        (Diagnostics, QueryIndex, TransactionStatus) = (error.Diagnostics, error.QueryIndex, error.TransactionStatus);
    }
    public DiagnosticMessage Diagnostics { get; }
    public override string? SqlState => Diagnostics.SqlState;
    public int? QueryIndex { get; }
    public TransactionStatus? TransactionStatus { get; }
}