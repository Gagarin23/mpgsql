using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text.Json;
using Mpgsql.Converters;
using Npgsql;

namespace Mpgsql.Benchmarks.Converters;

internal static class ConverterVerification
{
    internal static void Run(string? catalogPath)
    {
        var profiles = ConverterCatalog.Profiles;
        var publicConverters = typeof(Int64Converter).Assembly.GetExportedTypes()
            .Where(t => t.Namespace == "Mpgsql.Converters" && t.Name.EndsWith("Converter", StringComparison.Ordinal)).ToArray();
        var missing = publicConverters.Except(profiles.Select(p => p.ConverterType)).ToArray();
        if (missing.Length != 0)
        {
            throw new InvalidOperationException("Missing converter benchmarks: " + string.Join(", ", missing.Select(t => t.Name)));
        }
        if (profiles.Select(p => p.Id).Distinct().Count() != profiles.Count)
        {
            throw new InvalidOperationException("Duplicate converter profile ID.");
        }
        if (profiles.Where(p => p.Shape == ConverterShape.Scalar).Select(p => p.PostgreSqlType).Distinct().Count() != 26)
        {
            throw new InvalidOperationException("All 26 supported PostgreSQL scalar types must be covered.");
        }

        var metadata = new List<object>();
        var checks = 0;
        foreach (var profile in profiles)
        {
            int[] counts = profile.Shape == ConverterShape.Scalar ? [1] : [0, 1, 3, 4, 7, 8, 9, 256, 4096];
            int[] patterns = profile.Shape == ConverterShape.NullableArray ? [0, 1, 8] : [0];
            foreach (var count in counts)
            foreach (var nullEvery in patterns)
            {
                try
                {
                    using var test = profile.Create(count, nullEvery);
                    test.MpgsqlWrite();
                    test.NpgsqlWrite();
                    test.MpgsqlRead();
                    test.NpgsqlRead();
                    checks++;
                }
                catch (Exception e)
                {
                    throw new InvalidDataException($"Verification failed for {profile.Id}, count={count}, nullEvery={nullEvery}.", e);
                }
            }
            using var representative = profile.Create(profile.Shape == ConverterShape.Scalar ? 1 : 256,
                profile.Shape == ConverterShape.NullableArray ? 8 : 0);
            metadata.Add(new
            {
                profile.Id, profile.PostgreSqlType, profile.Representation, Shape = profile.Shape.ToString(),
                Converter = profile.ConverterType.Name, representative.PayloadLength, representative.NpgsqlConverter
            });
            Console.WriteLine($"Verified {profile.Id}: {representative.PayloadLength} bytes; {representative.NpgsqlConverter}");
        }
        if (catalogPath is not null)
        {
            File.WriteAllText(catalogPath, JsonSerializer.Serialize(new
            {
                Date = DateTimeOffset.Now,
                Runtime = RuntimeInformation.FrameworkDescription,
                NpgsqlVersion = typeof(NpgsqlConnection).Assembly.GetName().Version!.ToString(),
                Vector128 = Vector128.IsHardwareAccelerated, Avx2 = Avx2.IsSupported, Avx512F = Avx512F.IsSupported,
                Checks = checks, ConverterClasses = publicConverters.Length, Profiles = metadata
            }, new JsonSerializerOptions {WriteIndented = true}));
        }
        Console.WriteLine($"All {publicConverters.Length} public converter classes covered; {profiles.Count} profiles; {checks} byte/cross-read/segmented fixtures passed.");
    }
}