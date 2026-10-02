using Mpgsql.Benchmarks.NpgsqlBaseline;

namespace Mpgsql.Benchmarks;

internal static class NullableInt64ArrayVerification
{
    internal static long?[] Values(int count,
        int nullPercent)
    {
        var nonNulls = NpgsqlArrayVerification.Values(count);
        var result = new long?[count];
        for (int i = 0; i < count; i++)
            // Deterministic mixed positions, instead of a single contiguous run of NULLs.
            result[i] = (i * 37L + 17) % 100 < nullPercent ? null : nonNulls[i];
        return result;
    }

    internal static void Run()
    {
        foreach (int count in new[] {0, 1, 3, 4, 5, 8, 256, 682, 683, 4096, 65536})
        foreach (int nullPercent in new[] {0, 50, 100})
        {
            var write = new NullableInt64ArrayWriteBenchmarks {Count = count, NullPercent = nullPercent};
            try { write.Setup(); }
            finally { write.Cleanup(); }
            foreach (int bufferSize in new[] {0, 4096, 8192})
            {
                var read = new NullableInt64ArrayReadBenchmarks
                    {Count = count, NullPercent = nullPercent, ReaderBufferSize = bufferSize};
                try { read.Setup(); }
                finally { read.Cleanup(); }
            }
        }
        Console.WriteLine("Nullable bigint[]: Npgsql 10.0.3 wire bytes, cross-decoding, refill boundaries and zero-allocation reusable paths verified.");
    }
}