#if PROTOCOL_BASELINE
extern alias baseline;
using OriginalFrontend = baseline::Mpgsql.Protocol.FrontendMessage;
#endif

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Mpgsql.Protocol;

namespace Mpgsql.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
[IterationTime(150)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class ExecuteBenchmarks
{
    private const string Portal = "portal1";
    private ExecuteMessage _message;
    private readonly byte[] _buffer = new byte[64];
#if PROTOCOL_BASELINE
    private OriginalFrontend _original;
#endif

    [GlobalSetup]
    public void Setup()
    {
        _message = FrontendMessage.Execute(Portal,
            100);
#if PROTOCOL_BASELINE
        _original = OriginalFrontend.Execute(Portal,
            100);
        PacketVerification.Check(in _message,
            _original);
#else
        PacketVerification.Check(in _message);
#endif
    }

#if PROTOCOL_BASELINE
    [Benchmark(Baseline = true), BenchmarkCategory("Prepared")]
    public int OriginalPrepared()
    {
        int size = _original.GetByteCount();
        return _original.Write(_buffer.AsSpan(0,
            size)) + _buffer[size - 1];
    }

    [Benchmark(Baseline = true), BenchmarkCategory("PerRequest")]
    public int OriginalPerRequest()
    {
        var message = OriginalFrontend.Execute(Portal,
            100);
        int size = message.GetByteCount();
        return message.Write(_buffer.AsSpan(0,
            size)) + _buffer[size - 1];
    }
#endif

    [Benchmark, BenchmarkCategory("Prepared")]
    public int TypedPrepared()
    {
        int size = _message.GetByteCount();
        return _message.Write(_buffer.AsSpan(0,
            size)) + _buffer[size - 1];
    }

    [Benchmark, BenchmarkCategory("PerRequest")]
    public int TypedPerRequest()
    {
        var message = FrontendMessage.Execute(Portal,
            100);
        int size = message.GetByteCount();
        return message.Write(_buffer.AsSpan(0,
            size)) + _buffer[size - 1];
    }
}