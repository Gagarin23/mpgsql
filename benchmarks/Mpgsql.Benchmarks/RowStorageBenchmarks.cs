using System.Buffers;
using System.Buffers.Binary;
using BenchmarkDotNet.Attributes;
using Mpgsql.Internal;
using Mpgsql.Protocol;

namespace Mpgsql.Benchmarks;

// Diagnostic for the copied row lifetime; fixture construction is outside measurement.
// This synchronous microbenchmark does not replace full Session/DataSource measurements.
[MemoryDiagnoser, JsonExporterAttribute.Full, Config(typeof(QueryBenchmarkConfig)), IterationTime(150)]
public class RowStorageBenchmarks
{
    private readonly RowStoragePool _pool = new RowStoragePool();
    private BackendMessage _message;
    [Params(1, 8, 16)]
    public int Columns { get; set; }
    [Params(8, 8192)]
    public int FieldBytes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var bytes = new byte[checked(2 + Columns * (4 + FieldBytes))];
        BinaryPrimitives.WriteInt16BigEndian(bytes, checked((short)Columns));
        var offset = 2;
        for (var column = 0;
             column < Columns;
             column++)
        {
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(offset), FieldBytes);
            BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(offset + 4), column + 1L);
            offset += 4 + FieldBytes;
        }
        _message = new BackendMessage((byte)'D', BackendMessageKind.DataRow, new ReadOnlySequence<byte>(bytes), Columns);
        var expected = Columns * (long)FieldBytes + Columns * (Columns + 1L) / 2;
        for (var iteration = 0;
             iteration < 64;
             iteration++)
        {
            if (RentReadRelease() != expected)
            {
                throw new InvalidOperationException("Row storage checksum mismatch.");
            }
        }
    }

    [Benchmark]
    public long RentReadRelease()
    {
        using var row = _pool.Rent(_message, null, null);
        long sum = 0;
        for (var column = 0;
             column < Columns;
             column++)
        {
            var value = row[column]!.Value;
            sum += value.Length + BinaryPrimitives.ReadInt64BigEndian(value.FirstSpan);
        }
        return sum;
    }
}