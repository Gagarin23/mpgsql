using System.Buffers;
using System.Net;
using Mpgsql.Converters;
using Mpgsql.Types;

namespace Mpgsql.Tests.Converters;

public sealed class CidrConvertersTests
{
    [Fact]
    public void LiteralPayloadsArraysBuffersAndSegmentBoundaries()
    {
        var value = PgInet.FromIPAddress(IPAddress.Parse("192.0.2.0"), 24);
        ConverterAssertions.CheckScalar
        (
            value, "02180104c0000200", CidrConverter.GetByteCount, CidrConverter.Write,
            CidrConverter.Write, CidrConverter.Read, CidrConverter.Read
        );
        ConverterAssertions.CheckNullableScalar
        (
            CidrConverter.Write, CidrConverter.Write,
            CidrConverter.GetByteCount, CidrConverter.ReadNullable, CidrConverter.ReadNullable
        );
        ConverterAssertions.CheckArray
        (
            new[]
            {
                value,
                value
            }, (uint)TypeOid.Cidr, "02180104c0000200",
            CidrArrayConverter.GetByteCount, CidrArrayConverter.Write, CidrArrayConverter.Write,
            CidrArrayConverter.Read, CidrArrayConverter.Read, CidrArrayConverter.Read, CidrArrayConverter.Read
        );
        ConverterAssertions.CheckNullableArray<PgInet, CidrCodec>(value, "02180104c0000200");
    }
    [Fact]
    public void RejectsTruncatedAndTrailingPayloads()
    {
        var bytes = Convert.FromHexString("02180104c0000200");
        Assert.Throws<InvalidDataException>(() => CidrConverter.Read(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.Throws<InvalidDataException>(() => CidrConverter.Read(new ReadOnlySequence<byte>(bytes.AsMemory(0, bytes.Length - 1))));
        byte[] trailing = [.. bytes, 0];
        Assert.Throws<InvalidDataException>(() => CidrConverter.Read(trailing));
        Assert.Throws<InvalidDataException>(() => CidrConverter.Read(new ReadOnlySequence<byte>(trailing)));
    }
}