using System;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using Perfolizer.Horology;

namespace EasyEcs.Benchmark;

/// <summary>
/// The jobs of the benchmark projects. Both start call counting at once (<c>DOTNET_TC_CallCountingDelayMs=0</c>), so the
/// warmups reach optimized code (Tier1 with dynamic PGO, OSR'd loops replaced) before anything is measured; tiering
/// and PGO stay on, as in production.
/// <list type="bullet">
/// <item>Default: accurate in a few seconds per case. Run-to-run differences (code layout, tiering) are larger than
/// the noise within a run, so two processes are measured instead of many iterations of one. Keep / revert decisions
/// and the site's numbers use it.</item>
/// <item><c>--dev</c>: one process, 3 warmups and 5 iterations of 150 ms, about a fifth of the time. For trying ideas
/// and screening while developing; a result it shows is confirmed with the default job before it decides anything.</item>
/// </list>
/// A <c>--job</c> on the command line replaces either.
/// </summary>
public static class BenchmarkConfig
{
    /// <summary>The config for <paramref name="args"/>; <c>--dev</c> is taken out of them.</summary>
    public static IConfig Create(ref string[] args)
    {
        bool dev = Array.IndexOf(args, "--dev") >= 0;
        if (dev) args = Array.FindAll(args, a => a != "--dev");
        var job = dev
            ? Job.Default.WithLaunchCount(1).WithWarmupCount(3).WithIterationCount(5)
                .WithIterationTime(TimeInterval.FromMilliseconds(150)).WithId("Dev")
            : Job.Default.WithLaunchCount(2).WithWarmupCount(6).WithIterationCount(8)
                .WithIterationTime(TimeInterval.FromMilliseconds(250));
        return DefaultConfig.Instance.AddJob(job.WithEnvironmentVariable("DOTNET_TC_CallCountingDelayMs", "0").AsDefault());
    }
}
