using System;
using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// One AI system over 100,000 NPCs (a think step: the distance to a target, then a new heading), run every update as
/// <c>[ForEach]</c> and as <c>[ForEach(Slices = 4)]</c>, which visits a quarter of the rows per update: the cost of
/// one update (the spike a tick pays) against the whole work spread over four. <c>Cycle</c> runs four updates, every
/// row once in both forms, so it shows what slicing itself costs. Also per entity with the handle (a structural pass)
/// and in parallel. Steady state must allocate 0 bytes per update.
/// </summary>
[MemoryDiagnoser]
public class SlicesBenchmark
{
    [Params(100_000)] public int Entities;
    [Params(1, 4)] public int Slices;
    [Params("Stable", "Entity", "Parallel")] public string Form;

    private World _world;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Entities });
        _world.CreateEntities(new Entity[Entities], new Npc(0, 0, 1, 0));
        int entity = 0;
        foreach (var item in _world.Query<Npc>()) item.Component1 = new Npc(entity % 1_000, entity++ / 1_000, 1, 0);
        _world.AddSystem((Slices, Form) switch
        {
            (1, "Stable") => new Think(),
            (1, "Entity") => new ThinkWithEntity(),
            (1, _) => new ThinkInParallel(),
            (_, "Stable") => new ThinkSliced(),
            (_, "Entity") => new ThinkWithEntitySliced(),
            _ => (EcsSystem)new ThinkInParallelSliced(),
        });
        for (int i = 0; i < 20; i++) _world.Update();
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    /// <summary>One update: every row (Slices = 1) or a quarter of them (Slices = 4).</summary>
    [Benchmark]
    public void Update() => _world.Update();

    /// <summary>Four updates: every row once per cycle in both forms.</summary>
    [Benchmark]
    public void Cycle()
    {
        for (int i = 0; i < 4; i++) _world.Update();
    }

    public record struct Npc(float X, float Y, float Dx, float Dy) : IComponent;

    private static void Decide(ref Npc npc)
    {
        float tx = 500 - npc.X, ty = 50 - npc.Y;
        float length = MathF.Sqrt(tx * tx + ty * ty) + 1e-3f;
        npc.Dx = tx / length;
        npc.Dy = ty / length;
        npc.X += npc.Dx;
        npc.Y += npc.Dy;
    }

    public class Think : EcsSystem
    {
        [ForEach]
        private static void Run(ref Npc npc) => Decide(ref npc);
    }

    public class ThinkSliced : EcsSystem
    {
        [ForEach(Slices = 4)]
        private static void Run(ref Npc npc) => Decide(ref npc);
    }

    public class ThinkWithEntity : EcsSystem
    {
        [ForEach]
        private static void Run(Entity entity, ref Npc npc)
        {
            Decide(ref npc);
            if (npc.X > 1e9f) entity.Destroy(); // never: a pass that may change structure
        }
    }

    public class ThinkWithEntitySliced : EcsSystem
    {
        [ForEach(Slices = 4)]
        private static void Run(Entity entity, ref Npc npc)
        {
            Decide(ref npc);
            if (npc.X > 1e9f) entity.Destroy();
        }
    }

    public class ThinkInParallel : EcsSystem
    {
        [ForEach(Parallel = true)]
        private static void Run(ref Npc npc) => Decide(ref npc);
    }

    public class ThinkInParallelSliced : EcsSystem
    {
        [ForEach(Parallel = true, Slices = 4)]
        private static void Run(ref Npc npc) => Decide(ref npc);
    }
}
