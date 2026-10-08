using System.Buffers;
using System.Net;
using Mpgsql.Converters;
using Mpgsql.Types;

namespace Mpgsql.Tests.Converters;

public sealed class InetConvertersTests
{
    [Fact]
    public void LiteralPayloadsArraysBuffersAndSegmentBoundaries()
    {
        var value = PgInet.FromIPAddress(IPAddress.Parse("192.0.2.129"), 24);
        ConverterAssertions.CheckScalar
        (
            value, "02180004c0000281", InetConverter.GetByteCount, InetConverter.Write,
            InetConverter.Write, InetConverter.Read, InetConverter.Read
        );
        ConverterAssertions.CheckNullableScalar
        (
            InetConverter.Write, InetConverter.Write,
            InetConverter.GetByteCount, InetConverter.ReadNullable, InetConverter.ReadNullable
        );
        ConverterAssertions.CheckArray
        (
            new[]
            {
                value,
                value
            }, (uint)TypeOid.Inet, "02180004c0000281",
            InetArrayConverter.GetByteCount, InetArrayConverter.Write, InetArrayConverter.Write,
            InetArrayConverter.Read, InetArrayConverter.Read, InetArrayConverter.Read, InetArrayConverter.Read
        );
        ConverterAssertions.CheckNullableArray<PgInet, InetCodec>(value, "02180004c0000281");
    }
    [Fact]
    public void RejectsTruncatedAndTrailingPayloads()
    {
        var bytes = Convert.FromHexString("02180004c0000281");
        Assert.Throws<InvalidDataException>(() => InetConverter.Read(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.Throws<InvalidDataException>(() => InetConverter.Read(new ReadOnlySequence<byte>(bytes.AsMemory(0, bytes.Length - 1))));
        byte[] trailing = [.. bytes, 0];
        Assert.Throws<InvalidDataException>(() => InetConverter.Read(trailing));
        Assert.Throws<InvalidDataException>(() => InetConverter.Read(new ReadOnlySequence<byte>(trailing)));
    }
}