using System.Buffers;
using Mpgsql.Protocol;

namespace Mpgsql.IntegrationTests;

internal static partial class BuiltinConverterChecks
{

    private static void Value<T>(
        TestConnection connection, TypeOid oid,
        string sqlType, string literal,
        T value,
        Func<T, int> measure, ScalarWrite<T> write,
        ScalarRead<T> read,
        Func<ReadOnlyMemory<T?>, int> measureArray, ArrayWrite<T?> writeArray,
        ArrayRead<T?> readArray
    ) where T : struct
    {
        var scalar = new byte[measure(value)];
        write(value, scalar);
        var result = RoundTrip(connection, oid, literal, scalar)!.Value;
        var rewritten = new byte[scalar.Length];
        write(read(result.ToArray()), rewritten);
        Check
        (
            rewritten
                .AsSpan()
                .SequenceEqual(result.ToArray()), oid, "scalar decoder"
        );
        RoundTrip(connection, oid, "null::" + sqlType, null);

        T?[] elements = [value, null, value];
        var array = new byte[measureArray(elements)];
        writeArray(elements, array);
        var arrayOid = Enum.Parse<TypeOid>(oid + "Array");
        var arrayResult = RoundTrip(connection, arrayOid, "array[" + literal + ",null," + literal + "]", array)!.Value;
        var arrayRewritten = new byte[array.Length];
        writeArray(readArray(arrayResult.ToArray()), arrayRewritten);
        Check
        (
            arrayRewritten
                .AsSpan()
                .SequenceEqual(arrayResult.ToArray()), arrayOid, "array decoder"
        );
        var empty = new byte[measureArray(ReadOnlyMemory<T?>.Empty)];
        writeArray(ReadOnlyMemory<T?>.Empty, empty);
        RoundTrip(connection, arrayOid, "array[]::" + sqlType + "[]", empty);
        RoundTrip(connection, arrayOid, "null::" + sqlType + "[]", null);
        Console.WriteLine("PASS " + oid + "/" + arrayOid + " binary values, NULL elements, empty arrays and outer NULL");
    }

    private static void ReferenceValue(
        TestConnection connection, TypeOid oid,
        string sqlType, string literal,
        string value,
        Func<string?, int> measure, ScalarWrite<string?> write,
        ScalarRead<string> read,
        Func<ReadOnlyMemory<string?>, int> measureArray, ArrayWrite<string?> writeArray,
        ArrayRead<string?> readArray
    )
    {
        var scalar = new byte[measure(value)];
        write(value, scalar);
        var result = RoundTrip(connection, oid, literal, scalar)!.Value;
        Check(read(result.ToArray()) == value, oid, "string decoder");
        RoundTrip(connection, oid, "null::" + sqlType, null);
        string?[] elements = [value, null, value];
        var array = new byte[measureArray(elements)];
        writeArray(elements, array);
        var arrayOid = Enum.Parse<TypeOid>(oid + "Array");
        var arrayResult = RoundTrip(connection, arrayOid, "array[" + literal + ",null," + literal + "]", array)!.Value;
        Check
        (
            readArray(arrayResult.ToArray())
                .Span.SequenceEqual(elements), arrayOid, "string array decoder"
        );
        var empty = new byte[measureArray(ReadOnlyMemory<string?>.Empty)];
        writeArray(ReadOnlyMemory<string?>.Empty, empty);
        RoundTrip(connection, arrayOid, "array[]::" + sqlType + "[]", empty);
        RoundTrip(connection, arrayOid, "null::" + sqlType + "[]", null);
        Console.WriteLine("PASS " + oid + "/" + arrayOid + " binary UTF-8, NULL elements, empty arrays and outer NULL");
    }

    private static ReadOnlySequence<byte>? RoundTrip(
        TestConnection connection, TypeOid oid,
        string literal, ReadOnlyMemory<byte>? payload
    )
    {
        // The independent SQL literal verifies semantic encoding, not just a paired decoder.
        connection.Append
        (
            FrontendMessage.Parse
            (
                "select $1, " + literal, "", new[]
                {
                    (uint)oid
                }
            )
        );
        connection.Append
        (
            FrontendMessage.Bind
            (
                parameters: new[]
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
        connection.Expect(BackendMessageKind.BindComplete);
        var description = connection
            .Expect(BackendMessageKind.RowDescription)
            .GetRowDescription();
        Check
        (
            description.Length == 2 && description.Span[0].DataTypeOid == (uint)oid && description.Span[1].DataTypeOid == (uint)oid &&
            description.Span[0].Format == FormatCode.Binary && description.Span[1].Format == FormatCode.Binary, oid, "binary type metadata"
        );
        connection.Expect(BackendMessageKind.DataRow, out var row);
        var actual = row.Values.Span[0];
        var expected = row.Values.Span[1];
        Check(actual.HasValue == payload.HasValue && actual.HasValue == expected.HasValue, oid, "outer SQL NULL");
        if (actual.HasValue)
        {
            Check
            (
                actual
                    .Value.ToArray()
                    .AsSpan()
                    .SequenceEqual(expected!.Value.ToArray()), oid, "independent SQL literal"
            );
            // numeric_recv normalizes digits; Infinity's ignored dscale is 32 on send.
            // The independent SQL literal above still verifies the value for numeric.
            if (oid is not (TypeOid.Numeric or TypeOid.NumericArray) && !actual
                    .Value.ToArray()
                    .AsSpan()
                    .SequenceEqual(payload!.Value.Span))
            {
                throw new InvalidDataException($"{oid} binary bytes differ: server={Convert.ToHexString(actual.Value.ToArray())}, converter={Convert.ToHexString(payload.Value.Span)}.");
            }
        }
        connection.Expect(BackendMessageKind.CommandComplete);
        Check
        (
            connection
                .Expect(BackendMessageKind.ReadyForQuery)
                .GetTransactionStatus() == TransactionStatus.Idle, oid, "Sync boundary"
        );
        return actual;
    }

    private static void Check(
        bool condition, TypeOid oid,
        string message
    )
    {
        if (!condition)
        {
            throw new InvalidDataException($"Builtin converter check failed for {oid}: {message}.");
        }
    }

    private delegate int ScalarWrite<T>(T value, Span<byte> destination);

    private delegate T ScalarRead<T>(ReadOnlySpan<byte> payload);

    private delegate int ArrayWrite<T>(ReadOnlyMemory<T> value, Span<byte> destination);

    private delegate ReadOnlyMemory<T> ArrayRead<T>(ReadOnlySpan<byte> payload);
}