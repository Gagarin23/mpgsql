using System.Buffers;
using Mpgsql.Converters;
using Mpgsql.Internal;
using Mpgsql.Protocol;
using Mpgsql.Tests.Protocol;

namespace Mpgsql.Tests.Queries;

public sealed class QueryPacketTests
{
    [Fact]
    public void CompleteBigintPacketIncludesTagsLengthsAndFormats()
    {
        MpgsqlParameterValue[] parameters = [MpgsqlParameterValue.Int64(42)];
        var destination = new byte[QueryPacket.GetByteCount("select $1",
            parameters)];
        Assert.Equal(destination.Length,
            QueryPacket.Write("select $1",
                parameters,
                destination));
        Assert.Equal(TestWire.Bytes("50 00000015 00 73656c65637420243100 0001 00000014 " + "42 0000001c 00 00 0001 0001 0001 00000008 000000000000002a 0001 0001 " + "44 00000006 50 00 45 00000009 00 00000000"),
            destination);
    }

    [Fact]
    public void ScalarsArraysNullAndEmptyMatchLowLevelComposition()
    {
        ReadOnlyMemory<long> array = new long[] {-1, 42};
        ReadOnlyMemory<long?> nullable = new long?[] {null, long.MinValue};
        MpgsqlParameterValue[] parameters =
        [
            MpgsqlParameterValue.Int64(null), MpgsqlParameterValue.Int64Array(array),
            MpgsqlParameterValue.NullableInt64Array(nullable), MpgsqlParameterValue.Int64Array(ReadOnlyMemory<long>.Empty),
            MpgsqlParameterValue.NullableInt64Array(null)
        ];
        var a = new byte[Int64ArrayConverter.GetByteCount(array)];
        Int64ArrayConverter.Write(array,
            a);
        var b = new byte[NullableInt64ArrayConverter.GetByteCount(nullable)];
        NullableInt64ArrayConverter.Write(nullable,
            b);
        var empty = new byte[12];
        Int64ArrayConverter.Write(ReadOnlyMemory<long>.Empty,
            empty);
        var reference = new ArrayBufferWriter<byte>();
        FrontendMessage.Parse("select $1, $2, $3, $4, $5",
            parameterTypes: new uint[]
            {
                20,
                1016,
                1016,
                1016,
                1016
            }).Write(reference);
        FrontendMessage.Bind(parameters: new ReadOnlyMemory<byte>?[]
            {
                null,
                a,
                b,
                empty,
                null
            },
            parameterFormats: new[]
            {
                FormatCode.Binary
            },
            resultFormats: new[]
            {
                FormatCode.Binary
            }).Write(reference);
        FrontendMessage.Describe(StatementOrPortal.Portal).Write(reference);
        FrontendMessage.Execute().Write(reference);
        var actual = new byte[QueryPacket.GetByteCount("select $1, $2, $3, $4, $5",
            parameters)];
        QueryPacket.Write("select $1, $2, $3, $4, $5",
            parameters,
            actual);
        Assert.Equal(reference.WrittenSpan.ToArray(),
            actual);
    }

    [Fact]
    public void InsufficientCapacityAndInvalidParametersDoNotChangeDestination()
    {
        byte[] target =
        [
            .. Enumerable.Repeat((byte)0xa5,
                64)
        ];
        byte[] before = [.. target];
        Assert.Throws<ArgumentException>(() => QueryPacket.Write("select $1",
            new[]
            {
                MpgsqlParameterValue.Int64(1)
            },
            target));
        Assert.Equal(before,
            target);
        Assert.Throws<InvalidOperationException>(() => QueryPacket.Write("select $1",
            new MpgsqlParameterValue[1],
            target));
        Assert.Equal(before,
            target);
    }

    [Fact]
    public void NoParametersAndUtf8HaveCompleteBytePayload()
    {
        const string sql = "select 'я'";
        var actual = new byte[QueryPacket.GetByteCount(sql,
            [])];
        QueryPacket.Write(sql,
            [],
            actual);
        Assert.Equal(TestWire.Bytes("50 00000013 00 73656c6563742027d18f2700 0000 " + "42 00000010 00 00 0001 0001 0000 0001 0001 " + "44 00000006 50 00 45 00000009 00 00000000"),
            actual);
    }
}