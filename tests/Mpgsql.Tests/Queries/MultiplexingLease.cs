using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

internal static class MultiplexingLease
{
    internal static async ValueTask<MpgsqlResultReader> HoldAsync(ScriptedSession wire, MpgsqlMultiplexingDataSource source,
        CancellationToken token)
    {
        var opening = source.ExecuteReaderAsync("select held", cancellationToken: token);
        var tags = new List<char>();
        do { tags.AddRange(Tags(await wire.ReadOutputAsync())); } while (!tags.Contains('S'));
        await wire.WriteAsync(Join(Query(0), Ready()));
        return await opening;
    }
}