using Mpgsql.Converters;
using Mpgsql.Protocol;

namespace Mpgsql.IntegrationTests;

internal static class Int64Checks
{
    internal static void Run(TestConnection connection)
    {
        long?[] values = [long.MinValue, -1, 0, 1, long.MaxValue, 0x0102030405060708, -0x0102030405060708, null];
        connection.Append(FrontendMessage.Parse("select $1::bigint",
            "wire_int64",
            new[]
            {
                Int64Converter.TypeOid
            }));
        foreach (var value in values)
        {
            ReadOnlyMemory<byte>? parameter = null;
            if (value.HasValue)
            {
                var payload = new byte[Int64Converter.GetByteCount(value)];
                Int64Converter.Write(value,
                    payload);
                parameter = payload;
            }
            connection.Append(FrontendMessage.Bind(statement: "wire_int64",
                parameters: new[]
                {
                    parameter
                },
                parameterFormats: new[]
                {
                    FormatCode.Binary
                },
                resultFormats: new[]
                {
                    FormatCode.Binary
                }));
            connection.Append(FrontendMessage.Describe(StatementOrPortal.Portal));
            connection.Append(FrontendMessage.Execute());
            connection.Append(FrontendMessage.Sync());
        }
        connection.Flush();
        connection.Expect(BackendMessageKind.ParseComplete);
        foreach (var value in values)
        {
            connection.Expect(BackendMessageKind.BindComplete);
            var description = connection.Expect(BackendMessageKind.RowDescription).GetRowDescription();
            Check(description.Length == 1 && description.Span[0].DataTypeOid == Int64Converter.TypeOid && description.Span[0].Format == FormatCode.Binary,
                "bigint OID and binary format");
            connection.Expect(BackendMessageKind.DataRow,
                out var row);
            var field = row.Values.Span[0];
            Check(Int64Converter.ReadNullable(field) == value,
                "nullable binary round trip");
            if (value.HasValue)
            {
                Check(field.HasValue && field.Value.Length == 8 && Int64Converter.Read(field.Value) == value.Value,
                    "non-NULL binary round trip");
            }
            else
            {
                Check(!field.HasValue,
                    "outer SQL NULL field");
            }
            connection.Expect(BackendMessageKind.CommandComplete);
            Check(connection.Expect(BackendMessageKind.ReadyForQuery).GetTransactionStatus() == TransactionStatus.Idle,
                "ReadyForQuery boundary");
        }
        connection.Append(FrontendMessage.Close(StatementOrPortal.Statement,
            "wire_int64"));
        connection.Append(FrontendMessage.Sync());
        connection.Flush();
        connection.Expect(BackendMessageKind.CloseComplete);
        connection.Expect(BackendMessageKind.ReadyForQuery);
        Console.WriteLine("PASS scalar bigint/nullable bigint pipelined binary Bind/DataRow and Int64 extremes");
    }

    private static void Check(bool condition,
        string check)
    {
        if (!condition)
        {
            throw new InvalidDataException("Scalar bigint check failed: " + check);
        }
    }
}