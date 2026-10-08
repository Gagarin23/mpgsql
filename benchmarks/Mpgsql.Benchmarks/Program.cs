#if PROTOCOL_BASELINE
extern alias baseline;
using OriginalFrontend = baseline::Mpgsql.Protocol.FrontendMessage;
using OriginalBackend = baseline::Mpgsql.Protocol.BackendMessage;
#endif

using System.Runtime.CompilerServices;
using BenchmarkDotNet.Running;
using Mpgsql.Benchmarks;
using Mpgsql.Benchmarks.Queries;
using Mpgsql.Benchmarks.Comparison;
using Mpgsql.Protocol;

if (args.Contains("--verify-converters"))
{
    try
    {
        int index = Array.IndexOf(args, "--converter-catalog");
        Mpgsql.Benchmarks.Converters.ConverterVerification.Run(index < 0 ? null : args[index + 1]);
    }
    catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--verify-int64-simd-regression"))
{
    Int64ArraySimdRegressionVerification.Run();
    return;
}

if (args.Contains("--verify-profiler-api"))
{
    try
    {
        int apiIndex = Array.IndexOf(args, "--profiler-api");
        if (apiIndex < 0 || apiIndex + 1 == args.Length) throw new ArgumentException("--profiler-api is required.");
        BatchProfilerControl.VerifyApi(args[apiIndex + 1]);
    }
    catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--query-pipeline-profile"))
{
    try { await TcpPipelineProfileRunner.RunAsync(args).WaitAsync(TimeSpan.FromMinutes(3)); }
    catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--verify-query-pipeline-profile"))
{
    try { await TcpPipelineProfileRunner.VerifyAsync().WaitAsync(TimeSpan.FromMinutes(2)); }
    catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--query-batch-profile"))
{
    try { await TcpBatchProfileRunner.RunAsync(args).WaitAsync(TimeSpan.FromMinutes(3)); }
    catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--verify-query-batch-profile"))
{
    try { await TcpBatchProfileRunner.VerifyAsync().WaitAsync(TimeSpan.FromMinutes(2)); }
    catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--query-compare-load"))
{
    try { await TcpComparisonLoadRunner.RunAsync(args); }
    catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--verify-query-compare"))
{
    try { await TcpComparisonVerification.RunAsync().WaitAsync(TimeSpan.FromMinutes(3)); }
    catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--query-load"))
{
    try { await QueryLoadRunner.RunAsync(args); }
    catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--verify-query"))
{
    try { await QueryBenchmarkVerification.RunAsync(); }
    catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--copy-live"))
{
    try { Mpgsql.Benchmarks.Live.BinaryCopyLiveBenchmarks.Run(args); }
    catch (Exception error)
    {
        Console.Error.WriteLine(error);
        Environment.ExitCode = 1;
    }
    return;
}

if (args.Contains("--verify"))
{
    try { BuiltinConverterVerification.Run(); await QueryBenchmarkVerification.RunAsync(); await TcpComparisonVerification.RunAsync().WaitAsync(TimeSpan.FromMinutes(3)); }
    catch (Exception error)
    {
        Console.Error.WriteLine(error);
        Environment.ExitCode = 1;
        return;
    }
    new Int64ConverterBenchmarks().Setup();
    Console.WriteLine("Scalar bigint/nullable bigint results and zero-allocation span/writer/segmented paths verified.");
    new SyncBenchmarks().Setup();
    new ExecuteBenchmarks().Setup();
    foreach (int length in new[] {32, 4096})
        new ParseBenchmarks {QueryLength = length}.Setup();
    foreach (int count in new[] {1, 16})
        new BindBenchmarks {Parameters = count}.Setup();
    foreach (int count in new[] {1, 8, 64})
    foreach (bool fragmented in new[] {false, true})
        new DataRowBenchmarks {Columns = count, Fragmented = fragmented}.Setup();
    foreach (bool ready in new[] {false, true})
        new BackendControlBenchmarks {ReadyForQuery = ready}.Setup();
    foreach (int count in new[] {0, 1, 3, 4, 5, 8, 256, 4096, 65536})
    {
        var write = new Int64ArrayWriteBenchmarks {Count = count};
        write.Setup();
        write.CheckReusableAllocations();
        foreach (int segmentSize in new[] {0, 7, 4096})
        {
            var read = new Int64ArrayReadBenchmarks {Count = count, SegmentSize = segmentSize};
            read.Setup();
            read.CheckReusableAllocations();
        }
    }
    Console.WriteLine("All array encoder bytes, decoder results, and zero-allocation reusable paths verified.");
    Mpgsql.Benchmarks.NpgsqlBaseline.NpgsqlArrayVerification.Run();
    NullableInt64ArrayVerification.Run();
    BinaryCopyBenchmarks.Verify();

#if PROTOCOL_BASELINE
    Console.WriteLine("All benchmark packets and consumed rows match the baseline.");
    Console.WriteLine($"Original FrontendMessage: {Unsafe.SizeOf<OriginalFrontend>()} bytes");
    Console.WriteLine($"Original BackendMessage: {Unsafe.SizeOf<OriginalBackend>()} bytes");
#else
    Console.WriteLine("All current benchmark packets and consumed rows verified; no baseline snapshot is present.");
#endif
    Console.WriteLine($"EmptyMessage: {Unsafe.SizeOf<EmptyMessage>()} bytes");
    Console.WriteLine($"ExecuteMessage: {Unsafe.SizeOf<ExecuteMessage>()} bytes");
    Console.WriteLine($"ParseMessage: {Unsafe.SizeOf<ParseMessage>()} bytes");
    Console.WriteLine($"BindMessage: {Unsafe.SizeOf<BindMessage>()} bytes");
    Console.WriteLine($"BackendMessage: {Unsafe.SizeOf<BackendMessage>()} bytes");
    return;
}

var summaries = BenchmarkSwitcher.FromAssembly(typeof(SyncBenchmarks).Assembly).Run(args).ToArray();
if (summaries.Length == 0 || summaries.Any(summary => summary.HasCriticalValidationErrors
    || summary.Reports.Any(report => !report.Success || report.ResultStatistics is null)))
    Environment.ExitCode = 1;
