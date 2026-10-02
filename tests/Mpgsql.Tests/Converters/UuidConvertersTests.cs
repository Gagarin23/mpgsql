using System.Buffers;
using System.Net;
using Mpgsql.Converters;
using Mpgsql.Types;

namespace Mpgsql.Tests.Converters;

public sealed class UuidConvertersTests
{
    [Fact]
    public void LiteralPayloadsArraysBuffersAndSegmentBoundaries()
    {
        Guid value = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        ConverterAssertions.CheckScalar(value, "00112233445566778899aabbccddeeff", UuidConverter.GetByteCount, UuidConverter.Write,
            UuidConverter.Write, UuidConverter.Read, UuidConverter.Read);
        ConverterAssertions.CheckNullableScalar<Guid>(UuidConverter.Write, UuidConverter.Write,
            UuidConverter.GetByteCount, UuidConverter.ReadNullable, UuidConverter.ReadNullable);
        ConverterAssertions.CheckArray(new Guid[] {value, value}, (uint)TypeOid.Uuid, "00112233445566778899aabbccddeeff",
            UuidArrayConverter.GetByteCount, UuidArrayConverter.Write, UuidArrayConverter.Write,
            UuidArrayConverter.Read, UuidArrayConverter.Read, UuidArrayConverter.Read, UuidArrayConverter.Read);
        ConverterAssertions.CheckNullableArray<Guid, UuidCodec>(value, "00112233445566778899aabbccddeeff");
    }
    [Fact]
    public void RejectsTruncatedAndTrailingPayloads()
    {
        byte[] bytes = Convert.FromHexString("00112233445566778899aabbccddeeff");
        Assert.Throws<InvalidDataException>(() => UuidConverter.Read(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.Throws<InvalidDataException>(() => UuidConverter.Read(new ReadOnlySequence<byte>(bytes.AsMemory(0, bytes.Length - 1))));
        byte[] trailing = [.. bytes, 0];
        Assert.Throws<InvalidDataException>(() => UuidConverter.Read(trailing));
        Assert.Throws<InvalidDataException>(() => UuidConverter.Read(new ReadOnlySequence<byte>(trailing)));
    }
}