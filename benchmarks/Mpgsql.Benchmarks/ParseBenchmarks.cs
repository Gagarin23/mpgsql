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
public class ParseBenchmarks
{
    [Params(32, 4096)]
    public int QueryLength { get; set; }

    private string _query = "";
    private readonly uint[] _parameterTypes = [23, 25];
    private ParseMessage _message;
    private byte[] _buffer = [];
#if PROTOCOL_BASELINE
    private OriginalFrontend _original;
#endif

    [GlobalSetup]
    public void Setup()
    {
        const string prefix = "select $1, $2 -- ";
        _query = prefix + new string('x', QueryLength - prefix.Length);
        _message = FrontendMessage.Parse(_query, "stmt", _parameterTypes);
        _buffer = new byte[_message.GetByteCount()];
#if PROTOCOL_BASELINE
        _original = OriginalFrontend.Parse(_query, "stmt", _parameterTypes);
        PacketVerification.Check(in _message, _original);
#else
        PacketVerification.Check(in _message);
#endif
    }

#if PROTOCOL_BASELINE
    [Benchmark(Baseline = true), BenchmarkCategory("Prepared")]
    public int OriginalPrepared()
    {
        int size = _original.GetByteCount();
        return _original.Write(_buffer.AsSpan(0, size)) + _buffer[size - 1];
    }

    [Benchmark(Baseline = true), BenchmarkCategory("PerRequest")]
    public int OriginalPerRequest()
    {
        var message = OriginalFrontend.Parse(_query, "stmt", _parameterTypes);
        int size = message.GetByteCount();
        return message.Write(_buffer.AsSpan(0, size)) + _buffer[size - 1];
    }
#endif

    [Benchmark, BenchmarkCategory("Prepared")]
    public int TypedPrepared()
    {
        int size = _message.GetByteCount();
        return _message.Write(_buffer.AsSpan(0, size)) + _buffer[size - 1];
    }

    [Benchmark, BenchmarkCategory("PerRequest")]
    public int TypedPerRequest()
    {
        var message = FrontendMessage.Parse(_query, "stmt", _parameterTypes);
        int size = message.GetByteCount();
        return message.Write(_buffer.AsSpan(0, size)) + _buffer[size - 1];
    }
}
