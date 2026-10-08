using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Mpgsql.Benchmarks.Queries;

internal static class QueryRunMetadata
{
    internal static object Capture(string? transport = null)
    {
        string root = RepositoryRoot();
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string directory in new[] {"src/Mpgsql", "src/Mpgsql.Client", "benchmarks/Mpgsql.Benchmarks"})
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, directory), "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal))
            {
                continue;
            }
            if (Path.GetExtension(file) is not (".cs" or ".csproj" or ".props" or ".targets"))
            {
                continue;
            }
            hashes[relative] = Hash(file);
        }
        var variables = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        foreach (string name in new[] {"DOTNET_TieredCompilation", "DOTNET_TieredPGO", "DOTNET_ReadyToRun", "DOTNET_gcServer",
                     "DOTNET_GCHeapCount", "COMPlus_TieredCompilation", "COMPlus_TieredPGO", "COMPlus_gcServer"})
            variables[name] = Environment.GetEnvironmentVariable(name);
        return new
        {
            Utc = DateTimeOffset.UtcNow, SourceRoot = root, Commit = Git(root, "rev-parse", "HEAD"), WorkingTreeStatus = Git(root, "status", "--short"),
            Runtime = RuntimeInformation.FrameworkDescription, OS = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(), Environment.ProcessorCount,
            Cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"), Stopwatch.Frequency,
            GCSettings.IsServerGC, LatencyMode = GCSettings.LatencyMode.ToString(), JitAndGcEnvironment = variables,
            SourceHashesSha256 = hashes,
            AssembliesSha256 = new Dictionary<string, string>
            {
                ["Mpgsql"] = Hash(typeof(TypeOid).Assembly.Location),
                ["Mpgsql.Client"] = Hash(typeof(MpgsqlDataSource).Assembly.Location),
                ["Mpgsql.Benchmarks"] = Hash(typeof(QueryPacketBenchmarks).Assembly.Location),
                ["Npgsql"] = Hash(typeof(Npgsql.NpgsqlDataSource).Assembly.Location)
            },
            NpgsqlVersion = typeof(Npgsql.NpgsqlDataSource).Assembly.GetName().Version?.ToString(),
            AllocationScope = "Entire process: driver + in-process peer + timed runner work. No fixture subtraction.",
            ArrivalModel = "Closed loop: fixed sequential workers. Not a fixed arrival rate experiment.",
            Transport = transport ?? "Two System.IO.Pipelines Pipe endpoints per prewarmed session; no TCP/PostgreSQL/authentication.",
            Percentiles = "Nearest rank, ceil(p*N)-1 on sorted per-request ticks; no coordinated-omission correction."
        };
    }

    internal static string RepositoryRoot()
    {
        foreach (string start in new[] {Environment.CurrentDirectory, AppContext.BaseDirectory})
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "src", "Mpgsql", "Mpgsql.csproj")))
                {
                    return directory.FullName;
                }
        throw new DirectoryNotFoundException("Run the query load runner from the Mpgsql repository.");
    }
    private static string Hash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static string Git(string root, params string[] args)
    {
        string gitRoot = root;
        for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) || File.Exists(Path.Combine(directory.FullName, ".git")))
            { gitRoot = directory.FullName; break; }
        var start = new ProcessStartInfo("git") { WorkingDirectory = gitRoot, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("-c"); start.ArgumentList.Add("safe.directory=" + gitRoot.Replace('\\', '/'));
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start git.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(10000) || process.ExitCode != 0)
        {
            throw new InvalidOperationException("Git metadata failed: " + error);
        }
        return output.Trim();
    }
}
