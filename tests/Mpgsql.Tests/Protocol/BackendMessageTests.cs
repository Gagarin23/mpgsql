using System.Buffers;
using Mpgsql.Protocol;

namespace Mpgsql.Tests.Protocol;

public sealed class BackendMessageTests
{
    public static TheoryData<BackendMessageKind, string> Packets => new TheoryData<BackendMessageKind, string>
    {
        {BackendMessageKind.Authentication, "52 00000008 00000000"},
        {BackendMessageKind.Authentication, "52 00000008 00000002"},
        {BackendMessageKind.Authentication, "52 00000008 00000003"},
        {BackendMessageKind.Authentication, "52 0000000c 00000005 01020304"},
        {BackendMessageKind.Authentication, "52 00000008 00000007"},
        {BackendMessageKind.Authentication, "52 0000000a 00000008 0102"},
        {BackendMessageKind.Authentication, "52 00000008 00000009"},
        {BackendMessageKind.Authentication, "52 0000000d 0000000a 4d00 4e00 00"},
        {BackendMessageKind.Authentication, "52 0000000a 0000000b 0102"},
        {BackendMessageKind.Authentication, "52 00000008 0000000c"},
        {BackendMessageKind.Authentication, "52 0000000a 0000002a 0102"},
        {BackendMessageKind.BackendKeyData, "4b 0000000c 01020304 89abcdef"},
        {BackendMessageKind.ParseComplete, "31 00000004"},
        {BackendMessageKind.BindComplete, "32 00000004"},
        {BackendMessageKind.CloseComplete, "33 00000004"},
        {BackendMessageKind.CommandComplete, "43 0000000d 53454c454354203100"},
        {BackendMessageKind.CopyData, "64 00000006 00ff"},
        {BackendMessageKind.CopyData, "64 00000004"},
        {BackendMessageKind.CopyDone, "63 00000004"},
        {BackendMessageKind.CopyInResponse, "47 0000000b 00 0002 0000 0000"},
        {BackendMessageKind.CopyOutResponse, "48 0000000b 01 0002 0000 0001"},
        {BackendMessageKind.CopyBothResponse, "57 00000007 01 0000"},
        {BackendMessageKind.DataRow, "44 00000014 0003 ffffffff 00000000 00000002 c3a9"},
        {BackendMessageKind.DataRow, "44 00000006 0000"},
        {BackendMessageKind.EmptyQueryResponse, "49 00000004"},
        {
            BackendMessageKind.ErrorResponse,
            "45 0000001b 53 4552524f5200 43 585830303000 4d 62616400 59 7600 00"
        },
        {BackendMessageKind.FunctionCallResponse, "56 00000008 ffffffff"},
        {BackendMessageKind.FunctionCallResponse, "56 00000008 00000000"},
        {BackendMessageKind.FunctionCallResponse, "56 0000000a 00000002 0102"},
        {BackendMessageKind.NegotiateProtocolVersion, "76 0000001a 00000001 00000002 5f70715f2e7800 5f70715f2e7900"},
        {BackendMessageKind.NoData, "6e 00000004"},
        {BackendMessageKind.NoticeResponse, "4e 00000012 53 4e4f5449434500 4d 68657900 00"},
        {BackendMessageKind.NotificationResponse, "41 0000000c 0000002a 6300 7000"},
        {BackendMessageKind.ParameterDescription, "74 00000012 0003 00000017 00000000 ffffffff"},
        {BackendMessageKind.ParameterDescription, "74 00000006 0000"},
        {BackendMessageKind.ParameterStatus, "53 00000019 636c69656e745f656e636f64696e6700 5554463800"},
        {BackendMessageKind.ParameterStatus, "53 00000009 7800 d0af00"},
        {BackendMessageKind.PortalSuspended, "73 00000004"},
        {BackendMessageKind.ReadyForQuery, "5a 00000005 49"},
        {BackendMessageKind.ReadyForQuery, "5a 00000005 54"},
        {BackendMessageKind.ReadyForQuery, "5a 00000005 45"},
        {
            BackendMessageKind.RowDescription,
            "54 0000002e 0002 6e00 fffffffe ffff 00000017 0004 ffffffff 0001 7600 00000000 0000 00000019 ffff 00000007 0000"
        },
        {BackendMessageKind.RowDescription, "54 00000006 0000"},
        {BackendMessageKind.Unknown, "3f 00000007 010203"}
    };

    [Theory, MemberData(nameof(Packets))]
    public void ReadsEachMessageAcrossEverySplit(BackendMessageKind kind,
        string hex)
    {
        var bytes = TestWire.Bytes(hex);
        for (var split = 0; split <= bytes.Length; split++)
        {
            var input = TestWire.Chunks(ReadOnlyMemory<byte>.Empty,
                bytes.AsMemory(0,
                    split),
                ReadOnlyMemory<byte>.Empty,
                bytes.AsMemory(split),
                ReadOnlyMemory<byte>.Empty);
            Assert.True(BackendMessageReader.TryRead(ref input,
                out var message));
            Assert.Equal(kind,
                message.Kind);
            Assert.Equal(bytes[0],
                message.Type);
            Assert.Equal(bytes[5..],
                message.Payload.ToArray());
            Assert.True(input.IsEmpty);
        }
        Assert.Equal(kind,
            TestWire.Read(hex)
                .Kind); // one byte per segment, including empty bodies
    }

    [Theory, MemberData(nameof(Packets))]
    public void IncompletePacketsDoNotConsumeInput(BackendMessageKind _,
        string hex)
    {
        var bytes = TestWire.Bytes(hex);
        for (var length = 0; length < bytes.Length; length++)
        {
            var input = TestWire.ByteSegments(bytes[..length]);
            var before = input;
            Assert.False(BackendMessageReader.TryRead(ref input,
                out var message));
            Assert.Equal(before.Start,
                input.Start);
            Assert.Equal(before.End,
                input.End);
            Assert.Equal(before.Length,
                input.Length);
            Assert.True(message.Payload.IsEmpty);
        }
    }

    [Theory, MemberData(nameof(Packets))]
    public void ReadsOnlyOnePacket(BackendMessageKind kind,
        string hex)
    {
        var next = TestWire.Bytes("5a 00000005 49");
        byte[] bytes = [.. TestWire.Bytes(hex), .. next];
        var input = TestWire.ByteSegments(bytes);
        Assert.True(BackendMessageReader.TryRead(ref input,
            out var message));
        Assert.Equal(kind,
            message.Kind);
        Assert.Equal(next,
            input.ToArray());
        Assert.True(BackendMessageReader.TryRead(ref input,
            out var ready));
        Assert.Equal(TransactionStatus.Idle,
            ready.GetTransactionStatus());
        Assert.True(input.IsEmpty);
    }

    [Theory, InlineData("52 00000008 00000000", AuthenticationMethod.Ok, ""), InlineData("52 00000008 00000002", AuthenticationMethod.KerberosV5, ""), InlineData("52 00000008 00000003", AuthenticationMethod.CleartextPassword, ""),
     InlineData("52 0000000c 00000005 01020304", AuthenticationMethod.Md5Password, "01020304"), InlineData("52 00000008 00000007", AuthenticationMethod.Gss, ""),
     InlineData("52 0000000a 00000008 0102", AuthenticationMethod.GssContinue, "0102"), InlineData("52 00000008 00000009", AuthenticationMethod.Sspi, ""),
     InlineData("52 0000000a 0000000b 0102", AuthenticationMethod.SaslContinue, "0102"), InlineData("52 00000008 0000000c", AuthenticationMethod.SaslFinal, "")]
    public void ExposesAuthenticationCodeAndOpaqueData(string hex,
        AuthenticationMethod method,
        string data)
    {
        var authentication = TestWire.Read(hex).GetAuthentication();
        Assert.Equal(method,
            authentication.Method);
        Assert.Equal(TestWire.Bytes(data),
            authentication.Data.ToArray());
        Assert.True(authentication.Mechanisms.IsEmpty);
    }

    [Fact]
    public void ExposesSaslMechanismsAndPreservesUnknownAuthentication()
    {
        var authentication = TestWire.Read("52 0000000d 0000000a 4d00 4e00 00").GetAuthentication();
        Assert.Equal(AuthenticationMethod.Sasl,
            authentication.Method);
        Assert.Equal(new[]
            {
                "M",
                "N"
            },
            authentication.Mechanisms.ToArray());
        Assert.True(authentication.Data.IsEmpty);
        var unknown = TestWire.Read("52 0000000a 0000002a 0102").GetAuthentication();
        Assert.Equal((AuthenticationMethod)42,
            unknown.Method);
        Assert.Equal(new byte[]
            {
                1,
                2
            },
            unknown.Data.ToArray());
    }

    [Fact]
    public void ExposesCancellationKeyWithoutLosingHighBits()
    {
        var key = TestWire.Read("4b 0000000c 01020304 89abcdef").GetBackendKeyData();
        Assert.Equal(new BackendKeyData(0x01020304,
                unchecked((int)0x89abcdef)),
            key);
    }

    [Fact]
    public void ExposesCommandTagStatusNotificationAndNegotiation()
    {
        Assert.Equal("SELECT 1",
            TestWire.Read("43 0000000d 53454c454354203100")
                .GetCommandTag());
        var status = TestWire.Read("53 00000009 7800 d0af00").GetParameterStatus();
        Assert.Equal(new ParameterStatus("x",
                "Я"),
            status);
        var notification = TestWire.Read("41 0000000c 0000002a 6300 7000").GetNotification();
        Assert.Equal(new NotificationResponse(42,
                "c",
                "p"),
            notification);
        var negotiation = TestWire.Read("76 0000001a 00000001 00000002 5f70715f2e7800 5f70715f2e7900")
            .GetProtocolVersionNegotiation();
        Assert.Equal(1,
            negotiation.MinorVersion);
        Assert.Equal(new[]
            {
                "_pq_.x",
                "_pq_.y"
            },
            negotiation.UnrecognizedOptions.ToArray());
    }

    [Theory, InlineData("49", TransactionStatus.Idle), InlineData("54", TransactionStatus.InTransaction), InlineData("45", TransactionStatus.FailedTransaction)]
    public void ExposesTransactionStatus(string status,
        TransactionStatus expected)
    {
        Assert.Equal(expected,
            TestWire.Read("5a 00000005 " + status)
                .GetTransactionStatus());
    }

    [Fact]
    public void ExposesRowAndParameterMetadataWithSignedSizesAndUnsignedOids()
    {
        Assert.Equal(new uint[] {23, 0, uint.MaxValue},
            TestWire.Read("74 00000012 0003 00000017 00000000 ffffffff").GetParameterDescription().ToArray());
        var fields = TestWire.Read(
                "54 0000002e 0002 6e00 fffffffe ffff 00000017 0004 ffffffff 0001 7600 00000000 0000 00000019 ffff 00000007 0000")
            .GetRowDescription();
        Assert.Equal(new[]
            {
                new RowField("n",
                    0xfffffffe,
                    -1,
                    23,
                    4,
                    -1,
                    FormatCode.Binary),
                new RowField("v",
                    0,
                    0,
                    25,
                    -1,
                    7,
                    FormatCode.Text)
            },
            fields.ToArray());
    }

    [Fact]
    public void ExposesNullEmptyAndSegmentedDataRowValues()
    {
        var row = TestWire.Read("44 00000014 0003 ffffffff 00000000 00000002 c3a9").GetDataRow();
        Assert.Equal(3,
            row.Count);
        var values = row.GetEnumerator();
        Assert.True(values.MoveNext());
        Assert.Null(values.Current);
        Assert.True(values.MoveNext());
        Assert.True(values.Current.HasValue);
        Assert.True(values.Current.Value.IsEmpty);
        Assert.True(values.MoveNext());
        Assert.True(values.Current.HasValue);
        Assert.False(values.Current.Value.IsSingleSegment);
        Assert.Equal(TestWire.Bytes("c3a9"),
            values.Current.Value.ToArray());
        Assert.False(values.MoveNext());
        Assert.False(values.MoveNext());
        Assert.False(TestWire.Read("44 00000006 0000").GetDataRow().GetEnumerator().MoveNext());
    }

    [Fact]
    public void BinaryValuesBorrowOriginalBuffer()
    {
        var bytes = TestWire.Bytes("44 0000000d 0001 00000003 010203");
        var input = TestWire.ByteSegments(bytes);
        Assert.True(BackendMessageReader.TryRead(ref input,
            out var message));
        var values = message.GetDataRow().GetEnumerator();
        Assert.True(values.MoveNext());
        bytes[^1] = 42;
        Assert.Equal(new byte[]
            {
                1,
                2,
                42
            },
            values.Current!.Value.ToArray());
    }

    [Theory, InlineData("56 00000008 ffffffff", null), InlineData("56 00000008 00000000", ""), InlineData("56 0000000a 00000002 0102", "0102")]
    public void ExposesNullableFunctionResults(string hex,
        string? expected)
    {
        var value = TestWire.Read(hex).GetFunctionCallResult();
        if (expected is null)
        {
            Assert.Null(value);
        }
        else
        {
            Assert.True(value.HasValue);
            Assert.Equal(TestWire.Bytes(expected),
                value.Value.ToArray());
        }
    }

    [Fact]
    public void ExposesCopyDirectionFormatsAndRawData()
    {
        var copyIn = TestWire.Read("47 0000000b 00 0002 0000 0000").GetCopyResponse();
        Assert.Equal(FormatCode.Text,
            copyIn.Format);
        Assert.Equal([
                FormatCode.Text,
                FormatCode.Text
            ],
            copyIn.ColumnFormats.ToArray());
        var copyOut = TestWire.Read("48 0000000b 01 0002 0000 0001").GetCopyResponse();
        Assert.Equal(FormatCode.Binary,
            copyOut.Format);
        Assert.Equal([
                FormatCode.Text,
                FormatCode.Binary
            ],
            copyOut.ColumnFormats.ToArray());
        var copyBoth = TestWire.Read("57 00000007 01 0000").GetCopyResponse();
        Assert.Equal(FormatCode.Binary,
            copyBoth.Format);
        Assert.True(copyBoth.ColumnFormats.IsEmpty);
        Assert.Equal(TestWire.Bytes("00ff"),
            TestWire.Read("64 00000006 00ff")
                .GetCopyData()
                .ToArray());
    }

    [Fact]
    public void PreservesErrorFieldsIncludingUnknownCodes()
    {
        var error = TestWire.Read("45 0000001b 53 4552524f5200 43 585830303000 4d 62616400 59 7600 00");
        Assert.False(error.IsAsynchronous);
        var diagnostic = error.GetDiagnostics();
        Assert.Equal("ERROR",
            diagnostic.Severity);
        Assert.Null(diagnostic.InvariantSeverity);
        Assert.Equal("XX000",
            diagnostic.SqlState);
        Assert.Equal("bad",
            diagnostic.Message);
        Assert.Equal("v",
            diagnostic.GetField((byte)'Y'));
        Assert.Equal(new[] {(byte)'S', (byte)'C', (byte)'M', (byte)'Y'},
            diagnostic.Fields.ToArray().Select(f => f.Code).ToArray());
        var notice = TestWire.Read("4e 00000012 53 4e4f5449434500 4d 68657900 00");
        Assert.True(notice.IsAsynchronous);
        Assert.Equal("hey",
            notice.GetDiagnostics()
                .Message);
    }

    [Fact]
    public void WrongTypedAccessorsFailExplicitly()
    {
        var message = TestWire.Read("31 00000004");
        Assert.Throws<InvalidOperationException>(() => message.GetAuthentication());
        Assert.Throws<InvalidOperationException>(() => message.GetBackendKeyData());
        Assert.Throws<InvalidOperationException>(() => message.GetCommandTag());
        Assert.Throws<InvalidOperationException>(() => message.GetTransactionStatus());
        Assert.Throws<InvalidOperationException>(() => message.GetParameterStatus());
        Assert.Throws<InvalidOperationException>(() => message.GetNotification());
        Assert.Throws<InvalidOperationException>(() => message.GetParameterDescription());
        Assert.Throws<InvalidOperationException>(() => message.GetRowDescription());
        Assert.Throws<InvalidOperationException>(() => message.GetDataRow());
        Assert.Throws<InvalidOperationException>(() => message.GetFunctionCallResult());
        Assert.Throws<InvalidOperationException>(() => message.GetCopyData());
        Assert.Throws<InvalidOperationException>(() => message.GetCopyResponse());
        Assert.Throws<InvalidOperationException>(() => message.GetDiagnostics());
        Assert.Throws<InvalidOperationException>(() => message.GetProtocolVersionNegotiation());
    }

    [Fact]
    public void Int16BackendCountsAreUnsigned()
    {
        var packet = new byte[262147]; // tag + length + count + 65535 OIDs
        TestWire.Bytes("74 00040002 ffff").CopyTo(packet,
            0);
        var input = new ReadOnlySequence<byte>(packet);
        Assert.True(BackendMessageReader.TryRead(ref input,
            out var message));
        Assert.Equal(65535,
            message.GetParameterDescription()
                .Length);
        Assert.True(input.IsEmpty);
    }
}