using System;
using System.Numerics;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using EasyEcs;
using EasyEcs.Benchmark.Scenario;
using F = EcsComparison.FrifloPort;
using EntityStore = Friflo.Engine.ECS.EntityStore;
using static Friflo.Engine.ECS.EntityStoreExtensions;
using CommandBuffer = Friflo.Engine.ECS.CommandBuffer;
using Tags = Friflo.Engine.ECS.Tags;
using ComponentTypes = Friflo.Engine.ECS.ComponentTypes;
using Entity = EasyEcs.Entity;
using Position = EasyEcs.Benchmark.Scenario.Position;

namespace EcsComparison.Operations;

// Per-operation breakdown of the headline tick (diagnostics): the same operations in EasyEcs ([ForEach] systems run
// one group at a time) and Friflo (cached queries, Chunks + Span loops, CommandBuffer for changes inside a query).
// The world mixes the headline archetypes with interleaved ids: 30% players, 70% NPCs (all with Position, Velocity,
// Health, Attack, AoiCell), 10% of them Burning, and one projectile per three actors. Every operation leaves the
// world as it found it, so each invocation measures the same work.

public struct Iterate1 : ISystemGroup;
public struct Iterate2 : ISystemGroup;
public struct Iterate2Simd : ISystemGroup;
public struct Iterate3 : ISystemGroup;
public struct IterateWith : ISystemGroup;
public struct IterateEntity : ISystemGroup;
public struct IterateEntityBatch : ISystemGroup;
public struct IterateSingleton : ISystemGroup;
public struct Toggle : ISystemGroup;

/// <summary>Sum of the operation results (keeps every read alive).</summary>
public struct Sink : ISingletonComponent
{
    public long Value;
}

[UpdateInGroup<Iterate1>]
public class SumHealth : EcsSystem
{
    [ForEach]
    private static void Flip(ref Health h) => h.Max ^= 1;
}

[UpdateInGroup<Iterate2>]
public class Integrate : EcsSystem
{
    [ForEach, Without<Stunned>, Without<Dead>]
    private static void Move(ref Position p, ref Velocity v)
    {
        p.X += v.X;
        p.Y += v.Y;
        if (p.X < 0 || p.X > 4096) v.X = -v.X;
        if (p.Y < 0 || p.Y > 4096) v.Y = -v.Y;
    }
}

/// <summary>Iterate2 as a batch method: the same movement on whole runs of rows with SIMD (shared kernel).</summary>
[UpdateInGroup<Iterate2Simd>]
public class IntegrateSimd : EcsSystem
{
    [ForEach, Without<Stunned>, Without<Dead>]
    private static void Move(Span<Position> p, Span<Velocity> v) =>
        SimdMove.Run(MemoryMarshal.Cast<Position, float>(p), MemoryMarshal.Cast<Velocity, float>(v));
}

/// <summary>
/// Movement with bounce on interleaved X/Y floats: every lane is one coordinate, and its bounce condition only depends
/// on that coordinate, so the scalar code maps lane for lane onto Vector&lt;float&gt;. Used by both libraries.
/// </summary>
public static class SimdMove
{
    public static void Run(Span<float> p, Span<float> v)
    {
        var max = new Vector<float>(4096f);
        int i = 0;
        for (int width = Vector<float>.Count; i + width <= p.Length; i += width)
        {
            var position = new Vector<float>(p[i..]) + new Vector<float>(v[i..]);
            var velocity = new Vector<float>(v[i..]);
            var outside = Vector.LessThan(position, Vector<float>.Zero) | Vector.GreaterThan(position, max);
            position.CopyTo(p[i..]);
            Vector.ConditionalSelect(outside, -velocity, velocity).CopyTo(v[i..]);
        }

        for (; i < p.Length; i++)
        {
            p[i] += v[i];
            if (p[i] < 0 || p[i] > 4096) v[i] = -v[i];
        }
    }
}

[UpdateInGroup<Iterate3>]
public class Read3 : EcsSystem
{
    [ForEach]
    private static void Write(ref Position p, in Velocity v, in Health h) => p.X = v.X + h.Max;
}

[UpdateInGroup<IterateWith>]
public class PlayersHealth : EcsSystem
{
    [ForEach, With<Player>, Without<Dead>]
    private static void Flip(ref Health h) => h.Max ^= 1;
}

[UpdateInGroup<IterateEntity>]
public class EntityIds : EcsSystem
{
    [ForEach, Without<Dead>]
    private static void Sum(Entity entity, in Health h, ref Sink sink)
    {
        if (h.Value <= 0) sink.Value += entity.Id.Index; // never true: the handle is built, its use is rare (as in CheckDeath)
    }
}

[UpdateInGroup<IterateEntityBatch>]
public class EntityIdsBatch : EcsSystem
{
    [ForEach, Without<Dead>]
    private static void Sum(EntitySpan entities, ReadOnlySpan<Health> h, ref Sink sink)
    {
        for (int i = 0; i < h.Length; i++)
            if (h[i].Value <= 0) sink.Value += entities[i].Id.Index; // never true: a handle is built only for the rows that need one
    }
}

[UpdateInGroup<IterateSingleton>]
public class Cells : EcsSystem
{
    [ForEach, Without<Dead>]
    private static void UpdateCell(in Position p, ref AoiCell aoi, ref Sink sink)
    {
        int cell = (int)(p.Y * (1f / 64f)) * 64 + (int)(p.X * (1f / 64f)) + (int)(sink.Value & 1);
        if (cell == aoi.Cell) return;
        aoi.Cell = cell;
        sink.Value++;
    }
}

/// <summary>Stun 5% of the fighters, then recover all of them: 2 structural changes per stunned entity.</summary>
// The ports to the other libraries query the same components, so every library does the same work per row.
#pragma warning disable EECS026, EECS030 // and remove one entity at a time as they do (RemoveAll is the bulk benchmarks')
[UpdateInGroup<Toggle>]
public class StunToggle : EcsSystem
{
    [ForEach, Without<Stunned>]
    private static void Stun(Entity entity, in Attack attack)
    {
        if (Rng.Chance(0, entity.Id.Index, 2, 50)) entity.Set(new Stunned { Ticks = 1 });
    }
}

[UpdateInGroup<Toggle>]
public class StunRecover : EcsSystem
{
    [ForEach]
    private static void Recover(Entity entity, ref Stunned stunned) => entity.Remove<Stunned>();
}
#pragma warning restore EECS026, EECS030

[MemoryDiagnoser]
public class OperationBenchmark
{
    [Params(100_000)] public int Entities;

    private World _world;
    private Prefab _npc;
    private Entity[] _spawned;

    private EntityStore _store;
    private CommandBuffer _buffer;
    private Friflo.Engine.ECS.ArchetypeQuery<F.Health> _health;
    private Friflo.Engine.ECS.ArchetypeQuery<F.Position, F.Velocity> _moving;
    private Friflo.Engine.ECS.ArchetypeQuery<F.Position, F.Velocity, F.Health> _read3;
    private Friflo.Engine.ECS.ArchetypeQuery<F.Health> _players;
    private Friflo.Engine.ECS.ArchetypeQuery<F.Health> _alive;
    private Friflo.Engine.ECS.ArchetypeQuery<F.Position, F.AoiCell> _cells;
    private Friflo.Engine.ECS.ArchetypeQuery<F.Attack> _fighters;
    private Friflo.Engine.ECS.ArchetypeQuery<F.Stunned> _stunned;
    private Friflo.Engine.ECS.Entity[] _fSpawned;
    private Friflo.Engine.ECS.Entity _fNpc;
    private long _sink;

    private const int Spawn = 5_000;

    [GlobalSetup(Targets = [nameof(EasyEcs_Iterate1), nameof(EasyEcs_Iterate2), nameof(EasyEcs_Iterate2Simd), nameof(EasyEcs_Iterate3),
        nameof(EasyEcs_With), nameof(EasyEcs_Entity), nameof(EasyEcs_EntityBatch), nameof(EasyEcs_Singleton), nameof(EasyEcs_Toggle),
        nameof(EasyEcs_CreateDestroy), nameof(EasyEcs_Instantiate)])]
    public void SetupEasyEcs()
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Entities * 2 });
        _world.AddSingleton<Sink>();
        _world.AddSystem<SumHealth>();
        _world.AddSystem<Integrate>();
        _world.AddSystem<IntegrateSimd>();
        _world.AddSystem<Read3>();
        _world.AddSystem<PlayersHealth>();
        _world.AddSystem<EntityIds>();
        _world.AddSystem<EntityIdsBatch>();
        _world.AddSystem<Cells>();
        _world.AddSystem<StunToggle>();
        _world.AddSystem<StunRecover>();
        for (int i = 0; i < Entities; i++)
        {
            var r = Rules.Roll(i);
            var p = new Position { X = r.X, Y = r.Y };
            var v = new Velocity { X = r.VX, Y = r.VY };
            var h = new Health { Value = r.Health, Max = r.Health };
            var a = new Attack { Damage = r.Damage, Cooldown = r.Cooldown, Interval = r.Interval };
            var e = Rules.IsPlayer(i)
                ? _world.CreateEntity(p, v, h, a, new AoiCell { Cell = -1 }, new Player { AccountId = i })
                : _world.CreateEntity(p, v, h, a, new AoiCell { Cell = -1 }, new Npc { TemplateId = i % 50 });
            if (i % 10 == 9) e.Set(new Burning { Ticks = 1000, DamagePerTick = 0 });
            if (i % 3 == 0) _world.CreateEntity(p, v, new Lifetime { Ticks = 1000 }, new Projectile { OwnerId = i });
        }

        _spawned = new Entity[Spawn];
        _npc = new Prefab().Set(new Position()).Set(new Velocity()).Set(new Health { Value = 100, Max = 100 })
            .Set(new Attack { Damage = 5, Cooldown = 0, Interval = 20 }).Set(new AoiCell { Cell = -1 }).Set(new Npc { TemplateId = 7 });
        EasyEcs_Toggle();
        EasyEcs_CreateDestroy();
        EasyEcs_Instantiate();
    }

    [GlobalSetup(Targets = [nameof(Friflo_Iterate1), nameof(Friflo_Iterate2), nameof(Friflo_Iterate2Simd), nameof(Friflo_Iterate3),
        nameof(Friflo_With), nameof(Friflo_Entity), nameof(Friflo_EntityBatch), nameof(Friflo_Singleton), nameof(Friflo_Toggle),
        nameof(Friflo_CreateDestroy), nameof(Friflo_Instantiate)])]
    public void SetupFriflo()
    {
        _store = new EntityStore();
        _buffer = _store.GetCommandBuffer();
        _buffer.ReuseBuffer = true;
        var dead = Tags.Get<F.Dead>();
        var stunned = ComponentTypes.Get<F.Stunned>();
        _health = _store.Query<F.Health>();
        _moving = _store.Query<F.Position, F.Velocity>().WithoutAnyComponents(stunned).WithoutAnyTags(dead);
        _read3 = _store.Query<F.Position, F.Velocity, F.Health>();
        _players = _store.Query<F.Health>().AllComponents(ComponentTypes.Get<F.Player>()).WithoutAnyTags(dead);
        _alive = _store.Query<F.Health>().WithoutAnyTags(dead);
        _cells = _store.Query<F.Position, F.AoiCell>().WithoutAnyTags(dead);
        _fighters = _store.Query<F.Attack>().WithoutAnyComponents(stunned);
        _stunned = _store.Query<F.Stunned>();
        _store.EnsureCapacity(Entities * 2);
        for (int i = 0; i < Entities; i++)
        {
            var r = Rules.Roll(i);
            var p = new F.Position { X = r.X, Y = r.Y };
            var v = new F.Velocity { X = r.VX, Y = r.VY };
            var h = new F.Health { Value = r.Health, Max = r.Health };
            var a = new F.Attack { Damage = r.Damage, Cooldown = r.Cooldown, Interval = r.Interval };
            var e = Rules.IsPlayer(i)
                ? _store.CreateEntity(p, v, h, a, new F.AoiCell { Cell = -1 }, new F.Player { AccountId = i })
                : _store.CreateEntity(p, v, h, a, new F.AoiCell { Cell = -1 }, new F.Npc { TemplateId = i % 50 });
            if (i % 10 == 9) e.AddComponent(new F.Burning { Ticks = 1000, DamagePerTick = 0 });
            if (i % 3 == 0) _store.CreateEntity(p, v, new F.Lifetime { Ticks = 1000 }, new F.Projectile { OwnerId = i });
        }

        _fSpawned = new Friflo.Engine.ECS.Entity[Spawn];
        Friflo_Toggle();
        _fNpc = _store.CreateEntity(new F.Position(), new F.Velocity(), new F.Health { Value = 100, Max = 100 },
            new F.Attack { Damage = 5, Cooldown = 0, Interval = 20 }, new F.AoiCell { Cell = -1 }, new F.Npc { TemplateId = 7 });
        Friflo_CreateDestroy();
        Friflo_Instantiate();
    }

    [GlobalCleanup]
    public void Cleanup() => _world?.Dispose();

    // ---- EasyEcs ----

    [Benchmark] public void EasyEcs_Iterate1() => _world.Update<Iterate1>();
    [Benchmark] public void EasyEcs_Iterate2() => _world.Update<Iterate2>();
    [Benchmark] public void EasyEcs_Iterate2Simd() => _world.Update<Iterate2Simd>();
    [Benchmark] public void EasyEcs_Iterate3() => _world.Update<Iterate3>();
    [Benchmark] public void EasyEcs_With() => _world.Update<IterateWith>();
    [Benchmark] public void EasyEcs_Entity() => _world.Update<IterateEntity>();
    [Benchmark] public void EasyEcs_EntityBatch() => _world.Update<IterateEntityBatch>();
    [Benchmark] public void EasyEcs_Singleton() => _world.Update<IterateSingleton>();
    [Benchmark] public void EasyEcs_Toggle() => _world.Update<Toggle>();

    /// <summary>Spawn 5,000 projectiles (4 components) and destroy them.</summary>
    [Benchmark]
    public void EasyEcs_CreateDestroy()
    {
        var spawned = _spawned;
#pragma warning disable EECS031 // one CreateEntity per entity is the operation compared here
        for (int i = 0; i < spawned.Length; i++)
            spawned[i] = _world.CreateEntity(new Position(), new Velocity(), new Lifetime { Ticks = 20 },
                new Projectile { OwnerId = i });
#pragma warning restore EECS031
        for (int i = 0; i < spawned.Length; i++) spawned[i].Destroy();
    }

    /// <summary>Spawn 5,000 NPCs (6 components) from a prefab and destroy them: EasyEcs instantiates a Prefab (no world holds it).</summary>
    [Benchmark]
    public void EasyEcs_Instantiate()
    {
        var spawned = _spawned;
        _world.Instantiate(_npc, spawned);
        for (int i = 0; i < spawned.Length; i++) spawned[i].Destroy();
    }

    // ---- Friflo ----

    [Benchmark]
    public void Friflo_Iterate1()
    {
        foreach (var (healths, _) in _health.Chunks)
        {
            var h = healths.Span;
            for (int i = 0; i < h.Length; i++) h[i].Max ^= 1;
        }
    }

    [Benchmark]
    public void Friflo_Iterate2()
    {
        foreach (var (positions, velocities, _) in _moving.Chunks)
        {
            var ps = positions.Span;
            var vs = velocities.Span;
            for (int i = 0; i < ps.Length; i++)
            {
                ref var p = ref ps[i];
                ref var v = ref vs[i];
                p.X += v.X;
                p.Y += v.Y;
                if (p.X < 0 || p.X > 4096) v.X = -v.X;
                if (p.Y < 0 || p.Y > 4096) v.Y = -v.Y;
            }
        }
    }

    [Benchmark]
    public void Friflo_Iterate2Simd()
    {
        foreach (var (positions, velocities, _) in _moving.Chunks)
            SimdMove.Run(MemoryMarshal.Cast<F.Position, float>(positions.Span), MemoryMarshal.Cast<F.Velocity, float>(velocities.Span));
    }

    [Benchmark]
    public void Friflo_Iterate3()
    {
        foreach (var (positions, velocities, healths, _) in _read3.Chunks)
        {
            var ps = positions.Span;
            var vs = velocities.Span;
            var hs = healths.Span;
            for (int i = 0; i < ps.Length; i++) ps[i].X = vs[i].X + hs[i].Max;
        }
    }

    [Benchmark]
    public void Friflo_With()
    {
        foreach (var (healths, _) in _players.Chunks)
        {
            var h = healths.Span;
            for (int i = 0; i < h.Length; i++) h[i].Max ^= 1;
        }
    }

    /// <summary>Per entity, the handle passed to a callback (EasyEcs_Entity's style).</summary>
    [Benchmark]
    public void Friflo_Entity()
    {
        _alive.ForEachEntity(static (ref F.Health h, Friflo.Engine.ECS.Entity entity) =>
        {
            if (h.Value <= 0) EntitySum += entity.Id; // never true
        });
        _sink += EntitySum;
    }

    private static long EntitySum;

    /// <summary>Per chunk, the ids read only where needed (EasyEcs_EntityBatch's style).</summary>
    [Benchmark]
    public void Friflo_EntityBatch()
    {
        long sum = 0;
        foreach (var (healths, entities) in _alive.Chunks)
        {
            var h = healths.Span;
            var ids = entities.Ids;
            for (int i = 0; i < h.Length; i++)
                if (h[i].Value <= 0) sum += ids[i];
        }

        _sink += sum;
    }

    [Benchmark]
    public void Friflo_Singleton()
    {
        foreach (var (positions, cells, _) in _cells.Chunks)
        {
            var ps = positions.Span;
            var cs = cells.Span;
            for (int i = 0; i < ps.Length; i++)
            {
                int cell = (int)(ps[i].Y * (1f / 64f)) * 64 + (int)(ps[i].X * (1f / 64f)) + (int)(_sink & 1);
                if (cell == cs[i].Cell) continue;
                cs[i].Cell = cell;
                _sink++;
            }
        }
    }

    [Benchmark]
    public void Friflo_Toggle()
    {
        foreach (var (_, entities) in _fighters.Chunks)
        {
            var ids = entities.Ids;
            for (int i = 0; i < ids.Length; i++)
                if (Rng.Chance(0, ids[i] - 1, 2, 50)) _buffer.AddComponent(ids[i], new F.Stunned { Ticks = 1 });
        }

        _buffer.Playback();
        foreach (var (_, entities) in _stunned.Chunks)
        {
            var ids = entities.Ids;
            for (int i = 0; i < ids.Length; i++) _buffer.RemoveComponent<F.Stunned>(ids[i]);
        }

        _buffer.Playback();
    }

    [Benchmark]
    public void Friflo_CreateDestroy()
    {
        var spawned = _fSpawned;
        for (int i = 0; i < spawned.Length; i++)
            spawned[i] = _store.CreateEntity(new F.Position(), new F.Velocity(), new F.Lifetime { Ticks = 20 },
                new F.Projectile { OwnerId = i });
        for (int i = 0; i < spawned.Length; i++) spawned[i].DeleteEntity();
    }

    /// <summary>Spawn 5,000 NPCs (6 components) from a prefab and destroy them: Friflo clones an entity of the same store.</summary>
    [Benchmark]
    public void Friflo_Instantiate()
    {
        var spawned = _fSpawned;
        for (int i = 0; i < spawned.Length; i++) spawned[i] = _store.CloneEntity(_fNpc);
        for (int i = 0; i < spawned.Length; i++) spawned[i].DeleteEntity();
    }
}
