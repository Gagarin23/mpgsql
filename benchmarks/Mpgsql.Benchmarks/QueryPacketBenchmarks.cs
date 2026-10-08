using System.Buffers;
using BenchmarkDotNet.Attributes;
using Mpgsql.Internal;
using Mpgsql.Protocol;

namespace Mpgsql.Benchmarks;

[MemoryDiagnoser, JsonExporterAttribute.Full]
[Config(typeof(QueryBenchmarkConfig)), IterationTime(150)]
public class QueryPacketBenchmarks
{
    [Params("NoParameters", "Bigint1", "Bigint16", "Sql4096", "Text64KiB", "Jsonb64KiB", "Nullable4096", "Nullable65536")]
    public string Case { get; set; } = "NoParameters";
    private string _sql = "select 42::bigint";
    private MpgsqlParameter[] _parameters = [];
    private byte[] _output = [];

    [GlobalSetup]
    public void Setup()
    {
        (_sql, _parameters) = Inputs(Case);
        _output = new byte[QueryPacket.GetByteCount(_sql, _parameters)];
        VerifyBytes();
    }
    [Benchmark] public int GetByteCount() => QueryPacket.GetByteCount(_sql, _parameters);
    [Benchmark] public int Write() => QueryPacket.Write(_sql, _parameters, _output);
    [Benchmark] public int MeasureAndWrite()
    {
        int size = QueryPacket.GetByteCount(_sql, _parameters);
        return QueryPacket.Write(_sql, _parameters, _output.AsSpan(0, size));
    }

    internal void VerifyBytes()
    {
        var expected = new ArrayBufferWriter<byte>();
        uint[] oids = [.. _parameters.Select(x => x.PostgresTypeOid)];
        FrontendMessage.Parse(_sql, parameterTypes: oids).Write(expected);
        ReadOnlyMemory<byte>?[] values = [.. _parameters.Select(Payload)];
        FrontendMessage.Bind(parameters: values, parameterFormats: new[] {FormatCode.Binary},
            resultFormats: new[] {FormatCode.Binary}).Write(expected);
        FrontendMessage.Describe(StatementOrPortal.Portal).Write(expected);
        FrontendMessage.Execute().Write(expected);
        int written = Write();
        if (written != expected.WrittenCount || !expected.WrittenSpan.SequenceEqual(_output))
            throw new InvalidOperationException($"Complete query bytes differ: {Case}.");
        var before = Enumerable.Repeat((byte)0xA5, _output.Length - 1).ToArray();
        try { QueryPacket.Write(_sql, _parameters, before); throw new InvalidOperationException("Capacity check was missed."); }
        catch (ArgumentException) { }
        if (before.AsSpan().IndexOfAnyExcept((byte)0xA5) >= 0) throw new InvalidOperationException("Capacity failure mutated destination.");
    }

    private static ReadOnlyMemory<byte>? Payload(MpgsqlParameter parameter)
    {
        if (parameter.IsNull) return null;
        byte[] bytes = new byte[parameter.PayloadLength];
        if (parameter.WritePayload(bytes) != bytes.Length) throw new InvalidOperationException("Parameter size mismatch.");
        return bytes;
    }

    internal static void VerifyNullAndEmpty()
    {
        var benchmark = new QueryPacketBenchmarks
        {
            _sql = "select $1,$2,$3,$4,$5,$6",
            _parameters = [MpgsqlParameter.Int64(null), MpgsqlParameter.Text(null), MpgsqlParameter.Text(""),
                MpgsqlParameter.Bytea(ReadOnlyMemory<byte>.Empty), MpgsqlParameter.NullableInt64Array(null),
                MpgsqlParameter.NullableInt64Array(ReadOnlyMemory<long?>.Empty)]
        };
        benchmark._output = new byte[benchmark.GetByteCount()];
        benchmark.VerifyBytes();
    }

    private static (string, MpgsqlParameter[]) Inputs(string name)
    {
        const string sql = "select 42::bigint";
        return name switch
        {
            "NoParameters" => (sql, []),
            "Bigint1" => ("select $1::bigint", [MpgsqlParameter.Int64(42)]),
            "Bigint16" => ("select " + string.Join(',', Enumerable.Range(1, 16).Select(i => $"${i}::bigint")),
                [.. Enumerable.Range(0, 16).Select(i => MpgsqlParameter.Int64(i))]),
            "Sql4096" => (sql + " /*" + new string('x', 4096 - sql.Length - 5) + "*/", []),
            "Text64KiB" => ("select $1::text", [MpgsqlParameter.Text(new string('x', 65536))]),
            "Jsonb64KiB" => ("select $1::jsonb", [MpgsqlParameter.Jsonb(System.Text.Encoding.UTF8.GetBytes("\"" + new string('x', 65534) + "\""))]),
            "Nullable4096" => Array(4096),
            "Nullable65536" => Array(65536),
            _ => throw new ArgumentException("Unknown packet case.")
        };
        static (string, MpgsqlParameter[]) Array(int count)
        {
            long?[] values = [.. Enumerable.Range(0, count).Select(i => i % 4 == 0 ? (long?)null : i)];
            return ("select $1::bigint[]", [MpgsqlParameter.NullableInt64Array(values)]);
        }
    }
}
