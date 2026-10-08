using System.IO.Pipelines;
using BenchmarkDotNet.Attributes;
using Mpgsql.Internal;

namespace Mpgsql.Benchmarks;

// A source-lifetime diagnostic, separate from full-query/Npgsql comparisons.
// A single receiver blocks on capacity, and one consumer releases it.
[MemoryDiagnoser, JsonExporterAttribute.Full]
[Config(typeof(QueryBenchmarkConfig))]
public class RowBufferBudgetBenchmarks
{
    [Params(false, true)] public bool Cancellable { get; set; }
    private readonly Pipe _input = new();
    private readonly Pipe _output = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly RowBufferBudget _budget = new(1);
    private MpgsqlMessageSession _session = null!;
    private MpgsqlQueryBatch _batch = null!;
    private CancellationToken Token => Cancellable ? _lifetime.Token : default;

    [GlobalSetup]
    public async Task Setup()
    {
        _session = new(_input.Reader, _output.Writer);
        _batch = _session.CreateBatch();
        if (!Uncontended() || !await WaitAndRelease().ConfigureAwait(false) || _budget.Used != 0)
            throw new InvalidOperationException("Row capacity diagnostic verification.");
    }

    [Benchmark]
    public bool Uncontended()
    {
        bool result = _budget.ReserveAsync(1, _batch, Token).GetAwaiter().GetResult();
        _budget.Release(1);
        return result;
    }

    [Benchmark]
    public async ValueTask<bool> WaitAndRelease()
    {
        _ = await _budget.ReserveAsync(1, _batch, Token).ConfigureAwait(false);
        var pending = _budget.ReserveAsync(1, _batch, Token);
        _budget.Release(1);
        bool result = await pending.ConfigureAwait(false);
        _budget.Release(1);
        return result;
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_budget.Used != 0) throw new InvalidOperationException("Capacity diagnostic retains bytes.");
        await _batch.DisposeAsync().ConfigureAwait(false);
        await _session.DisposeAsync().ConfigureAwait(false);
        await _input.Writer.CompleteAsync().ConfigureAwait(false);
        await _output.Reader.CompleteAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
