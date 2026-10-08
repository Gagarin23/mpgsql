using System.Buffers;
using System.Text;
using Mpgsql.Converters;
using Mpgsql.Protocol;
using Mpgsql.Types;

namespace Mpgsql.IntegrationTests;

internal static class PayloadValidationChecks
{
    internal static void Run(TestConnection connection)
    {
        var cases = new List<(TypeOid Oid, byte[] Payload)>();
        foreach (var oid in new[]
                 {
                     TypeOid.Text,
                     TypeOid.Json,
                     TypeOid.Jsonb,
                     TypeOid.Xml
                 })
        foreach (var bytes in new byte[][]
                 {
                     [0x80],
                     [65, 0, 66]
                 })
        {
            var payload = new byte[bytes.Length + (oid == TypeOid.Jsonb ? 1 : 0)];
            switch (oid)
            {
                case TypeOid.Text: TextConverter.WriteUtf8(bytes, payload); break;
                case TypeOid.Json: JsonConverter.WriteUtf8(bytes, payload); break;
                case TypeOid.Jsonb: JsonbConverter.WriteUtf8(bytes, payload); break;
                case TypeOid.Xml: XmlConverter.WriteUtf8(bytes, payload); break;
            }
            cases.Add((oid, payload));
        }
        PgTime[] times = [default, new PgTime(-1)];
        var timePayload = new byte[TimeArrayConverter.GetByteCount(times)];
        TimeArrayConverter.Write(times, timePayload);
        cases.Add((TypeOid.TimeArray, timePayload));
        var number = new PgNumeric
        (
            0, 0, PgNumericSign.Positive, new ushort[]
            {
                10000
            }
        );
        var numericPayload = new byte[NumericConverter.GetByteCount(number)];
        NumericConverter.Write(number, numericPayload);
        cases.Add((TypeOid.Numeric, numericPayload));
        foreach (var (oid, payload) in cases)
        {
            connection.Append
            (
                FrontendMessage.Parse
                (
                    "select $1", parameterTypes: new[]
                    {
                        (uint)oid
                    }
                )
            );
            connection.Append
            (
                FrontendMessage.Bind
                (
                    parameters: new ReadOnlyMemory<byte>?[]
                    {
                        payload
                    },
                    parameterFormats: new[]
                    {
                        FormatCode.Binary
                    }, resultFormats: new[]
                    {
                        FormatCode.Binary
                    }
                )
            );
            connection.Append(FrontendMessage.Describe(StatementOrPortal.Portal));
            connection.Append(FrontendMessage.Execute());
            connection.Append(FrontendMessage.Sync());
            connection.Flush();
            connection.Expect(BackendMessageKind.ParseComplete);
            var sqlState = ReadSqlState(connection.Expect(BackendMessageKind.ErrorResponse));
            if (!sqlState.StartsWith("22", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Unexpected {oid} payload rejection: {sqlState}.");
            }
            if (connection
                    .Expect(BackendMessageKind.ReadyForQuery)
                    .GetTransactionStatus() != TransactionStatus.Idle)
            {
                throw new InvalidDataException("Value rejection did not recover at Sync.");
            }
            var recovery = connection.Query("select 1");
            if (!recovery.Any(message => message.Kind == BackendMessageKind.DataRow))
            {
                throw new InvalidDataException("A valid query failed after payload rejection.");
            }
        }
        Console.WriteLine("PASS Server-side UTF-8/NUL, fixed array range and numeric digit rejection with Sync recovery");
    }

    private static string ReadSqlState(BackendMessage error)
    {
        // JSON/XML diagnostics can echo the deliberately malformed bytes from the value.
        // Inspect only the ASCII SQLSTATE field; framing and recovery do not require text decoding.
        var reader = new SequenceReader<byte>(error.Payload);
        while (reader.TryRead(out var code) && code != 0)
        {
            if (!reader.TryReadTo(out ReadOnlySequence<byte> value, 0))
            {
                throw new InvalidDataException("Unterminated diagnostic field.");
            }
            if (code == (byte)'C')
            {
                return Encoding.ASCII.GetString(value);
            }
        }
        throw new InvalidDataException("The server did not include SQLSTATE.");
    }
}