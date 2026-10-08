#if PROTOCOL_BASELINE
extern alias baseline;
using OriginalFrontend = baseline::Mpgsql.Protocol.FrontendMessage;
using OriginalFormat = baseline::Mpgsql.Protocol.FormatCode;
#endif
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Mpgsql.Protocol;

namespace Mpgsql.Benchmarks;

[MemoryDiagnoser, SimpleJob(1, 3, 8), IterationTime(150), GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory), CategoriesColumn]
public class BindBenchmarks
{
    private readonly FormatCode[] _formats = [FormatCode.Binary];
    private byte[] _buffer = [];
    private BindMessage _message;

    private ReadOnlyMemory<byte>?[] _parameters = [];
    [Params(1, 16)]
    public int Parameters { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        byte[] value =
        [
            .. Enumerable
                .Range
                (
                    0,
                    64
                )
                .Select(i => (byte)i)
        ];
        _parameters = new ReadOnlyMemory<byte>?[Parameters];
        for (var i = 0;
             i < _parameters.Length;
             i++)
        {
            _parameters[i] = i % 5 == 4 ? null : value;
        }
        _message = FrontendMessage.Bind
        (
            "portal",
            "stmt",
            _parameters,
            _formats,
            _formats
        );
        _buffer = new byte[_message.GetByteCount()];
#if PROTOCOL_BASELINE
        _original = OriginalFrontend.Bind
        (
            "portal",
            "stmt",
            _parameters,
            _originalFormats,
            _originalFormats
        );
        PacketVerification.Check
        (
            in _message,
            _original
        );
#else
        PacketVerification.Check(in _message);
#endif
    }

    [Benchmark, BenchmarkCategory("Prepared")]
    public int TypedPrepared()
    {
        var size = _message.GetByteCount();
        return _message.Write
        (
            _buffer.AsSpan
            (
                0,
                size
            )
        ) + _buffer[size - 1];
    }

    [Benchmark, BenchmarkCategory("PerRequest")]
    public int TypedPerRequest()
    {
        var message = FrontendMessage.Bind
        (
            "portal",
            "stmt",
            _parameters,
            _formats,
            _formats
        );
        var size = message.GetByteCount();
        return message.Write
        (
            _buffer.AsSpan
            (
                0,
                size
            )
        ) + _buffer[size - 1];
    }
#if PROTOCOL_BASELINE
    private readonly OriginalFormat[] _originalFormats = [OriginalFormat.Binary];
    private OriginalFrontend _original;
#endif

#if PROTOCOL_BASELINE
    [Benchmark(Baseline = true), BenchmarkCategory("Prepared")]
    public int OriginalPrepared()
    {
        var size = _original.GetByteCount();
        return _original.Write
        (
            _buffer.AsSpan
            (
                0,
                size
            )
        ) + _buffer[size - 1];
    }

    [Benchmark(Baseline = true), BenchmarkCategory("PerRequest")]
    public int OriginalPerRequest()
    {
        var message = OriginalFrontend.Bind
        (
            "portal",
            "stmt",
            _parameters,
            _originalFormats,
            _originalFormats
        );
        var size = message.GetByteCount();
        return message.Write
        (
            _buffer.AsSpan
            (
                0,
                size
            )
        ) + _buffer[size - 1];
    }
#endif
}