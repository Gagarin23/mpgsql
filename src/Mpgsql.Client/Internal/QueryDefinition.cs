namespace Mpgsql.Internal;

internal readonly record struct QueryDefinition(string Sql, ReadOnlyMemory<MpgsqlParameter> Parameters, int EncodedSize = 0);
