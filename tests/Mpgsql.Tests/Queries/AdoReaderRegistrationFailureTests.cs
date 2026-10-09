using System.IO.Pipelines;
using System.Reflection;
using static Mpgsql.Tests.Queries.ScriptedSession;

namespace Mpgsql.Tests.Queries;

public sealed class AdoReaderRegistrationFailureTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TransportFailureBeforeReaderRegistrationKeepsItsOriginalIdentity()
    {
        await using var wire = new RegistrationWire();
        var batch = wire.Session.CreateBatch(Token);
        var failure = new IOException("Original transport failure before reader registration.");
        wire.Session.Abort(failure);
        var operationError = await Assert.ThrowsAsync<IOException>(
            () => batch.ReadResultsAsync().AsTask().WaitAsync(TestTimeout, Token));
        var sessionError = await Assert.ThrowsAsync<IOException>(
            () => wire.Session.Completion.WaitAsync(TestTimeout, Token));
        Assert.Same(failure, operationError);
        Assert.Same(failure, sessionError);
        Assert.False(wire.Session.IsHealthy);
    }

    [Fact]
    public async Task HealthyCompletedDiscardStillReportsReaderDisposal()
    {
        await using var wire = new RegistrationWire();
        var batch = wire.Session.CreateBatch(Token);
        batch.CompleteIfUnpublished();
        batch.BeginDiscard();
        var error = await Assert.ThrowsAsync<ObjectDisposedException>(
            () => batch.ReadResultsAsync().AsTask().WaitAsync(TestTimeout, Token));
        Assert.Equal(nameof(MpgsqlResultReader), error.ObjectName);
        Assert.True(wire.Session.IsHealthy);
        await batch.DisposeAsync();
        Assert.True(wire.Session.IsIdleAndHealthy);
    }

    private sealed class RegistrationWire : IAsyncDisposable
    {
        private readonly Pipe _incoming = new(new PipeOptions(useSynchronizationContext: false));
        private readonly Pipe _outgoing = new(new PipeOptions(useSynchronizationContext: false));
        internal MpgsqlMessageSession Session { get; }
        internal RegistrationWire()
        {
            var constructor = typeof(MpgsqlMessageSession).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(candidate => candidate.GetParameters().Length == 5);
            Session = (MpgsqlMessageSession)constructor.Invoke([_incoming.Reader, _outgoing.Writer, Token, null, true]);
        }
        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync().AsTask().WaitAsync(TestTimeout, Token);
            await _incoming.Writer.CompleteAsync();
            await _outgoing.Reader.CompleteAsync();
        }
    }
}
