using System;
using BenchmarkDotNet.Attributes;
using EcsComparison.ArchPort;
using EcsComparison.DefaultEcsPort;
using EcsComparison.FrifloPort;

namespace EcsComparison;

/// <summary>
/// One server tick of the headline world (EasyEcs.Benchmark/WorldSimulation.cs) in each library: same rules,
/// Rng, spawn mix and entity counts, single-threaded, each library used through its recommended fast path.
/// Each case builds only its own world; setup warms it up for 200 ticks, requires a busy world and requires the
/// counters to match an EasyEcs reference run of the same length (see Check.Matches).
/// </summary>
[MemoryDiagnoser]
public class EcsComparisonBenchmark
{
    [Params(10_000, 100_000)] public int Entities;

    private EasyEcsSimulation _easyEcs;
    private ArchSimulation _arch;
    private FrifloSimulation _friflo;
    private DefaultEcsSimulation _defaultEcs;

    [GlobalSetup(Target = nameof(EasyEcs))] public void SetupEasyEcs() => _easyEcs = Prepare(new EasyEcsSimulation(Entities));
    [GlobalSetup(Target = nameof(Arch))] public void SetupArch() => _arch = Prepare(new ArchSimulation(Entities));
    [GlobalSetup(Target = nameof(Friflo))] public void SetupFriflo() => _friflo = Prepare(new FrifloSimulation(Entities));
    [GlobalSetup(Target = nameof(DefaultEcs))] public void SetupDefaultEcs() => _defaultEcs = Prepare(new DefaultEcsSimulation(Entities));

    [GlobalCleanup]
    public void Cleanup()
    {
        _easyEcs?.Dispose();
        _arch?.Dispose();
        _friflo?.Dispose();
        _defaultEcs?.Dispose();
    }

    [Benchmark(Baseline = true)] public void EasyEcs() => _easyEcs.Tick();
    [Benchmark] public void Arch() => _arch.Tick();
    [Benchmark] public void Friflo() => _friflo.Tick();
    [Benchmark] public void DefaultEcs() => _defaultEcs.Tick();

    private T Prepare<T>(T sim) where T : ISimulation
    {
        var counters = Check.WarmUp(sim);
        Console.WriteLine($"// {sim.Library,-10} {counters}");
        if (sim is not EasyEcsSimulation)
        {
            // The reference world is built after the measured one and dropped before measuring.
            using var reference = new EasyEcsSimulation(Entities);
            var expected = Check.WarmUp(reference);
            Console.WriteLine($"// {"EasyEcs",-10} {expected} (reference)");
            Check.Matches(expected, counters, sim.Library);
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return sim;
    }
}
