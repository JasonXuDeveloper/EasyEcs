using System;
using System.Runtime.CompilerServices;

namespace EasyEcs.Benchmark.Showcase;

// A battle server that uses every feature of EasyEcs the way users should: plain data, one job per system, state
// changes as data. The README example is this file; BattleServerBenchmark measures it and BattleServerTests check it.

public record struct Position(float X, float Y) : IComponent;
public record struct Velocity(float X, float Y) : IComponent;
public record struct Health(int Value, int Max) : IComponent, ITrackChanges;    // replicated: writes are tracked
public record struct Replica(int Health) : IComponent;                        // what clients last received
public record struct Attack(int Damage, int Interval, int Cooldown) : IComponent;
public record struct Burning(int Ticks) : IComponent;                         // status effects come and go
public record struct Frozen(int Ticks) : IComponent;
public record struct Lifetime(int Ticks) : IComponent;
public record struct Zone(int Id) : IComponent, IIndexed<int> { public int Key => Id; }                  // players by zone
public record struct OwnedBy(Entity Owner) : IComponent, IIndexed<Entity> { public Entity Key => Owner; } // a relation
public struct Player : IComponent;                                            // tags: empty components
public struct Npc : IComponent;
public struct Dead : IComponent;

public record struct Battle(long Tick, int Hits, int Deaths, int Shots, int Replicated, int Afflicted) : ISingletonComponent;

public struct Waypoints : ISingletonComponent
{
    public const int Count = 8;
    public WaypointArray Points;
}

[InlineArray(Waypoints.Count)]
public struct WaypointArray { private Position _first; }

public struct Replication : ISystemGroup;                                     // runs when the server sends updates

public partial class SteeringSystem : EcsSystem                               // heavy per-entity math: on all cores
{
    [ForEach(Parallel = true), With<Npc>, Without<Dead>, Without<Frozen>]
    static void Steer(in Position p, ref Velocity v, in Waypoints waypoints)
    {
        var target = waypoints.Points[(int)(p.X + p.Y) & (Waypoints.Count - 1)];
        float dx = target.X - p.X, dy = target.Y - p.Y, length = MathF.Sqrt(dx * dx + dy * dy) + 1;
        v = new Velocity(dx / length * 2, dy / length * 2);
    }
}

public partial class MovementSystem : EcsSystem
{
    public override int Priority => 1;

    [ForEach, Without<Dead>, Without<Frozen>]                                 // whole runs of rows: SIMD-friendly
    static void Move(Span<Position> positions, ReadOnlySpan<Velocity> velocities)
    {
        for (int i = 0; i < positions.Length; i++)
            positions[i] = new Position(positions[i].X + velocities[i].X, positions[i].Y + velocities[i].Y);
    }
}

public partial class ZoneSystem : EcsSystem                                   // the zone index follows these writes
{
    public override int Priority => 2;

    [ForEach]
    static void Enter(Entity player, in Position p, in Zone zone)              // `in`: reading is not a change
    {
        int id = Math.Clamp((int)(p.X / 1024), 0, 3);
        if (zone.Id != id) player.Get<Zone>() = new Zone(id);                  // only real moves are writes
    }
}

public partial class CombatSystem : EcsSystem
{
    public override int Priority => 3;

    [ForEach, Without<Dead>, Without<Frozen>]
    static void Fight(Entity unit, ref Attack attack, ref Battle battle)
    {
        if (--attack.Cooldown > 0) return;
        attack.Cooldown = attack.Interval;
        unit.Get<Health>().Value -= attack.Damage;                            // a hit is a change of Health
        battle.Hits++;
        uint roll = Dice.Roll(battle.Tick, unit.Id);
        if (roll % 50 == 0 && !unit.Has<Burning>()) unit.Add<Burning>() = new Burning(10);   // applied at once
        else if (roll % 70 == 1 && !unit.Has<Frozen>()) unit.Add<Frozen>() = new Frozen(4);
    }
}

public partial class StatusSystem : EcsSystem
{
    public override int Priority => 4;

    [ForEach]
    static void Burn(Entity unit, ref Burning burning, ref Health hp)
    {
        hp.Value -= 3;
        if (--burning.Ticks == 0) unit.Remove<Burning>();                     // flips a bit: the row stays
    }

    [ForEach]
    static void Thaw(Entity unit, ref Frozen frozen)
    {
        if (--frozen.Ticks == 0) unit.Remove<Frozen>();
    }

    [ForEach, WithAny<Burning>, WithAny<Frozen>]                              // burning or frozen (or both)
    static void Count(in Health hp, ref Battle battle) => battle.Afflicted++;
}

public partial class DeathSystem : EcsSystem
{
    public override int Priority => 5;

    [ForEach, Without<Dead>]
    static void Die(Entity unit, in Health hp, ref Battle battle)
    {
        if (hp.Value > 0) return;
        unit.Add<Dead>();                                                     // whoever cares queries Dead
        battle.Deaths++;
    }
}

public partial class RespawnSystem : EcsSystem                                // DeathSystem does not know it exists
{
    public override int Priority => 6;

    [ForEach, With<Player>, With<Dead>]
    static void Respawn(Entity player, ref Health hp, World world)
    {
        foreach (var shot in world.Index<OwnedBy, Entity>().Find(player)) shot.Destroy();   // recall its projectiles
        hp.Value = hp.Max;
        player.Remove<Dead>();
    }

    [ForEach, With<Npc>, With<Dead>]
    static void Despawn(Entity npc, in Position p, World world)
    {
        npc.Destroy();
        BattleServer.SpawnNpc(world, new Position(p.Y, p.X), npc.Id);
    }
}

public partial class ProjectileSystem : EcsSystem
{
    public override int Priority => 7;

    [ForEach, With<Player>, Without<Dead>]
    static void Shoot(Entity player, in Position p, in Velocity v, ref Battle battle, World world)
    {
        if (Dice.Roll(battle.Tick, player.Id) % 20 != 0) return;
        battle.Shots++;
        world.CreateEntity(p, new Velocity(v.X * 4, v.Y * 4), new Lifetime(20), new OwnedBy(player));
    }

    [ForEach]
    static void Expire(Entity shot, ref Lifetime life)
    {
        if (--life.Ticks == 0) shot.Destroy();
    }
}

public partial class RegenerationSystem : EcsSystem
{
    public override int Priority => 8;
    public override int UpdateInterval => 10;                                 // every 10th update

    [ForEach, With<Player>, Without<Dead>]
    static void Regenerate(Entity player, in Health hp)
    {
        if (hp.Value < hp.Max) player.Get<Health>().Value = Math.Min(hp.Max, hp.Value + 20);
    }
}

public sealed class ClockSystem : EcsSystem
{
    public override int Priority => 100;
    protected override void OnUpdate() => World.GetSingleton<Battle>().Tick++;
}

[UpdateInGroup<Replication>]
public partial class ReplicationSystem : EcsSystem                            // only what changed since the last send
{
    [ForEach, Changed<Health>]
    static void Send(in Health hp, ref Replica replica, ref Battle battle)
    {
        replica = new Replica(hp.Value);
        battle.Replicated++;
    }
}

public static class Dice                                       // deterministic per tick and entity
{
    public static uint Roll(long tick, int id)
    {
        ulong x = (ulong)tick * 0x9E3779B97F4A7C15UL ^ (uint)id * 0xBF58476D1CE4E5B9UL;
        x ^= x >> 31;
        x *= 0x94D049BB133111EBUL;
        return (uint)(x ^ (x >> 29));
    }
}

public static class BattleServer
{
    /// <summary>A world of <paramref name="units"/> units, a third of them players.</summary>
    public static World Create(int units)
    {
        var world = new World(new WorldOptions { InitialEntityCapacity = units * 2 });
        world.AddSingleton<Battle>();
        ref var waypoints = ref world.AddSingleton<Waypoints>();
        for (int i = 0; i < Waypoints.Count; i++) waypoints.Points[i] = new Position(512 + i * 400, 512 + (i * 1777) % 3000);
        world.AddSystem<SteeringSystem>();
        world.AddSystem<MovementSystem>();
        world.AddSystem<ZoneSystem>();
        world.AddSystem<CombatSystem>();
        world.AddSystem<StatusSystem>();
        world.AddSystem<DeathSystem>();
        world.AddSystem<RespawnSystem>();
        world.AddSystem<ProjectileSystem>();
        world.AddSystem<RegenerationSystem>();
        world.AddSystem<ClockSystem>();
        world.AddSystem<ReplicationSystem>();

        var players = new Entity[units / 3];                                  // one batch: one table growth
        world.CreateEntities(players, new Position(), new Velocity(1, 0), new Health(500, 500), new Replica(500),
            new Attack(8, 6, 0), new Zone(0), new Player());
        for (int i = 0; i < players.Length; i++)
        {
            uint r = Dice.Roll(0, i);
            players[i].Get<Position>() = new Position(r % 4096, (r >> 12) % 4096);
            players[i].Get<Velocity>() = new Velocity((int)(r % 5) - 2, (int)((r >> 8) % 5) - 2);
            players[i].Get<Attack>().Cooldown = (int)(r % 6);
        }

        for (int i = players.Length; i < units; i++)
        {
            uint r = Dice.Roll(0, i);
            SpawnNpc(world, new Position(r % 4096, (r >> 12) % 4096), i);
        }

        return world;
    }

    public static void SpawnNpc(World world, Position at, int seed)
    {
        uint r = Dice.Roll(seed, seed);
        int hp = 100 + (int)(r % 100);
        world.CreateEntity(at, new Velocity(), new Health(hp, hp), new Replica(hp),
            new Attack(5 + (int)(r % 5), 5 + (int)(r % 10), (int)(r % 10)), new Npc());
    }

    /// <summary>One server tick: the simulation every tick, replication every third.</summary>
    public static void Tick(World world)
    {
        world.Update();
        if (world.GetSingleton<Battle>().Tick % 3 == 0) world.Update<Replication>();
    }
}
