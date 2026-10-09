using Mpgsql.Internal;
using Mpgsql.Protocol;

namespace Mpgsql;

public sealed partial class MpgsqlMessageSession
{
    private int _pendingAdoWrites;
    private volatile bool _adoInlineWriting;
    private TaskCompletionSource? _adoWriterIdle;
    private volatile bool _backgroundOutputActive;
    private TaskCompletionSource? _backgroundOutputIdle;

    // Called by the sole background writer within its existing publication gate.
    // This also covers general work's IO tail during an external ADO handoff.
    private void BeginBackgroundOutput()
    {
        if (_backgroundOutputActive)
            return;
        _backgroundOutputIdle = null;
        _backgroundOutputActive = true;
    }

    private void EndBackgroundOutput()
    {
        if (_adoSession)
        {
            lock (_gate)
            {
                _adoIdleSince = Environment.TickCount64;
                _backgroundOutputActive = false;
            }
        }
        else
            _backgroundOutputActive = false;
        Volatile.Read(ref _backgroundOutputIdle)?.TrySetResult();
    }

    // Used only during the rare external-factory handoff. The writer clears the
    // flag without a new normal-path cleanup lock; rechecking closes a missed wake.
    private Task WaitForBackgroundOutputIdleAsync()
    {
        lock (_gate)
        {
            if (!_backgroundOutputActive)
                return Task.CompletedTask;
            var idle = _backgroundOutputIdle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_backgroundOutputActive)
                idle.TrySetResult();
            return idle.Task;
        }
    }

    // Called under _gate, before either writer may observe the admitted work.
    private void AdmitWriterWork(OutboundWork work)
    {
        if (work.ResponseGroup is MpgsqlQueryBatch batch && _collecting is null)
        {
            _collecting = batch;
            _responses.Enqueue(batch);
        }
        AddPendingWriterWork(work);
    }

    // Explicit Sync helpers retain their separate response/collecting transitions.
    private void AddPendingWriterWork(OutboundWork work)
    {
        work.Group.AddWrite(work);
        if (_adoSession)
        {
            StopAdoIdleOwner();
            work.AdoWriterTracked = true;
            _pendingAdoWrites++;
        }
    }

    private void ReleaseWriterWork(OutboundWork work, bool inline = false)
    {
        try { work.Group.RemoveWrite(work); }
        finally
        {
            if (work.AdoWriterTracked)
            {
                lock (_gate)
                {
                    work.AdoWriterTracked = false;
                    _pendingAdoWrites--;
                    _adoIdleSince = Environment.TickCount64;
                    if (inline)
                    {
                        // Encoding and Flush have stopped touching output before wake-up.
                        _adoInlineWriting = false;
                        _adoWriterIdle?.TrySetResult();
                        _adoWriterIdle = null;
                    }
                }
            }
        }
    }

    private Task WaitForInlineWriterAsync()
    {
        lock (_gate)
            return _adoInlineWriting
                ? (_adoWriterIdle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task
                : Task.CompletedTask;
    }

    // Every prevalidated packet plus Sync fits below the normal writer chunk bound;
    // a practical small batch (including 16 queries) can use the same exclusive path.
    // Execute on the caller until Flush first suspends, then let direct input run
    // concurrently with delivery. Admin/large-group paths keep the writer FIFO.
    private async Task WriteSmallAdoAsync(OutboundWork work)
    {
        try
        {
            await WaitForAdoIdleOwnerAsync().ConfigureAwait(false);
            if (!work.TryStart())
                return;
            lock (_gate) { ThrowIfStopped(); }
            long bytes = 0;
            var operations = 0;
            try
            {
                if (!work.CancellationRequested
                    && !WriteQueryGroup(work, ref bytes, ref operations))
                    throw new InvalidOperationException("An inline query exceeded its admitted size.");
            }
            catch (OperationCanceledException) when (work.CancellationRequested
                                                     && !_lifetime.IsCancellationRequested)
            {
                // Inputs are no longer being encoded. Published commands still need Sync.
            }
            FrontendMessage.Sync().Write(_output);
            work.Group.SealPublished();
            work.Published();
            var flush = await _output.FlushAsync(_lifetime.Token).ConfigureAwait(false);
            if (flush.IsCanceled)
                throw new OperationCanceledException(_lifetime.Token);
            if (flush.IsCompleted)
                throw new IOException("The output transport stopped reading.");
            work.Complete();
        }
        catch (Exception error) { work.Complete(Fail(error)); }
        finally { ReleaseWriterWork(work, inline: true); }
    }
}
