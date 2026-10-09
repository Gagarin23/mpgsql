using Mpgsql.Protocol;

namespace Mpgsql;

public sealed partial class MpgsqlMessageSession
{
    private const int AdoIdleGraceMilliseconds = 20;
    private Timer? _adoIdleTimer;
    private TaskCompletionSource? _adoIdleOwner;
    private CancellationTokenSource? _adoIdleCancellation;
    private long _adoIdleSince;
    private bool _adoIdlePartialFrame;

    // One session timer observes genuine idle periods. Tight operation loops never
    // activate this control-only reader; ordinary rows retain their pull owner.
    private void InstallAdoIdleMonitor()
    {
        _adoIdleSince = Environment.TickCount64;
        _adoIdleTimer = new Timer(static state => ((MpgsqlMessageSession)state!).OnAdoIdleTimer(),
            this, AdoIdleGraceMilliseconds, AdoIdleGraceMilliseconds);
    }

    private void OnAdoIdleTimer()
    {
        TaskCompletionSource owner;
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            if (_disposed != 0 || _failure is not null || _lifetime.IsCancellationRequested
                || _adoIdleOwner is not null || _adoCursor is not null || _batches.Count != 0 || _responses.Count != 0
                || _collecting is not null || _pendingAdoWrites != 0 || _adoInlineWriting
                || _backgroundOutputActive || _adoDraining || _adoHasPendingInput || _adoReadOutstanding
                || Environment.TickCount64 - _adoIdleSince < AdoIdleGraceMilliseconds)
                return;
            owner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellation = new CancellationTokenSource();
            _adoIdleCancellation = cancellation;
            Volatile.Write(ref _adoIdleOwner, owner);
        }
        _ = ReadAdoIdleAsync(owner, cancellation);
    }

    // Called only in existing lifecycle/admission gates. Cancelling this private
    // token cannot leave CancelPendingRead set for the following operation.
    private void StopAdoIdleOwner()
    {
        _adoIdleSince = Environment.TickCount64;
        _adoIdleCancellation?.Cancel();
    }

    private Task WaitForAdoIdleOwnerAsync() => Volatile.Read(ref _adoIdleOwner)?.Task ?? Task.CompletedTask;

    private async ValueTask StopAdoIdleMonitorAsync()
    {
        Timer? timer;
        Task owner;
        lock (_gate)
        {
            timer = _adoIdleTimer;
            _adoIdleTimer = null;
            StopAdoIdleOwner();
            owner = WaitForAdoIdleOwnerAsync();
        }
        if (timer is not null)
            await timer.DisposeAsync().ConfigureAwait(false);
        await owner.ConfigureAwait(false);
    }

    private async Task ReadAdoIdleAsync(TaskCompletionSource owner, CancellationTokenSource cancellation)
    {
        try
        {
            while (true)
            {
                System.IO.Pipelines.ReadResult read;
                try { read = await _input.ReadAsync(cancellation.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested && !_lifetime.IsCancellationRequested)
                {
                    // Cancellation can win before a pipe delivers bytes it already
                    // retained. This owner observes that cold buffered result once,
                    // without starting another IO read, before publication can resume.
                    if (!_input.TryRead(out read))
                        break;
                }
                var input = read.Buffer;
                try
                {
                    // Even a stop wake can carry completed control frames. Observe
                    // their owned diagnostics before handing input to a publisher.
                    while (!input.IsEmpty && _frames.TryRead(ref input, false, out var message, out var memory, out _))
                    {
                        try { AcceptAdoIdleMessage(message); }
                        finally { memory?.Dispose(); }
                    }
                    _lifetime.Token.ThrowIfCancellationRequested();
                    if (read.IsCompleted)
                        throw new EndOfStreamException(_frames.HasPartialFrame
                            ? "Truncated backend frame." : "The backend closed the transport.");
                }
                finally
                {
                    // An incomplete control frame remains in the common assembler;
                    // its idle attribution must survive a subsequent query admission.
                    _adoIdlePartialFrame = _frames.HasPartialFrame;
                    var stopping = cancellation.IsCancellationRequested || _lifetime.IsCancellationRequested;
                    _input.AdvanceTo(input.Start, stopping ? input.Start : input.End);
                }
                if (cancellation.IsCancellationRequested)
                    break;
            }
        }
        catch (Exception error) { Fail(error); }
        finally
        {
            // The single read has been awaited and advanced before its cancellation
            // source or ownership signal can be released to a waiting writer/reader.
            lock (_gate)
            {
                cancellation.Dispose();
                _adoIdleCancellation = null;
                _adoIdleSince = Environment.TickCount64;
                Volatile.Write(ref _adoIdleOwner, null);
            }
            owner.TrySetResult();
        }
    }

    private void AcceptAdoIdleMessage(BackendMessage message)
    {
        if (message.IsAsynchronous)
        {
            RouteAsynchronous(message);
            return;
        }
        if (message.Kind == BackendMessageKind.ErrorResponse)
        {
            DiagnosticMessage diagnostics;
            lock (_gate) { _unrecoveredError = diagnostics = message.GetDiagnostics(); }
            // Idle diagnostics have no query index or inferred transaction status,
            // including a fragmented frame finished after an operation was admitted.
            throw new MpgsqlServerException(diagnostics, null, null);
        }
        throw new InvalidDataException($"Unexpected idle backend message: {message.Kind}.");
    }
}
