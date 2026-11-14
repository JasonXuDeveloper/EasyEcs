using System;
using System.Collections.Generic;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Characteristics;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains;
using BenchmarkDotNet.Toolchains.CsProj;
using BenchmarkDotNet.Toolchains.DotNetCli;
using BenchmarkDotNet.Validators;
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
/// <para>The runtime is the host's: the projects target net10.0 and net11.0, and <c>dotnet run --framework net11.0</c>
/// measures .NET 11. <c>--frameworks net10.0,net11.0</c> runs the job once per runtime in one process instead, the
/// runtimes interleaved per case, which is how one runtime is compared with the other (BenchmarkDotNet's own
/// <c>--runtimes</c> knows no runtime newer than its release: <c>OnRuntime</c>).</para>
/// </summary>
public static class BenchmarkConfig
{
    /// <summary>The config for <paramref name="args"/>; <c>--dev</c> is taken out of them.</summary>
    public static IConfig Create(ref string[] args)
    {
        bool dev = Array.IndexOf(args, "--dev") >= 0;
        if (dev) args = Array.FindAll(args, a => a != "--dev");
        string[] frameworks = [];
        int at = Array.IndexOf(args, "--frameworks");
        if (at >= 0 && at + 1 < args.Length)
        {
            frameworks = args[at + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            args = [.. args[..at], .. args[(at + 2)..]];
        }
        var job = dev
            ? Job.Default.WithLaunchCount(1).WithWarmupCount(3).WithIterationCount(5)
                .WithIterationTime(TimeInterval.FromMilliseconds(150)).WithId("Dev")
            : Job.Default.WithLaunchCount(2).WithWarmupCount(6).WithIterationCount(8)
                .WithIterationTime(TimeInterval.FromMilliseconds(250));
        job = job.WithEnvironmentVariable("DOTNET_TC_CallCountingDelayMs", "0");
        string host = $"net{Environment.Version.Major}.{Environment.Version.Minor}";
        // Sequential builds: BenchmarkDotNet 0.15 decides on parallel builds by GetRuntimeVersion, which throws for a
        // runtime it does not name (net11.0); with this option set it never asks
        var config = DefaultConfig.Instance.WithOptions(ConfigOptions.DisableParallelBuild);
        if (frameworks.Length == 0) return config.AddJob(OnRuntime(job, host, true).AsDefault());
        foreach (string framework in frameworks)
            config = config.AddJob(OnRuntime(job.WithId(framework), framework, framework == host));
        return config;
    }

    /// <summary>
    /// <paramref name="job"/> on <paramref name="framework"/>. BenchmarkDotNet 0.15 names no runtime past .NET 10, and
    /// its SDK check throws on one it does not name (GetRuntimeVersion of RuntimeMoniker.NotRecognized), also for the
    /// host's own runtime: such a runtime (net11.0) gets the project toolchain of its target framework without that
    /// check. A runtime it names (net10.0) keeps everything as before; the host's then needs no runtime of its own.
    /// </summary>
    private static Job OnRuntime(Job job, string framework, bool isHost)
    {
        if (framework == "net10.0") return isHost ? job : job.WithRuntime(CoreRuntime.Core10_0);
        var runtime = CoreRuntime.CreateForNewVersion(framework, ".NET " + framework[3..]);
        return job.WithRuntime(runtime)
            .WithToolchain(new Unchecked(CsProjCoreToolchain.From(new NetCoreAppSettings(framework, null, runtime.Name))));
    }

    /// <summary>A toolchain whose validation is skipped: the build fails on its own if the SDK lacks the runtime.</summary>
    private sealed class Unchecked(IToolchain inner) : IToolchain
    {
        public string Name => inner.Name;
        public IGenerator Generator => inner.Generator;
        public IBuilder Builder => inner.Builder;
        public IExecutor Executor => inner.Executor;
        public bool IsInProcess => inner.IsInProcess;
        public IEnumerable<ValidationError> Validate(BenchmarkCase benchmarkCase, IResolver resolver) => [];
    }
}
