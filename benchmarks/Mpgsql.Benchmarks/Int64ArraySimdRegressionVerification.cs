using System.Buffers.Binary;
using Mpgsql.Benchmarks.NpgsqlBaseline;
using Mpgsql.Converters;

namespace Mpgsql.Benchmarks;

internal static class Int64ArraySimdRegressionVerification
{
    internal static void Run()
    {
        foreach (var count in new[]
                 {
                     0,
                     1,
                     3,
                     4,
                     5,
                     8,
                     9,
                     257,
                     4096
                 })
        {
            var values = NpgsqlArrayVerification.Values(count);
            var original = new byte[Int64ArraySimdBaseline.GetByteCount(count)];
            var shared = new byte[original.Length];
            Int64ArraySimdBaseline.Write(values, original);
            Int64ArrayConverter.Write(values, shared);
            if (!original
                    .AsSpan()
                    .SequenceEqual(shared))
            {
                throw new InvalidDataException("Int64 encoder regression.");
            }
            CheckReads(original, count);
        }

        var payload = new byte[Int64ArraySimdBaseline.GetByteCount(9)];
        Int64ArraySimdBaseline.Write(NpgsqlArrayVerification.Values(9), payload);
        for (var index = 0;
             index < 9;
             index++)
        {
            foreach (var flags in new[]
                     {
                         0,
                         1
                     })
            foreach (var length in new[]
                     {
                         -1,
                         -2,
                         0,
                         4,
                         7,
                         9,
                         int.MaxValue
                     })
            {
                var damaged = payload.ToArray();
                BinaryPrimitives.WriteInt32BigEndian(damaged.AsSpan(4), flags);
                BinaryPrimitives.WriteInt32BigEndian(damaged.AsSpan(20 + 12 * index), length);
                CheckReads(damaged, 9);
            }
        }
        for (var size = 0;
             size < payload.Length;
             size++)
        {
            CheckReads(payload[..size], 9);
        }
        CheckReads([.. payload, 0], 9);
        foreach (var offset in new[]
                 {
                     0,
                     4,
                     8,
                     12,
                     16
                 })
        foreach (var value in new[]
                 {
                     int.MinValue,
                     -1,
                     0,
                     1,
                     2,
                     7,
                     int.MaxValue
                 })
        {
            var damaged = payload.ToArray();
            BinaryPrimitives.WriteInt32BigEndian(damaged.AsSpan(offset), value);
            CheckReads(damaged, 9);
        }
        for (var index = 0;
             index < 9;
             index++)
        {
            var offset = 20 + 12 * index;
            byte[] withNull = [.. payload.AsSpan(0, offset), 0xff, 0xff, 0xff, 0xff, .. payload.AsSpan(offset + 12)];
            BinaryPrimitives.WriteInt32BigEndian(withNull.AsSpan(4), 1);
            CheckReads(withNull, 9);
        }
        Console.WriteLine("PASS exact pre-consolidation Int64 reference: wire bytes, return counts, exceptions/messages, and partial destination writes (contiguous/segmented).");
    }

    private static void CheckReads(byte[] bytes, int count)
    {
        var original = Enumerable
            .Repeat(42L, count + 2)
            .ToArray();
        var shared = original.ToArray();
        Check
        (
            () => Int64ArraySimdBaseline.Read(bytes.AsSpan(), original.AsSpan(1)),
            () => Int64ArrayConverter.Read(bytes.AsSpan(), shared.AsSpan(1)), original, shared
        );
        Check(() => Int64ArraySimdBaseline.Read(bytes.AsSpan()), () => Int64ArrayConverter.Read(bytes.AsSpan()));
        foreach (var segmentSize in new[]
                 {
                     0,
                     1,
                     7,
                     19,
                     20,
                     21,
                     48,
                     75,
                     4096
                 })
        {
            // The sequence helper accepts an empty byte array as a single empty segment.
            var sequence = NpgsqlArrayVerification.Sequence(bytes, segmentSize);
            original
                .AsSpan()
                .Fill(42);
            shared
                .AsSpan()
                .Fill(42);
            Check
            (
                () => Int64ArraySimdBaseline.Read(sequence, original.AsSpan(1)),
                () => Int64ArrayConverter.Read(sequence, shared.AsSpan(1)), original, shared
            );
            Check(() => Int64ArraySimdBaseline.Read(sequence), () => Int64ArrayConverter.Read(sequence));
        }
    }

    private static void Check<T>(
        Func<T> original, Func<T> shared,
        long[]? originalStorage = null, long[]? sharedStorage = null
    )
    {
        T? expected = default,
            actual = default;
        Exception? originalError = null,
            sharedError = null;
        try { expected = original(); }
        catch (Exception error) { originalError = error; }
        try { actual = shared(); }
        catch (Exception error) { sharedError = error; }
        if (originalError?.GetType() != sharedError?.GetType() || originalError?.Message != sharedError?.Message)
        {
            throw new InvalidDataException("Int64 exception regression.");
        }
        if (originalError is null)
        {
            var equal = expected is ReadOnlyMemory<long> expectedMemory && actual is ReadOnlyMemory<long> actualMemory
                ? expectedMemory.Span.SequenceEqual(actualMemory.Span)
                : EqualityComparer<T>.Default.Equals(expected!, actual!);
            if (!equal)
            {
                throw new InvalidDataException("Int64 decoded result regression.");
            }
        }
        if (originalStorage is not null && !originalStorage
                .AsSpan()
                .SequenceEqual(sharedStorage))
        {
            throw new InvalidDataException("Int64 destination mutation regression.");
        }
    }
}