using System.Text;

namespace Mpgsql.Benchmarks.Queries;

// These SQL strings identify fixed wire transcripts; the peer never executes SQL.
internal sealed record QueryScenario(string Name, string Sql, int Rows, int Columns = 1,
    int ByteaBytes = 0, bool Null = false, bool NoData = false, bool Error = false,
    bool Returning = false)
{
    internal byte[] SqlUtf8 { get; } = Encoding.UTF8.GetBytes(Sql);
    internal byte[] Blob { get; } = CreateBlob(ByteaBytes);

    internal MpgsqlParameter[] Parameters(int worker) => ByteaBytes == 0
        ? [MpgsqlParameter.Int64(worker + 1L)]
        : [MpgsqlParameter.Int64(worker + 1L), MpgsqlParameter.Bytea(Blob)];

    internal long Expected(int worker)
    {
        if (Null || NoData || Error) return 0;
        long result = 0;
        for (int row = 0; row < Rows; row++)
        {
            int integers = ByteaBytes == 0 ? Columns : 1;
            for (int column = 0; column < integers; column++)
                result += worker + 1L + (long)row * integers + column;
            if (ByteaBytes != 0) result += ByteaBytes + Blob[0] + Blob[^1];
        }
        return result;
    }

    private static byte[] CreateBlob(int length)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = unchecked((byte)(i * 31 + 7));
        return bytes;
    }

    internal static readonly QueryScenario Empty = new("Empty", "select $1::bigint where false", 0);
    internal static readonly QueryScenario One = new("OneBigint", "select $1::bigint", 1);
    internal static readonly QueryScenario Wide = new("Rows128Columns8",
        "select $1::bigint+8*(i-1), $1::bigint+8*(i-1)+1, $1::bigint+8*(i-1)+2, " +
        "$1::bigint+8*(i-1)+3, $1::bigint+8*(i-1)+4, $1::bigint+8*(i-1)+5, " +
        "$1::bigint+8*(i-1)+6, $1::bigint+8*(i-1)+7 from generate_series(1,128) i", 128, 8);
    internal static readonly QueryScenario Many = new("Rows4096", "select $1::bigint+i-1 from generate_series(1,4096) i", 4096);
    internal static readonly QueryScenario Blob64K = new("Bytea64KiB", "select $1::bigint, $2::bytea", 1, 2, 65536);
    internal static readonly QueryScenario NullValue = new("ScalarNull", "select NULL::bigint where $1::bigint>0", 1, Null: true);
    internal static readonly QueryScenario ScalarMany = new("ScalarRows128", "select $1::bigint+i-1 from generate_series(1,128) i", 128);
    internal static readonly QueryScenario NonQuery = new("NonQuery", "update benchmark_stub set value=$1::bigint where false", 0, NoData: true);
    internal static readonly QueryScenario ReturningRows = new("ReturningRows128", "update benchmark_stub set value=$1::bigint returning value", 128, Returning: true);
    internal static readonly QueryScenario Failing = new("Error", "select 1::bigint/($1::bigint-$1::bigint)", 0, Error: true);
    internal static readonly QueryScenario Slow = new("SlowRows128Bytea8KiB",
        "select $1::bigint+i-1, $2::bytea from generate_series(1,128) i", 128, 2, 8192);

    internal static readonly QueryScenario[] Readers = [Empty, One, Wide, Many, Blob64K];
    internal static readonly QueryScenario[] All = [.. Readers, NullValue, ScalarMany, NonQuery, ReturningRows, Failing, Slow];
    internal static QueryScenario Find(string name) => All.Single(x => x.Name == name);
}
