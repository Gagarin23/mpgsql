using System.Buffers;
using System.Net;
using Mpgsql.Converters;
using Mpgsql.Types;

namespace Mpgsql.Tests.Converters;

public sealed class OidConvertersTests
{
    [Fact]
    public void LiteralPayloadsArraysBuffersAndSegmentBoundaries()
    {
        uint value = 0xfedcba98u;
        ConverterAssertions.CheckScalar(value, "fedcba98", OidConverter.GetByteCount, OidConverter.Write,
            OidConverter.Write, OidConverter.Read, OidConverter.Read);
        ConverterAssertions.CheckNullableScalar<uint>(OidConverter.Write, OidConverter.Write,
            OidConverter.GetByteCount, OidConverter.ReadNullable, OidConverter.ReadNullable);
        ConverterAssertions.CheckArray(new uint[] {value, value}, (uint)TypeOid.Oid, "fedcba98",
            OidArrayConverter.GetByteCount, OidArrayConverter.Write, OidArrayConverter.Write,
            OidArrayConverter.Read, OidArrayConverter.Read, OidArrayConverter.Read, OidArrayConverter.Read);
        ConverterAssertions.CheckNullableArray<uint, OidCodec>(value, "fedcba98");
    }
    [Fact]
    public void RejectsTruncatedAndTrailingPayloads()
    {
        byte[] bytes = Convert.FromHexString("fedcba98");
        Assert.Throws<InvalidDataException>(() => OidConverter.Read(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.Throws<InvalidDataException>(() => OidConverter.Read(new ReadOnlySequence<byte>(bytes.AsMemory(0, bytes.Length - 1))));
        byte[] trailing = [.. bytes, 0];
        Assert.Throws<InvalidDataException>(() => OidConverter.Read(trailing));
        Assert.Throws<InvalidDataException>(() => OidConverter.Read(new ReadOnlySequence<byte>(trailing)));
    }
}