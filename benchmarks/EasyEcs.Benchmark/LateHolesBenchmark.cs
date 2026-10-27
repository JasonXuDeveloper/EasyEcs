using System;
using System.Threading;
using BenchmarkDotNet.Attributes;
using static EasyEcs.Benchmark.KernelBenchmark;

namespace EasyEcs.Benchmark;

/// <summary>
/// Passes whose runner reached Tier1 while their table had no holes, measured after every third row lost the
/// component the method reads (removed in place: a presence bit), so every word of the table is masked. Tier1 code is
/// compiled once: whatever the masked loops read that the first calls never touched (the permutation tables of a
/// component of several fields, the lane masks) is what that code reads for the rest of the process. <c>Move2</c> /
/// <c>Move3</c> deinterleave 2 / 3 floats (permutes in masked words, a flat loop without holes), <c>Step</c> one float
/// (lane masks only). A program meets this whenever a table gains its first holes after the pass ran a while.
/// </summary>
[MemoryDiagnoser, IsaColumn]
public class LateHolesBenchmark
{
    [Params(1_000, 100_000)] public int Entities;

    private World _world;

    [GlobalSetup(Target = nameof(Move2))]
    public void SetupMove2() => Setup(static w => Kernels.Move2(w), static e => e.Remove<V2>());

    [GlobalSetup(Target = nameof(Move3))]
    public void SetupMove3() => Setup(static w => Kernels.Move3(w), static e => e.Remove<V3>());

    [GlobalSetup(Target = nameof(Step))]
    public void SetupStep() => Setup(static w => Kernels.Step(w), static e => e.Remove<Mass>());

    /// <summary>
    /// The world, the pass run until its runner is compiled at Tier1 (calls, then time for the tiering thread), then a
    /// hole in every third row.
    /// </summary>
    private void Setup(Action<World> pass, Action<Entity> hole)
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Entities });
        _world.SetPull(0.75f);
        var random = new Random(1);
        float F() => (float)random.NextDouble() * 10 - 5;
        var entities = new Entity[Entities];
        _world.Reserve<P2, V2, P3, V3, Heat, Speed, Mass>(Entities);
        for (int i = 0; i < Entities; i++)
            entities[i] = _world.CreateEntity(new P2 { X = F(), Y = F() }, new V2 { X = F(), Y = F() }, new P3 { X = F(), Y = F(), Z = F() },
                new V3 { X = F(), Y = F(), Z = F() }, new Heat { Value = F() * 20 }, new Speed { Value = F() }, new Mass { Value = F() + 5 });
        for (int round = 0; round < 3; round++)
        {
            for (int i = 0; i < 100; i++) pass(_world);
            Thread.Sleep(300);
        }

        for (int i = 0; i < Entities; i += 3) hole(entities[i]);
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark]
    public void Move2() => Kernels.Move2(_world);

    [Benchmark]
    public void Move3() => Kernels.Move3(_world);

    [Benchmark]
    public void Step() => Kernels.Step(_world);
}
