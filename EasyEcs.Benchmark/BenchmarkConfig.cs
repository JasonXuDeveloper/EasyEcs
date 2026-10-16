using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using Perfolizer.Horology;

namespace EasyEcs.Benchmark;

/// <summary>
/// The default job of the benchmark projects: accurate in a few seconds per case. Run-to-run differences (code
/// layout, tiering) are larger than the noise within a run, so two processes are measured instead of many iterations
/// of one. Call counting starts at once, so the warmups reach optimized (Tier1, PGO) code before anything is measured.
/// A <c>--job</c> on the command line replaces it.
/// </summary>
public static class BenchmarkConfig
{
    public static IConfig Create() => DefaultConfig.Instance.AddJob(Job.Default
        .WithLaunchCount(2)
        .WithWarmupCount(6)
        .WithIterationCount(8)
        .WithIterationTime(TimeInterval.FromMilliseconds(250))
        .WithEnvironmentVariable("DOTNET_TC_CallCountingDelayMs", "0")
        .AsDefault());
}
