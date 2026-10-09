using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

// Capture once before warmup. Metadata never enters request or row timing.
internal static class ComparisonRunMetadata
{
    internal static object Runtime()
    {
        var variables = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        foreach (var name in new[]
        {
            "DOTNET_TieredCompilation", "DOTNET_TieredPGO", "DOTNET_ReadyToRun",
            "DOTNET_TC_QuickJit", "DOTNET_TC_QuickJitForLoops", "DOTNET_gcServer",
            "DOTNET_GCHeapCount", "DOTNET_EnableHWIntrinsic", "COMPlus_TieredCompilation",
            "COMPlus_TieredPGO", "COMPlus_ReadyToRun", "COMPlus_gcServer",
            "COMPlus_GCHeapCount", "COMPlus_EnableHWIntrinsic"
        })
        {
            variables.Add(name, Environment.GetEnvironmentVariable(name));
        }
        var entryPath = Assembly.GetEntryAssembly()?.Location;
        return new
        {
            CapturedUtc = DateTimeOffset.UtcNow,
            Cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            JitAndGcEnvironment = variables,
            LatencyMode = GCSettings.LatencyMode.ToString(),
            RunnerRuntimeConfig = entryPath is null ? null : RuntimeConfig(entryPath)
        };
    }

    internal static object Snapshot(VersionContext context, string benchmarkPath)
    {
        benchmarkPath = Path.GetFullPath(benchmarkPath);
        var directory = Path.GetDirectoryName(benchmarkPath)!;
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in new[]
        {
            "Mpgsql.Benchmarks", "Mpgsql.Protocol", "Mpgsql.Sessions", "Mpgsql",
            "Mpgsql.Multiplexing", "Mpgsql.Client", "Npgsql"
        })
        {
            var path = Path.Combine(directory, name + ".dll");
            if (File.Exists(path))
            {
                hashes.Add(name, Hash(path));
            }
        }
        var native = context.LoadFromAssemblyName(new AssemblyName("Npgsql"));
        return new
        {
            BenchmarkPath = benchmarkPath,
            AssembliesSha256 = hashes,
            NpgsqlPackageVersion = native.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            NpgsqlAssemblyVersion = native.GetName().Version?.ToString(),
            BenchmarkRuntimeConfig = RuntimeConfig(benchmarkPath),
            RuntimeConfigUse = "Benchmark configuration is preserved for provenance; the executing process uses the runner runtime configuration"
        };
    }

    private static object RuntimeConfig(string assemblyPath)
    {
        var path = Path.ChangeExtension(assemblyPath, ".runtimeconfig.json");
        var exists = File.Exists(path);
        return new
        {
            Path = path,
            Exists = exists,
            Sha256 = exists ? Hash(path) : null,
            Contents = exists ? JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path)) : (JsonElement?)null
        };
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
