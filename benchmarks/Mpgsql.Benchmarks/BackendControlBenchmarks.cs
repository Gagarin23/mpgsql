#if PROTOCOL_BASELINE
extern alias baseline;
using OriginalReader = baseline::Mpgsql.Protocol.BackendMessageReader;
#endif
using System.Buffers;
using BenchmarkDotNet.Attributes;
using Mpgsql.Protocol;

namespace Mpgsql.Benchmarks;

[MemoryDiagnoser, SimpleJob(1, 3, 8), IterationTime(150)]
public class BackendControlBenchmarks
{

    private ReadOnlySequence<byte> _packet;
    [Params(false, true)]
    public bool ReadyForQuery { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _packet = new ReadOnlySequence<byte>
        (
            ReadyForQuery
                ? [(byte)'Z', 0, 0, 0, 5, (byte)'I']
                : [(byte)'1', 0, 0, 0, 4]
        );
#if PROTOCOL_BASELINE
        if (Original() != Current())
        {
            throw new InvalidOperationException("Control packet classification differs from the baseline.");
        }
#else
        Current();
#endif
    }

#if PROTOCOL_BASELINE
    [Benchmark(Baseline = true)]
    public int Original()
    {
        var input = _packet;
        if (!OriginalReader.TryRead
            (
                ref input,
                out var message
            ))
        {
            throw new InvalidOperationException();
        }
        return (int)message.Kind + (int)message.Payload.Length + (int)input.Length;
    }
#endif

    [Benchmark]
    public int Current()
    {
        var input = _packet;
        if (!BackendMessageReader.TryRead
            (
                ref input,
                out var message
            ))
        {
            throw new InvalidOperationException();
        }
        return (int)message.Kind + (int)message.Payload.Length + (int)input.Length;
    }
}