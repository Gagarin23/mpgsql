using System.Buffers;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class AdoDirectFrameTests
{
    [Theory]
    [InlineData("44800000000001")]
    [InlineData("44040000010001")]
    [InlineData("44000000050001")]
    [InlineData("44000000060001")]
    [InlineData("44000000060000")]
    [InlineData("440000000A0001FFFFFFFE")]
    [InlineData("440000000A000100000001")]
    [InlineData("440000000B0001FFFFFFFF00")]
    public async Task BufferedDataRowRejectsInvalidFrameAndFieldBounds(string hex)
    {
        var token = TestContext.Current.CancellationToken;
        await using var wire = new ScriptedSession();
        await using var source = new MpgsqlDataSource(_ => ValueTask.FromResult(wire.Session), (_, _) => ValueTask.CompletedTask);
        await using var connection = await source.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand("select malformed row");
        var opening = command.ExecuteReaderAsync(token);
        var sent = new List<byte>();
        do { sent.AddRange(await wire.ReadOutputAsync()); }
        while (!Tags([.. sent]).Contains('S'));
        var producing = wire.WriteAsync(Join(Begin(20), Convert.FromHexString(hex)));
        var error = await Assert.ThrowsAsync<MpgsqlException>(() => opening.WaitAsync(TestTimeout, token));
        Assert.IsType<InvalidDataException>(error.InnerException);
        // Transport failure can fault the peer's pending flush too. Observe both
        // tasks; neither may keep the borrowed input alive after abort.
        _ = await Record.ExceptionAsync(() => producing.WaitAsync(TestTimeout, token));
        Assert.False(wire.Session.IsHealthy);
        Assert.Equal(0, wire.Session.BufferedRowBytes);
        Assert.Equal(0, wire.Session.CopiedRowBytes);
    }
}
