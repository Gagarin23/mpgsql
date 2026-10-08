namespace Mpgsql.Internal;

internal readonly record struct QueryDefinition
(
    string Sql,
    ReadOnlyMemory<MpgsqlParameterValue> Parameters,
    int EncodedSize = 0,
    MpgsqlPreparedStatement? PreparedStatement = null
)
{
    internal int Measure()
    {
        return PreparedStatement is { } statement
            ? QueryPacket.GetPreparedByteCount(statement.Name, Parameters.Span)
            : QueryPacket.GetByteCount(Sql, Parameters.Span);
    }
}