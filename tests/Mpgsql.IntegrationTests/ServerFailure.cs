using Mpgsql.Protocol;

namespace Mpgsql.IntegrationTests;

internal sealed class ServerFailure(DiagnosticMessage diagnostics) : Exception(
    $"PostgreSQL {diagnostics.SqlState}: {diagnostics.Message}")
{
    public string? SqlState { get; } = diagnostics.SqlState;
}