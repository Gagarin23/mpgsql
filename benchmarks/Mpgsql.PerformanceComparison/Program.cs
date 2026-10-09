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
    throw new ArgumentException("Usage: <baseline Mpgsql.Benchmarks.dll> <candidate Mpgsql.Benchmarks.dll> <output.json> [--concurrent|--bytea-diagnostic|--mpgsql[=case,case]|--mpgsql-ado[=case,case]|--npgsql[=case,case]|--npgsql-ado[=case,case]|--npgsql-ado-standard[=case,case]|--npgsql-ado-live[=case,case]|--npgsql-ado-live-standard[=case,case]|--npgsql-ado-execution[=case,case]|--npgsql-converters|--npgsql-converters-focused[=id,id]|--npgsql-concurrent|--npgsql-buffers]");
}
using var comparisonProcess = Process.GetCurrentProcess();
if (OperatingSystem.IsWindows())
{
    try { comparisonProcess.PriorityClass = ProcessPriorityClass.High; }
    catch (Win32Exception error) { Console.WriteLine("Priority unchanged: " + error.Message); }
}
Console.WriteLine($"Process priority: {comparisonProcess.PriorityClass}; logical CPUs: {Environment.ProcessorCount}; server GC: {GCSettings.IsServerGC}");
if (args.Length == 4 && args[3] == "--npgsql-converters")
{
    ConverterComparison.Run(args[0], args[1], args[2]);
    return;
}
if (args.Length == 4 && args[3] == "--npgsql-converters-focused")
{
    ConverterComparison.Run(args[0], args[1], args[2], focused: true);
    return;
}
const string focusedPrefix = "--npgsql-converters-focused=";
if (args.Length == 4 && args[3].StartsWith(focusedPrefix, StringComparison.Ordinal))
{
    var selectedIds = args[3][focusedPrefix.Length..].Split(',', StringSplitOptions.TrimEntries);
    ConverterComparison.Run(args[0], args[1], args[2], focused: true, selectedIds: selectedIds);
    return;
}
if (args.Length == 4 && args[3] == "--npgsql-concurrent")
{
    await NpgsqlConcurrentComparison.RunAsync(args[0], args[1], args[2]);
    return;
}
if (args.Length == 4 && args[3] == "--npgsql-buffers")
{
    await NpgsqlBufferComparison.RunAsync(args[0], args[1], args[2]);
    return;
}
const string adoPrefix = "--npgsql-ado";
const string standardAdoPrefix = "--npgsql-ado-standard";
const string executionAdoPrefix = "--npgsql-ado-execution";
const string selfAdoPrefix = "--mpgsql-ado";
const string liveAdoPrefix = "--npgsql-ado-live";
const string liveStandardAdoPrefix = "--npgsql-ado-live-standard";
if (args.Length == 4 && (args[3] == liveAdoPrefix || args[3].StartsWith(liveAdoPrefix + "=", StringComparison.Ordinal)
    || args[3] == liveStandardAdoPrefix || args[3].StartsWith(liveStandardAdoPrefix + "=", StringComparison.Ordinal)))
{
    var standard = args[3] == liveStandardAdoPrefix || args[3].StartsWith(liveStandardAdoPrefix + "=", StringComparison.Ordinal);
    var prefix = standard ? liveStandardAdoPrefix : liveAdoPrefix;
    var selectedCases = args[3].Length == prefix.Length ? null
        : args[3][(prefix.Length + 1)..].Split(',', StringSplitOptions.TrimEntries);
    await NpgsqlComparison.RunAsync(args[0], args[1], args[2], selectedCases: selectedCases,
        adoOnly: true, standardAdo: standard, adoOptions: AdoComparisonOptions.FromEnvironment(), liveOnly: true);
    return;
}
if (args.Length == 4 && (args[3] == selfAdoPrefix || args[3].StartsWith(selfAdoPrefix + "=", StringComparison.Ordinal)))
{
    var selectedCases = args[3].Length == selfAdoPrefix.Length ? null
        : args[3][(selfAdoPrefix.Length + 1)..].Split(',', StringSplitOptions.TrimEntries);
    await NpgsqlComparison.RunAsync(args[0], args[1], args[2], compareMpgsqlBaseline: true,
        selectedCases: selectedCases, adoOnly: true, adoOptions: AdoComparisonOptions.FromEnvironment());
    return;
}
if (args.Length == 4 && (args[3] == executionAdoPrefix || args[3].StartsWith(executionAdoPrefix + "=", StringComparison.Ordinal)))
{
    var selectedCases = args[3].Length == executionAdoPrefix.Length ? null
        : args[3][(executionAdoPrefix.Length + 1)..].Split(',', StringSplitOptions.TrimEntries);
    await NpgsqlComparison.RunAsync(args[0], args[1], args[2], selectedCases: selectedCases,
        adoOnly: true, standardAdo: true, adoOptions: AdoComparisonOptions.FromEnvironment(), executionOnly: true);
    return;
}
if (args.Length == 4 && (args[3] == adoPrefix || args[3].StartsWith(adoPrefix + "=", StringComparison.Ordinal)
    || args[3] == standardAdoPrefix || args[3].StartsWith(standardAdoPrefix + "=", StringComparison.Ordinal)))
{
    var standardAdo = args[3] == standardAdoPrefix || args[3].StartsWith(standardAdoPrefix + "=", StringComparison.Ordinal);
    var prefix = standardAdo ? standardAdoPrefix : adoPrefix;
    var selectedCases = args[3].Length == prefix.Length ? null
        : args[3][(prefix.Length + 1)..].Split(',', StringSplitOptions.TrimEntries);
    await NpgsqlComparison.RunAsync(args[0], args[1], args[2], selectedCases: selectedCases,
        adoOnly: true, standardAdo: standardAdo, adoOptions: AdoComparisonOptions.FromEnvironment());
    return;
}
if (args.Length == 4 && args[3] == "--npgsql")
{
    await NpgsqlComparison.RunAsync(args[0], args[1], args[2]);
    return;
}
if (args.Length == 4 && args[3] == "--mpgsql")
{
    await NpgsqlComparison.RunAsync(args[0], args[1], args[2], compareMpgsqlBaseline: true);
    return;
}
const string npgsqlTcpPrefix = "--npgsql=";
const string mpgsqlTcpPrefix = "--mpgsql=";
if (args.Length == 4 && (args[3].StartsWith(npgsqlTcpPrefix, StringComparison.Ordinal)
    || args[3].StartsWith(mpgsqlTcpPrefix, StringComparison.Ordinal)))
{
    var compareMpgsqlBaseline = args[3].StartsWith(mpgsqlTcpPrefix, StringComparison.Ordinal);
    var prefix = compareMpgsqlBaseline ? mpgsqlTcpPrefix : npgsqlTcpPrefix;
    var selectedCases = args[3][prefix.Length..].Split(',', StringSplitOptions.TrimEntries);
    await NpgsqlComparison.RunAsync(args[0], args[1], args[2],
        compareMpgsqlBaseline: compareMpgsqlBaseline, selectedCases: selectedCases);
    return;
}
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
await File.WriteAllTextAsync
(
    output, JsonSerializer.Serialize
    (
        new
        {
            Baseline = Path.GetFullPath(args[0]),
            Candidate = Path.GetFullPath(args[1]),
            Runtime = RuntimeInformation.FrameworkDescription,
            ProcessPriority = comparisonProcess.PriorityClass.ToString(),
            LogicalProcessors = Environment.ProcessorCount,
            ServerGC = GCSettings.IsServerGC,
            Samples = 40,
            WarmupSeconds = 5,
            TargetSampleMilliseconds = 120,
            Confidence = "95% paired Student t interval; t(39)=2.023",
            AllocationScope = "Whole managed process, including identical TCP peer; Batch16 caller construction excluded",
            ExclusiveBaseline = "Previous explicit connection/typed command; single-use command construction outside timing, candidate command reused",
            Results = results
        }, new JsonSerializerOptions
        {
            WriteIndented = true
        }
    )
);

async Task CompareAsync(
    string scenario, string path,
    string oldClass, string oldMethod,
    string newClass, string newMethod
)
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
    long? oldCopied = oldRun.CopiedRowBytes,
        newCopied = newRun.CopiedRowBytes;
    var pairs = new List<Pair>();
    for (var sample = 0;
         sample < 40;
         sample++)
    {
        Measurement before,
            after;
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
    double oldMean = pairs.Average(p => p.BaselineNanoseconds),
        newMean = pairs.Average(p => p.CandidateNanoseconds);
    var logRatio = pairs.Average(p => Math.Log(p.CandidateNanoseconds / p.BaselineNanoseconds));
    var sd = Math.Sqrt(pairs.Sum(p => Math.Pow(Math.Log(p.CandidateNanoseconds / p.BaselineNanoseconds) - logRatio, 2)) / (pairs.Count - 1));
    var halfWidth = 2.023 * sd / Math.Sqrt(pairs.Count);
    double ratio = Math.Exp(logRatio),
        lower = Math.Exp(logRatio - halfWidth),
        upper = Math.Exp(logRatio + halfWidth);
    results.Add
    (
        new
        {
            Case = scenario,
            Path = path,
            InvocationsPerSample = invocations,
            OperationsPerInvocation = oldRun.Operations,
            BaselineMicroseconds = oldMean / 1000,
            CandidateMicroseconds = newMean / 1000,
            BaselineOperationsPerSecond = 1e9 / oldMean,
            CandidateOperationsPerSecond = 1e9 / newMean,
            BaselineBytes = pairs.Average(p => p.BaselineBytes),
            CandidateBytes = pairs.Average(p => p.CandidateBytes),
            BaselineCopiedBytesPerOperation = (oldRun.CopiedRowBytes - oldCopied) / (double)(pairs.Count * invocations * oldRun.Operations),
            CandidateCopiedBytesPerOperation = (newRun.CopiedRowBytes - newCopied) / (double)(pairs.Count * invocations * newRun.Operations),
            PairedRatio = ratio,
            Ratio95Lower = lower,
            Ratio95Upper = upper,
            MeetsFivePercentBound = upper <= 1.05,
            Pairs = pairs
        }
    );
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
    private readonly Action? _checkIdle;
    private readonly Func<(long Queries, long Syncs, long ReplyBytes, int Connections)>? _peerCounters;
    private readonly bool _validateWireCounters;
    private BenchmarkRun(
        object instance, Type type,
        string method, bool validateWireCounters
    )
    {
        const BindingFlags members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        _run = type.GetMethod(method)!.CreateDelegate<Func<Task<long>>>(instance);
        _cleanup = type.GetMethod("Cleanup")!.CreateDelegate<Func<Task>>(instance);
        var isLive = nameIsLive(type);
        if ((!isLive || method is "MpgsqlFacadeBatch" or "MpgsqlFacadeBatchStandard" or "NpgsqlFreshBatch")
            && type.GetMethod(method.StartsWith("Npgsql", StringComparison.Ordinal) ? "PrepareNpgsql" : "PrepareMpgsql") is { } prepare)
        {
            _prepare = prepare.CreateDelegate<Action>(instance);
            Operations = 32;
        }
        else
        {
            Operations = 1;
        }
        Operations = (int?)type.GetProperty("OperationsPerInvocation", members)?.GetValue(instance) ?? Operations;
        QueriesPerOperation = (int?)type.GetProperty("QueriesPerOperation", members)?.GetValue(instance) ?? (_prepare is null ? 1 : 16);
        SyncsPerOperation = (int?)type.GetProperty("SyncsPerOperation", members)?.GetValue(instance) ?? 1;
        ExpectedChecksum = (long?)type.GetProperty("ExpectedChecksum", members)?.GetValue(instance);
        if (Operations <= 0 || QueriesPerOperation <= 0 || SyncsPerOperation <= 0)
            throw new InvalidDataException("Benchmark operation counts must be positive.");
        var fixture = (type.GetField("_m", members) ?? type.GetField("_fixture", members))?.GetValue(instance);
        _copied = fixture is null ? null : CreateCopiedCounter(fixture);
        var bufferFixture = method.StartsWith("Npgsql", StringComparison.Ordinal)
            ? (type.GetField("_nativeFixture", members) ?? type.GetField("_n", members))?.GetValue(instance)
            : fixture;
        ConfiguredReadBufferSize = (int?)bufferFixture?.GetType().GetProperty("ReadBufferSize", members)?.GetValue(bufferFixture);
        ConnectionString = (string?)bufferFixture?.GetType().GetProperty("ConnectionString", members)?.GetValue(bufferFixture);
        TransportInstrumented = method.StartsWith("Npgsql", StringComparison.Ordinal)
            ? false : isLive ? false : (bool?)fixture?.GetType().GetProperty("InstrumentTransport", members)?.GetValue(fixture);
        CoalesceReplies = (bool?)bufferFixture?.GetType().GetProperty("CoalesceReplies", members)?.GetValue(bufferFixture) ?? false;
        LiveEndpoint = isLive ? type.GetProperty("Endpoint", members)?.GetValue(instance) : null;
        LiveBackendProcessId = isLive ? (int?)type.GetProperty("BackendProcessId", members)?.GetValue(instance) : null;
        LiveServerVersion = isLive ? (string?)type.GetProperty("ServerVersion", members)?.GetValue(instance) : null;
        _validateWireCounters = validateWireCounters;
        if (validateWireCounters)
        {
            var actualFixture = bufferFixture ?? throw new InvalidDataException("Acceptance fixture is missing.");
            _checkIdle = actualFixture.GetType().GetMethod("CheckIdle", members)!
                .CreateDelegate<Action>(actualFixture);
            var peer = actualFixture.GetType().GetProperty("Peer", members)!.GetValue(actualFixture)!;
            _peerCounters = peer.GetType().GetMethod("Counters", members)!
                .CreateDelegate<Func<(long Queries, long Syncs, long ReplyBytes, int Connections)>>(peer);
        }
        else if (isLive)
            _checkIdle = type.GetMethod("CheckIdle", members)!.CreateDelegate<Action>(instance);
    }
    private static bool nameIsLive(Type type) => type.FullName == "Mpgsql.Benchmarks.PostgresAdoBenchmarks";
    private BenchmarkRun(
        Func<Task<long>> run, Func<Task> cleanup,
        Action prepare, Func<long>? copied
    )
    {
        _run = run;
        _cleanup = cleanup;
        _prepare = prepare;
        _copied = copied;
        Operations = 1;
    }
    internal long? CopiedRowBytes => _copied?.Invoke();
    internal int Operations { get; }
    internal int QueriesPerOperation { get; } = 1;
    internal int SyncsPerOperation { get; } = 1;
    internal long? ExpectedChecksum { get; }
    internal int? ConfiguredReadBufferSize { get; }
    internal string? ConnectionString { get; }
    internal bool? TransportInstrumented { get; }
    internal bool CoalesceReplies { get; }
    internal object? LiveEndpoint { get; }
    internal int? LiveBackendProcessId { get; }
    internal string? LiveServerVersion { get; }
    internal long Checksum { get; private set; }
    public ValueTask DisposeAsync()
    {
        return new ValueTask(_cleanup());
    }
    private static Func<long>? CreateCopiedCounter(object fixture)
    {
        const BindingFlags members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        if (fixture
                .GetType()
                .GetProperty("Transports", members)
                ?.GetValue(fixture) is not IEnumerable transports)
        {
            return null;
        }
        var counters = new List<Func<long>>();
        foreach (var transport in transports)
        {
            var session = transport
                .GetType()
                .GetProperty("Session", members)!.GetValue(transport)!;
            counters.Add
            (
                session
                    .GetType()
                    .GetProperty("CopiedRowBytes", members)!.GetMethod!.CreateDelegate<Func<long>>(session)
            );
        }
        return () => counters.Sum(read => read());
    }
    internal static async Task<BenchmarkRun> CreateAsync(
        VersionContext context, string name,
        string method, string scenario, int bufferOverride = 0, bool? instrumentTransport = null,
        bool validateWireCounters = false, bool? coalesceReplies = null
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bufferOverride);
        if (name == "LegacyConnection")
        {
            if (bufferOverride != 0)
            {
                throw new NotSupportedException("The legacy benchmark has no read-buffer override.");
            }
            return await CreateLegacyAsync(context, scenario);
        }
        var type = context.Benchmarks.GetType("Mpgsql.Benchmarks." + name, true)!;
        var instance = Activator.CreateInstance(type)!;
        type
            .GetProperty("Case")
            ?.SetValue(instance, scenario);
        if (instrumentTransport is { } instrumentation && !nameIsLive(type))
        {
            var property = type.GetProperty("InstrumentTransport")
                ?? throw new NotSupportedException("The benchmark has no transport-instrumentation setting.");
            property.SetValue(instance, instrumentation);
        }
        if (coalesceReplies is { } coalescing && (coalescing || type.GetProperty("CoalesceReplies") is not null))
        {
            var property = type.GetProperty("CoalesceReplies")
                ?? throw new NotSupportedException("The benchmark has no coalesced-reply setting.");
            property.SetValue(instance, coalescing);
        }
        if (bufferOverride != 0)
        {
            var bufferProperty = type.GetProperty("ReadBufferSize")
                ?? throw new NotSupportedException("The benchmark has no read-buffer override.");
            bufferProperty.SetValue(instance, bufferOverride);
        }
        var setupName = method.StartsWith("Npgsql", StringComparison.Ordinal)
            ? method switch
            {
                "NpgsqlConnection" => "SetupConnection",
                "NpgsqlPool" => "SetupPool",
                "NpgsqlMultiplexed" => "SetupMultiplexed",
                _ => "SetupNpgsql"
            }
            : "SetupMpgsql";
        await (Task)(type.GetMethod(setupName) ?? type.GetMethod("Setup"))!.Invoke(instance, null)!;
        var run = new BenchmarkRun(instance, type, method, validateWireCounters);
        if (instrumentTransport is { } expectedInstrumentation && run.TransportInstrumented != expectedInstrumentation)
        {
            await run.DisposeAsync();
            throw new InvalidDataException("The fixture did not apply the requested instrumentation setting.");
        }
        if (bufferOverride != 0 && run.ConfiguredReadBufferSize != bufferOverride)
        {
            await run.DisposeAsync();
            throw new InvalidDataException("The fixture did not apply the requested read-buffer size.");
        }
        if (coalesceReplies is { } expectedCoalescing && run.CoalesceReplies != expectedCoalescing)
        {
            await run.DisposeAsync();
            throw new InvalidDataException("The fixture did not apply the requested reply policy.");
        }
        run._prepare?.Invoke();
        run.Checksum = await run._run();
        if (run.ExpectedChecksum is { } expectedChecksum && run.Checksum != expectedChecksum)
        {
            await run.DisposeAsync();
            throw new InvalidDataException("Execution benchmark checksum does not match its specified result.");
        }
        run._checkIdle?.Invoke();
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
        var source = fixture
            .GetType()
            .GetProperty("Source", members)!.GetValue(fixture)!;
        var opening = source
            .GetType()
            .GetMethod("OpenConnectionAsync")!.Invoke(source, [default(CancellationToken)])!;
        var openTask = (Task)opening
            .GetType()
            .GetMethod("AsTask")!.Invoke(opening, null)!;
        await openTask;
        var connection = openTask
            .GetType()
            .GetProperty("Result")!.GetValue(openTask)!;
        var creator = connection
            .GetType()
            .GetMethod("CreateCommand", [typeof(string)])!.CreateDelegate<Func<string, object>>(connection);
        var query = type.GetField("_scenario", members)!.GetValue(instance)!;
        var sql = (string)query
            .GetType()
            .GetProperty("Sql")!.GetValue(query)!;
        var catalog = type.GetField("_catalog", members)!.GetValue(instance)!;
        var inputs = (Array)catalog
            .GetType()
            .GetProperty("Inputs", members)!.GetValue(catalog)!;
        var values = (Array)((Array)inputs.GetValue(0)!).GetValue(0)!;
        var buffers = (byte[][])fixture
            .GetType()
            .GetProperty("Buffers", members)!.GetValue(fixture)!;
        var command = creator(sql);
        var commandArgument = Expression.Parameter(typeof(object));
        var parameters = Expression.Property(Expression.Convert(commandArgument, command.GetType()), "Parameters");
        var add = parameters.Type.GetMethod
        (
            "Add", [
                values
                    .GetType()
                    .GetElementType()!
            ]
        )!;
        var adds = Enumerable
            .Range(0, values.Length)
            .Select(i => Expression.Call(parameters, add, Expression.ArrayIndex(Expression.Constant(values), Expression.Constant(i))));
        var copyParameters = Expression
            .Lambda<Action<object>>(Expression.Block(adds), commandArgument)
            .Compile();

        void Prepare()
        {
            command = creator(sql);
            copyParameters(command);
        }

        Prepare();
        var execute = command!
            .GetType()
            .GetMethod("ExecuteReaderAsync")!;
        var reader = execute
            .ReturnType.GetGenericArguments()[0];
        var consumer = context.Benchmarks.GetType("Mpgsql.Benchmarks.Comparison.TcpQueryOperations", true)!
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single
            (m => m.Name == "ConsumeAsync" && m
                    .GetParameters()[0].ParameterType == reader
            );
        var bridge = (Func<object, Task<long>>)typeof(BenchmarkRun).GetMethod(nameof(CreateLegacyBridge), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(reader)
            .Invoke(null, [execute, consumer, query, buffers[0]])!;
        var cleanup = type.GetMethod("Cleanup")!.CreateDelegate<Func<Task>>(instance);
        var run = new BenchmarkRun
        (
            () => bridge(command!), async () =>
            {
                await ((IAsyncDisposable)connection).DisposeAsync();
                await cleanup();
            }, Prepare, CreateCopiedCounter(fixture)
        );
        run.Checksum = await run._run();
        return run;
    }
    private static Func<object, Task<long>> CreateLegacyBridge<T>(
        MethodInfo method, MethodInfo consumer,
        object scenario, byte[] buffer
    )
    {
        var parameter = Expression.Parameter(typeof(object));
        var execute = Expression
            .Lambda<Func<object, ValueTask<T>>>(Expression.Call(Expression.Convert(parameter, method.DeclaringType!), method, Expression.Constant(default(CancellationToken))), parameter)
            .Compile();
        var reader = Expression.Parameter(typeof(T));
        var consume = Expression
            .Lambda<Func<T, Task<long>>>(Expression.Call(consumer, reader, Expression.Constant(scenario), Expression.Constant(buffer)), reader)
            .Compile();
        return async command =>
        {
            try
            {
                var value = await execute(command)
                    .ConfigureAwait(false);
                await using ((IAsyncDisposable)value!)
                {
                    return await consume(value)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                await ((IAsyncDisposable)command)
                    .DisposeAsync()
                    .ConfigureAwait(false);
            }
        };
    }
    internal async Task<Measurement> MeasureAsync(int invocations)
    {
        _checkIdle?.Invoke();
        var before = _peerCounters?.Invoke() ?? default;
        long elapsed = 0,
            allocated = 0;
        if (_prepare is null)
        {
            long bytes = GC.GetTotalAllocatedBytes(true),
                start = Stopwatch.GetTimestamp();
            for (var i = 0;
                 i < invocations;
                 i++)
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
            for (var i = 0;
                 i < invocations;
                 i++)
            {
                _prepare();
                long bytes = GC.GetTotalAllocatedBytes(true),
                    start = Stopwatch.GetTimestamp();
                if (await _run() != Checksum)
                {
                    throw new InvalidDataException("Unstable checksum.");
                }
                elapsed += Stopwatch.GetTimestamp() - start;
                allocated += GC.GetTotalAllocatedBytes(true) - bytes;
            }
        }
        _checkIdle?.Invoke();
        if (_validateWireCounters)
        {
            var after = _peerCounters!.Invoke();
            var expectedSyncs = (long)invocations * Operations * SyncsPerOperation;
            var expectedQueries = (long)invocations * Operations * QueriesPerOperation;
            if (after.Queries - before.Queries != expectedQueries || after.Syncs - before.Syncs != expectedSyncs
                || after.Connections != before.Connections)
            {
                throw new InvalidDataException("Acceptance block query/Sync count or physical connection count changed.");
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
