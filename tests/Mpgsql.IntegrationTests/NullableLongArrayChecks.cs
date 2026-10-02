using System.Buffers.Binary;
using Mpgsql.Converters;
using Mpgsql.Protocol;

namespace Mpgsql.IntegrationTests;

internal static class NullableLongArrayChecks
{
    internal static void Run(TestConnection connection)
    {
        long?[] large = new long?[4097];
        for (int i = 0; i < large.Length; i++)
            large[i] = i % 3 == 0 ? null : unchecked(long.MinValue + i);
        long?[][] cases = [[], [null], [long.MinValue, -1, 0, 1, long.MaxValue],
            [null, long.MinValue, null, 0x0102030405060708, long.MaxValue, null], large];
        connection.Append(FrontendMessage.Parse("select $1::bigint[]", "wire_nullable_long",
            new[] { NullableInt64ArrayConverter.ArrayTypeOid }));
        foreach (var values in cases)
        {
            byte[] payload = new byte[NullableInt64ArrayConverter.GetByteCount(values)];
            NullableInt64ArrayConverter.Write(values, payload);
            connection.Append(FrontendMessage.Bind(statement: "wire_nullable_long",
                parameters: new ReadOnlyMemory<byte>?[] { payload },
                parameterFormats: new[] { FormatCode.Binary }, resultFormats: new[] { FormatCode.Binary }));
            connection.Append(FrontendMessage.Execute());
            connection.Append(FrontendMessage.Sync());
        }
        connection.Flush();
        connection.Expect(BackendMessageKind.ParseComplete);
        var storage = new long?[large.Length];
        foreach (var values in cases)
        {
            connection.Expect(BackendMessageKind.BindComplete);
            connection.Expect(BackendMessageKind.DataRow, out var row);
            var payload = row.Values.Span[0]!.Value;
            Check(NullableInt64ArrayConverter.Read(payload).Span.SequenceEqual(values), "owned round trip");
            int count = NullableInt64ArrayConverter.Read(payload, storage);
            Check(count == values.Length && storage.AsSpan(0, count).SequenceEqual(values), "reused round trip");
            Complete(connection);
        }

        // Like array_recv, the nullable reader identifies NULLs by lengths, independently of flags.
        byte[] atypical = new byte[NullableInt64ArrayConverter.GetByteCount(new long?[] { null, 42 })];
        NullableInt64ArrayConverter.Write(new long?[] { null, 42 }, atypical);
        BinaryPrimitives.WriteInt32BigEndian(atypical.AsSpan(4), 0);
        connection.Append(FrontendMessage.Bind(statement: "wire_nullable_long",
            parameters: new ReadOnlyMemory<byte>?[] { atypical },
            parameterFormats: new[] { FormatCode.Binary }, resultFormats: new[] { FormatCode.Binary }));
        connection.Append(FrontendMessage.Execute()); connection.Append(FrontendMessage.Sync()); connection.Flush();
        connection.Expect(BackendMessageKind.BindComplete);
        connection.Expect(BackendMessageKind.DataRow, out var normalized);
        Check(NullableInt64ArrayConverter.Read(normalized.Values.Span[0]!.Value).Span.SequenceEqual(new long?[] { null, 42 }),
            "server accepts NULL length with flag zero");
        Complete(connection);
        connection.Append(FrontendMessage.Close(StatementOrPortal.Statement, "wire_nullable_long"));
        connection.Append(FrontendMessage.Sync()); connection.Flush();
        connection.Expect(BackendMessageKind.CloseComplete); connection.Expect(BackendMessageKind.ReadyForQuery);

        connection.Append(FrontendMessage.Parse("select '[-2:1]={NULL,-9223372036854775808,NULL,9223372036854775807}'::bigint[], null::bigint[], '{}'::bigint[]"));
        connection.Append(FrontendMessage.Bind(resultFormats: new[] { FormatCode.Binary }));
        connection.Append(FrontendMessage.Execute()); connection.Append(FrontendMessage.Sync()); connection.Flush();
        connection.Expect(BackendMessageKind.ParseComplete); connection.Expect(BackendMessageKind.BindComplete);
        connection.Expect(BackendMessageKind.DataRow, out var server);
        var fields = server.Values.Span;
        Check(NullableInt64ArrayConverter.Read(fields[0]!.Value).Span.SequenceEqual(new long?[] { null, long.MinValue, null, long.MaxValue }),
            "server-generated lower bounds and NULLs");
        Check(!fields[1].HasValue && NullableInt64ArrayConverter.Read(fields[2]!.Value).IsEmpty,
            "outer SQL NULL and empty array stay distinct");
        Complete(connection);
        Console.WriteLine("PASS nullable bigint[] pipelined Bind/DataRow, mixed/all NULL, reused storage, bounds and NULL flags");
    }

    private static void Complete(TestConnection connection)
    {
        connection.Expect(BackendMessageKind.CommandComplete);
        Check(connection.Expect(BackendMessageKind.ReadyForQuery).GetTransactionStatus() == TransactionStatus.Idle,
            "ReadyForQuery boundary");
    }

    private static void Check(bool condition, string check)
    {
        if (!condition) throw new InvalidDataException("Nullable bigint[] check failed: " + check);
    }
}
