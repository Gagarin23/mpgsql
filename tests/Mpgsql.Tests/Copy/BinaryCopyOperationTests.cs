using Mpgsql.Copy;
using Mpgsql.Protocol;
using Mpgsql.Tests.Protocol;

namespace Mpgsql.Tests.Copy;

public class BinaryCopyOperationTests
{
    private static BackendMessage Ready => TestWire.Read("5a 00000005 49");
    private static BackendMessage Command => TestWire.Read("43 0000000b 434f5059203100");
    private static BackendMessage Response(bool import)
    {
        return TestWire.Read((import ? "47" : "48") + " 00000009 01 0001 0001");
    }

    [Theory, InlineData(false), InlineData(true)]
    public void ImportCompletesAtReadyAndRequiresANewExtendedSync(bool extended)
    {
        var copy = new BinaryCopyOperation(Response(true),
            extended);
        Assert.True(copy.CanSendData);
        Assert.Equal(1,
            copy.ColumnCount);
        copy.CopyDoneSent();
        Assert.False(copy.CanSendData);
        Assert.Equal(extended,
            copy.RequiresSync);
        copy.Accept(Command);
        Assert.False(copy.IsCompleted);
        Assert.Equal(1ul,
            copy.RowsCopied);
        Assert.Equal(extended,
            copy.RequiresSync);
        if (extended)
        {
            Assert.Throws<InvalidDataException>(() => copy.Accept(Ready));
            copy.SyncSent();
        }
        copy.Accept(Ready);
        Assert.True(copy.IsCompleted);
        Assert.Equal(TransactionStatus.Idle,
            copy.TransactionStatus);
    }

    [Fact]
    public void ExportHasASeparateDataPhaseAndRoutesNoticesIndependently()
    {
        var copy = new BinaryCopyOperation(Response(false));
        Assert.False(copy.Accept(TestWire.Read("4e 00000009 4d6f6b00 00")));
        Assert.True(copy.Accept(TestWire.Read("64 00000005 aa")));
        Assert.Throws<InvalidDataException>(() => copy.Accept(Command));
        copy.Accept(TestWire.Read("63 00000004"));
        copy.Accept(Command);
        copy.Accept(Ready);
        Assert.True(copy.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => copy.Accept(Command));
    }

    [Theory, InlineData(false), InlineData(true)]
    public void CopyFailRecoversOnlyAtReady(bool extended)
    {
        var copy = new BinaryCopyOperation(Response(true),
            extended);
        copy.CopyFailSent();
        Assert.Throws<InvalidDataException>(() => copy.Accept(Command));
        var error = TestWire.Read("45 0000000c 43353730313400 00");
        copy.Accept(error);
        Assert.Equal("57014",
            copy.Error!.Value.SqlState);
        Assert.Null(copy.RowsCopied);
        if (extended)
        {
            copy.SyncSent();
        }
        copy.Accept(Ready);
        Assert.True(copy.IsCompleted);
    }

    [Fact]
    public void ServerCanFailImportBeforeTheClientSendsDone()
    {
        var copy = new BinaryCopyOperation(Response(true),
            true);
        copy.Accept(TestWire.Read("45 0000000c 43323250303300 00"));
        Assert.False(copy.CanSendData);
        Assert.True(copy.RequiresSync);
        Assert.Throws<InvalidOperationException>(() => copy.CopyDoneSent());
        Assert.Throws<InvalidDataException>(() => copy.Accept(Ready));
        copy.SyncSent();
        copy.Accept(Ready);
    }

    [Fact]
    public void ExtendedExportCanSyncImmediatelyAfterCopyDone()
    {
        var copy = new BinaryCopyOperation(Response(false),
            true);
        Assert.False(copy.RequiresSync);
        copy.Accept(TestWire.Read("63 00000004"));
        Assert.True(copy.RequiresSync);
        copy.SyncSent();
        Assert.False(copy.RequiresSync);
        copy.Accept(Command);
        copy.Accept(Ready);
        Assert.True(copy.IsCompleted);
    }

    [Fact]
    public void ExtendedExportCanUseASyncQueuedBeforeReadingData()
    {
        var copy = new BinaryCopyOperation(Response(false),
            true);
        copy.SyncSent(); // COPY OUT does not ignore a Sync queued after Execute.
        copy.Accept(TestWire.Read("64 00000005 aa"));
        copy.Accept(TestWire.Read("63 00000004"));
        Assert.False(copy.RequiresSync);
        copy.Accept(Command);
        copy.Accept(Ready);
        Assert.True(copy.IsCompleted);
    }

    [Fact]
    public void RejectsTextCopyAndSyncDuringTheCopyDataPhase()
    {
        Assert.Throws<NotSupportedException>(() => new BinaryCopyOperation(TestWire.Read("47 00000009 00 0001 0000")));
        Assert.Throws<ArgumentException>(() => new BinaryCopyOperation(TestWire.Read("57 00000009 01 0001 0001")));
        Assert.Throws<InvalidDataException>(() => new BinaryCopyOperation(TestWire.Read("47 00000009 01 0001 0000")));
        var copy = new BinaryCopyOperation(Response(true),
            true);
        Assert.Throws<InvalidOperationException>(() => copy.SyncSent());
        Assert.Throws<InvalidDataException>(() => copy.Accept(Ready));
    }
}