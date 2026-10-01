using System.Buffers;
using Mpgsql.Protocol;

namespace Mpgsql.Tests.Protocol;

// Scripted wire transcripts verify message composition and response framing, without a database.
// Command scheduling, authentication algorithms and protocol state ownership belong above this codec.
public sealed class ProtocolSequenceTests
{
    [Fact]
    public void ComposesExtendedQueryAndReadsInterleavedResponses()
    {
        var output = new ArrayBufferWriter<byte>();
        FrontendMessage.Parse("select $1", parameterTypes: new uint[] { 23 }).Write(output);
        FrontendMessage.Bind(parameters: new ReadOnlyMemory<byte>?[] { TestWire.Bytes("0000002a") },
            parameterFormats: new[] { FormatCode.Binary }, resultFormats: new[] { FormatCode.Binary }).Write(output);
        FrontendMessage.Describe(StatementOrPortal.Portal).Write(output);
        FrontendMessage.Execute().Write(output);
        FrontendMessage.Sync().Write(output);
        Assert.Equal(TestWire.Bytes(
            "50 00000015 00 73656c65637420243100 0001 00000017 " +
            "42 00000018 00 00 0001 0001 0001 00000004 0000002a 0001 0001 " +
            "44 00000006 50 00 45 00000009 00 00000000 53 00000004"), output.WrittenSpan.ToArray());

        var input = TestWire.ByteSegments(TestWire.Bytes(
            "31 00000004 " +
            "4e 0000000a 4d 68657900 00 " + // Notice between confirmations
            "32 00000004 " +
            "53 00000008 7800 7900 " + // ParameterStatus is independent of command state
            "54 0000001a 0001 6e00 00000000 0000 00000017 0004 ffffffff 0001 " +
            "44 0000000e 0001 00000004 0000002a " +
            "43 0000000d 53454c454354203100 " +
            "41 0000000c 0000002a 6300 7000 " + // Notification immediately before ReadyForQuery
            "5a 00000005 49"));
        var responses = new List<BackendMessageKind>();
        var events = new List<BackendMessageKind>();
        while (BackendMessageReader.TryRead(ref input, out var message))
        {
            (message.IsAsynchronous ? events : responses).Add(message.Kind);
            if (message.Kind == BackendMessageKind.DataRow)
            {
                var values = message.GetDataRow().GetEnumerator();
                Assert.True(values.MoveNext());
                Assert.Equal(TestWire.Bytes("0000002a"), values.Current!.Value.ToArray());
                Assert.False(values.MoveNext());
            }
        }
        Assert.True(input.IsEmpty);
        Assert.Equal(new[]
        {
            BackendMessageKind.ParseComplete, BackendMessageKind.BindComplete,
            BackendMessageKind.RowDescription, BackendMessageKind.DataRow,
            BackendMessageKind.CommandComplete, BackendMessageKind.ReadyForQuery
        }, responses);
        Assert.Equal(new[]
        {
            BackendMessageKind.NoticeResponse, BackendMessageKind.ParameterStatus, BackendMessageKind.NotificationResponse
        }, events);
    }

    [Fact]
    public void ReadsStartupAuthenticationThroughReadyBoundary()
    {
        var output = new ArrayBufferWriter<byte>();
        FrontendMessage.Startup("u").Write(output);
        FrontendMessage.Password("pw").Write(output);
        Assert.Equal(TestWire.Bytes(
            "00000025 00030000 7573657200 7500 636c69656e745f656e636f64696e6700 5554463800 00 " +
            "70 00000007 707700"), output.WrittenSpan.ToArray());
        var input = TestWire.ByteSegments(TestWire.Bytes(
            "52 00000008 00000003 52 00000008 00000000 " +
            "4b 0000000c 01020304 89abcdef " +
            "53 00000019 636c69656e745f656e636f64696e6700 5554463800 " +
            "5a 00000005 49"));
        Assert.True(BackendMessageReader.TryRead(ref input, out var challenge));
        Assert.Equal(AuthenticationMethod.CleartextPassword, challenge.GetAuthentication().Method);
        Assert.True(BackendMessageReader.TryRead(ref input, out var authenticated));
        Assert.Equal(AuthenticationMethod.Ok, authenticated.GetAuthentication().Method);
        Assert.True(BackendMessageReader.TryRead(ref input, out var key));
        Assert.Equal(0x01020304, key.GetBackendKeyData().ProcessId);
        Assert.True(BackendMessageReader.TryRead(ref input, out var encoding));
        Assert.Equal(new ParameterStatus("client_encoding", "UTF8"), encoding.GetParameterStatus());
        Assert.True(BackendMessageReader.TryRead(ref input, out var ready));
        Assert.Equal(TransactionStatus.Idle, ready.GetTransactionStatus());
        Assert.True(input.IsEmpty);
    }

    [Fact]
    public void DistinguishesPortalSuspensionFromCommandCompletion()
    {
        var output = new ArrayBufferWriter<byte>();
        FrontendMessage.Execute("p", 1).Write(output);
        FrontendMessage.Flush().Write(output);
        // Continue the portal before Sync could end its transaction lifetime.
        FrontendMessage.Execute("p").Write(output);
        FrontendMessage.Sync().Write(output);
        Assert.Equal(TestWire.Bytes(
            "45 0000000a 7000 00000001 48 00000004 45 0000000a 7000 00000000 53 00000004"),
            output.WrittenSpan.ToArray());
        Assert.Equal(new[]
        {
            BackendMessageKind.DataRow, BackendMessageKind.PortalSuspended, BackendMessageKind.DataRow,
            BackendMessageKind.CommandComplete, BackendMessageKind.ReadyForQuery
        }, ReadKinds("44 0000000b 0001 00000001 31 73 00000004 " +
            "44 0000000b 0001 00000001 32 43 0000000d 53454c454354203200 5a 00000005 49"));
    }

    [Fact]
    public void ReadsErrorSyncBoundaryAndSubsequentSuccessfulCycle()
    {
        // Bind/Execute skipped by the server produce no successful confirmations.
        // The codec exposes ErrorResponse and ReadyForQuery separately for the scheduler to recover.
        Assert.Equal(new[]
        {
            BackendMessageKind.ErrorResponse, BackendMessageKind.ReadyForQuery,
            BackendMessageKind.ParseComplete, BackendMessageKind.BindComplete, BackendMessageKind.NoData,
            BackendMessageKind.CommandComplete, BackendMessageKind.ReadyForQuery
        }, ReadKinds("45 0000000a 4d 62616400 00 5a 00000005 49 " +
            "31 00000004 32 00000004 6e 00000004 43 0000000d 555044415445203100 5a 00000005 49"));
    }

    [Fact]
    public void ComposesCopyInAndReadsCopyOutAsDistinctMessages()
    {
        var output = new ArrayBufferWriter<byte>();
        FrontendMessage.CopyData(TestWire.Bytes("610a")).Write(output);
        FrontendMessage.CopyDone().Write(output);
        FrontendMessage.Sync().Write(output);
        Assert.Equal(TestWire.Bytes("64 00000006 610a 63 00000004 53 00000004"), output.WrittenSpan.ToArray());
        Assert.Equal(new[]
        {
            BackendMessageKind.CopyInResponse, BackendMessageKind.CommandComplete, BackendMessageKind.ReadyForQuery
        }, ReadKinds("47 00000009 00 0001 0000 43 0000000b 434f5059203100 5a 00000005 49"));
        Assert.Equal(new[]
        {
            BackendMessageKind.CopyOutResponse, BackendMessageKind.CopyData, BackendMessageKind.CopyDone,
            BackendMessageKind.CommandComplete, BackendMessageKind.ReadyForQuery
        }, ReadKinds("48 00000009 00 0001 0000 64 00000006 610a " +
            "63 00000004 43 0000000b 434f5059203100 5a 00000005 49"));
    }

    private static List<BackendMessageKind> ReadKinds(string hex)
    {
        var input = TestWire.ByteSegments(TestWire.Bytes(hex));
        var kinds = new List<BackendMessageKind>();
        while (BackendMessageReader.TryRead(ref input, out var message))
            kinds.Add(message.Kind);
        Assert.True(input.IsEmpty);
        return kinds;
    }
}
