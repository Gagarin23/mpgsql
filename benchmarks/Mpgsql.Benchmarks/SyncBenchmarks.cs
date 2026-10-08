#if PROTOCOL_BASELINE
extern alias baseline;
using OriginalFrontend = baseline::Mpgsql.Protocol.FrontendMessage;
#endif
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Mpgsql.Protocol;

namespace Mpgsql.Benchmarks;

[MemoryDiagnoser, SimpleJob(1, 3, 8), IterationTime(150), GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory), CategoriesColumn]
public class SyncBenchmarks
{
    private readonly byte[] _buffer = new byte[5];
    private EmptyMessage _message;
#if PROTOCOL_BASELINE
    private OriginalFrontend _original;
#endif

    [GlobalSetup]
    public void Setup()
    {
        _message = FrontendMessage.Sync();
#if PROTOCOL_BASELINE
        _original = OriginalFrontend.Sync();
        PacketVerification.Check(in _message,
            _original);
#else
        PacketVerification.Check(in _message);
#endif
    }

    [Benchmark, BenchmarkCategory("Prepared")]
    public int TypedPrepared()
    {
        var size = _message.GetByteCount();
        return _message.Write(_buffer.AsSpan(0,
            size)) + _buffer[size - 1];
    }

    [Benchmark, BenchmarkCategory("PerRequest")]
    public int TypedPerRequest()
    {
        var message = FrontendMessage.Sync();
        var size = message.GetByteCount();
        return message.Write(_buffer.AsSpan(0,
            size)) + _buffer[size - 1];
    }

#if PROTOCOL_BASELINE
    [Benchmark(Baseline = true), BenchmarkCategory("Prepared")]
    public int OriginalPrepared()
    {
        var size = _original.GetByteCount();
        return _original.Write(_buffer.AsSpan(0,
            size)) + _buffer[size - 1];
    }

    [Benchmark(Baseline = true), BenchmarkCategory("PerRequest")]
    public int OriginalPerRequest()
    {
        var message = OriginalFrontend.Sync();
        var size = message.GetByteCount();
        return message.Write(_buffer.AsSpan(0,
            size)) + _buffer[size - 1];
    }
#endif
}