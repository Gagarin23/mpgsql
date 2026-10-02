#if PROTOCOL_BASELINE
extern alias baseline;
using OriginalFrontend = baseline::Mpgsql.Protocol.FrontendMessage;
using OriginalFormat = baseline::Mpgsql.Protocol.FormatCode;
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
public class BindBenchmarks
{
    [Params(1, 16)]
    public int Parameters { get; set; }

    private ReadOnlyMemory<byte>?[] _parameters = [];
    private readonly FormatCode[] _formats = [FormatCode.Binary];
    private BindMessage _message;
    private byte[] _buffer = [];
#if PROTOCOL_BASELINE
    private readonly OriginalFormat[] _originalFormats = [OriginalFormat.Binary];
    private OriginalFrontend _original;
#endif

    [GlobalSetup]
    public void Setup()
    {
        byte[] value =
        [
            .. Enumerable.Range(0,
                64).Select(i => (byte)i)
        ];
        _parameters = new ReadOnlyMemory<byte>?[Parameters];
        for (int i = 0; i < _parameters.Length; i++)
            _parameters[i] = i % 5 == 4 ? null : value;
        _message = FrontendMessage.Bind("portal",
            "stmt",
            _parameters,
            _formats,
            _formats);
        _buffer = new byte[_message.GetByteCount()];
#if PROTOCOL_BASELINE
        _original = OriginalFrontend.Bind("portal",
            "stmt",
            _parameters,
            _originalFormats,
            _originalFormats);
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
        var message = OriginalFrontend.Bind("portal",
            "stmt",
            _parameters,
            _originalFormats,
            _originalFormats);
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
        var message = FrontendMessage.Bind("portal",
            "stmt",
            _parameters,
            _formats,
            _formats);
        int size = message.GetByteCount();
        return message.Write(_buffer.AsSpan(0,
            size)) + _buffer[size - 1];
    }
}