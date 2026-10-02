using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Mpgsql.Protocol;

namespace Mpgsql.IntegrationTests;

internal static class LiveProtocolChecks
{
    public static void Run(TestConnection connection)
    {
        Check(connection.AsynchronousMessages.Any(m => m.Kind == BackendMessageKind.ParameterStatus),
            "Startup ParameterStatus");
        SimpleQuery(connection);
        ExtendedQuery(connection);
        Int64Checks.Run(connection);
        LongArrayChecks.Run(connection);
        NullableLongArrayChecks.Run(connection);
        BinaryCopyChecks.Run(connection);
        Pipeline(connection);
        ErrorRecovery(connection);
        PortalSuspension(connection);
        DescribeAndClose(connection);
        Copy(connection);
        Asynchronous(connection);
    }

    private static void SimpleQuery(TestConnection connection)
    {
        var messages = connection.Query("select 42, null::text, ''::text");
        Check(messages.Select(m => m.Kind)
                .SequenceEqual([
                    BackendMessageKind.RowDescription,
                    BackendMessageKind.DataRow,
                    BackendMessageKind.CommandComplete,
                    BackendMessageKind.ReadyForQuery
                ]),
            "Simple Query response sequence");
        var row = messages[1].GetDataRow();
        var values = new List<string?>();
        foreach (var value in row)
            values.Add(value.HasValue ? Encoding.UTF8.GetString(value.Value.ToArray()) : null);
        Check(values.SequenceEqual([
                "42",
                null,
                ""
            ]),
            "Simple Query NULL/empty values");
        Console.WriteLine("PASS Simple Query and text DataRow");
    }

    private static void ExtendedQuery(TestConnection connection)
    {
        connection.Append(FrontendMessage.Parse("select $1::int4, null::text, ''::text",
            "wire_stmt",
            new uint[]
            {
                23
            }));
        AppendBind(connection,
            42,
            "wire_portal");
        connection.Append(FrontendMessage.Describe(StatementOrPortal.Portal,
            "wire_portal"));
        connection.Append(FrontendMessage.Execute("wire_portal"));
        connection.Append(FrontendMessage.Sync());
        connection.Flush();
        connection.Expect(BackendMessageKind.ParseComplete);
        connection.Expect(BackendMessageKind.BindComplete);
        var description = connection.Expect(BackendMessageKind.RowDescription).GetRowDescription();
        Check(description.Length == 3 && description.Span[0].DataTypeOid == 23 && description.Span[0].Format == FormatCode.Binary,
            "Portal RowDescription");
        ExpectValue(connection,
            42);
        connection.Expect(BackendMessageKind.CommandComplete);
        ExpectReady(connection);
        Console.WriteLine("PASS Parse/Bind/Describe/Execute/Sync and indexed binary DataRow");
    }

    private static void Pipeline(TestConnection connection)
    {
        foreach (int value in new[] {43, 44})
        {
            AppendBind(connection,
                value);
            connection.Append(FrontendMessage.Execute());
            connection.Append(FrontendMessage.Sync());
        }
        connection.Flush();
        foreach (int value in new[] {43, 44})
        {
            connection.Expect(BackendMessageKind.BindComplete);
            ExpectValue(connection,
                value);
            connection.Expect(BackendMessageKind.CommandComplete);
            ExpectReady(connection);
        }
        Console.WriteLine("PASS two Extended Query cycles sent before reading responses");
    }

    private static void ErrorRecovery(TestConnection connection)
    {
        connection.Append(FrontendMessage.Parse("select (",
            "wire_broken"));
        connection.Append(FrontendMessage.Bind(statement: "wire_broken"));
        connection.Append(FrontendMessage.Execute());
        connection.Append(FrontendMessage.Sync());
        AppendBind(connection,
            45);
        connection.Append(FrontendMessage.Execute());
        connection.Append(FrontendMessage.Sync());
        connection.Flush();
        Check(connection.Expect(BackendMessageKind.ErrorResponse)
                  .GetDiagnostics()
                  .SqlState
              == "42601",
            "Parse syntax error");
        ExpectReady(connection); // Failed Parse/Bind/Execute confirmations must be skipped until this boundary.
        connection.Expect(BackendMessageKind.BindComplete);
        ExpectValue(connection,
            45);
        connection.Expect(BackendMessageKind.CommandComplete);
        ExpectReady(connection);
        Console.WriteLine("PASS ErrorResponse skips commands until Sync and next pipelined cycle succeeds");
    }

    private static void PortalSuspension(TestConnection connection)
    {
        connection.Append(FrontendMessage.Parse("select generate_series(1, 3)",
            "wire_series"));
        connection.Append(FrontendMessage.Bind("wire_series_portal",
            "wire_series",
            resultFormats: new[]
            {
                FormatCode.Binary
            }));
        connection.Append(FrontendMessage.Execute("wire_series_portal",
            1));
        connection.Append(FrontendMessage.Flush());
        connection.Flush();
        connection.Expect(BackendMessageKind.ParseComplete);
        connection.Expect(BackendMessageKind.BindComplete);
        ExpectSingleInt(connection,
            1);
        connection.Expect(BackendMessageKind.PortalSuspended);
        connection.Append(FrontendMessage.Execute("wire_series_portal"));
        connection.Append(FrontendMessage.Sync());
        connection.Flush();
        ExpectSingleInt(connection,
            2);
        ExpectSingleInt(connection,
            3);
        connection.Expect(BackendMessageKind.CommandComplete);
        ExpectReady(connection);
        Console.WriteLine("PASS Flush, PortalSuspended and resumed Execute");
    }

    private static void DescribeAndClose(TestConnection connection)
    {
        connection.Append(FrontendMessage.Describe(StatementOrPortal.Statement,
            "wire_stmt"));
        connection.Append(FrontendMessage.Close(StatementOrPortal.Statement,
            "wire_stmt"));
        connection.Append(FrontendMessage.Close(StatementOrPortal.Statement,
            "wire_series"));
        connection.Append(FrontendMessage.Sync());
        connection.Flush();
        var types = connection.Expect(BackendMessageKind.ParameterDescription).GetParameterDescription();
        Check(types.Length == 1 && types.Span[0] == 23,
            "Statement ParameterDescription");
        Check(connection.Expect(BackendMessageKind.RowDescription)
                  .GetRowDescription()
                  .Length
              == 3,
            "Statement RowDescription");
        connection.Expect(BackendMessageKind.CloseComplete);
        connection.Expect(BackendMessageKind.CloseComplete);
        ExpectReady(connection);
        Console.WriteLine("PASS statement Describe and Close");
    }

    private static void Copy(TestConnection connection)
    {
        connection.Send(FrontendMessage.Query("COPY (SELECT generate_series(1, 3)) TO STDOUT"));
        Check(connection.Expect(BackendMessageKind.CopyOutResponse)
                  .GetCopyResponse()
                  .Format
              == FormatCode.Text,
            "COPY OUT format");
        using var bytes = new MemoryStream();
        // The server may split COPY data at any row boundary.
        while (true)
        {
            var messages = ReadCopyData(connection);
            if (messages is null)
            {
                break;
            }
            bytes.Write(messages);
        }
        Check(Encoding.UTF8.GetString(bytes.ToArray()) == "1\n2\n3\n",
            "COPY OUT bytes");
        Check(connection.Expect(BackendMessageKind.CommandComplete)
                  .GetCommandTag()
              == "COPY 3",
            "COPY OUT completion");
        ExpectReady(connection);

        connection.Query("CREATE TEMP TABLE mpgsql_wire_copy (value int)");
        connection.Send(FrontendMessage.Query("COPY mpgsql_wire_copy FROM STDIN"));
        connection.Expect(BackendMessageKind.CopyInResponse);
        connection.Append(FrontendMessage.CopyData("4\n5\n"u8.ToArray()));
        connection.Append(FrontendMessage.CopyDone());
        connection.Flush();
        Check(connection.Expect(BackendMessageKind.CommandComplete)
                  .GetCommandTag()
              == "COPY 2",
            "COPY IN completion");
        ExpectReady(connection);
        var query = connection.Query("select count(*), sum(value) from mpgsql_wire_copy");
        var values = new List<string?>();
        foreach (var value in query.Single(m => m.Kind == BackendMessageKind.DataRow).GetDataRow())
            values.Add(value.HasValue ? Encoding.UTF8.GetString(value.Value.ToArray()) : null);
        Check(values.SequenceEqual([
                "2",
                "9"
            ]),
            "COPY IN stored values");

        connection.Send(FrontendMessage.Query("COPY mpgsql_wire_copy FROM STDIN"));
        connection.Expect(BackendMessageKind.CopyInResponse);
        connection.Send(FrontendMessage.CopyFail("intentional protocol test"));
        Check(connection.Expect(BackendMessageKind.ErrorResponse)
                  .GetDiagnostics()
                  .SqlState
              == "57014",
            "COPY failure");
        ExpectReady(connection);
        Console.WriteLine("PASS COPY OUT, COPY IN and CopyFail using a connection-local temporary table");
    }

    private static byte[]? ReadCopyData(TestConnection connection)
    {
        // COPY output is a distinct subprotocol: CopyDone ends its data phase.
        var message = connection.ExpectCopyDataOrDone();
        return message.Kind == BackendMessageKind.CopyDone ? null : message.GetCopyData().ToArray();
    }

    private static void Asynchronous(TestConnection connection)
    {
        connection.Query("DO $$ BEGIN RAISE NOTICE 'mpgsql notice'; END $$; LISTEN mpgsql_wire_test; NOTIFY mpgsql_wire_test, 'ok'");
        connection.Query("select 1"); // Also drain notifications delivered after the preceding ReadyForQuery.
        Check(connection.AsynchronousMessages.Any(m => m.Kind == BackendMessageKind.NoticeResponse
                                                       && m.GetDiagnostics()
                                                           .Message
                                                       == "mpgsql notice"),
            "NoticeResponse");
        Check(connection.AsynchronousMessages.Any(m => m.Kind == BackendMessageKind.NotificationResponse
                                                       && m.GetNotification()
                                                           .Channel
                                                       == "mpgsql_wire_test"
                                                       && m.GetNotification()
                                                           .Payload
                                                       == "ok"),
            "NotificationResponse");
        Console.WriteLine("PASS interleaved NoticeResponse/NotificationResponse without losing query responses");
    }

    private static void AppendBind(TestConnection connection,
        int value,
        string portal = "")
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes,
            value);
        connection.Append(FrontendMessage.Bind(portal,
            "wire_stmt",
            new ReadOnlyMemory<byte>?[]
            {
                bytes
            },
            new[]
            {
                FormatCode.Binary
            },
            new[]
            {
                FormatCode.Binary
            }));
    }

    private static void ExpectValue(TestConnection connection,
        int expected)
    {
        connection.Expect(BackendMessageKind.DataRow,
            out var row);
        var values = row.Values.Span;
        Check(row.Count == 3
              && values[0].HasValue
              && values[0]!.Value.Length == 4
              && BinaryPrimitives.ReadInt32BigEndian(values[0]!.Value.ToArray()) == expected
              && values[1] is null
              && values[2].HasValue
              && values[2]!.Value.IsEmpty,
            "Indexed binary/null/empty DataRow");
    }

    private static void ExpectSingleInt(TestConnection connection,
        int expected)
    {
        connection.Expect(BackendMessageKind.DataRow,
            out var row);
        Check(row.Count == 1 && row.Values.Span[0].HasValue && BinaryPrimitives.ReadInt32BigEndian(row.Values.Span[0]!.Value.ToArray()) == expected,
            "Suspended portal row");
    }

    private static void ExpectReady(TestConnection connection) => Check(
        connection.Expect(BackendMessageKind.ReadyForQuery).GetTransactionStatus() == TransactionStatus.Idle,
        "ReadyForQuery idle boundary");

    private static void Check(bool condition,
        string description)
    {
        if (!condition)
        {
            throw new InvalidDataException($"Live protocol check failed: {description}.");
        }
    }
}