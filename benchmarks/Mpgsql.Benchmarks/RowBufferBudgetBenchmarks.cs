using System.IO.Pipelines;
using BenchmarkDotNet.Attributes;
using Mpgsql.Internal;

namespace Mpgsql.Benchmarks;

// A source-lifetime diagnostic, separate from full-query/Npgsql comparisons.
// A single receiver blocks on capacity, and one consumer releases it.
[MemoryDiagnoser, JsonExporterAttribute.Full, Config(typeof(QueryBenchmarkConfig))]
public class RowBufferBudgetBenchmarks
{
    private readonly RowBufferBudget _budget = new RowBufferBudget(1);
    private readonly Pipe _input = new Pipe();
    private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
    private readonly Pipe _output = new Pipe();
    private MpgsqlQueryBatch _batch = null!;
    private MpgsqlMessageSession _session = null!;
    [Params(false, true)]
    public bool Cancellable { get; set; }
    private CancellationToken Token => Cancellable ? _lifetime.Token : default;

    [GlobalSetup]
    public async Task Setup()
    {
        _session = new MpgsqlMessageSession(_input.Reader, _output.Writer);
        _batch = _session.CreateBatch();
        if (!Uncontended() || !await WaitAndRelease().ConfigureAwait(false) || _budget.Used != 0)
        {
            throw new InvalidOperationException("Row capacity diagnostic verification.");
        }
    }

    [Benchmark]
    public bool Uncontended()
    {
        var result = _budget.ReserveAsync(1, _batch, Token).GetAwaiter().GetResult();
        _budget.Release(1);
        return result;
    }

    [Benchmark]
    public async ValueTask<bool> WaitAndRelease()
    {
        _ = await _budget.ReserveAsync(1, _batch, Token).ConfigureAwait(false);
        var pending = _budget.ReserveAsync(1, _batch, Token);
        _budget.Release(1);
        var result = await pending.ConfigureAwait(false);
        _budget.Release(1);
        return result;
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_budget.Used != 0)
        {
            throw new InvalidOperationException("Capacity diagnostic retains bytes.");
        }
        await _batch.DisposeAsync().ConfigureAwait(false);
        await _session.DisposeAsync().ConfigureAwait(false);
        await _input.Writer.CompleteAsync().ConfigureAwait(false);
        await _output.Reader.CompleteAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }
}