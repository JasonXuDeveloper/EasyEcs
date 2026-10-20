using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// Giving memory back after a peak: 100,000 entities spread over <see cref="Tables"/> tables (combinations of eight
/// tags) under a system and cached queries, then all but one in a hundred destroyed. <c>TrimExcess</c> drops the
/// emptied tables and shrinks the others (one call per iteration, on a world built for it); <c>TrimTrimmed</c> is the
/// call on a world with nothing left to give back (its fixed cost: the walk over tables, queries and buffers).
/// The steady state it must leave alone: WorldSimulationBenchmark and BattleServerBenchmark do not call it.
/// </summary>
[MemoryDiagnoser]
public class TrimBenchmark
{
    [Params(16, 256)] public int Tables;

    private const int Peak = 100_000;
    private World _world;
    private World _trimmed;

    [GlobalSetup]
    public void Trimmed()
    {
        _trimmed = Build(Tables);
        _trimmed.TrimExcess();
    }

    [GlobalCleanup]
    public void Dispose() => _trimmed.Dispose();

    [IterationSetup(Target = nameof(TrimExcess))]
    public void AfterPeak() => _world = Build(Tables);

    [IterationCleanup(Target = nameof(TrimExcess))]
    public void Cleanup() => _world.Dispose();

    [Benchmark, InvocationCount(1)]
    public int TrimExcess()
    {
        _world.TrimExcess();
        return _world.EntityCount;
    }

    [Benchmark]
    public int TrimTrimmed()
    {
        _trimmed.TrimExcess();
        return _trimmed.EntityCount;
    }

    private static World Build(int tables)
    {
        var world = new World(new WorldOptions { InitialEntityCapacity = Peak });
        world.AddSystem<Drift>();
        var entities = new Entity[Peak];
        world.CreateEntities(entities, new Position(1, 2), new Velocity(1, 1));
        for (int i = 0; i < entities.Length; i++)
        {
            int combination = i & (tables - 1);
            var entity = entities[i];
            if ((combination & 1) != 0) entity.Set(new T0());
            if ((combination & 2) != 0) entity.Set(new T1());
            if ((combination & 4) != 0) entity.Set(new T2());
            if ((combination & 8) != 0) entity.Set(new T3());
            if ((combination & 16) != 0) entity.Set(new T4());
            if ((combination & 32) != 0) entity.Set(new T5());
            if ((combination & 64) != 0) entity.Set(new T6());
            if ((combination & 128) != 0) entity.Set(new T7());
        }

        world.Update();
        _ = world.Count<Position>(QueryFilter.Create().With<T0>()) + world.Count<Position>(QueryFilter.Create().Without<T7>());
        for (int i = 0; i < entities.Length; i++)
            if (i % 100 != 0) entities[i].Destroy();
        return world;
    }

    public record struct Position(float X, float Y) : IComponent;
    public record struct Velocity(float X, float Y) : IComponent;
    public struct T0 : IComponent;
    public struct T1 : IComponent;
    public struct T2 : IComponent;
    public struct T3 : IComponent;
    public struct T4 : IComponent;
    public struct T5 : IComponent;
    public struct T6 : IComponent;
    public struct T7 : IComponent;

    public class Drift : EcsSystem
    {
        [ForEach]
        private static void Move(ref Position position, in Velocity velocity)
        {
            position.X += velocity.X;
            position.Y += velocity.Y;
        }
    }
}
