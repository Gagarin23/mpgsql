using System.Buffers;
using Mpgsql.Internal;
using Mpgsql.Protocol;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class AdoQueuedCancellationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ManualCancellationWithoutRequestTokenKeepsQueuedSyncAndSkipsReleasedInputs()
    {
        await using var wire = new ScriptedSession(blockWrites: true);
        await using var preceding = wire.Session.CreateBatch(Token);
        var first = preceding.SendExecution(new QueryDefinition("select $1", new[] { MpgsqlParameterValue.Int64(7) }), null);
        var held = await wire.Outgoing.Reader.ReadAsync(Token).AsTask().WaitAsync(TestTimeout, Token);
        using var input = new BlockingInputMemory();
        // Manual cancellation must work with the uncancellable default request token.
#pragma warning disable xUnit1051
        await using var cancelled = wire.Session.CreateBatch(CancellationToken.None);
#pragma warning restore xUnit1051
        try
        {
            Assert.Equal(Expected("select $1", 7), held.Buffer.ToArray());
            var work = cancelled.SendExecution(default,
            [
                new QueryDefinition("select $1::bigint[]", new[] { MpgsqlParameterValue.Int64Array(input.Memory) }),
                new QueryDefinition("select skipped", default)
            ]);
            cancelled.ProcessCancellation();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.Completion.WaitAsync(TestTimeout, Token));
            Assert.False(error.CancellationToken.CanBeCanceled);
            Assert.Equal(0, input.Reads);
            input.Revoke();
            wire.Outgoing.Reader.AdvanceTo(held.Buffer.End);
            held = default;

            Assert.Equal(SyncBytes(), await wire.ReadOutputAsync());
            await first.Delivery.WaitAsync(TestTimeout, Token);
            await wire.WriteAsync(Join(Begin(20), Command("SELECT 0"), Ready(), Ready()));
            await Task.WhenAll(preceding.Completion, cancelled.Completion).WaitAsync(TestTimeout, Token);
            Assert.Equal(0, input.Reads);
            Assert.True(wire.Session.IsHealthy);

            // The Sync-only cancellation must recover at RFQ and preserve the next execution.
            await using var next = wire.Session.CreateBatch(Token);
            var nextWork = next.SendExecution(new QueryDefinition("select $1", new[] { MpgsqlParameterValue.Int64(9) }), null);
            Assert.Equal(Expected("select $1", 9), await wire.ReadOutputAsync());
            await nextWork.Delivery.WaitAsync(TestTimeout, Token);
            await wire.WriteAsync(Join(Query(9), Ready()));
            await using var reader = await next.ReadResultsAsync().AsTask().WaitAsync(TestTimeout, Token);
            Assert.True(await reader.ReadAsync().AsTask().WaitAsync(TestTimeout, Token));
            Assert.Equal(9L, reader.GetInt64(0));
            Assert.False(await reader.ReadAsync().AsTask().WaitAsync(TestTimeout, Token));
            Assert.False(await reader.NextResultAsync().AsTask().WaitAsync(TestTimeout, Token));
            await next.Completion.WaitAsync(TestTimeout, Token);
            Assert.True(wire.Session.IsHealthy);
            Assert.Equal(0, wire.Session.BufferedRowBytes);
        }
        finally
        {
            if (!held.Buffer.IsEmpty)
                wire.Outgoing.Reader.AdvanceTo(held.Buffer.End);
        }
    }

    private static byte[] Expected(string sql, long value)
    {
        var output = new ArrayBufferWriter<byte>();
        FrontendMessage.Parse(sql, parameterTypes: new uint[] { 20 }).Write(output);
        FrontendMessage.Bind(parameters: new ReadOnlyMemory<byte>?[] { Int64(value) },
            parameterFormats: new[] { FormatCode.Binary }, resultFormats: new[] { FormatCode.Binary }).Write(output);
        FrontendMessage.Describe(StatementOrPortal.Portal).Write(output);
        FrontendMessage.Execute().Write(output);
        FrontendMessage.Sync().Write(output);
        return output.WrittenSpan.ToArray();
    }

    private static byte[] SyncBytes()
    {
        var output = new ArrayBufferWriter<byte>();
        FrontendMessage.Sync().Write(output);
        return output.WrittenSpan.ToArray();
    }
}
