using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;

if (args.Length is not (3 or 4))
{
    throw new ArgumentException("Usage: <baseline Mpgsql.Benchmarks.dll> <candidate Mpgsql.Benchmarks.dll> <output.json> [--concurrent|--bytea-diagnostic]");
}
using var comparisonProcess = Process.GetCurrentProcess();
if (OperatingSystem.IsWindows())
{
    try { comparisonProcess.PriorityClass = ProcessPriorityClass.High; }
    catch (Win32Exception error) { Console.WriteLine("Priority unchanged: " + error.Message); }
}
Console.WriteLine($"Process priority: {comparisonProcess.PriorityClass}; logical CPUs: {Environment.ProcessorCount}; server GC: {GCSettings.IsServerGC}");
if (args.Length == 4 && args[3] == "--concurrent")
{
    await ConcurrentComparison.RunAsync(args[0], args[1], args[2]);
    return;
}
var diagnostic = args.Length == 4 && args[3] == "--bytea-diagnostic";
if (args.Length == 4 && !diagnostic)
{
    throw new ArgumentException("Unknown comparison mode.");
}

// Both versions run against their own identical TCP peers in one process. Alternate
// the order of each pair to bound machine drift. Caller construction is outside
// the Batch16 timed/allocated interval, as in the existing BDN batch benchmark.
var baseline = new VersionContext(args[0]);
var candidate = new VersionContext(args[1]);
var results = new List<object>();
string[] cases = diagnostic ? ["Bytea64KiB"] : ["Empty", "OneBigint", "Rows128Columns8", "Rows4096", "Bytea64KiB"];
foreach (var name in cases)
{
    await CompareAsync(name, "Multiplexing", "TcpReaderComparisonBenchmarks", "MpgsqlDataSource", "TcpReaderComparisonBenchmarks", "MpgsqlDataSource");
    await CompareAsync(name, "ExclusiveTyped", "LegacyConnection", "LegacyCommand", "TcpAdoReaderBenchmarks", "ReusedTyped");
}
if (diagnostic)
{
    await CompareAsync("Bytea64KiB", "Raw", "TcpReaderComparisonBenchmarks", "MpgsqlRaw", "TcpReaderComparisonBenchmarks", "MpgsqlRaw");
}
else
{
    await CompareAsync("Batch16", "FreshBatchExecution", "TcpFacadeBatchComparisonBenchmarks", "MpgsqlFacadeBatch", "TcpFacadeBatchComparisonBenchmarks", "MpgsqlFacadeBatch");
}
var output = Path.GetFullPath(args[2]);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
{
    Baseline = Path.GetFullPath(args[0]), Candidate = Path.GetFullPath(args[1]),
    Runtime = RuntimeInformation.FrameworkDescription,
    ProcessPriority = comparisonProcess.PriorityClass.ToString(), LogicalProcessors = Environment.ProcessorCount,
    ServerGC = GCSettings.IsServerGC,
    Samples = 40, WarmupSeconds = 5, TargetSampleMilliseconds = 120,
    Confidence = "95% paired Student t interval; t(39)=2.023",
    AllocationScope = "Whole managed process, including identical TCP peer; Batch16 caller construction excluded",
    ExclusiveBaseline = "Previous explicit connection/typed command; single-use command construction outside timing, candidate command reused",
    Results = results
}, new JsonSerializerOptions {WriteIndented = true}));

async Task CompareAsync(string scenario, string path,
    string oldClass, string oldMethod,
    string newClass, string newMethod)
{
    await using var oldRun = await BenchmarkRun.CreateAsync(baseline, oldClass, oldMethod, scenario);
    await using var newRun = await BenchmarkRun.CreateAsync(candidate, newClass, newMethod, scenario);
    if (oldRun.Checksum != newRun.Checksum)
    {
        throw new InvalidDataException("Baseline/candidate checksum mismatch.");
    }
    var warming = Stopwatch.StartNew();
    while (warming.Elapsed < TimeSpan.FromSeconds(5))
    {
        await oldRun.MeasureAsync(1);
        await newRun.MeasureAsync(1);
    }
    var pilot = await oldRun.MeasureAsync(1);
    var invocations = Math.Clamp((int)(120_000_000 / (pilot.Nanoseconds * oldRun.Operations)), 8, 2048);
    long? oldCopied = oldRun.CopiedRowBytes, newCopied = newRun.CopiedRowBytes;
    var pairs = new List<Pair>();
    for (var sample = 0; sample < 40; sample++)
    {
        Measurement before, after;
        if ((sample & 1) == 0)
        {
            before = await oldRun.MeasureAsync(invocations);
            after = await newRun.MeasureAsync(invocations);
        }
        else
        {
            after = await newRun.MeasureAsync(invocations);
            before = await oldRun.MeasureAsync(invocations);
        }
        pairs.Add(new Pair(before.Nanoseconds, after.Nanoseconds, before.Bytes, after.Bytes));
    }
    double oldMean = pairs.Average(p => p.BaselineNanoseconds), newMean = pairs.Average(p => p.CandidateNanoseconds);
    var logRatio = pairs.Average(p => Math.Log(p.CandidateNanoseconds / p.BaselineNanoseconds));
    var sd = Math.Sqrt(pairs.Sum(p => Math.Pow(Math.Log(p.CandidateNanoseconds / p.BaselineNanoseconds) - logRatio, 2)) / (pairs.Count - 1));
    var halfWidth = 2.023 * sd / Math.Sqrt(pairs.Count);
    double ratio = Math.Exp(logRatio), lower = Math.Exp(logRatio - halfWidth), upper = Math.Exp(logRatio + halfWidth);
    results.Add(new
    {
        Case = scenario, Path = path, InvocationsPerSample = invocations, OperationsPerInvocation = oldRun.Operations,
        BaselineMicroseconds = oldMean / 1000, CandidateMicroseconds = newMean / 1000,
        BaselineOperationsPerSecond = 1e9 / oldMean, CandidateOperationsPerSecond = 1e9 / newMean,
        BaselineBytes = pairs.Average(p => p.BaselineBytes), CandidateBytes = pairs.Average(p => p.CandidateBytes),
        BaselineCopiedBytesPerOperation = (oldRun.CopiedRowBytes - oldCopied) / (double)(pairs.Count * invocations * oldRun.Operations),
        CandidateCopiedBytesPerOperation = (newRun.CopiedRowBytes - newCopied) / (double)(pairs.Count * invocations * newRun.Operations),
        PairedRatio = ratio, Ratio95Lower = lower, Ratio95Upper = upper, MeetsFivePercentBound = upper <= 1.05, Pairs = pairs
    });
    Console.WriteLine($"{path}/{scenario}: {oldMean / 1000:F2} -> {newMean / 1000:F2} us; ratio {ratio:F4} [{lower:F4}, {upper:F4}], bytes {pairs.Average(p => p.BaselineBytes):F0} -> {pairs.Average(p => p.CandidateBytes):F0}");
}

internal sealed class VersionContext : AssemblyLoadContext
{
    private readonly string _directory;
    internal VersionContext(string path) : base(false)
    {
        path = Path.GetFullPath(path);
        _directory = Path.GetDirectoryName(path)!;
        Benchmarks = LoadFromAssemblyPath(path);
    }
    internal Assembly Benchmarks { get; }
    protected override Assembly? Load(AssemblyName name)
    {
        var path = Path.Combine(_directory, name.Name + ".dll");
        return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
    }
}

internal sealed class BenchmarkRun : IAsyncDisposable
{
    private readonly Func<Task> _cleanup;
    private readonly Func<long>? _copied;
    private readonly Action? _prepare;
    private readonly Func<Task<long>> _run;
    private BenchmarkRun(object instance, Type type,
        string method)
    {
        _run = type.GetMethod(method)!.CreateDelegate<Func<Task<long>>>(instance);
        _cleanup = type.GetMethod("Cleanup")!.CreateDelegate<Func<Task>>(instance);
        if (type.GetMethod("PrepareMpgsql") is { } prepare)
        {
            _prepare = prepare.CreateDelegate<Action>(instance);
            Operations = 32;
        }
        else
        {
            Operations = 1;
        }
        const BindingFlags members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var fixture = (type.GetField("_m", members) ?? type.GetField("_fixture", members))?.GetValue(instance);
        _copied = fixture is null ? null : CreateCopiedCounter(fixture);
    }
    private BenchmarkRun(Func<Task<long>> run, Func<Task> cleanup,
        Action prepare, Func<long>? copied)
    {
        _run = run;
        _cleanup = cleanup;
        _prepare = prepare;
        _copied = copied;
        Operations = 1;
    }
    internal long? CopiedRowBytes => _copied?.Invoke();
    internal int Operations { get; }
    internal long Checksum { get; private set; }
    public ValueTask DisposeAsync()
    {
        return new ValueTask(_cleanup());
    }
    private static Func<long>? CreateCopiedCounter(object fixture)
    {
        const BindingFlags members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        if (fixture.GetType().GetProperty("Transports", members)?.GetValue(fixture) is not IEnumerable transports)
        {
            return null;
        }
        var counters = new List<Func<long>>();
        foreach (var transport in transports)
        {
            var session = transport.GetType().GetProperty("Session", members)!.GetValue(transport)!;
            counters.Add(session.GetType().GetProperty("CopiedRowBytes", members)!.GetMethod!.CreateDelegate<Func<long>>(session));
        }
        return () => counters.Sum(read => read());
    }
    internal static async Task<BenchmarkRun> CreateAsync(VersionContext context, string name,
        string method, string scenario)
    {
        if (name == "LegacyConnection")
        {
            return await CreateLegacyAsync(context, scenario);
        }
        var type = context.Benchmarks.GetType("Mpgsql.Benchmarks." + name, true)!;
        var instance = Activator.CreateInstance(type)!;
        type.GetProperty("Case")?.SetValue(instance, scenario);
        await (Task)(type.GetMethod("SetupMpgsql") ?? type.GetMethod("Setup"))!.Invoke(instance, null)!;
        var run = new BenchmarkRun(instance, type, method);
        run._prepare?.Invoke();
        run.Checksum = await run._run();
        return run;
    }
    private static async Task<BenchmarkRun> CreateLegacyAsync(VersionContext context, string scenario)
    {
        const BindingFlags members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var type = context.Benchmarks.GetType("Mpgsql.Benchmarks.TcpReaderComparisonBenchmarks", true)!;
        var instance = Activator.CreateInstance(type)!;
        type.GetProperty("Case")!.SetValue(instance, scenario);
        await (Task)type.GetMethod("SetupMpgsql")!.Invoke(instance, null)!;
        var fixture = type.GetField("_m", members)!.GetValue(instance)!;
        var source = fixture.GetType().GetProperty("Source", members)!.GetValue(fixture)!;
        var opening = source.GetType().GetMethod("OpenConnectionAsync")!.Invoke(source, [default(CancellationToken)])!;
        var openTask = (Task)opening.GetType().GetMethod("AsTask")!.Invoke(opening, null)!;
        await openTask;
        var connection = openTask.GetType().GetProperty("Result")!.GetValue(openTask)!;
        var creator = connection.GetType().GetMethod("CreateCommand", [typeof(string)])!.CreateDelegate<Func<string, object>>(connection);
        var query = type.GetField("_scenario", members)!.GetValue(instance)!;
        var sql = (string)query.GetType().GetProperty("Sql")!.GetValue(query)!;
        var catalog = type.GetField("_catalog", members)!.GetValue(instance)!;
        var inputs = (Array)catalog.GetType().GetProperty("Inputs", members)!.GetValue(catalog)!;
        var values = (Array)((Array)inputs.GetValue(0)!).GetValue(0)!;
        var buffers = (byte[][])fixture.GetType().GetProperty("Buffers", members)!.GetValue(fixture)!;
        var command = creator(sql);
        var commandArgument = Expression.Parameter(typeof(object));
        var parameters = Expression.Property(Expression.Convert(commandArgument, command.GetType()), "Parameters");
        var add = parameters.Type.GetMethod("Add", [values.GetType().GetElementType()!])!;
        var adds = Enumerable.Range(0, values.Length).Select(i => Expression.Call(parameters, add, Expression.ArrayIndex(Expression.Constant(values), Expression.Constant(i))));
        var copyParameters = Expression.Lambda<Action<object>>(Expression.Block(adds), commandArgument).Compile();

        void Prepare()
        {
            command = creator(sql);
            copyParameters(command);
        }

        Prepare();
        var execute = command!.GetType().GetMethod("ExecuteReaderAsync")!;
        var reader = execute.ReturnType.GetGenericArguments()[0];
        var consumer = context.Benchmarks.GetType("Mpgsql.Benchmarks.Comparison.TcpQueryOperations", true)!
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static).Single(m => m.Name == "ConsumeAsync" && m.GetParameters()[0].ParameterType == reader);
        var bridge = (Func<object, Task<long>>)typeof(BenchmarkRun).GetMethod(nameof(CreateLegacyBridge), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(reader).Invoke(null, [execute, consumer, query, buffers[0]])!;
        var cleanup = type.GetMethod("Cleanup")!.CreateDelegate<Func<Task>>(instance);
        var run = new BenchmarkRun(() => bridge(command!), async () =>
        {
            await ((IAsyncDisposable)connection).DisposeAsync();
            await cleanup();
        }, Prepare, CreateCopiedCounter(fixture));
        run.Checksum = await run._run();
        return run;
    }
    private static Func<object, Task<long>> CreateLegacyBridge<T>(MethodInfo method, MethodInfo consumer,
        object scenario, byte[] buffer)
    {
        var parameter = Expression.Parameter(typeof(object));
        var execute = Expression.Lambda<Func<object, ValueTask<T>>>(Expression.Call(Expression.Convert(parameter, method.DeclaringType!), method, Expression.Constant(default(CancellationToken))), parameter).Compile();
        var reader = Expression.Parameter(typeof(T));
        var consume = Expression.Lambda<Func<T, Task<long>>>(Expression.Call(consumer, reader, Expression.Constant(scenario), Expression.Constant(buffer)), reader).Compile();
        return async command =>
        {
            try
            {
                var value = await execute(command).ConfigureAwait(false);
                await using ((IAsyncDisposable)value!)
                {
                    return await consume(value).ConfigureAwait(false);
                }
            }
            finally { await ((IAsyncDisposable)command).DisposeAsync().ConfigureAwait(false); }
        };
    }
    internal async Task<Measurement> MeasureAsync(int invocations)
    {
        long elapsed = 0, allocated = 0;
        if (_prepare is null)
        {
            long bytes = GC.GetTotalAllocatedBytes(true), start = Stopwatch.GetTimestamp();
            for (var i = 0; i < invocations; i++)
            {
                if (await _run() != Checksum)
                {
                    throw new InvalidDataException("Unstable checksum.");
                }
            }
            elapsed = Stopwatch.GetTimestamp() - start;
            allocated = GC.GetTotalAllocatedBytes(true) - bytes;
        }
        else
        {
            for (var i = 0; i < invocations; i++)
            {
                _prepare();
                long bytes = GC.GetTotalAllocatedBytes(true), start = Stopwatch.GetTimestamp();
                if (await _run() != Checksum)
                {
                    throw new InvalidDataException("Unstable checksum.");
                }
                elapsed += Stopwatch.GetTimestamp() - start;
                allocated += GC.GetTotalAllocatedBytes(true) - bytes;
            }
        }
        return new Measurement(elapsed * 1e9 / Stopwatch.Frequency / invocations / Operations, (double)allocated / invocations / Operations);
    }
}

internal readonly record struct Measurement(double Nanoseconds, double Bytes);

internal readonly record struct Pair
(
    double BaselineNanoseconds,
    double CandidateNanoseconds,
    double BaselineBytes,
    double CandidateBytes
);