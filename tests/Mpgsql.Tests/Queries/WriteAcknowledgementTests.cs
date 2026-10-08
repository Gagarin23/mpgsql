using System.Buffers;
using System.Buffers.Binary;
using Mpgsql.Protocol;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class WriteAcknowledgementTests
{
    private const string Sql = "select array_length($1::bigint[], 1)::bigint";

    private static async Task ObserveAsync(Task acknowledgement)
    {
        await acknowledgement.ConfigureAwait(false);
    }

    private static byte[] ExpectedExecution()
    {
        // ndim, flags, element OID, dimension length/lower bound, then int32 length + int64 values.
        var array = new byte[44];
        BinaryPrimitives.WriteInt32BigEndian(array, 1);
        BinaryPrimitives.WriteUInt32BigEndian(array.AsSpan(8), 20);
        BinaryPrimitives.WriteInt32BigEndian(array.AsSpan(12), 2);
        BinaryPrimitives.WriteInt32BigEndian(array.AsSpan(16), 1);
        BinaryPrimitives.WriteInt32BigEndian(array.AsSpan(20), 8);
        BinaryPrimitives.WriteInt64BigEndian(array.AsSpan(24), 11);
        BinaryPrimitives.WriteInt32BigEndian(array.AsSpan(32), 8);
        BinaryPrimitives.WriteInt64BigEndian(array.AsSpan(36), 22);
        var bytes = new ArrayBufferWriter<byte>();
        FrontendMessage.Parse(Sql, parameterTypes: new uint[] {1016}).Write(bytes);
        FrontendMessage.Bind(parameters: new ReadOnlyMemory<byte>?[] {array},
            parameterFormats: new[] {FormatCode.Binary}, resultFormats: new[] {FormatCode.Binary}).Write(bytes);
        FrontendMessage.Describe(StatementOrPortal.Portal).Write(bytes);
        FrontendMessage.Execute().Write(bytes);
        FrontendMessage.Sync().Write(bytes);
        return bytes.WrittenSpan.ToArray();
    }

    [Theory, InlineData(0), InlineData(1), InlineData(2)]
    // success
    // logical cancellation during encoding
     // transport EOF during encoding
    public async Task MultipleObserversKeepTheInputBarrierAndCompletionStatus(int outcome)
    {
        var testToken = TestContext.Current.CancellationToken;
        using var input = new BlockingInputMemory(true);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(testToken);
        await using var wire = new ScriptedSession();
        await using var batch = wire.Session.CreateBatch(request.Token);
        var send = batch.SendQueryAsync(Sql, new[] {MpgsqlParameterValue.Int64Array(input.Memory)}).AsTask();
        var first = ObserveAsync(send);
        var second = ObserveAsync(send);
        var sync = batch.SendSyncAsync().AsTask();
        await input.Entered.WaitAsync(TestTimeout, testToken);
        try
        {
            if (outcome == 1)
            {
                request.Cancel();
            }
            else if (outcome == 2)
            {
                await wire.Incoming.Writer.CompleteAsync();
                await Assert.ThrowsAnyAsync<IOException>(() => wire.Session.Completion.WaitAsync(TestTimeout, testToken));
            }
            Assert.False(send.IsCompleted);
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
        }
        finally { input.Resume(); }

        if (outcome == 2)
        {
            foreach (var observation in new[] {send, first, second, sync})
                await Assert.ThrowsAnyAsync<IOException>(() => observation.WaitAsync(TestTimeout, testToken));
            Assert.True(send.IsFaulted);
            await Assert.ThrowsAnyAsync<IOException>(() => batch.ObserveCompletionAsync().AsTask());
            Assert.False(wire.Session.IsHealthy);
        }
        else
        {
            var bytes = new List<byte>();
            while (!Tags([.. bytes]).Contains('S'))
            {
                bytes.AddRange(await wire.ReadOutputAsync());
            }
            Assert.Equal(outcome == 0 ? ExpectedExecution() : new byte[] {(byte)'S', 0, 0, 0, 4}, bytes.ToArray());
            await sync.WaitAsync(TestTimeout, testToken);
            if (outcome == 1)
            {
                foreach (var observation in new[] {send, first, second})
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observation.WaitAsync(TestTimeout, testToken));
                Assert.True(send.IsCanceled);
                await wire.WriteAsync(Ready(), 1);
            }
            else
            {
                await Task.WhenAll(send, first, second).WaitAsync(TestTimeout, testToken);
                Assert.True(send.IsCompletedSuccessfully);
                await wire.WriteAsync(Join(Query(2), Ready()), 1);
                await using var reader = await batch.ReadResultsAsync();
                Assert.Equal(0, reader.QueryIndex);
                Assert.True(await reader.ReadAsync());
                Assert.Equal(2, reader.GetInt64(0));
                Assert.False(await reader.NextResultAsync());
            }
            await batch.Completion.WaitAsync(TestTimeout, testToken);
            await batch.DisposeAsync();

            await using var following = wire.Session.CreateBatch(testToken);
            var nextSend = following.SendQueryAsync("select 42::bigint").AsTask();
            var nextSync = following.SendSyncAsync().AsTask();
            while (!Tags(await wire.ReadOutputAsync()).Contains('S')) { }
            await Task.WhenAll(nextSend, nextSync).WaitAsync(TestTimeout, testToken);
            await wire.WriteAsync(Join(Query(42), Ready()), 3);
            await using var nextReader = await following.ReadResultsAsync();
            Assert.True(await nextReader.ReadAsync());
            Assert.Equal(42, nextReader.GetInt64(0));
            Assert.False(await nextReader.NextResultAsync());
            await nextReader.DisposeAsync();
            await following.DisposeAsync();
            Assert.True(wire.Session.IsIdleAndHealthy);
        }
        input.Revoke();
        Assert.Equal(1, input.Reads);
        Assert.Equal(0, wire.Session.BufferedRowBytes);
    }
}