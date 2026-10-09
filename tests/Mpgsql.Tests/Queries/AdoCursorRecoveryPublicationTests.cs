using System.Buffers;
using Mpgsql.Internal;
using Mpgsql.Protocol;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class AdoCursorRecoveryPublicationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReadyForQueryPublishesSessionRecoveryBeforeCursorCompletion()
    {
        await using var wire = new ScriptedSession();
        await wire.Session.ClaimForAdoDataSourceAsync(8 * 1024 * 1024, Token);
        await using var cursor = wire.Session.CreateAdoCursor(Token, new NoReaderOwner());
        var sent = cursor.SendExecution(new QueryDefinition("select invalid", default), null);
        var output = await wire.ReadOutputAsync();
        Assert.Equal(new[] { 'P', 'B', 'D', 'E', 'S' }, Tags(output));
        await sent.Completion.WaitAsync(TestTimeout, Token);

        // Receipt uses the real input path, so the session owns the diagnostic
        // which would otherwise be reused by a concurrent transport failure.
        var receiving = wire.Session.WaitForAdoInputAsync(cursor).AsTask();
        var writing = wire.WriteAsync(Packet('E', "SERROR\0C22012\0Mbad\0\0"u8.ToArray()));
        Assert.True(await receiving.WaitAsync(TestTimeout, Token));
        Assert.False(wire.Session.TryReadAdoCursorEvent(cursor, out _));
        await writing.WaitAsync(TestTimeout, Token);
        Assert.False(cursor.ProtocolCompleted);
        Assert.Equal(TransactionStatus.Idle, wire.Session.LastTransactionStatus);

        // Test the exact cursor publication seam without allowing the session's
        // outer parser loop to perform any later cleanup on its behalf.
        var completion = cursor.Completion;
        Assert.Equal(CursorEvent.ProtocolEnd, cursor.Accept(Decode(Ready('E')), draining: true));
        Assert.True(completion.IsCompleted);
        Assert.Equal(TransactionStatus.FailedTransaction, wire.Session.LastTransactionStatus);
        var sqlError = await Assert.ThrowsAsync<MpgsqlServerException>(
            () => cursor.ObserveCompletionAsync().AsTask());
        Assert.Equal("22012", sqlError.SqlState);
        Assert.Equal(TransactionStatus.FailedTransaction, sqlError.TransactionStatus);
        Assert.Null(sqlError.InnerException);

        var failure = new IOException("A distinct failure after the recovered boundary.");
        wire.Session.Abort(failure);
        var sessionError = await Assert.ThrowsAsync<IOException>(
            () => wire.Session.Completion.WaitAsync(TestTimeout, Token));
        Assert.Same(failure, sessionError);
    }

    private static BackendMessage Decode(byte[] bytes)
    {
        var input = new ReadOnlySequence<byte>(bytes);
        Assert.True(BackendMessageReader.TryRead(ref input, out var message));
        Assert.True(input.IsEmpty);
        return message;
    }

    private sealed class NoReaderOwner : IResultExecutionOwner
    {
        public ValueTask EndReaderAsync(bool discard)
            => throw new InvalidOperationException("No reader movement is used by this control-publication test.");
    }
}
