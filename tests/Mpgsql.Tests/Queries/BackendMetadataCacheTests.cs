using System.Buffers;
using System.Buffers.Binary;
using Mpgsql.Internal;
using Mpgsql.Protocol;
using Mpgsql.Tests.Protocol;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class BackendMetadataCacheTests
{
    private static BackendMessage Decode(byte[] bytes, bool segmented)
    {
        var sequence = segmented ? TestWire.ByteSegments(bytes) : new ReadOnlySequence<byte>(bytes);
        Assert.True(BackendMessageReader.TryRead(ref sequence, out var message));
        Assert.True(sequence.IsEmpty);
        return message;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CachedMetadataOwnsItsWireAndDecodedStorage(bool segmented)
    {
        var cache = new BackendMetadataCache();
        var bytes = Description(20, 25);
        var first = cache.RowDescription(Decode(bytes, segmented));
        var same = cache.RowDescription(Decode(Description(20, 25), !segmented));
        Assert.True(first.Equals(same));
        Array.Fill(bytes, (byte)0xa5); // simulates releasing/reusing the receive buffer
        Assert.True(first.Equals(cache.RowDescription(Decode(Description(20, 25), segmented))));
        var replaced = cache.RowDescription(Decode(Description(25), segmented));
        Assert.Equal(25u, replaced.Span[0].DataTypeOid);
        Assert.Equal(2, first.Length);
        Assert.Equal(20u, first.Span[0].DataTypeOid);
        Assert.Equal("cx", first.Span[0].Name);

        var tagBytes = Command("SELECT 7");
        var tag = cache.CommandTag(Decode(tagBytes, segmented));
        Array.Fill(tagBytes, (byte)0xa5);
        Assert.Same(tag, cache.CommandTag(Decode(Command("SELECT 7"), !segmented)));
        Assert.Equal("SELECT 128", cache.CommandTag(Decode(Command("SELECT 128"), segmented)));
        Assert.Equal("SELECT 7", tag);
    }

    [Theory]
    [InlineData(7)] // name
    [InlineData(10)] // table OID
    [InlineData(14)] // attribute number
    [InlineData(16)] // type OID
    [InlineData(20)] // type size
    [InlineData(22)] // type modifier
    [InlineData(27)] // binary/text format
    public void EveryDescriptionFieldParticipatesInMatching(int offset)
    {
        var cache = new BackendMetadataCache();
        var original = cache.RowDescription(Decode(Description(20), false));
        var changed = Description(20);
        changed[offset] ^= 1;
        var expected = Decode(changed, true).GetRowDescription();
        var actual = cache.RowDescription(Decode(changed, true));
        Assert.Equal(expected.ToArray(), actual.ToArray());
        Assert.False(original.Equals(actual));
        Assert.Equal(Decode(Description(20), false).GetRowDescription().ToArray(), original.ToArray());
    }

    [Fact]
    public void OversizedMetadataIsDecodedWithoutReplacingBoundedEntry()
    {
        var cache = new BackendMetadataCache();
        var small = cache.RowDescription(Decode(Description(20), false));
        var payload = new byte[2 + 5001 + 18];
        BinaryPrimitives.WriteUInt16BigEndian(payload, 1);
        payload.AsSpan(2, 5000).Fill((byte)'a');
        Description(20).AsSpan(10, 18).CopyTo(payload.AsSpan(5003));
        var large = Decode(Packet('T', payload), true);
        Assert.Equal(5000, cache.RowDescription(large).Span[0].Name.Length);
        Assert.False(cache.RowDescription(large).Equals(cache.RowDescription(large)));
        Assert.True(small.Equals(cache.RowDescription(Decode(Description(20), false))));
        string hugeTag = "SELECT " + new string('1', 2048);
        var command = Decode(Command(hugeTag), true);
        Assert.Equal(hugeTag, cache.CommandTag(command));
        Assert.NotSame(cache.CommandTag(command), cache.CommandTag(command));
    }
}
