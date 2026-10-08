using System.IO.Pipelines;

namespace Mpgsql.Benchmarks.Queries;

internal sealed class CountingPipeWriter(PipeWriter inner) : PipeWriter
{
    private long _bytes;
    private long _flushes;
    internal long Flushes => Interlocked.Read(ref _flushes);
    internal long Bytes => Interlocked.Read(ref _bytes);
    public override void Advance(int bytes)
    {
        Interlocked.Add(ref _bytes, bytes);
        inner.Advance(bytes);
    }
    public override Memory<byte> GetMemory(int sizeHint = 0)
    {
        return inner.GetMemory(sizeHint);
    }
    public override Span<byte> GetSpan(int sizeHint = 0)
    {
        return inner.GetSpan(sizeHint);
    }
    public override void CancelPendingFlush()
    {
        inner.CancelPendingFlush();
    }
    public override void Complete(Exception? exception = null)
    {
        inner.Complete(exception);
    }
    public override ValueTask CompleteAsync(Exception? exception = null)
    {
        return inner.CompleteAsync(exception);
    }
    public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _flushes);
        return inner.FlushAsync(cancellationToken);
    }
}