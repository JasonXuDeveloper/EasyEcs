using System.Runtime.CompilerServices;

namespace EasyEcs.Benchmark.Scenario;

// A realistic MMO/battle-server world: players and NPCs move, fight, gain and lose status effects,
// projectiles spawn and expire every tick, entities die and respawn. Systems are written as users should write them:
// one responsibility each, stateless (world state lives in the WorldStats singleton), coupled only via data.

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
public struct Dead : IComponent;

public struct WorldStats : ISingletonComponent
{
    public long Tick;
    public int CellChanges, Hits, Deaths, NpcKills, Spawns;
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
public partial class MovementSystem : EcsSystem
{
    public override int Priority => 0;

    [ForEach, Without<Stunned>, Without<Dead>]
    private static void Move(ref Position p, ref Velocity v)
    {
        p.X += v.X;
        p.Y += v.Y;
        if (p.X < 0 || p.X > 4096) v.X = -v.X;
        if (p.Y < 0 || p.Y > 4096) v.Y = -v.Y;
    }
}

/// <summary>Area-of-interest grid: recompute the cell, count cell changes (would trigger enter/leave sync).</summary>
public partial class AoiSystem : EcsSystem
{
    public override int Priority => 1;

    [ForEach, Without<Dead>]
    private static void UpdateCell(in Position p, ref AoiCell aoi, World world)
    {
        int cell = (int)(p.Y * (1f / 64f)) * 64 + (int)(p.X * (1f / 64f));
        if (cell == aoi.Cell) return;
        aoi.Cell = cell;
        world.GetSingleton<WorldStats>().CellChanges++;
    }
}

/// <summary>NPC/player attacks on cooldown; some hits set the target on fire or stun it (structural changes).</summary>
public partial class CombatSystem : EcsSystem
{
    public override int Priority => 2;

    [ForEach, Without<Dead>, Without<Stunned>]
    private static void Fight(Entity entity, ref Attack attack, ref Health health, World world)
    {
        if (--attack.Cooldown > 0) return;
        attack.Cooldown = attack.Interval;

        ref var stats = ref world.GetSingleton<WorldStats>();
        stats.Hits++;
        health.Value -= attack.Damage;

        if (Rng.Chance(stats.Tick, entity.Id, 1, 50) && !entity.Has<Burning>())
            entity.Set(new Burning { Ticks = 10, DamagePerTick = 3 });
        else if (Rng.Chance(stats.Tick, entity.Id, 2, 20) && !entity.Has<Stunned>())
            entity.Set(new Stunned { Ticks = 5 });
    }
}

/// <summary>Status effects tick and expire (component removal = archetype transition every few ticks per entity).</summary>
public partial class StatusSystem : EcsSystem
{
    public override int Priority => 3;

    [ForEach]
    private static void Burn(Entity entity, ref Burning burning, ref Health health)
    {
        health.Value -= burning.DamagePerTick;
        if (--burning.Ticks <= 0)
            entity.Remove<Burning>();
    }

    [ForEach]
    private static void Recover(Entity entity, ref Stunned stunned)
    {
        if (--stunned.Ticks <= 0)
            entity.Remove<Stunned>();
    }
}

/// <summary>Turns depleted health into death: marks the entity (respawn reacts later) and counts it.</summary>
public partial class DeathSystem : EcsSystem
{
    public override int Priority => 4;

    [ForEach, Without<Dead>]
    private static void CheckDeath(Entity entity, in Health health, World world)
    {
        if (health.Value > 0) return;
        entity.Set(new Dead());
        ref var stats = ref world.GetSingleton<WorldStats>();
        stats.Deaths++;
        if (entity.Has<Npc>()) stats.NpcKills++;
    }
}

/// <summary>Projectiles expire (entity churn every tick).</summary>
public partial class LifetimeSystem : EcsSystem
{
    public override int Priority => 5;

    [ForEach]
    private static void Expire(Entity entity, ref Lifetime lifetime)
    {
        if (--lifetime.Ticks <= 0)
            entity.Destroy();
    }
}

/// <summary>Dead NPCs are replaced by fresh ones; dead players respawn in place.</summary>
public partial class RespawnSystem : EcsSystem
{
    public override int Priority => 6;

    [ForEach, With<Npc>]
    private static void Despawn(Entity entity, in Dead _, World world)
    {
        entity.Destroy();
        Simulation.SpawnNpc(world, entity.Id * 31 + 7);
    }

    [ForEach, With<Player>]
    private static void Respawn(Entity entity, in Dead _, ref Health health)
    {
        health.Value = health.Max;
        entity.Remove<Dead>();
    }
}

/// <summary>About 5% of living players fire a projectile each tick.</summary>
public partial class ShootingSystem : EcsSystem
{
    public override int Priority => 7;

    [ForEach, With<Player>, Without<Dead>]
    private static void Shoot(Entity shooter, in Position p, in Velocity v, World world)
    {
        ref var stats = ref world.GetSingleton<WorldStats>();
        if (!Rng.Chance(stats.Tick, shooter.Id, 3, 50)) return;
        stats.Spawns++;
        world.CreateEntity(p, new Velocity { X = v.X * 4, Y = v.Y * 4 }, new Lifetime { Ticks = 20 },
            new Projectile { OwnerId = shooter.Id });
    }
}

/// <summary>Ticks the world clock (last).</summary>
public sealed class ClockSystem : EcsSystem
{
    public override int Priority => 100;

    protected override void OnUpdate() => World.GetSingleton<WorldStats>().Tick++;
}

public static class Simulation
{
    /// <summary>
    /// Run warm-up ticks and verify the simulation is actually busy (fights, deaths, spawns, AOI churn), so a
    /// benchmark can never silently measure an idle world.
    /// </summary>
    public static void WarmUp(World world, int ticks = 200)
    {
        ref var stats = ref world.GetSingleton<WorldStats>();
        for (int i = 0; i < ticks; i++) world.Update();
        if (stats.Hits == 0 || stats.Deaths == 0 || stats.Spawns == 0 || stats.CellChanges == 0)
            throw new System.InvalidOperationException(
                $"Simulation is idle: hits={stats.Hits} deaths={stats.Deaths} spawns={stats.Spawns} cells={stats.CellChanges}");
    }

    /// <summary>Build a world with <paramref name="entities"/> actors: 30% players, 70% NPCs.</summary>
    public static World Create(int entities)
    {
        var world = new World(new WorldOptions { InitialEntityCapacity = entities * 2 });
        world.AddSingleton<WorldStats>();
        world.AddSystem<MovementSystem>();
        world.AddSystem<AoiSystem>();
        world.AddSystem<CombatSystem>();
        world.AddSystem<StatusSystem>();
        world.AddSystem<DeathSystem>();
        world.AddSystem<LifetimeSystem>();
        world.AddSystem<RespawnSystem>();
        world.AddSystem<ShootingSystem>();
        world.AddSystem<ClockSystem>();

        for (int i = 0; i < entities; i++)
        {
            if (i % 10 < 3) SpawnPlayer(world, i);
            else SpawnNpc(world, i);
        }

        return world;
    }

    private static void SpawnPlayer(World world, int seed)
    {
        var (p, v, h, a) = Roll(seed);
        h.Max = h.Value = 500;
        world.CreateEntity(p, v, h, a, new AoiCell { Cell = -1 }, new Player { AccountId = seed });
    }

    public static void SpawnNpc(World world, int seed)
    {
        var (p, v, h, a) = Roll(seed);
        world.CreateEntity(p, v, h, a, new AoiCell { Cell = -1 }, new Npc { TemplateId = seed % 50 });
    }

    private static (Position, Velocity, Health, Attack) Roll(int seed)
    {
        uint r = Rng.Hash(seed, seed);
        int hp = 100 + (int)(r % 100);
        return (new Position { X = r % 4096, Y = (r >> 12) % 4096 },
            new Velocity { X = (int)(r % 7) - 3, Y = (int)((r >> 8) % 7) - 3 },
            new Health { Value = hp, Max = hp },
            new Attack { Interval = 5 + (int)(r % 10), Cooldown = (int)(r % 10), Damage = 5 + (int)(r % 5) });
    }
}
