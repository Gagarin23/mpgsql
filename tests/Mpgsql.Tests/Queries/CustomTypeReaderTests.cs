using System.Buffers;
using System.Buffers.Binary;
using System.Data.Common;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class CustomTypeReaderTests
{
    [Fact]
    public async Task DbReaderReadsExplicitCustomOidWithImmutableSourceMapping()
    {
        var token = TestContext.Current.CancellationToken;
        var mapper = new MpgsqlTypeMapper().Register<Identifier>(90001, payload => new Identifier(BinaryPrimitives.ReadInt64BigEndian(payload.ToArray())));
        await using var wire = new ScriptedSession();
        await using DbDataSource source = new MpgsqlDataSource(_ => ValueTask.FromResult(wire.Session), (_, _) => ValueTask.CompletedTask, new MpgsqlDataSourceOptions {TypeMapper = mapper});
        mapper.Register<Identifier>(90001, _ => throw new InvalidOperationException("The source must own its original snapshot."));
        await using var connection = await source.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "select custom";
        var opening = command.ExecuteReaderAsync(token);
        await wire.ReadOutputAsync();
        await wire.WriteAsync(Join(Begin(90001), Row(Int64(42)), Row((byte[]?)null), Command("SELECT 2"), Ready()));
        await using var reader = await opening;
        Assert.True(await reader.ReadAsync(token));
        Assert.Equal(42, reader.GetFieldValue<Identifier>(0).Value);
        Assert.Throws<InvalidCastException>(() => reader.GetFieldValue<long>(0));
        Assert.True(await reader.ReadAsync(token));
        Assert.Null(reader.GetFieldValue<Identifier>(0));
        Assert.False(await reader.NextResultAsync(token));
    }

    private sealed record Identifier(long Value);
}