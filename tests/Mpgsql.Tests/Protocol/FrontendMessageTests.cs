using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Mpgsql.Protocol;

namespace Mpgsql.Tests.Protocol;

public sealed class FrontendMessageTests
{
    // Literal vectors from protocol 3.0 field order; every vector includes its complete header.
    public static TheoryData<FrontendTestCase, string> Packets => new()
    {
        {
            FrontendMessage.Startup("u"),
            "00000025 00030000 7573657200 7500 636c69656e745f656e636f64696e6700 5554463800 00"
        },
        {
            FrontendMessage.Startup("u",
                "d"),
            "00000030 00030000 7573657200 7500 646174616261736500 6400 636c69656e745f656e636f64696e6700 5554463800 00"
        },
        {
            FrontendMessage.Startup(new KeyValuePair<string, string>[]
            {
                new("user",
                    "u"),
                new("client_encoding",
                    "UTF8")
            }),
            "00000025 00030000 7573657200 7500 636c69656e745f656e636f64696e6700 5554463800 00"
        },
        {FrontendMessage.SslRequest(), "00000008 04d2162f"},
        {FrontendMessage.GssEncRequest(), "00000008 04d21630"},
        {
            FrontendMessage.CancelRequest(0x01020304,
                unchecked((int)0x89abcdef)),
            "00000010 04d2162e 01020304 89abcdef"
        },
        {FrontendMessage.Query("select 1"), "51 0000000d 73656c656374203100"},
        {FrontendMessage.Query("Я"), "51 00000007 d0af00"},
        {FrontendMessage.Query(""), "51 00000005 00"},
        {
            FrontendMessage.Parse("select $1",
                parameterTypes: new uint[]
                {
                    23
                }),
            "50 00000015 00 73656c65637420243100 0001 00000017"
        },
        {
            FrontendMessage.Parse("q",
                "s",
                new uint[]
                {
                    23,
                    uint.MaxValue
                }),
            "50 00000012 7300 7100 0002 00000017 ffffffff"
        },
        {FrontendMessage.Parse(""), "50 00000008 00 00 0000"},
        {FrontendMessage.Bind(), "42 0000000c 00 00 0000 0000 0000"},
        {
            FrontendMessage.Bind("p",
                "s",
                new ReadOnlyMemory<byte>?[]
                {
                    TestWire.Bytes("0000002a"),
                    null,
                    ReadOnlyMemory<byte>.Empty
                },
                new[]
                {
                    FormatCode.Binary
                },
                new[]
                {
                    FormatCode.Text
                }),
            "42 00000022 7000 7300 0001 0001 0003 00000004 0000002a ffffffff 00000000 0001 0000"
        },
        {
            FrontendMessage.Bind(parameters: new ReadOnlyMemory<byte>?[] {TestWire.Bytes("37"), ReadOnlyMemory<byte>.Empty},
                parameterFormats: new[] {FormatCode.Text, FormatCode.Binary},
                resultFormats: new[] {FormatCode.Binary, FormatCode.Text}),
            "42 0000001d 00 00 0002 0000 0001 0002 00000001 37 00000000 0002 0001 0000"
        },
        {
            FrontendMessage.Describe(StatementOrPortal.Statement,
                "s"),
            "44 00000007 53 7300"
        },
        {FrontendMessage.Describe(StatementOrPortal.Portal), "44 00000006 50 00"},
        {
            FrontendMessage.Close(StatementOrPortal.Statement,
                "s"),
            "43 00000007 53 7300"
        },
        {FrontendMessage.Close(StatementOrPortal.Portal), "43 00000006 50 00"},
        {
            FrontendMessage.Execute("p",
                42),
            "45 0000000a 7000 0000002a"
        },
        {FrontendMessage.Execute(), "45 00000009 00 00000000"},
        {FrontendMessage.Flush(), "48 00000004"},
        {FrontendMessage.Sync(), "53 00000004"},
        {FrontendMessage.Terminate(), "58 00000004"},
        {FrontendMessage.Password("pw"), "70 00000007 707700"},
        {FrontendMessage.Password(""), "70 00000005 00"},
        {FrontendMessage.GssResponse(TestWire.Bytes("00ff")), "70 00000006 00ff"},
        {
            FrontendMessage.SaslInitialResponse("M",
                TestWire.Bytes("0102")),
            "70 0000000c 4d00 00000002 0102"
        },
        {FrontendMessage.SaslInitialResponse("M"), "70 0000000a 4d00 ffffffff"},
        {
            FrontendMessage.SaslInitialResponse("M",
                ReadOnlyMemory<byte>.Empty),
            "70 0000000a 4d00 00000000"
        },
        {FrontendMessage.SaslResponse(TestWire.Bytes("0102")), "70 00000006 0102"},
        {FrontendMessage.CopyData(TestWire.Bytes("00ff")), "64 00000006 00ff"},
        {FrontendMessage.CopyData(ReadOnlyMemory<byte>.Empty), "64 00000004"},
        {FrontendMessage.CopyDone(), "63 00000004"},
        {FrontendMessage.CopyFail("bad"), "66 00000008 62616400"},
        {
            FrontendMessage.FunctionCall(0xfffffffe,
                new ReadOnlyMemory<byte>?[]
                {
                    TestWire.Bytes("0000002a"),
                    null
                },
                new[]
                {
                    FormatCode.Binary
                },
                FormatCode.Binary),
            "46 0000001c fffffffe 0001 0001 0002 00000004 0000002a ffffffff 0001"
        },
        {FrontendMessage.FunctionCall(1), "46 0000000e 00000001 0000 0000 0000"},
        {
            FrontendMessage.Raw((byte)'P',
                TestWire.Bytes("010203")),
            "50 00000007 010203"
        },
        {
            FrontendMessage.RawStartup(TestWire.Bytes("00030000 7573657200 7500 00")),
            "00000010 00030000 7573657200 7500 00"
        }
    };

    [Theory]
    [MemberData(nameof(Packets), DisableDiscoveryEnumeration = true)]
    public void WritesCompleteWirePacket(FrontendTestCase message,
        string hex)
    {
        byte[] expected = TestWire.Bytes(hex);
        Assert.Equal(expected.Length,
            message.GetByteCount());
        var destination = Enumerable.Repeat((byte)0xcc,
            expected.Length + 3).ToArray();
        Assert.Equal(expected.Length,
            message.Write(destination));
        Assert.Equal(expected,
            destination[..expected.Length]);
        Assert.Equal(new byte[]
            {
                0xcc,
                0xcc,
                0xcc
            },
            destination[expected.Length..]);

        var buffer = new ArrayBufferWriter<byte>();
        message.Write(buffer);
        Assert.Equal(expected,
            buffer.WrittenSpan.ToArray());
    }

    [Theory]
    [MemberData(nameof(Packets), DisableDiscoveryEnumeration = true)]
    public void InsufficientDestinationRemainsUnchanged(FrontendTestCase message,
        string hex)
    {
        var destination = Enumerable.Repeat((byte)0xcc,
            TestWire.Bytes(hex)
                .Length
            - 1).ToArray();
        var before = destination.ToArray();
        Assert.Throws<ArgumentException>(() => message.Write(destination));
        Assert.Equal(before,
            destination);
    }

    [Fact]
    public void BufferWriterReceivesCompleteSizeHintAndOneAdvance()
    {
        var destination = new RecordingWriter();
        var message = FrontendMessage.Bind(parameters: new ReadOnlyMemory<byte>?[] {new byte[8192]});
        message.Write(destination);
        Assert.Equal(message.GetByteCount(),
            destination.SizeHint);
        Assert.Equal(message.GetByteCount(),
            destination.Advanced);
        Assert.Equal(1,
            destination.GetSpanCalls);
        Assert.Equal(1,
            destination.AdvanceCalls);
    }

    [Fact]
    public void ValidatesBindFormatCardinalityAndCodes()
    {
        var values = new ReadOnlyMemory<byte>?[3];
        Assert.Throws<ArgumentException>(() => FrontendMessage.Bind(parameters: values,
            parameterFormats: new[] {FormatCode.Text, FormatCode.Binary}).GetByteCount());
        Assert.Throws<ArgumentOutOfRangeException>(() => FrontendMessage.Bind(parameters: values,
            parameterFormats: new[] {(FormatCode)2}).GetByteCount());
        Assert.Throws<ArgumentOutOfRangeException>(() => FrontendMessage.Bind(
            resultFormats: new[] {(FormatCode)(-1)}).GetByteCount());
    }

    [Fact]
    public void ValidatesFunctionCallFormats()
    {
        Assert.Throws<ArgumentException>(() => FrontendMessage.FunctionCall(1,
            new ReadOnlyMemory<byte>?[3],
            new[]
            {
                FormatCode.Text,
                FormatCode.Binary
            }).GetByteCount());
        Assert.Throws<ArgumentOutOfRangeException>(() => FrontendMessage.FunctionCall(1,
            resultFormat: (FormatCode)2).GetByteCount());
    }

    [Fact]
    public void Int16CountsAllow65535AndReject65536()
    {
        var message = FrontendMessage.Parse("",
            parameterTypes: new uint[65535]);
        var destination = new byte[message.GetByteCount()];
        message.Write(destination);
        Assert.Equal(ushort.MaxValue,
            BinaryPrimitives.ReadUInt16BigEndian(destination.AsSpan(7,
                2)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FrontendMessage.Parse("",
                parameterTypes: new uint[65536]).GetByteCount());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FrontendMessage.Bind(parameters: new ReadOnlyMemory<byte>?[65536]).GetByteCount());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FrontendMessage.Bind(resultFormats: new FormatCode[65536]).GetByteCount());
    }

    [Fact]
    public void ValidatesTargetsRowLimitAndSaslMechanism()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FrontendMessage.Describe((StatementOrPortal)0).GetByteCount());
        Assert.Throws<ArgumentOutOfRangeException>(() => FrontendMessage.Close((StatementOrPortal)0).GetByteCount());
        Assert.Throws<ArgumentOutOfRangeException>(() => FrontendMessage.Execute(maxRows: -1).GetByteCount());
        Assert.Throws<ArgumentException>(() => FrontendMessage.SaslInitialResponse("").GetByteCount());
        Assert.Throws<ArgumentNullException>(() => FrontendMessage.Query(null!).GetByteCount());
        Assert.Throws<InvalidOperationException>(() => default(ParseMessage).GetByteCount());
        Assert.Throws<ArgumentOutOfRangeException>(() => FrontendMessage.Raw(0,
            default));
    }

    [Fact]
    public void StartupRequiresUserAndUtf8AndUniqueNames()
    {
        Assert.Throws<ArgumentException>(() => FrontendMessage.Startup("").GetByteCount());
        Assert.Throws<ArgumentException>(() => FrontendMessage.Startup(
            new KeyValuePair<string, string>[]
            {
                new("database",
                    "d")
            }).GetByteCount());
        Assert.Throws<ArgumentException>(() => FrontendMessage.Startup(
            new KeyValuePair<string, string>[]
            {
                new("user",
                    "u"),
                new("client_encoding",
                    "LATIN1")
            }).GetByteCount());
        Assert.Throws<ArgumentException>(() => FrontendMessage.Startup(
            new KeyValuePair<string, string>[]
            {
                new("user",
                    "u"),
                new("user",
                    "v")
            }).GetByteCount());
        Assert.Throws<ArgumentException>(() => FrontendMessage.Startup(
            new KeyValuePair<string, string>[]
            {
                new("user",
                    "u"),
                new("",
                    "v")
            }).GetByteCount());
    }

    [Fact]
    public void ParametersAreBorrowedUntilSerialization()
    {
        byte[] parameter = [1];
        var message = FrontendMessage.Bind(parameters: new ReadOnlyMemory<byte>?[] {parameter});
        parameter[0] = 42;
        var destination = new byte[message.GetByteCount()];
        message.Write(destination);
        Assert.Equal(TestWire.Bytes("42 00000011 00 00 0000 0001 00000001 2a 0000"),
            destination);
    }

    [Fact]
    public void DefaultTypedMessagesFailBeforeAnyWrites()
    {
        FrontendTestCase[] messages =
        [
            default(RawFrontendMessage), default(TextMessage), default(EmptyMessage), default(TargetMessage),
            default(ExecuteMessage), default(ParseMessage), default(BindMessage), default(FunctionCallMessage),
            default(StartupMessage), default(EncryptionRequestMessage), default(CancelRequestMessage), default(SaslInitialResponseMessage)
        ];
        foreach (var message in messages)
        {
            byte[] destination =
            [
                .. Enumerable.Repeat((byte)42,
                    64)
            ];
            var writer = new RecordingWriter();
            Assert.Throws<InvalidOperationException>(() => message.GetByteCount());
            Assert.Throws<InvalidOperationException>(() => message.Write(destination));
            Assert.Throws<InvalidOperationException>(() => message.Write(writer));
            Assert.All(destination,
                b => Assert.Equal(42,
                    b));
            Assert.Equal(0,
                writer.GetSpanCalls);
        }
    }

    private sealed class RecordingWriter : IBufferWriter<byte>
    {
        private byte[] _buffer = [];
        internal int SizeHint { get; private set; }
        internal int Advanced { get; private set; }
        internal int GetSpanCalls { get; private set; }
        internal int AdvanceCalls { get; private set; }

        public void Advance(int count)
        {
            Advanced = count;
            AdvanceCalls++;
        }
        public Memory<byte> GetMemory(int sizeHint = 0) => throw new NotSupportedException();
        public Span<byte> GetSpan(int sizeHint = 0)
        {
            SizeHint = sizeHint;
            GetSpanCalls++;
            _buffer = new byte[sizeHint];
            return _buffer;
        }
    }
}