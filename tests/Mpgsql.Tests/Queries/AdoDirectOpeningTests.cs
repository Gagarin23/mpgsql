using System.Buffers;
using System.Data;
using Mpgsql.Protocol;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class AdoDirectOpeningTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory, InlineData(false), InlineData(true)]
    public async Task ConnectionCloseBeforeFirstMetadataWaitsForOpeningRecoveryAndAllowsReuse(bool batchPath)
    {
        await using var wire = new ScriptedSession(lifetime: Token);
        await using var source = new MpgsqlDataSource(_ => ValueTask.FromResult(wire.Session),
            (_, _) => ValueTask.CompletedTask);
        using var connection = await source.OpenConnectionAsync(Token);
        using var command = connection.CreateCommand("select $1");
        command.Parameters.Add(MpgsqlParameter.Int64(7));
        using var batch = connection.CreateBatch();
        var batchCommand = new MpgsqlBatchCommand("select $1");
        batchCommand.Parameters.Add(MpgsqlParameter.Int64(7));
        batch.BatchCommands.Add(batchCommand);
        var parameters = batchPath ? batchCommand.Parameters : command.Parameters;

        // The entry itself returns an operation. Its reader is registered before
        // the first metadata/input wait even though no public reader exists yet.
        var operation = batchPath ? batch.ExecuteReaderValueTaskAsync(cancellationToken: Token)
            : command.ExecuteReaderValueTaskAsync(cancellationToken: Token);
        var opening = operation.AsTask();
        Assert.Equal(Expected(7), await wire.ReadOutputAsync());
        Assert.False(opening.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => parameters[0].Value = 9L);
        var closing = connection.CloseAsync();
        Assert.False(closing.IsCompleted);
        var recovery = wire.WriteAsync(Join(Query(7), Ready()), 1);
        await closing.WaitAsync(TestTimeout, Token);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => opening.WaitAsync(TestTimeout, Token));
        await recovery.WaitAsync(TestTimeout, Token);
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.True(wire.Session.IsIdleAndHealthy);

        parameters[0].Value = 9L;
        await connection.OpenAsync(Token);
        var next = batchPath ? batch.ExecuteReaderValueTaskAsync(cancellationToken: Token)
            : command.ExecuteReaderValueTaskAsync(cancellationToken: Token);
        Assert.Equal(Expected(9), await wire.ReadOutputAsync());
        var writing = wire.WriteAsync(Join(Query(9), Ready()), 3);
        using var reader = await next.AsTask().WaitAsync(TestTimeout, Token);
        Assert.True(reader.HasRows);
        Assert.Throws<InvalidOperationException>(() => reader.GetInt64(0));
        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal(9L, reader.GetInt64(0));
        Assert.False(await reader.ReadAsync(Token));
        Assert.False(await reader.NextResultAsync(Token));
        await writing.WaitAsync(TestTimeout, Token);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    private static byte[] Expected(long value)
    {
        var output = new ArrayBufferWriter<byte>();
        FrontendMessage.Parse("select $1", parameterTypes: new uint[] { 20 }).Write(output);
        FrontendMessage.Bind(parameters: new ReadOnlyMemory<byte>?[] { Int64(value) },
            parameterFormats: new[] { FormatCode.Binary }, resultFormats: new[] { FormatCode.Binary }).Write(output);
        FrontendMessage.Describe(StatementOrPortal.Portal).Write(output);
        FrontendMessage.Execute().Write(output);
        FrontendMessage.Sync().Write(output);
        return output.WrittenSpan.ToArray();
    }
}
