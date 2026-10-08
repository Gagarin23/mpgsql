using Mpgsql.Converters;
using Mpgsql.Protocol;

namespace Mpgsql.IntegrationTests;

internal static class LongArrayChecks
{
    internal static void Run(TestConnection connection)
    {
        var large = new long[4097];
        for (var i = 0;
             i < large.Length;
             i++)
        {
            large[i] = unchecked((long)(0x0123456789abcdefUL * (ulong)(i + 1)));
        }
        long[][] cases = [[], [0x0102030405060708], [long.MinValue, -1, 0, 1, long.MaxValue, 42, -42], large];
        connection.Append
        (
            FrontendMessage.Parse
            (
                "select $1::bigint[]",
                "wire_long_memory",
                new[]
                {
                    Int64ArrayConverter.ArrayTypeOid
                }
            )
        );
        foreach (var values in cases)
        {
            var payload = new byte[Int64ArrayConverter.GetByteCount(values)];
            Int64ArrayConverter.Write
            (
                values,
                payload
            );
            connection.Append
            (
                FrontendMessage.Bind
                (
                    statement: "wire_long_memory",
                    parameters: new ReadOnlyMemory<byte>?[]
                    {
                        payload
                    },
                    parameterFormats: new[]
                    {
                        FormatCode.Binary
                    },
                    resultFormats: new[]
                    {
                        FormatCode.Binary
                    }
                )
            );
            connection.Append(FrontendMessage.Describe(StatementOrPortal.Portal));
            connection.Append(FrontendMessage.Execute());
            connection.Append(FrontendMessage.Sync());
        }
        connection.Flush();
        connection.Expect(BackendMessageKind.ParseComplete);
        var reusable = new long[large.Length];
        foreach (var values in cases)
        {
            connection.Expect(BackendMessageKind.BindComplete);
            var fields = connection
                .Expect(BackendMessageKind.RowDescription)
                .GetRowDescription();
            Check
            (
                fields.Length == 1 && fields.Span[0].DataTypeOid == Int64ArrayConverter.ArrayTypeOid && fields.Span[0].Format == FormatCode.Binary,
                "bigint[] RowDescription"
            );
            connection.Expect
            (
                BackendMessageKind.DataRow,
                out var row
            );
            Check
            (
                row.Count == 1 && row.Values.Span[0].HasValue,
                "bigint[] non-NULL field"
            );
            var payload = row.Values.Span[0]!.Value;
            Check
            (
                Int64ArrayConverter
                    .Read(payload)
                    .Span.SequenceEqual(values),
                "bigint[] owned round trip"
            );
            var count = Int64ArrayConverter.Read
            (
                payload,
                reusable
            );
            Check
            (
                count == values.Length
                && reusable
                    .AsSpan
                    (
                        0,
                        count
                    )
                    .SequenceEqual(values),
                "bigint[] reused round trip"
            );
            if (values.Length == 0)
            {
                Check
                (
                    payload.Length == 12,
                    "PostgreSQL empty array canonical header"
                );
            }
            Complete(connection);
        }
        connection.Append
        (
            FrontendMessage.Close
            (
                StatementOrPortal.Statement,
                "wire_long_memory"
            )
        );
        connection.Append(FrontendMessage.Sync());
        connection.Flush();
        connection.Expect(BackendMessageKind.CloseComplete);
        Ready(connection);

        Begin
        (
            connection,
            "select '[-2:1]={-9223372036854775808,72623859790382856,-1,9223372036854775807}'::bigint[], null::bigint[], '{}'::bigint[]"
        );
        connection.Expect
        (
            BackendMessageKind.DataRow,
            out var serverRow
        );
        Check
        (
            serverRow.Count == 3,
            "server array field count"
        );
        var serverValues = serverRow.Values.Span;
        Check
        (
            serverValues[0].HasValue
            && Int64ArrayConverter
                .Read(serverValues[0]!.Value)
                .Span
                .SequenceEqual
                (
                    [
                        long.MinValue,
                        0x0102030405060708,
                        -1,
                        long.MaxValue
                    ]
                ),
            "server lower bound and extremes"
        );
        Check
        (
            serverValues[1] is null,
            "SQL NULL array remains an outer NULL field"
        );
        Check
        (
            serverValues[2].HasValue
            && Int64ArrayConverter.Read(serverValues[2]!.Value)
                .IsEmpty,
            "empty array remains a non-NULL field"
        );
        Complete(connection);

        connection.Query
        (
            "CREATE TEMP TABLE mpgsql_long_bitmap (value bigint[]); " +
            "INSERT INTO mpgsql_long_bitmap VALUES (array[null::bigint]); UPDATE mpgsql_long_bitmap SET value[1] = 42"
        );
        Begin
        (
            connection,
            "select value from mpgsql_long_bitmap"
        );
        connection.Expect
        (
            BackendMessageKind.DataRow,
            out var bitmapRow
        );
        Check
        (
            Int64ArrayConverter
                .Read(bitmapRow.Values.Span[0]!.Value)
                .Span.SequenceEqual([42]),
            "retained NULL bitmap without actual NULL elements"
        );
        Complete(connection);

        foreach (var sql in new[]
                 {
                     "select array[1::bigint, null::bigint]",
                     "select array[[1::bigint, 2], [3, 4]]"
                 })
        {
            Begin
            (
                connection,
                sql
            );
            connection.Expect
            (
                BackendMessageKind.DataRow,
                out var unsupported
            );
            var rejected = false;
            try { _ = Int64ArrayConverter.Read(unsupported.Values.Span[0]!.Value); }
            catch (NotSupportedException) { rejected = true; }
            Check
            (
                rejected,
                "unrepresentable server array is explicitly rejected"
            );
            Complete(connection);
        }
        Console.WriteLine("PASS binary bigint[] memory conversion, pipelined round trips, empty/NULL, bounds and unsupported shapes");
    }

    private static void Begin(
        TestConnection connection,
        string sql
    )
    {
        connection.Append(FrontendMessage.Parse(sql));
        connection.Append
        (
            FrontendMessage.Bind
            (
                resultFormats: new[]
                {
                    FormatCode.Binary
                }
            )
        );
        connection.Append(FrontendMessage.Execute());
        connection.Append(FrontendMessage.Sync());
        connection.Flush();
        connection.Expect(BackendMessageKind.ParseComplete);
        connection.Expect(BackendMessageKind.BindComplete);
    }

    private static void Complete(TestConnection connection)
    {
        connection.Expect(BackendMessageKind.CommandComplete);
        Ready(connection);
    }

    private static void Ready(TestConnection connection)
    {
        Check
        (
            connection
                .Expect(BackendMessageKind.ReadyForQuery)
                .GetTransactionStatus() == TransactionStatus.Idle,
            "bigint[] ReadyForQuery idle boundary"
        );
    }

    private static void Check(
        bool condition,
        string description
    )
    {
        if (!condition)
        {
            throw new InvalidDataException($"Live array check failed: {description}.");
        }
    }
}