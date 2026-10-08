using System.Globalization;

namespace Mpgsql.Internal;

internal static class ResultConsumption
{
    internal static async ValueTask<MpgsqlScalarResult<T>> ScalarAsync<T>(MpgsqlResultReader reader)
    {
        await using (reader.ConfigureAwait(false))
        {
            MpgsqlScalarResult<T> result = default;
            bool selected = false;
            do
            {
                if (!selected && reader.IsRowSet)
                {
                    selected = true;
                    if (await reader.ReadAsync().ConfigureAwait(false))
                        result = reader.IsDBNull(0) ? new(true, default) : new(false, reader.GetFieldValue<T>(0));
                }
                while (await reader.ReadAsync().ConfigureAwait(false)) { }
            } while (await reader.NextResultAsync().ConfigureAwait(false));
            return result;
        }
    }

    internal static async ValueTask<long> NonQueryAsync(MpgsqlResultReader reader)
    {
        await using (reader.ConfigureAwait(false))
        {
            long total = 0;
            bool any = false;
            do
            {
                while (await reader.ReadAsync().ConfigureAwait(false)) { }
                if (AffectedRows(reader.CommandTag) is { } count)
                {
                    total = checked(total + count);
                    any = true;
                }
            } while (await reader.NextResultAsync().ConfigureAwait(false));
            return any ? total : -1;
        }
    }

    private static long? AffectedRows(string? tag)
    {
        if (tag is null || !(tag.StartsWith("INSERT ", StringComparison.Ordinal)
            || tag.StartsWith("UPDATE ", StringComparison.Ordinal) || tag.StartsWith("DELETE ", StringComparison.Ordinal)
            || tag.StartsWith("MERGE ", StringComparison.Ordinal))) return null;
        if (!long.TryParse(tag.AsSpan(tag.LastIndexOf(' ') + 1), NumberStyles.None, CultureInfo.InvariantCulture, out long rows))
            throw new InvalidDataException("Invalid affected-row count in CommandComplete.");
        return rows;
    }
}
