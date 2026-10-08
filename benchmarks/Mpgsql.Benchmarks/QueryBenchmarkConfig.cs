using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;

namespace Mpgsql.Benchmarks;

public sealed class QueryBenchmarkConfig : ManualConfig
{
    public QueryBenchmarkConfig()
    {
        // Reuse the user's power policy; do not change machine-wide settings for a Pipe baseline.
        AddJob(Job.Default.WithId("QueryBaseline").WithLaunchCount(1).WithWarmupCount(3).WithIterationCount(8)
            .DontEnforcePowerPlan());
    }
}