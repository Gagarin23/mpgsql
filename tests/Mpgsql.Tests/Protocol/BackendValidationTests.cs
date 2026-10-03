using System.Buffers;
using Mpgsql.Protocol;

namespace Mpgsql.Tests.Protocol;

public sealed class BackendValidationTests
{
    public static TheoryData<string> InvalidPackets =>
    [
        "31 00000005 01", "32 00000005 01", "33 00000005 01",
        "63 00000005 01", "49 00000005 01", "6e 00000005 01", "73 00000005 01",
        "4b 00000008 00000001", // incomplete cancellation key
        "5a 00000004", "5a 00000006 4900", "5a 00000005 58",
        "43 00000005 78", "43 00000007 780001",
        "52 00000004", "52 00000009 00000000 01", "52 0000000b 00000005 010203",
        "52 0000000a 0000000a 4d00", // SASL mechanism list has no final terminator
        "52 00000009 0000000a 00", // SASL mechanism list is empty
        "52 0000000c 0000000a 4d00 0001", // trailing byte after SASL terminator
        "54 00000005 00", "54 00000006 0001", // incomplete RowDescription
        "54 00000019 0001 00 00000000 0000 00000017 0004 ffffffff 0002",
        "44 0000000a 0001 fffffffe", // only -1 is NULL
        "44 0000000a 0001 00000001", // missing value bytes
        "44 00000007 0000 ff", "44 00000006 ffff",
        "74 00000006 0001", "74 00000005 00",
        "53 00000006 7800", "41 00000008 00000000",
        "45 00000009 4d62616400", // no diagnostics terminator
        "45 00000008 4d626164", "4e 00000005 4d", "45 00000006 0001",
        "47 00000007 02 0000", // invalid overall COPY format
        "47 00000009 00 0001 0001", // binary column in text COPY
        "48 00000009 01 0001 0002", "47 00000007 00 0001",
        "76 0000000c ffffffff 00000000", "76 0000000c 00000000 ffffffff",
        "76 0000000c 00000000 7fffffff", "76 0000000d 00000000 00000001 78",
        "56 00000004", "56 00000008 fffffffe", "56 00000008 00000001", "56 00000009 00000000 ff"
    ];

    [Theory]
    [InlineData("43 00000007 c32800"), InlineData("43 00000006 c300")]
    [InlineData("52 0000000b 0000000a c300 00"), InlineData("53 00000008 c300 7800")]
    public void Utf8IsDecodedOnlyWhenAccessingTheString(string hex)
    {
        byte[] bytes = TestWire.Bytes(hex);
        foreach (bool fragmented in new[] {false, true})
        {
            var input = fragmented ? TestWire.ByteSegments(bytes) : new ReadOnlySequence<byte>(bytes);
            Assert.True(BackendMessageReader.TryRead(ref input, out var message));
            Assert.True(input.IsEmpty);
            Assert.Throws<InvalidDataException>(() =>
            {
                switch (message.Kind)
                {
                    case BackendMessageKind.CommandComplete: message.GetCommandTag(); break;
                    case BackendMessageKind.Authentication: message.GetAuthentication(); break;
                    case BackendMessageKind.ParameterStatus: message.GetParameterStatus(); break;
                }
            });
        }
    }

    [Theory]
    [MemberData(nameof(InvalidPackets))]
    public void RejectsMalformedCompleteBodiesWithoutConsumingInput(string hex)
    {
        byte[] bytes = TestWire.Bytes(hex);
        foreach (bool fragmented in new[] {false, true})
        {
            var input = fragmented ? TestWire.ByteSegments(bytes) : new ReadOnlySequence<byte>(bytes);
            var before = input;
            Assert.Throws<InvalidDataException>(() => BackendMessageReader.TryRead(ref input,
                out _));
            Assert.Equal(before.Start,
                input.Start);
            Assert.Equal(before.End,
                input.End);
            Assert.Equal(bytes,
                input.ToArray());
        }
    }

    [Theory]
    [InlineData("43 00000000")]
    [InlineData("43 00000003")]
    [InlineData("43 ffffffff")]
    [InlineData("43 7fffffff")]
    public void RejectsInvalidOrExcessiveLengthBeforeWaitingForBody(string hex)
    {
        var input = TestWire.ByteSegments(TestWire.Bytes(hex));
        var before = input;
        Assert.Throws<InvalidDataException>(() => BackendMessageReader.TryRead(ref input,
            out _));
        Assert.Equal(before.Start,
            input.Start);
    }

    [Fact]
    public void MessageLengthLimitIsExplicitAndConfigurable()
    {
        var input = new ReadOnlySequence<byte>(TestWire.Bytes("64 00000006 0102"));
        Assert.Throws<InvalidDataException>(() => BackendMessageReader.TryRead(ref input,
            out _,
            maxMessageLength: 5));
        Assert.True(BackendMessageReader.TryRead(ref input,
            out _,
            maxMessageLength: 6));
        Assert.True(input.IsEmpty);
        Assert.Throws<ArgumentOutOfRangeException>(() => BackendMessageReader.TryRead(ref input,
            out _,
            maxMessageLength: 3));
    }

    [Theory]
    [InlineData("f09f988000", "😀")]
    [InlineData("e282ac00", "€")]
    [InlineData("d0af00", "Я")]
    public void DecodesUtf8ScalarsAcrossEveryByteBoundary(string textHex,
        string expected)
    {
        byte[] text = TestWire.Bytes(textHex);
        byte[] frame = new byte[text.Length + 5];
        frame[0] = (byte)'C';
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1),
            text.Length + 4);
        text.CopyTo(frame,
            5);
        var input = TestWire.ByteSegments(frame);
        Assert.True(BackendMessageReader.TryRead(ref input,
            out var message));
        Assert.Equal(expected,
            message.GetCommandTag());
    }

    [Theory]
    [InlineData(EncryptionRequestKind.Ssl, "53", true)]
    [InlineData(EncryptionRequestKind.Ssl, "4e", false)]
    [InlineData(EncryptionRequestKind.Gss, "47", true)]
    [InlineData(EncryptionRequestKind.Gss, "4e", false)]
    public void ReadsOnlyOneEncryptionNegotiationByte(EncryptionRequestKind request,
        string response,
        bool expected)
    {
        byte[] following = TestWire.Bytes("52 00000008 00000000");
        var input = TestWire.ByteSegments([.. TestWire.Bytes(response), .. following]);
        Assert.True(BackendMessageReader.TryReadEncryptionResponse(ref input,
            request,
            out bool accepted));
        Assert.Equal(expected,
            accepted);
        Assert.Equal(following,
            input.ToArray());
        Assert.True(BackendMessageReader.TryRead(ref input,
            out var message));
        Assert.Equal(AuthenticationMethod.Ok,
            message.GetAuthentication()
                .Method);
    }

    [Theory]
    [InlineData(EncryptionRequestKind.Ssl, "47")]
    [InlineData(EncryptionRequestKind.Gss, "53")]
    [InlineData(EncryptionRequestKind.Ssl, "00")]
    public void RejectsEncryptionReplyFromWrongPhaseWithoutConsumption(EncryptionRequestKind request,
        string response)
    {
        var input = TestWire.ByteSegments(TestWire.Bytes(response));
        var before = input;
        Assert.Throws<InvalidDataException>(() => BackendMessageReader.TryReadEncryptionResponse(ref input,
            request,
            out _));
        Assert.Equal(before.Start,
            input.Start);
    }

    [Fact]
    public void EmptyEncryptionReplyNeedsMoreData()
    {
        var input = ReadOnlySequence<byte>.Empty;
        Assert.False(BackendMessageReader.TryReadEncryptionResponse(ref input,
            EncryptionRequestKind.Ssl,
            out _));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BackendMessageReader.TryReadEncryptionResponse(ref input,
                (EncryptionRequestKind)42,
                out _));
    }
}
