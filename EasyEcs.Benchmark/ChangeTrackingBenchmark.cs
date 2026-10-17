using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// Replicating changed state, the job change tracking exists for: 100,000 entities, of which <see cref="Written"/> get
/// a new health per tick (input applied through handles), then one system copies health into the replicated copy.
/// <c>Changed</c> visits only the written rows ([Changed&lt;Health&gt;]); <c>All</c> visits every row, as a system
/// without change tracking must. 10 writes is a quiet tick (unwritten blocks of 4,096 rows are skipped whole); 1,000
/// writes spread 97 rows apart touch most 64-row words. Steady state allocates 0 bytes either way.
/// </summary>
[MemoryDiagnoser]
public partial class ChangeTrackingBenchmark
{
    [Params(100_000)] public int Entities;
    [Params(10, 1_000)] public int Written;
    [Params(false, true)] public bool Changed;

    private World _world;
    private Entity[] _entities;
    private int _next;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Entities });
        _entities = new Entity[Entities];
        _world.CreateEntities(_entities, new Health(100), new Replica(100));
        if (Changed) _world.AddSystem<ReplicateChanged>();
        else _world.AddSystem<ReplicateAll>();
        for (int i = 0; i < 20; i++) Tick();
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark]
    public void Tick()
    {
        // Input: a different slice of entities takes damage each tick.
        for (int i = 0; i < Written; i++)
        {
            _next = (_next + 97) % _entities.Length;
            _entities[_next].Get<Health>().Value--;
        }

        _world.Update<Replication>();
    }

    private static readonly QueryFilter HealthChanged = QueryFilter.Create().Changed<Health>(0);   // re-ticked with ChangedSince

    /// <summary>
    /// The same job as an ad-hoc query: <c>Changed</c> filters by the tick taken after the previous update (writes between
    /// two updates share one tick), so the default group's update (no systems) ends the tick.
    /// </summary>
    [Benchmark]
    public void QueryTick()
    {
        ulong mark = _world.ChangeTick;
        for (int i = 0; i < Written; i++)
        {
            _next = (_next + 97) % _entities.Length;
            _entities[_next].Get<Health>().Value--;
        }

        if (Changed)
        {
            foreach (var item in _world.Query<Health, Replica>(HealthChanged.Since(mark)))
                item.Component2 = new Replica(item.Component1.Value);
        }
        else
        {
            foreach (var item in _world.Query<Health, Replica>())
                if (item.Component2.Value != item.Component1.Value) item.Component2 = new Replica(item.Component1.Value);
        }

        _world.Update();
    }

    public struct Replication : ISystemGroup;

    public record struct Health(int Value) : IComponent, ITrackChanges;

    /// <summary>The value last sent to clients.</summary>
    public record struct Replica(int Value) : IComponent;

    [UpdateInGroup<Replication>]
    public partial class ReplicateChanged : EcsSystem
    {
        [ForEach, Changed<Health>]
        private static void Copy(in Health health, ref Replica replica) => replica = new Replica(health.Value);
    }

    [UpdateInGroup<Replication>]
    public partial class ReplicateAll : EcsSystem
    {
        [ForEach]
        private static void Copy(in Health health, ref Replica replica)
        {
            if (replica.Value != health.Value) replica = new Replica(health.Value);
        }
    }
}
