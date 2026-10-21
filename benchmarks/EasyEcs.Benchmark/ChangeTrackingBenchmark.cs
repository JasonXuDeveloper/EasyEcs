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
public class ChangeTrackingBenchmark
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

    public record struct Health(int Value) : IComponent, ITracked;

    /// <summary>The value last sent to clients.</summary>
    public record struct Replica(int Value) : IComponent;

    [UpdateInGroup<Replication>]
    public class ReplicateChanged : EcsSystem
    {
        [ForEach, Changed<Health>]
        private static void Copy(in Health health, ref Replica replica) => replica = new Replica(health.Value);
    }

    [UpdateInGroup<Replication>]
    public class ReplicateAll : EcsSystem
    {
        [ForEach]
        private static void Copy(in Health health, ref Replica replica)
        {
            if (replica.Value != health.Value) replica = new Replica(health.Value);
        }
    }
}

/// <summary>
/// A system that recomputes a tracked component every tick while only some values actually change: 100,000 aims, of
/// which 10% move each tick (each entity's aim steps every tenth tick, the phases spread evenly). <c>Mut = false</c>
/// writes through <c>ref Aim</c>, which stamps every visited row; <c>Mut = true</c> writes through
/// <c>Mut&lt;Aim&gt;.SetIfChanged</c>, which compares and stamps only the 10% that changed. <c>Write</c> is the writer
/// alone (the price of the comparison against the bulk stamp), <c>WriteAndRead</c> adds the downstream
/// <c>[Changed&lt;Aim&gt;]</c> replication, which visits every row after the ref writer and a tenth of them after the
/// Mut writer. Steady state allocates 0 bytes either way.
/// </summary>
[MemoryDiagnoser]
public class ChangeTrackingWriteBenchmark
{
    [Params(100_000)] public int Entities;
    [Params(false, true)] public bool Mut;

    private World _world;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Entities });
        _world.AddSingleton<Clock>();
        var entities = new Entity[Entities];
        _world.CreateEntities(entities, new Phase(0), new Aim(0), new Sent(0));
        for (int i = 0; i < entities.Length; i++) entities[i].Get<Phase>() = new Phase(i % 10);
        if (Mut) _world.AddSystem<AimIfChanged>();
        else _world.AddSystem<AimByRef>();
        _world.AddSystem<SendChanged>();
        for (int i = 0; i < 20; i++) WriteAndRead();
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark]
    public void Write()
    {
        _world.GetSingleton<Clock>().Tick++;
        _world.Update<Aiming>();
    }

    [Benchmark]
    public void WriteAndRead()
    {
        _world.GetSingleton<Clock>().Tick++;
        _world.Update<Aiming>();
        _world.Update<Sending>();
    }

    public struct Aiming : ISystemGroup;

    public struct Sending : ISystemGroup;

    public record struct Clock(int Tick) : ISingletonComponent;

    /// <summary>When in a cycle of ten ticks the entity's aim steps.</summary>
    public record struct Phase(int Value) : IComponent;

    public record struct Aim(int Value) : IComponent, ITracked;

    /// <summary>The aim last sent to clients.</summary>
    public record struct Sent(int Value) : IComponent;

    [UpdateInGroup<Aiming>]
    public class AimByRef : EcsSystem
    {
        [ForEach]
        private static void Recompute(in Phase phase, ref Aim aim, in Clock clock) => aim = new Aim((clock.Tick + phase.Value) / 10);
    }

    [UpdateInGroup<Aiming>]
    public class AimIfChanged : EcsSystem
    {
        [ForEach]
        private static void Recompute(in Phase phase, Mut<Aim> aim, in Clock clock) => aim.SetIfChanged(new Aim((clock.Tick + phase.Value) / 10));
    }

    [UpdateInGroup<Sending>]
    public class SendChanged : EcsSystem
    {
        [ForEach, Changed<Aim>]
        private static void Send(in Aim aim, ref Sent sent) => sent = new Sent(aim.Value);
    }
}

/// <summary>
/// A tracked component a system writes on some rows only (EECS053): 100,000 players, of which 100 die per tick (input
/// through handles) and respawn at the spawn point, then the <c>[Changed&lt;Position&gt;]</c> replication sends the
/// positions that changed. <c>Mut = false</c> takes <c>ref Position</c> and returns early for the living, so every
/// visited row is stamped and the replication visits every row; <c>Mut = true</c> takes <c>Mut&lt;Position&gt;</c>,
/// which stamps the 100 rows it writes. <c>Write</c> is the respawn alone, <c>WriteAndRead</c> adds the replication.
/// Steady state allocates 0 bytes either way.
/// </summary>
[MemoryDiagnoser]
public class ChangeTrackingSomeRowsBenchmark
{
    [Params(100_000)] public int Entities;
    [Params(false, true)] public bool Mut;

    private const int Deaths = 100;
    private World _world;
    private Entity[] _entities;
    private int _next;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Entities });
        _entities = new Entity[Entities];
        _world.CreateEntities(_entities, new Health(100), new Position(0), new Sent(0));
        if (Mut) _world.AddSystem<RespawnMut>();
        else _world.AddSystem<RespawnRef>();
        _world.AddSystem<SendChanged>();
        for (int i = 0; i < 20; i++) WriteAndRead();
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark]
    public void Write()
    {
        Kill();
        _world.Update<Respawning>();
    }

    [Benchmark]
    public void WriteAndRead()
    {
        Kill();
        _world.Update<Respawning>();
        _world.Update<Sending>();
    }

    /// <summary>Input: a different slice of players dies each tick (health is not tracked: no stamp).</summary>
    private void Kill()
    {
        for (int i = 0; i < Deaths; i++)
        {
            _next = (_next + 97) % _entities.Length;
            _entities[_next].Get<Health>().Value = 0;
        }
    }

    public struct Respawning : ISystemGroup;

    public struct Sending : ISystemGroup;

    public record struct Health(int Value) : IComponent;

    public record struct Position(int Value) : IComponent, ITracked;

    /// <summary>The position last sent to clients.</summary>
    public record struct Sent(int Value) : IComponent;

    [UpdateInGroup<Respawning>]
    public class RespawnRef : EcsSystem
    {
#pragma warning disable EECS053 // the reported side
        [ForEach]
        private static void Respawn(ref Health health, ref Position position)
        {
            if (health.Value > 0) return;
            health.Value = 100;
            position = new Position(position.Value + 1);
        }
#pragma warning restore EECS053
    }

    [UpdateInGroup<Respawning>]
    public class RespawnMut : EcsSystem
    {
        [ForEach]
        private static void Respawn(ref Health health, Mut<Position> position)
        {
            if (health.Value > 0) return;
            health.Value = 100;
            position.Write() = new Position(position.Value.Value + 1);
        }
    }

    [UpdateInGroup<Sending>]
    public class SendChanged : EcsSystem
    {
        [ForEach, Changed<Position>]
        private static void Send(in Position position, ref Sent sent) => sent = new Sent(position.Value);
    }
}

/// <summary>
/// A loop that only reads a tracked component, next to the <c>[Changed&lt;Health&gt;]</c> replication of the same
/// world: 100,000 entities, 10 written per tick, and a read of every health (a total for a status page) between the
/// writes and the replication. <c>Read = false</c> walks <c>Query&lt;Health&gt;</c>, whose items stamp every row they
/// visit, so the replication visits every row; <c>Read = true</c> walks <c>Read&lt;Health&gt;</c>, which stamps nothing,
/// so the replication visits the 10 written rows. Steady state allocates 0 bytes either way.
/// </summary>
[MemoryDiagnoser]
public class ChangeTrackingReadBenchmark
{
    [Params(100_000)] public int Entities;
    [Params(false, true)] public bool Read;

    private World _world;
    private Entity[] _entities;
    private int _next;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Entities });
        _entities = new Entity[Entities];
        _world.CreateEntities(_entities, new ChangeTrackingBenchmark.Health(100), new ChangeTrackingBenchmark.Replica(100));
        _world.AddSystem<ChangeTrackingBenchmark.ReplicateChanged>();
        for (int i = 0; i < 20; i++) Tick();
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark]
    public long Tick()
    {
        for (int i = 0; i < 10; i++)
        {
            _next = (_next + 97) % _entities.Length;
            _entities[_next].Get<ChangeTrackingBenchmark.Health>().Value--;
        }

        long total = 0;
        if (Read)
        {
            foreach (var item in _world.Read<ChangeTrackingBenchmark.Health>()) total += item.Component1.Value;
        }
        else
        {
#pragma warning disable EECS046 // the stamping of a loop that only reads is what this case measures
            foreach (var item in _world.Query<ChangeTrackingBenchmark.Health>()) total += item.Component1.Value;
#pragma warning restore EECS046
        }

        _world.Update<ChangeTrackingBenchmark.Replication>();
        return total;
    }
}
