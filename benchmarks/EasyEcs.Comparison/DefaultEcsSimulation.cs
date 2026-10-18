using System;
using DefaultEcs;
using DefaultEcs.System;
using Rng = EasyEcs.Benchmark.Scenario.Rng;

namespace EcsComparison.DefaultEcsPort;

// DefaultEcs: one AEntitySetSystem per query, filters as [With]/[Without] attributes, components read through
// World.GetComponents<T>() over the span of entities (the fast path its docs recommend over entity.Get<T>()).
// DefaultEcs keeps entity sets up to date immediately, so a system that changes structure while iterating
// passes useBuffer: true (the set is copied before the update), its documented mechanism; changes are therefore
// immediate, as in EasyEcs. DefaultEcs has no public entity id: GetHashCode() returns it (1-based), so the Rng gets
// id - 1 to roll the same numbers as the 0-based libraries.

public struct Position { public float X, Y; }
public struct Velocity { public float X, Y; }
public struct Health { public int Value, Max; }
public struct AoiCell { public int Cell; }
public struct Attack { public int Damage, Cooldown, Interval; }
public struct Lifetime { public int Ticks; }
public struct Burning { public int Ticks, DamagePerTick; }
public struct Player { public long AccountId; }
public struct Npc { public int TemplateId; }
public struct Projectile { public int OwnerId; }
public struct Stunned { public int Ticks; }
public struct Dead;

public sealed class DefaultEcsSimulation : ISimulation
{
    private readonly World _world = new();
    private readonly SequentialSystem<Counters> _systems;
    private readonly Counters _c = new();

    public DefaultEcsSimulation(int entities)
    {
        _systems = new SequentialSystem<Counters>(
            new MovementSystem(_world), new AoiSystem(_world), new CombatSystem(_world), new BurnSystem(_world),
            new RecoverSystem(_world), new DeathSystem(_world), new LifetimeSystem(_world), new DespawnSystem(_world),
            new RespawnSystem(_world), new ShootingSystem(_world));

        for (int i = 0; i < entities; i++) Spawn(_world, i, Rules.IsPlayer(i));
    }

    public string Library => "DefaultEcs";

    public void Tick()
    {
        _systems.Update(_c);
        _c.Tick++;
    }

    public Counters Snapshot()
    {
        int n = 0;
        foreach (var _ in _world) n++;
        _c.Entities = n;
        return _c;
    }

    public void Dispose()
    {
        _systems.Dispose();
        _world.Dispose();
    }

    internal static int Id(in Entity e) => e.GetHashCode() - 1;

    internal static void Spawn(World world, int seed, bool player)
    {
        var r = Rules.Roll(seed);
        var e = world.CreateEntity();
        e.Set(new Position { X = r.X, Y = r.Y });
        e.Set(new Velocity { X = r.VX, Y = r.VY });
        int hp = player ? Rules.PlayerHealth : r.Health;
        e.Set(new Health { Value = hp, Max = hp });
        e.Set(new Attack { Damage = r.Damage, Cooldown = r.Cooldown, Interval = r.Interval });
        e.Set(new AoiCell { Cell = -1 });
        if (player) e.Set(new Player { AccountId = seed });
        else e.Set(new Npc { TemplateId = seed % Rules.NpcTemplates });
    }
}

[With(typeof(Position), typeof(Velocity)), Without(typeof(Stunned), typeof(Dead))]
public sealed class MovementSystem(World world) : AEntitySetSystem<Counters>(world, false)
{
    protected override void Update(Counters c, ReadOnlySpan<Entity> entities)
    {
        var positions = World.GetComponents<Position>();
        var velocities = World.GetComponents<Velocity>();
        foreach (ref readonly var e in entities)
        {
            ref var p = ref positions[e];
            ref var v = ref velocities[e];
            p.X += v.X;
            p.Y += v.Y;
            if (p.X < 0 || p.X > Rules.MapSize) v.X = -v.X;
            if (p.Y < 0 || p.Y > Rules.MapSize) v.Y = -v.Y;
        }
    }
}

[With(typeof(Position), typeof(AoiCell)), Without(typeof(Dead))]
public sealed class AoiSystem(World world) : AEntitySetSystem<Counters>(world, false)
{
    protected override void Update(Counters c, ReadOnlySpan<Entity> entities)
    {
        var positions = World.GetComponents<Position>();
        var cells = World.GetComponents<AoiCell>();
        foreach (ref readonly var e in entities)
        {
            ref readonly var p = ref positions[e];
            int cell = Rules.Cell(p.X, p.Y);
            ref var aoi = ref cells[e];
            if (cell == aoi.Cell) continue;
            aoi.Cell = cell;
            c.CellChanges++;
        }
    }
}

[With(typeof(Attack), typeof(Health)), Without(typeof(Dead), typeof(Stunned))]
public sealed class CombatSystem(World world) : AEntitySetSystem<Counters>(world, true)
{
    protected override void Update(Counters c, ReadOnlySpan<Entity> entities)
    {
        var attacks = World.GetComponents<Attack>();
        var healths = World.GetComponents<Health>();
        foreach (ref readonly var e in entities)
        {
            ref var attack = ref attacks[e];
            if (--attack.Cooldown > 0) continue;
            attack.Cooldown = attack.Interval;
            c.Hits++;
            healths[e].Value -= attack.Damage;

            int id = DefaultEcsSimulation.Id(e);
            if (Rng.Chance(c.Tick, id, Rules.BurnSalt, Rules.BurnPermille) && !e.Has<Burning>())
                e.Set(new Burning { Ticks = Rules.BurnTicks, DamagePerTick = Rules.BurnDamage });
            else if (Rng.Chance(c.Tick, id, Rules.StunSalt, Rules.StunPermille) && !e.Has<Stunned>())
                e.Set(new Stunned { Ticks = Rules.StunTicks });
        }
    }
}

[With(typeof(Burning), typeof(Health))]
public sealed class BurnSystem(World world) : AEntitySetSystem<Counters>(world, true)
{
    protected override void Update(Counters c, ReadOnlySpan<Entity> entities)
    {
        var burnings = World.GetComponents<Burning>();
        var healths = World.GetComponents<Health>();
        foreach (ref readonly var e in entities)
        {
            ref var burning = ref burnings[e];
            healths[e].Value -= burning.DamagePerTick;
            if (--burning.Ticks <= 0) e.Remove<Burning>();
        }
    }
}

[With(typeof(Stunned))]
public sealed class RecoverSystem(World world) : AEntitySetSystem<Counters>(world, true)
{
    protected override void Update(Counters c, ReadOnlySpan<Entity> entities)
    {
        var stuns = World.GetComponents<Stunned>();
        foreach (ref readonly var e in entities)
            if (--stuns[e].Ticks <= 0) e.Remove<Stunned>();
    }
}

[With(typeof(Health)), Without(typeof(Dead))]
public sealed class DeathSystem(World world) : AEntitySetSystem<Counters>(world, true)
{
    protected override void Update(Counters c, ReadOnlySpan<Entity> entities)
    {
        var healths = World.GetComponents<Health>();
        foreach (ref readonly var e in entities)
        {
            if (healths[e].Value > 0) continue;
            e.Set<Dead>();
            c.Deaths++;
            if (e.Has<Npc>()) c.NpcKills++;
        }
    }
}

[With(typeof(Lifetime))]
public sealed class LifetimeSystem(World world) : AEntitySetSystem<Counters>(world, true)
{
    protected override void Update(Counters c, ReadOnlySpan<Entity> entities)
    {
        var lifetimes = World.GetComponents<Lifetime>();
        foreach (ref readonly var e in entities)
            if (--lifetimes[e].Ticks <= 0) e.Dispose();
    }
}

[With(typeof(Npc), typeof(Dead))]
public sealed class DespawnSystem(World world) : AEntitySetSystem<Counters>(world, true)
{
    protected override void Update(Counters c, ReadOnlySpan<Entity> entities)
    {
        foreach (ref readonly var e in entities)
        {
            int id = DefaultEcsSimulation.Id(e);
            e.Dispose();
            DefaultEcsSimulation.Spawn(World, Rules.RespawnSeed(id), player: false);
        }
    }
}

[With(typeof(Player), typeof(Dead), typeof(Health))]
public sealed class RespawnSystem(World world) : AEntitySetSystem<Counters>(world, true)
{
    protected override void Update(Counters c, ReadOnlySpan<Entity> entities)
    {
        var healths = World.GetComponents<Health>();
        foreach (ref readonly var e in entities)
        {
            ref var health = ref healths[e];
            health.Value = health.Max;
            e.Remove<Dead>();
        }
    }
}

[With(typeof(Player), typeof(Position), typeof(Velocity)), Without(typeof(Dead))]
public sealed class ShootingSystem(World world) : AEntitySetSystem<Counters>(world, true)
{
    protected override void Update(Counters c, ReadOnlySpan<Entity> entities)
    {
        foreach (ref readonly var e in entities)
        {
            int id = DefaultEcsSimulation.Id(e);
            if (!Rng.Chance(c.Tick, id, Rules.ShootSalt, Rules.ShootPermille)) continue;
            c.Spawns++;
            // Read the shooter before creating: a new entity may grow the component pools.
            var p = e.Get<Position>();
            var v = e.Get<Velocity>();
            var projectile = World.CreateEntity();
            projectile.Set(p);
            projectile.Set(new Velocity { X = v.X * Rules.ProjectileSpeed, Y = v.Y * Rules.ProjectileSpeed });
            projectile.Set(new Lifetime { Ticks = Rules.ProjectileTicks });
            projectile.Set(new Projectile { OwnerId = id });
        }
    }
}
