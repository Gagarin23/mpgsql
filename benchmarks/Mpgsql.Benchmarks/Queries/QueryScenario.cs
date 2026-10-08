using System.Text;

namespace Mpgsql.Benchmarks.Queries;

// These SQL strings identify fixed wire transcripts; the peer never executes SQL.
internal sealed record QueryScenario
(
    string Name,
    string Sql,
    int Rows,
    int Columns = 1,
    int ByteaBytes = 0,
    bool Null = false,
    bool NoData = false,
    bool Error = false,
    bool Returning = false
)
{

    internal static readonly QueryScenario Empty = new QueryScenario("Empty", "select $1::bigint where false", 0);
    internal static readonly QueryScenario One = new QueryScenario("OneBigint", "select $1::bigint", 1);

    internal static readonly QueryScenario Wide = new QueryScenario
    (
        "Rows128Columns8",
        "select $1::bigint+8*(i-1), $1::bigint+8*(i-1)+1, $1::bigint+8*(i-1)+2, " + "$1::bigint+8*(i-1)+3, $1::bigint+8*(i-1)+4, $1::bigint+8*(i-1)+5, " + "$1::bigint+8*(i-1)+6, $1::bigint+8*(i-1)+7 from generate_series(1,128) i",
        128, 8
    );

    internal static readonly QueryScenario Many = new QueryScenario("Rows4096", "select $1::bigint+i-1 from generate_series(1,4096) i", 4096);
    internal static readonly QueryScenario Blob64K = new QueryScenario("Bytea64KiB", "select $1::bigint, $2::bytea", 1, 2, 65536);
    internal static readonly QueryScenario NullValue = new QueryScenario("ScalarNull", "select NULL::bigint where $1::bigint>0", 1, Null: true);
    internal static readonly QueryScenario ScalarMany = new QueryScenario("ScalarRows128", "select $1::bigint+i-1 from generate_series(1,128) i", 128);
    internal static readonly QueryScenario NonQuery = new QueryScenario("NonQuery", "update benchmark_stub set value=$1::bigint where false", 0, NoData: true);
    internal static readonly QueryScenario ReturningRows = new QueryScenario("ReturningRows128", "update benchmark_stub set value=$1::bigint returning value", 128, Returning: true);
    internal static readonly QueryScenario Failing = new QueryScenario("Error", "select 1::bigint/($1::bigint-$1::bigint)", 0, Error: true);

    internal static readonly QueryScenario Slow = new QueryScenario("SlowRows128Bytea8KiB", "select $1::bigint+i-1, $2::bytea from generate_series(1,128) i", 128, 2, 8192);

    internal static readonly QueryScenario[] Readers = [Empty, One, Wide, Many, Blob64K];
    internal static readonly QueryScenario[] All = [.. Readers, NullValue, ScalarMany, NonQuery, ReturningRows, Failing, Slow];
    internal byte[] SqlUtf8 { get; } = Encoding.UTF8.GetBytes(Sql);
    internal byte[] Blob { get; } = CreateBlob(ByteaBytes);

    internal MpgsqlParameterValue[] Parameters(int worker)
    {
        return ByteaBytes == 0
            ? [MpgsqlParameterValue.Int64(worker + 1L)]
            : [MpgsqlParameterValue.Int64(worker + 1L), MpgsqlParameterValue.Bytea(Blob)];
    }

    internal long Expected(int worker)
    {
        if (Null || NoData || Error)
        {
            return 0;
        }
        long result = 0;
        for (var row = 0;
             row < Rows;
             row++)
        {
            var integers = ByteaBytes == 0 ? Columns : 1;
            for (var column = 0;
                 column < integers;
                 column++)
            {
                result += worker + 1L + (long)row * integers + column;
            }
            if (ByteaBytes != 0)
            {
                result += ByteaBytes + Blob[0] + Blob[^1];
            }
        }
        return result;
    }

    private static byte[] CreateBlob(int length)
    {
        var bytes = new byte[length];
        for (var i = 0;
             i < length;
             i++)
        {
            bytes[i] = unchecked((byte)(i * 31 + 7));
        }
        return bytes;
    }
    internal static QueryScenario Find(string name)
    {
        return All.Single(x => x.Name == name);
    }
}