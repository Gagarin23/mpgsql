using Mpgsql.Protocol;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class ResponsePrefixTests
{
    [Fact]
    public async Task OrdinaryQueriesBeforeAndAfterPreparedOperationsKeepEveryIndex()
    {
        await using var wire = new ScriptedSession();
        var statement = wire.Session.CreatePreparedStatement("select $1", new uint[] { 20 });
        await using var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        var sends = new List<Task>();
        var responses = new List<byte[]>();
        for (int i = 0; i < 8; i++)
        {
            sends.Add(batch.SendQueryAsync("select ordinary").AsTask());
            responses.Add(Query(i + 1));
        }
        sends.Add(batch.SendPrepareAsync(statement).AsTask());
        sends.Add(batch.SendQueryAsync(statement, new[] { MpgsqlParameter.Int64(99) }).AsTask());
        responses.Add(Packet('1'));
        responses.Add(Join(Packet('2'), Description(20), Row(Int64(99)), Command()));
        for (int i = 0; i < 4; i++)
        {
            sends.Add(batch.SendQueryAsync("select following").AsTask());
            responses.Add(Query(201 + i));
        }
        sends.Add(batch.SendCloseAsync(statement).AsTask());
        sends.Add(batch.SendSyncAsync().AsTask());
        responses.Add(Packet('3'));
        responses.Add(Ready());
        await Task.WhenAll(sends).WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        await wire.ReadOutputAsync();
        await wire.WriteAsync(Join([.. responses]), 1);
        await using var reader = await batch.ReadResultsAsync();
        for (int i = 0; i < 13; i++)
        {
            Assert.Equal(i, reader.QueryIndex);
            Assert.True(await reader.ReadAsync());
            Assert.Equal(i < 8 ? i + 1 : i == 8 ? 99 : 201 + i - 9, reader.GetInt64(0));
            Assert.False(await reader.ReadAsync());
            Assert.Equal(i < 12, await reader.NextResultAsync());
        }
        await batch.Completion.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.True(statement.Prepared.IsCompletedSuccessfully);
        Assert.Equal(0, wire.Session.TrackedStatementCount);
        await FollowingQueryAsync(wire);
    }

    [Fact]
    public async Task PublicationAfterConsumedAdministrativeResponsesKeepsTheNextQueryIndex()
    {
        await using var wire = new ScriptedSession();
        var statement = wire.Session.CreatePreparedStatement("select $1", new uint[] { 20 });
        await using var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await batch.SendPrepareAsync(statement);
        await batch.SendQueryAsync("select first");
        await wire.ReadOutputAsync();
        await wire.WriteAsync(Join(Packet('1'), Query(1)), 1);
        await using var reader = await batch.ReadResultsAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt64(0));
        Assert.False(await reader.ReadAsync());
        var next = reader.NextResultAsync().AsTask();
        Assert.False(next.IsCompleted);

        var sends = new[] { batch.SendQueryAsync("select second").AsTask(),
            batch.SendQueryAsync("select third").AsTask(), batch.SendCloseAsync(statement).AsTask(),
            batch.SendSyncAsync().AsTask() };
        await Task.WhenAll(sends).WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        await wire.ReadOutputAsync();
        await wire.WriteAsync(Join(Query(2), Query(3), Packet('3'), Ready()), 1);
        Assert.True(await next.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
        for (int i = 1; i < 3; i++)
        {
            Assert.Equal(i, reader.QueryIndex);
            Assert.True(await reader.ReadAsync());
            Assert.Equal(i + 1, reader.GetInt64(0));
            Assert.False(await reader.ReadAsync());
            Assert.Equal(i < 2, await reader.NextResultAsync());
        }
        await batch.Completion.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(0, wire.Session.TrackedStatementCount);
        await FollowingQueryAsync(wire);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(15)]
    public async Task ErrorInOrdinarySequenceKeepsItsIndexAndFailsSkippedPreparation(int failedIndex)
    {
        await using var wire = new ScriptedSession();
        var statement = wire.Session.CreatePreparedStatement("select $1", new uint[] { 20 });
        await using var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        var sends = new List<Task>();
        for (int i = 0; i < 16; i++) sends.Add(batch.SendQueryAsync("select ordinary").AsTask());
        sends.Add(batch.SendPrepareAsync(statement).AsTask());
        sends.Add(batch.SendSyncAsync().AsTask());
        await Task.WhenAll(sends).WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        await wire.ReadOutputAsync();
        for (int i = 0; i < failedIndex; i++) await wire.WriteAsync(Query(i));
        await wire.WriteAsync(Error());
        Assert.False(batch.Completion.IsCompleted);
        Assert.False(statement.Prepared.IsCompleted);
        await wire.WriteAsync(Ready());
        var error = await Assert.ThrowsAsync<MpgsqlServerException>(() => batch.Completion.WaitAsync(TestTimeout,
            TestContext.Current.CancellationToken));
        Assert.Equal(failedIndex, error.QueryIndex);
        Assert.Equal(TransactionStatus.Idle, error.TransactionStatus);
        Assert.Same(error, await Assert.ThrowsAsync<MpgsqlServerException>(() => statement.Prepared));
        await Assert.ThrowsAsync<MpgsqlServerException>(() => batch.ReadResultsAsync().AsTask());
        Assert.Equal(0, wire.Session.TrackedStatementCount);
        await FollowingQueryAsync(wire);
    }

    private static async Task FollowingQueryAsync(ScriptedSession wire)
    {
        Assert.Equal(0, wire.Session.BufferedRowBytes);
        Assert.True(wire.Session.IsIdleAndHealthy);
        await using var batch = wire.Session.CreateBatch(TestContext.Current.CancellationToken);
        await batch.SendQueryAsync("select following");
        await batch.SendSyncAsync();
        await wire.ReadOutputAsync();
        await wire.WriteAsync(Join(Query(777), Ready()));
        await using var reader = await batch.ReadResultsAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(777, reader.GetInt64(0));
        Assert.False(await reader.NextResultAsync());
        await batch.Completion.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(0, wire.Session.BufferedRowBytes);
        Assert.True(wire.Session.IsIdleAndHealthy);
    }
}
