using System.Runtime.CompilerServices;
using EasyEcs.Core;
using EasyEcs.Core.Components;
using EasyEcs.Core.Systems;

namespace EasyEcs.Benchmark.Simulation;

// A realistic MMO/battle-server world: players and NPCs move, fight, gain and lose status effects,
// projectiles spawn and expire every tick. Every system is a [Query] system, as users would write it.

public struct Position : IComponent { public float X, Y; }
public struct Velocity : IComponent { public float X, Y; }
public struct Health : IComponent { public int Value, Max; }
public struct AoiCell : IComponent { public int Cell; }
public struct Attack : IComponent { public int Damage, Cooldown, Interval; }
public struct Lifetime : IComponent { public int Ticks; }
public struct Burning : IComponent { public int Ticks, DamagePerTick; }
public struct Player : IComponent { public long AccountId; }
public struct Npc : IComponent { public int TemplateId; }
public struct Projectile : IComponent { public int OwnerId; }
public struct Stunned : IComponent { public int Ticks; }
public struct Dead : IComponent { }

public struct WorldStats : ISingletonComponent
{
    public long Tick;
    public int CellChanges, Hits, Deaths, Spawns;
    public int Width;
    public float CellSize;
}

/// <summary>Deterministic per-entity pseudo random (no shared Random, no allocation).</summary>
public static class Rng
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Hash(long tick, int id)
    {
        ulong x = (ulong)tick * 0x9E3779B97F4A7C15UL ^ (uint)id * 0xBF58476D1CE4E5B9UL;
        x ^= x >> 31;
        x *= 0x94D049BB133111EBUL;
        return (uint)(x ^ (x >> 29));
    }

    /// <summary>True with probability permille / 1000.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Chance(long tick, int id, int salt, uint permille) => Hash(tick + salt * 7919L, id) % 1000 < permille;
}

/// <summary>Integrates movement, bounces at the borders, skips stunned and dead entities.</summary>
public partial class MovementSystem : SystemBase
{
    public override int Priority => 0;

    [Query, Without<Stunned>, Without<Dead>]
    private static void Move(ref Position p, ref Velocity v)
    {
        p.X += v.X;
        p.Y += v.Y;
        if (p.X < 0 || p.X > 4096) v.X = -v.X;
        if (p.Y < 0 || p.Y > 4096) v.Y = -v.Y;
    }
}

/// <summary>Area-of-interest grid: recompute the cell, count cell changes (would trigger enter/leave sync).</summary>
public partial class AoiSystem : SystemBase
{
    public override int Priority => 1;

    [Query, Without<Dead>]
    private static void UpdateCell(in Position p, ref AoiCell aoi, Context context)
    {
        int cell = (int)(p.Y * (1f / 64f)) * 64 + (int)(p.X * (1f / 64f));
        if (cell == aoi.Cell) return;
        aoi.Cell = cell;
        context.GetSingletonComponent<WorldStats>().Value.CellChanges++;
    }
}

/// <summary>NPC/player attacks on cooldown; some hits set the target on fire or stun it (structural changes).</summary>
public partial class CombatSystem : SystemBase
{
    public override int Priority => 2;

    [Query, Without<Dead>, Without<Stunned>]
    private static void Fight(int entityId, ref Attack attack, ref Health health, Context context)
    {
        if (--attack.Cooldown > 0) return;
        attack.Cooldown = attack.Interval;

        ref var stats = ref context.GetSingletonComponent<WorldStats>().Value;
        stats.Hits++;
        health.Value -= attack.Damage;

        var entity = context.GetEntityById(entityId);
        if (Rng.Chance(stats.Tick, entityId, 1, 50) && !entity.Value.HasComponent<Burning>())
        {
            ref var burning = ref entity.Value.AddComponent<Burning>().Value;
            burning.Ticks = 10;
            burning.DamagePerTick = 3;
        }
        else if (Rng.Chance(stats.Tick, entityId, 2, 20) && !entity.Value.HasComponent<Stunned>())
        {
            entity.Value.AddComponent<Stunned>().Value.Ticks = 5;
        }
    }
}

/// <summary>Status effects expire (component removal = archetype transition every few ticks per entity).</summary>
public partial class StatusSystem : SystemBase
{
    public override int Priority => 3;

    [Query]
    private static void Burn(int entityId, ref Burning burning, ref Health health, Context context)
    {
        health.Value -= burning.DamagePerTick;
        if (--burning.Ticks <= 0)
            context.GetEntityById(entityId).Value.RemoveComponent<Burning>();
    }

    [Query]
    private static void Recover(int entityId, ref Stunned stunned, Context context)
    {
        if (--stunned.Ticks <= 0)
            context.GetEntityById(entityId).Value.RemoveComponent<Stunned>();
    }

    [Query, Without<Dead>]
    private static void CheckDeath(int entityId, ref Health health, Context context)
    {
        if (health.Value > 0) return;
        context.GetEntityById(entityId).Value.AddComponent<Dead>();
        context.GetSingletonComponent<WorldStats>().Value.Deaths++;
    }
}

/// <summary>Projectiles expire; dead NPCs respawn; players respawn in place. Entity churn every tick.</summary>
public partial class LifecycleSystem : SystemBase
{
    public override int Priority => 4;

    [Query]
    private static void Expire(int entityId, ref Lifetime lifetime, Context context)
    {
        if (--lifetime.Ticks <= 0)
            context.DestroyEntity(context.GetEntityById(entityId));
    }

    [Query, With<Dead>, With<Npc>]
    private static void Despawn(int entityId, in Position p, Context context)
    {
        context.DestroyEntity(context.GetEntityById(entityId));
        World.SpawnNpc(context, entityId * 31 + 7);
    }

    [Query, With<Dead>, With<Player>]
    private static void Respawn(int entityId, ref Health health, Context context)
    {
        health.Value = health.Max;
        context.GetEntityById(entityId).Value.RemoveComponent<Dead>();
    }

    [Query, With<Player>, Without<Dead>]
    private static void Shoot(int entityId, in Position p, in Velocity v, Context context)
    {
        // ~5% of players fire a projectile each tick
        ref var stats = ref context.GetSingletonComponent<WorldStats>().Value;
        if (!Rng.Chance(stats.Tick, entityId, 3, 50)) return;
        stats.Spawns++;
        var bullet = context.CreateEntity();
        var (bp, bv, life, proj) = bullet.Value.AddComponents<Position, Velocity, Lifetime, Projectile>();
        bp.Value = p;
        bv.Value.X = v.X * 4;
        bv.Value.Y = v.Y * 4;
        life.Value.Ticks = 20;
        proj.Value.OwnerId = entityId;
    }
}

/// <summary>Ticks the world clock (last).</summary>
public partial class ClockSystem : SystemBase, IExecuteSystem
{
    public override int Priority => 100;

    public Cysharp.Threading.Tasks.UniTask OnExecute(Context context)
    {
        context.GetSingletonComponent<WorldStats>().Value.Tick++;
        return Cysharp.Threading.Tasks.UniTask.CompletedTask;
    }
}

public static class World
{
    /// <summary>
    /// Run warm-up ticks and verify the simulation is actually busy (fights, deaths, spawns, AOI churn), so a
    /// benchmark can never silently measure an idle world.
    /// </summary>
    public static void WarmUp(Context world, int ticks = 200)
    {
        ref var stats = ref world.GetSingletonComponent<WorldStats>().Value;
        for (int i = 0; i < ticks; i++) world.Update().GetAwaiter().GetResult();
        if (stats.Hits == 0 || stats.Deaths == 0 || stats.Spawns == 0 || stats.CellChanges == 0)
            throw new System.InvalidOperationException(
                $"Simulation is idle: hits={stats.Hits} deaths={stats.Deaths} spawns={stats.Spawns} cells={stats.CellChanges}");
    }

    /// <summary>Build a world with <paramref name="entities"/> actors: 30% players, 70% NPCs.</summary>
    public static Context Create(int entities)
    {
        var ctx = new Context(new Context.Options(parallel: false, initialEntityCapacity: entities * 2));
        ctx.AddSingletonComponent<WorldStats>();
        ctx.AddSystem<MovementSystem>();
        ctx.AddSystem<AoiSystem>();
        ctx.AddSystem<CombatSystem>();
        ctx.AddSystem<StatusSystem>();
        ctx.AddSystem<LifecycleSystem>();
        ctx.AddSystem<ClockSystem>();

        for (int i = 0; i < entities; i++)
        {
            if (i % 10 < 3) SpawnPlayer(ctx, i);
            else SpawnNpc(ctx, i);
        }

        ctx.Init().GetAwaiter().GetResult();
        return ctx;
    }

    private static void SpawnPlayer(Context ctx, int seed)
    {
        var e = ctx.CreateEntity();
        var (p, v, h, a) = e.Value.AddComponents<Position, Velocity, Health, Attack>();
        var (aoi, player) = e.Value.AddComponents<AoiCell, Player>();
        Init(seed, ref p.Value, ref v.Value, ref h.Value, ref a.Value);
        h.Value.Max = h.Value.Value = 500;
        player.Value.AccountId = seed;
        aoi.Value.Cell = -1;
    }

    public static void SpawnNpc(Context ctx, int seed)
    {
        var e = ctx.CreateEntity();
        var (p, v, h, a, aoi, npc) = e.Value.AddComponents<Position, Velocity, Health, Attack, AoiCell, Npc>();
        Init(seed, ref p.Value, ref v.Value, ref h.Value, ref a.Value);
        npc.Value.TemplateId = seed % 50;
        aoi.Value.Cell = -1;
    }

    private static void Init(int seed, ref Position p, ref Velocity v, ref Health h, ref Attack a)
    {
        uint r = Rng.Hash(seed, seed);
        p.X = r % 4096;
        p.Y = (r >> 12) % 4096;
        v.X = (int)(r % 7) - 3;
        v.Y = (int)((r >> 8) % 7) - 3;
        h.Max = h.Value = 100 + (int)(r % 100);
        a.Interval = 5 + (int)(r % 10);
        a.Cooldown = (int)(r % 10);
        a.Damage = 5 + (int)(r % 5);
    }
}
