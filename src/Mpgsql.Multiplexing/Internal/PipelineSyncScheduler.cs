using System.Diagnostics;
using Mpgsql.Internal;

namespace Mpgsql.Multiplexing.Internal;

// Present only on DataSource transports that explicitly opt into shared Sync boundaries.
// One timer per transport measures from the first admission, never from the latest arrival.
internal sealed class PipelineSyncScheduler : IDisposable
{
    private readonly long _delayTicks;
    private readonly Lock _gate = new Lock();
    private readonly MpgsqlMessageSession _session;
    private readonly int _size;
    private readonly Timer _timer;
    private int _count;
    private SharedSyncGroup? _current;
    private long _deadline;
    private bool _disposed;

    internal PipelineSyncScheduler(MpgsqlMessageSession session, int size,
        TimeSpan delay)
    {
        _session = session;
        _size = size;
        _delayTicks = (long)Math.Ceiling(delay.TotalSeconds * Stopwatch.Frequency);
        _timer = new Timer(static state => ((PipelineSyncScheduler)state!).Tick(), this,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _current = null;
            _timer.Dispose();
        }
    }

    internal (OutboundWork Work, SharedSyncGroup Group) Enqueue(MpgsqlQueryBatch batch, QueryDefinition query)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // An error already received belongs to the old segment. A new independent request
            // starts after its Sync; it is never replayed or silently inserted into recovery.
            if (_current?.HasError == true)
            {
                Close();
            }
            var first = _current is null;
            var group = _current ?? _session.CreateSharedSyncBatch().SyncGroup;
            var started = Stopwatch.GetTimestamp();
            OutboundWork work;
            try { work = _session.SendGroupedExecution(batch, group.Boundary, query); }
            catch
            {
                if (first)
                {
                    group.Boundary.CompleteIfUnpublished();
                }
                throw;
            }
            if (first)
            {
                _current = group;
                _deadline = started + _delayTicks;
                _count = 0;
            }
            _count++;
            // After input admission, always return its work, even if Sync admission fails:
            // the producer must still wait for the encoder to release borrowed parameters.
            try
            {
                if (_count >= _size)
                {
                    Close();
                }
                else if (first)
                {
                    Arm();
                }
            }
            catch (Exception error) { _session.Abort(error); }
            return (work, group);
        }
    }

    private void Arm()
    {
        var remaining = Math.Max(1, _deadline - Stopwatch.GetTimestamp());
        var delay = TimeSpan.FromMilliseconds(Math.Max(1, Math.Ceiling(remaining * 1000d / Stopwatch.Frequency)));
        _timer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private void Close()
    {
        var group = _current!;
        _current = null;
        _count = 0;
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        try { _session.SendSharedSync(group); }
        catch (Exception error) { _session.Abort(error); }
    }

    private void Tick()
    {
        lock (_gate)
        {
            if (_disposed || _current is null)
            {
                return;
            }
            try
            {
                // A callback already queued for an earlier group must not close a new group early.
                if (Stopwatch.GetTimestamp() < _deadline)
                {
                    Arm();
                }
                else
                {
                    Close();
                }
            }
            catch (Exception error) { _session.Abort(error); }
        }
    }
}