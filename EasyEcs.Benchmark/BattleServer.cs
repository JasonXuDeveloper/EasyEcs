using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EasyEcs.Benchmark.Showcase;

// A battle server that uses every feature of EasyEcs the way users should: plain data, one job per system, state
// changes as data. doc/example.md shows this file; BattleServerBenchmark measures it and BattleServerTests check it.

public record struct Position(float X, float Y) : IComponent;
public record struct Velocity(float X, float Y) : IComponent;
public record struct Health(int Value, int Max) : IComponent, ITrackChanges;    // replicated: writes are tracked
public record struct Replica(int Health, bool Burning) : IComponent;          // what clients last received
public record struct Mana(int Value, int Max) : IComponent;
public record struct Attack(int Damage, int Interval, int Cooldown) : IComponent;
public record struct Burning(int Ticks) : IComponent, ITrackChanges;          // status effects come and go; clients
                                                                              // see fires start and go out
public record struct Frozen(int Ticks) : IComponent;
public record struct Lifetime(int Ticks) : IComponent;
public record struct Zone(int Id) : IComponent, IIndexed<int> { public int Key => Id; }                  // players by zone
public record struct OwnedBy(Entity Owner) : IComponent, IIndexed<Entity> { public Entity Key => Owner; } // a relation
public record struct Loot(int Item);                                          // players carry a DynamicBuffer<Loot>
public struct Player : IComponent;                                            // tags: empty components
public struct Npc : IComponent;
public struct Dead : IComponent;

public record struct Battle(long Tick, int Hits, int Deaths, int Shots, int Replicated, int Afflicted, bool Ceasefire)
    : ISingletonComponent;

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
    protected override void OnCreate()                                        // the system sets up the data it steers by
    {
        ref var waypoints = ref World.GetOrAddSingleton<Waypoints>();
        for (int i = 0; i < Waypoints.Count; i++) waypoints.Points[i] = new Position(512 + i * 400, 512 + i * 1777 % 3000);
    }

    protected override void OnDestroy() => World.RemoveSingleton<Waypoints>();

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

    [ForEach, Without<Dead>, Without<Frozen>]                                 // whole runs of rows, vectorized
    static void Move(Span<Position> positions, ReadOnlySpan<Velocity> velocities)
    {
        // Two floats each, no padding: as Vector<float> spans, every lane is one coordinate (the JIT does not
        // vectorize loops by itself)
        var p = MemoryMarshal.Cast<Position, Vector<float>>(positions);
        var v = MemoryMarshal.Cast<Velocity, Vector<float>>(velocities);
        for (int i = 0; i < p.Length; i++) p[i] += v[i];
        var pf = MemoryMarshal.Cast<Position, float>(positions);
        var vf = MemoryMarshal.Cast<Velocity, float>(velocities);
        for (int i = p.Length * Vector<float>.Count; i < pf.Length; i++) pf[i] += vf[i];  // the rows left over
    }
}

public partial class ManaSystem : EcsSystem                                   // same priority as MovementSystem, other
{                                                                             // data: both run at once
    public override int Priority => 1;

    [ForEach]
    static void Regenerate(ref Mana mana) => mana.Value = Math.Min(mana.Max, mana.Value + 1);
}

public partial class ZoneSystem : EcsSystem                                   // the zone index follows these writes
{
    public override int Priority => 2;

    [ForEach]
    static void Enter(Entity player, in Position p, in Zone zone)              // `in`: reading is not a change
    {
        int id = Math.Clamp((int)(p.X / 1024), 0, 3);
        if (zone.Id != id) player.SetZone(id);                                 // only real moves are writes
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
        if (roll % 50 == 0) unit.GetOrAdd<Burning>() = new Burning(10);       // applied at once; a new fire restarts it
        else if (roll % 70 == 1) unit.SetFrozen(4);                           // never frozen here: Without<Frozen>
    }
}

public partial class StatusSystem : EcsSystem
{
    public override int Priority => 4;

    [ForEach]
    static void Burn(Entity unit, ref Burning burning, ref Health hp)
    {
        hp.Value -= 3;
        if (--burning.Ticks == 0) unit.RemoveBurning();                       // flips a bit: the row stays
    }

    [ForEach]
    static void Thaw(Entity unit, ref Frozen frozen)
    {
        if (--frozen.Ticks == 0) unit.RemoveFrozen();
    }
}

public sealed class AfflictionSystem : EcsSystem                              // registered after StatusSystem
{
    public override int Priority => 4;

    // A count, not a loop: Count reads presence bits, 64 rows per load
    protected override void OnUpdate() =>
        World.Battle.Afflicted += World.Count<Health>(BattleServer.Afflicted);
}

public partial class DeathSystem : EcsSystem
{
    public override int Priority => 5;

    [ForEach, Without<Dead>]                                                  // a plain scan: most healths change often
    static void Die(Entity unit, in Health hp, ref Battle battle)
    {
        if (hp.Value > 0) return;
        unit.SetDead();                                                       // whoever cares queries Dead
        battle.Deaths++;
    }
}

public partial class RespawnSystem : EcsSystem                                // DeathSystem does not know it exists
{
    public override int Priority => 6;

    [ForEach, With<Player>, With<Dead>]
    static void Respawn(Entity player, ref Health hp, ref DynamicBuffer<Loot> bag, World world)
    {
        foreach (var shot in world.OwnedByIndex.Find(player)) shot.Destroy();  // recall its projectiles
        hp.Value = hp.Max;
        bag.Clear();                                                          // a death empties the bag
        player.Set(new Position(2048, 2048), new Mana(0, 100));               // several components, one call
        player.Remove<Dead, Burning, Frozen>();                               // back without statuses: one transition
    }

    [ForEach, With<Npc>, With<Dead>]
    static void Despawn(Entity npc, in Position p, World world)
    {
        npc.Destroy();
        var at = new Position(p.Y, p.X);
        if (world.ZoneIndex.TryFind(npc.Id & 3, out var player))              // hunt a player of some zone
        {
            at = player.Position;
            ref var bag = ref player.Get<DynamicBuffer<Loot>>();                     // the hunted player gets the loot
            if (bag.Count == BattleServer.BagSize) bag.RemoveAt(0);           // a full bag drops its oldest item
            bag.Add(new Loot(npc.Id & 15));
        }

        BattleServer.SpawnNpc(world, at, npc.Id);
    }
}

public partial class ProjectileSystem : EcsSystem
{
    public override int Priority => 7;

    [ForEach, With<Player>, Without<Dead>]
    static void Shoot(Entity player, in Position p, in Velocity v, ref Mana mana, ref Battle battle, World world)
    {
        if (battle.Ceasefire || mana.Value < BattleServer.ShotCost || Dice.Roll(battle.Tick, player.Id) % 20 != 0) return;
        mana.Value -= BattleServer.ShotCost;
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
    protected override void OnCreate() => World.GetOrAddSingleton<Battle>();
    protected override void OnUpdate() => World.Battle.Tick++;
}

[UpdateInGroup<Replication>]
public partial class ReplicationSystem : EcsSystem                            // only what changed since the last send
{
    [ForEach, Changed<Health>]
    static void Send(in Health hp, ref Replica replica, ref Battle battle)
    {
        replica.Health = hp.Value;
        battle.Replicated++;
    }

    [ForEach, Changed<Burning>]                                               // set on fire, or burning on
    static void SendFire(ref Replica replica, ref Battle battle)
    {
        if (replica.Burning) return;
        replica.Burning = true;
        battle.Replicated++;
    }

    [ForEach, Removed<Burning>]                                               // the fire went out: the row lacks it now
    static void SendFireOut(ref Replica replica, ref Battle battle)
    {
        if (!replica.Burning) return;                                         // lit and out between two sends
        replica.Burning = false;
        battle.Replicated++;
    }

    [ForEach, Changed<DynamicBuffer<Loot>>]                                          // bags changed by a ref write
    static void SendBag(in DynamicBuffer<Loot> bag, ref Battle battle) => battle.Replicated += 1 + bag.Count;
}

public static class Dice
{
    public static uint Roll(long tick, int id)
    {
        ulong x = (ulong)tick * 0x9E3779B97F4A7C15UL ^ (uint)id * 0xBF58476D1CE4E5B9UL;
        x ^= x >> 31;
        x *= 0x94D049BB133111EBUL;
        return (uint)(x ^ (x >> 29));
    }
}

/// <summary>What the server reports: computed by struct jobs over the world, outside the tick.</summary>
public record struct Census(int Players, int Alive, long Health, long Mana, Entity Weakest, int ReadyToAttack);

/// <summary>
/// The host of one battle: owns its world on one thread, ticks it, and answers questions about it between ticks.
/// </summary>
public sealed class BattleServer : IDisposable
{
    public const int ShotCost = 30;
    public const int BagSize = 8;

    private static readonly QueryFilter Players = QueryFilter.Create().With<Player>();     // build filters once
    private static readonly QueryFilter AlivePlayers = Players.Without<Dead>();
    private static readonly QueryFilter ReadyToAttack = QueryFilter.Create().Without<Dead>().Without<Frozen>();
    private static readonly QueryFilter Npcs = QueryFilter.Create().With<Npc>();
    internal static readonly QueryFilter Afflicted = QueryFilter.Create().WithAny<Burning>().WithAny<Frozen>();
    private static readonly QueryFilter UnsavedPlayers = Players.Changed<Health>(0);   // the tick is set per call

    // NPC kinds: recipes that belong to no world (no query sees them), copied by Instantiate
    private static readonly Prefab Grunt = new Prefab().Set(new Position(), new Velocity())   // several at once
        .SetHealth(120, 120).SetReplica(120, false).SetAttack(6, 8, 0).SetNpc();       // or one by name
    private static readonly Prefab Brute = new Prefab().Set(new Position(), new Velocity())
        .SetHealth(200, 200).SetReplica(200, false).SetAttack(9, 12, 0).SetNpc();

    public readonly World World;
    private readonly SingletonRef<Battle> _battle;                            // resolved once, no lookup per tick
    private WorldSnapshot _save;
    private ulong _savedAt;

    /// <summary>A battle of <paramref name="units"/> units, a third of them players.</summary>
    public BattleServer(int units)
    {
        World = new World(new WorldOptions
        {
            InitialEntityCapacity = units * 2,
            ParallelSystems = true,                                           // systems of one priority side by side
            MeasureSystems = true,                                            // per-system timings in system.Stats
        });
        World.SystemError += OnSystemError;

        World.AddSystem<ClockSystem>();                                       // OnCreate adds the Battle singleton
        World.AddSystem<SteeringSystem>();
        World.AddSystem<MovementSystem>();
        World.AddSystem<ManaSystem>();
        World.AddSystem<ZoneSystem>();
        World.AddSystem<CombatSystem>();
        World.AddSystem<StatusSystem>();
        World.AddSystem<AfflictionSystem>();
        World.AddSystem<DeathSystem>();
        World.AddSystem<RespawnSystem>();
        World.AddSystem<ProjectileSystem>();
        World.AddSystem<RegenerationSystem>();
        World.AddSystem<ReplicationSystem>();
        _battle = World.SingletonRef<Battle>();

        var players = new Entity[units / 3];                                  // in bulk: one table growth
        World.CreateEntities(players, new Position(), new Velocity(1, 0), new Health(500, 500), new Replica(500, false),
            new Mana(100, 100), new Attack(8, 6, 0), new Zone(0), new Player(), new DynamicBuffer<Loot>()); // bags
        for (int i = 0; i < players.Length; i++)
        {
            uint r = Dice.Roll(0, i);
            players[i].Position = new Position(r % 4096, (r >> 12) % 4096);
            players[i].Velocity = new Velocity((int)(r % 5) - 2, (int)((r >> 8) % 5) - 2);
            players[i].Attack.Cooldown = (int)(r % 6);
            players[i].Get<DynamicBuffer<Loot>>().EnsureCapacity(BagSize);    // room once: a full bag never grows
        }

        var npcs = new Entity[units - players.Length];
        World.Instantiate(Grunt, npcs.AsSpan(0, npcs.Length / 2));              // copies of a recipe, in bulk
        World.Instantiate(Brute, npcs.AsSpan(npcs.Length / 2));
        for (int i = 0; i < npcs.Length; i++)
        {
            uint r = Dice.Roll(0, players.Length + i);
            npcs[i].Position = new Position(r % 4096, (r >> 12) % 4096);
            npcs[i].Attack.Cooldown = (int)(r % 10);
        }
    }

    public static void SpawnNpc(World world, Position at, int seed)
    {
        uint r = Dice.Roll(seed, seed);
        var npc = world.Instantiate((r & 1) == 0 ? Grunt : Brute);            // then only what differs is written
        npc.Position = at;
        npc.Attack.Cooldown = (int)(r % 10);
    }

    public ref Battle Battle => ref _battle.Value;

    /// <summary>The last system that threw; it was switched off and the battle went on.</summary>
    public (EcsSystem System, Exception Error) Failure { get; private set; }

    /// <summary>One server tick: the simulation every tick, replication every third.</summary>
    public void Tick()
    {
        World.Update();
        if (Battle.Tick % 3 == 0) World.Update<Replication>();
    }

    /// <summary>Whether players hold fire: a rule of the battle is data, so shots in flight still expire.</summary>
    public bool Ceasefire
    {
        get => Battle.Ceasefire;
        set => Battle.Ceasefire = value;
    }

    public int PlayersIn(int zone) => World.ZoneIndex.Count(zone);

    public bool AnyoneBurning => World.Any<Burning>();

    public bool AnyoneAfflicted => World.Any<Health>(Afflicted);              // burning or frozen

    public int ShotsLeft(Entity player) => player.IsAlive && player.TryGetMana(out var mana) ? mana.Value / ShotCost : 0;

    /// <summary>A client's hit on the unit it names by id: the id turns back into a handle of the living entity.</summary>
    public bool Hit(int targetId, int damage)
    {
        if (!World.TryGetEntity(targetId, out var target) || target.IsDead) return false;
        ref var hp = ref target.TryGetRef<Health>(out bool found);           // optional access by ref
        if (!found) return false;
        hp.Value -= damage;                                                   // a write: replicated with the next send
        return true;
    }

    /// <summary>
    /// A mirror image of a living unit as it is now (health, statuses, bag, zone): a copy of the entity, which the
    /// indexes and the next send learn about.
    /// </summary>
    public Entity Mirror(Entity unit) => unit.IsAlive ? unit.Clone() : default;

    /// <summary>The items in a player's bag, summed: a read (<c>Read</c>), never a change.</summary>
    public int LootValue(Entity player)
    {
        int value = 0;
        foreach (var loot in player.Read<DynamicBuffer<Loot>>()) value += loot.Item;
        return value;
    }

    /// <summary>A client hands a player's oldest item to another player: two tracked writes, replicated next send.</summary>
    public bool Give(Entity from, Entity to)
    {
        // Check with reads first: a ref access counts as a change, and an unchanged bag must not be replicated
        if (!from.Has<DynamicBuffer<Loot>>() || !to.Has<DynamicBuffer<Loot>>()) return false;
        if (from.Read<DynamicBuffer<Loot>>().Count == 0) return false;
        if (to.Read<DynamicBuffer<Loot>>().Count == BagSize) return false;
        ref var source = ref from.Get<DynamicBuffer<Loot>>();
        to.Get<DynamicBuffer<Loot>>().Add(source[0]);
        source.RemoveAt(0);
        return true;
    }

    /// <summary>The living player closest to <paramref name="at"/>: an ad-hoc query.</summary>
    public Entity NearestPlayer(Position at)
    {
        Entity nearest = default;
        float best = float.MaxValue;
        foreach (var item in World.Query<Position>(AlivePlayers))            // Position is not tracked: reads are free
        {
            float dx = item.Component1.X - at.X, dy = item.Component1.Y - at.Y, distance = dx * dx + dy * dy;
            if (distance < best) (best, nearest) = (distance, item.Entity);
        }

        return nearest;
    }

    public Census Census()
    {
        var health = new SumHealth();
        World.ForEachBatch<Health, SumHealth>(ref health, AlivePlayers);
        var mana = new SumMana();
        World.ForEach<Mana, SumMana>(ref mana);
        var weakest = new FindWeakest { Lowest = int.MaxValue };
        World.ForEachEntity<Health, FindWeakest>(ref weakest, AlivePlayers);
        return new Census(World.Count<Player>(), World.Count<Player>(AlivePlayers), health.Total, mana.Total,
            weakest.Entity, World.Count<Attack>(ReadyToAttack));
    }

    /// <summary>A new round: projectiles gone, everyone healed, the NPCs scattered.</summary>
    public int NewRound()
    {
        int cleared = World.DestroyEntities<Lifetime>();                      // one pass, however many
        var heal = new HealFully();
        World.ForEachParallel<Health, HealFully>(ref heal);                   // a struct job on all cores
        var scatter = new Scatter { Round = Battle.Tick };
        World.ForEachEntityParallel<Position, Scatter>(ref scatter, Npcs);    // the same, receiving each handle
        return cleared;
    }

    /// <summary>Save to memory; <see cref="Unsaved"/> counts the players written since.</summary>
    public void Save()
    {
        _save = World.Capture();
        _savedAt = World.ChangeTick;
    }

    public void Load() => World.Restore(_save);

    public int Unsaved => World.Count<Health>(UnsavedPlayers.Since(_savedAt));

    /// <summary>The system that took the most time per run so far (<see cref="WorldOptions.MeasureSystems"/>).</summary>
    public EcsSystem Slowest()
    {
        EcsSystem slowest = null;
        foreach (var system in World.Systems)
            if (slowest == null || system.Stats.Average > slowest.Stats.Average) slowest = system;
        return slowest;
    }

    private void OnSystemError(EcsSystem system, Exception error)
    {
        system.Enabled = false;                                               // isolate the failure, keep the battle
        Failure = (system, error);
    }

    public void Dispose() => World.Dispose();                                 // OnDestroy of every system

    private struct SumHealth : IForEachBatch<Health>
    {
        public long Total;
        public static int Writes => 0;                                        // reads only: rows stay unchanged
        public void Execute(EntitySpan entities, Span<Health> hp)
        {
            // Value, Max, Value, Max, ...: sum every lane, keep the even ones (an int lane holds 4M rows of 500)
            var lanes = MemoryMarshal.Cast<Health, Vector<int>>(hp);
            var sum = Vector<int>.Zero;
            for (int i = 0; i < lanes.Length; i++) sum += lanes[i];
            Total += Vector.Sum(sum & Vector.Equals(Vector<int>.Indices & Vector<int>.One, Vector<int>.Zero));
            for (int i = lanes.Length * Vector<int>.Count / 2; i < hp.Length; i++) Total += hp[i].Value;  // left over
        }
    }

    private struct SumMana : IForEach<Mana>, INoStructuralChanges
    {
        public long Total;
        public static int Writes => 0;
        public void Execute(ref Mana mana) => Total += mana.Value;
    }

    private struct FindWeakest : IForEachEntity<Health>, INoStructuralChanges
    {
        public Entity Entity;
        public int Lowest;
        public static int Writes => 0;
        public void Execute(Entity entity, ref Health hp) { if (hp.Value < Lowest) (Lowest, Entity) = (hp.Value, entity); }
    }

    private struct HealFully : IForEach<Health>, INoStructuralChanges        // one copy per thread: no shared state
    {
        public void Execute(ref Health hp) => hp.Value = hp.Max;
    }

    private struct Scatter : IForEachEntity<Position>, INoStructuralChanges   // NPCs start the round anywhere
    {
        public long Round;

        public void Execute(Entity npc, ref Position p)
        {
            uint r = Dice.Roll(Round, npc.Id);                                // per entity, the same on any thread
            p = new Position(r % 4096, (r >> 12) % 4096);
        }
    }
}
