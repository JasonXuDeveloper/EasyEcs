using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EasyEcs.Benchmark.Showcase;

// A battle server that uses every feature of EasyEcs the way users should: plain data, one job per system, state
// changes as data. The docs page example.mdx (https://ecs.xgamedev.net/en/docs/example) includes its regions, so the
// page always shows this code; BattleServerBenchmark measures it and BattleServerTests check it.

// #region components
public record struct Position(float X, float Y) : IComponent;
public record struct Velocity(float X, float Y) : IComponent;
public record struct Health(int Value, int Max) : IComponent, ITracked;         // replicated: writes are tracked
public record struct Replica(int Health, bool Burning, int Waypoint) : IComponent;  // what clients last received
public record struct Mana(int Value, int Max) : IComponent;
public record struct Attack(int Damage, int Interval, int Cooldown) : IComponent;
public record struct Burning(int Ticks) : IComponent, ITracked;               // status effects come and go; clients
                                                                              // see fires start and go out
public record struct Frozen(int Ticks) : IComponent;                          // while it lasts, Attack and Velocity
                                                                              // are disabled: no query sees them
public record struct Goal(int Waypoint) : IComponent, ITracked;              // where an NPC heads: replicated,
                                                                              // re-planned often, rarely different
public record struct Lifetime(int Ticks) : IComponent;
public partial record struct Zone([Group] int Id) : IComponent;              // a table per zone
public partial record struct Kills([IndexKey] int Count) : IComponent;            // indexed, ordered: a leaderboard
public partial record struct Account([UniqueKey] int Id) : IComponent;       // one hero per account
public record struct Spawn(Position At, int Seed) : ICleanupComponent;        // outlives its NPC: where the next appears
public record struct Loot(int Item);                                          // players carry a DynamicBuffer<Loot>
public record struct Friend(EntityId Target) : IRelation;                     // and a DynamicBuffer<Friend> of links
public struct Player : IComponent;                                            // tags: empty components
public struct Npc : IComponent;
public struct Dead : IComponent;
// #endregion components

// #region singletons
public record struct Battle(long Tick, int Hits, int Deaths, int Shots, int Replicated, int Afflicted) : ISingletonComponent;

public record struct Rules(int ShotCost, int BagSize, bool Ceasefire) : ISingletonComponent;   // the battle's rules: data

public struct Waypoints : ISingletonComponent
{
    public const int Count = 8;
    public WaypointArray Points;
}

[InlineArray(Waypoints.Count)]
public struct WaypointArray { private Position _first; }

public struct Replication : ISystemGroup;                                     // runs when the server sends updates
// #endregion singletons

// #region movement
public class PlanningSystem : EcsSystem                                       // where each NPC heads next
{
    protected override void OnCreate()                                        // the system sets up the data it plans with
    {
        ref var waypoints = ref World.GetOrAddWaypoints();
        for (int i = 0; i < Waypoints.Count; i++) waypoints.Points[i] = new Position(512 + i * 400, 512 + i * 1777 % 3000);
    }

    protected override void OnDestroy() => World.RemoveWaypoints();

    // A plan need not be fresh every tick: each run plans a quarter of the NPCs, so each one every fourth tick
    [ForEach(Slices = 4), Without<Dead>]
    static void Plan(in Position p, Mut<Goal> goal, in Waypoints waypoints)
    {
        int next = goal.Value.Waypoint;                                       // Value: a read, not a write
        float nearest = float.MaxValue;
        for (int i = 0; i < Waypoints.Count; i++)                             // the nearest waypoint not reached yet
        {
            float dx = waypoints.Points[i].X - p.X, dy = waypoints.Points[i].Y - p.Y, distance = dx * dx + dy * dy;
            if (distance > 64 * 64 && distance < nearest) (nearest, next) = (distance, i);
        }

        goal.SetIfChanged(new Goal(next));                                    // mostly the same goal: no stamp, no send
    }
}

public class SteeringSystem : EcsSystem                                       // heavy per-entity math: on all cores
{
    [ForEach(Parallel = true), With<Npc>, Without<Dead>, Without<Frozen>]
    static void Steer(in Position p, in Goal goal, ref Velocity v, in Waypoints waypoints)
    {
        var target = waypoints.Points[goal.Waypoint];
        float dx = target.X - p.X, dy = target.Y - p.Y, length = MathF.Sqrt(dx * dx + dy * dy) + 1;
        v = new Velocity(dx / length * 2, dy / length * 2);
    }
}

public class MovementSystem : EcsSystem
{
    [ForEach, Without<Dead>]                                                  // whole runs of rows, vectorized
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

[UpdateInterval(10, Phase = 5)]                                               // every 10th update, five updates after
public class ManaSystem : EcsSystem                                           // RegenerationSystem: their work spreads;
{                                                                             // data no earlier system touches: it runs
                                                                              // beside PlanningSystem

    [ForEach]
    static void Regenerate(ref Mana mana) => mana.Value = Math.Min(mana.Max, mana.Value + 10);
}

public class ZoneSystem : EcsSystem                                           // a new zone moves the row to its table
{
    [ForEach]
    static void Enter(Entity player, in Position p, in Zone zone)              // `in`: reading is not a change
    {
        int id = Math.Clamp((int)(p.X / 1024), 0, 3);
        if (zone.Id != id) player.SetZone(id);                                 // only real moves are writes
    }
}
// #endregion movement

// #region combat
public class CombatSystem : EcsSystem                                         // damage only: statuses are StatusSystem's
{
    [ForEach, Without<Dead>]                                                  // a frozen unit's Attack is disabled
    static void Fight(ref Attack attack, Mut<Health> hp, ref Battle battle)
    {
        if (--attack.Cooldown > 0) return;
        attack.Cooldown = attack.Interval;
        // The fight is abstracted: a unit pays for the hits it trades from its own Health. A hit is a change of Health,
        // so only the units hit this tick are stamped.
        hp.Write().Value -= attack.Damage;
        battle.Hits++;
    }
}

public class StatusSystem : EcsSystem
{
    [ForEach, Without<Dead>]                                                  // the units that attacked this tick
    static void Afflict(Entity unit, in Attack attack, in Battle battle)
    {
        if (attack.Cooldown != attack.Interval) return;                      // CombatSystem just reset it
        uint roll = Dice.Roll(battle.Tick, unit.Id.Index);
        if (roll % 50 == 0) unit.GetOrAddBurning() = new Burning(10);         // applied at once; a new fire restarts it
        else if (roll % 70 == 1)
        {
            unit.SetFrozen(4);
            unit.DisableAttack();                                             // kept, but no query sees them: no attacks,
            unit.DisableVelocity();                                           // no steering, no moves, no shots
        }
    }

    [ForEach]
    static void Burn(Entity unit, ref Burning burning, ref Health hp)
    {
        hp.Value -= 3;
        if (--burning.Ticks == 0) unit.RemoveBurning();                       // flips a bit: the row stays
    }

    [ForEach]
    static void Thaw(Entity unit, ref Frozen frozen)
    {
        if (--frozen.Ticks > 0) return;
        unit.RemoveFrozen();
        unit.EnableAttack();                                                  // back with the values they had
        unit.EnableVelocity();
    }
}

public sealed class AfflictionSystem : EcsSystem                              // registered after StatusSystem
{
    // A count, not a loop: Count reads presence bits, 64 rows per load
    protected override void OnUpdate() =>
        World.Battle.Afflicted += World.Count<Health>(BattleServer.Afflicted);
}

public class DeathSystem : EcsSystem
{
    [ForEach, Without<Dead>]                                                  // a plain scan: most healths change often
    static void Die(Entity unit, in Health hp, ref Battle battle)
    {
        if (hp.Value > 0) return;
        unit.SetDead();                                                       // whoever cares queries Dead
        battle.Deaths++;
    }
}
// #endregion combat

// #region respawn
public class RespawnSystem : EcsSystem                                        // DeathSystem does not know it exists
{
    [ForEach, With<Player>, With<Dead>]
    static void Respawn(Entity player, ref Health hp, ref DynamicBuffer<Loot> bag, in DynamicBuffer<Friend> friends,
        World world)
    {
        foreach (var shot in world.Children(player)) shot.Destroy();          // recall its projectiles
        hp.Value = hp.Max;
        bag.Clear();                                                          // a death empties the bag
        var at = new Position(2048, 2048);
        foreach (var link in friends)                                         // next to a living friend, if any
            if (world.TryGetEntity(link.Target, out var friend) && !friend.IsDead)
            {
                at = friend.Position;
                break;
            }

        player.Set(at, new Mana(0, 100), new Kills(0));                       // several components, one call; a death
                                                                              // ends the kill streak
        player.Remove<Dead, Burning, Frozen>();                               // back without statuses: one transition
        player.EnableAttack();                                                // false when it was not frozen
        player.EnableVelocity();
    }

    // [Consume]: each NPC is destroyed after the method, which retires it (Spawn stays, with the Retired tag)
    [ForEach, Consume, With<Npc>, With<Dead>]
    static void Despawn(Entity npc, in Position p, ref Spawn spawn, in Rules rules, World world)
    {
        spawn = new Spawn(new Position(p.Y, p.X), npc.Id.Index);              // where the next one appears
        Entity hunter = default;
        foreach (var item in world.Query<Position>(BattleServer.Zones[npc.Id.Index & 3]))  // a player of some zone:
        {                                                                     // one table to look at
            hunter = item.Entity;
            spawn.At = item.Position;
            break;
        }

        if (hunter.IsAlive)
        {
            ref var bag = ref hunter.WriteLoot();                             // the hunter gets the loot
            if (bag.Count == rules.BagSize) bag.RemoveAt(0);                  // a full bag drops its oldest item
            bag.Add(new Loot(npc.Id.Index & 15));
            hunter.SetKills(hunter.Kills.Count + 1);                          // the leaderboard follows
        }
    }

    [ForEach, With<Retired>]                                                  // what retired NPCs left behind
    static void Revive(Entity old, in Spawn spawn, World world)
    {
        BattleServer.SpawnNpc(world, spawn.At, spawn.Seed);
        old.RemoveSpawn();                                                    // its last cleanup component: freed
    }
}
// #endregion respawn

// #region projectiles
public class ProjectileSystem : EcsSystem
{
    [ForEach, With<Player>, Without<Dead>]                                    // a frozen player's Velocity is disabled
    static void Shoot(Entity player, in Position p, in Velocity v, ref Mana mana, ref Battle battle, in Rules rules, World world)
    {
        if (rules.Ceasefire || mana.Value < rules.ShotCost || Dice.Roll(battle.Tick, player.Id.Index) % 20 != 0) return;
        mana.Value -= rules.ShotCost;
        battle.Shots++;
        world.Instantiate(BattleServer.Shot, p, new Velocity(v.X * 4, v.Y * 4), new ChildOf(player));  // one row copy;
    }                                                                         // a child: gone with its player

    [ForEach]
    static void Expire(Entity shot, ref Lifetime life)
    {
        if (--life.Ticks == 0) shot.Destroy();
    }
}

[UpdateInterval(10)]                                                          // every 10th update
public class RegenerationSystem : EcsSystem
{
    [ForEach, With<Player>, Without<Dead>]
    static void Regenerate(Mut<Health> hp, Maybe<Burning> burning)            // burning or not: one method
    {
        var (value, max) = hp.Value;                                          // a read: nothing stamped
        if (value < max) hp.Write().Value = Math.Min(max, value + (burning.HasValue ? 5 : 20));
    }
}

[UpdateAfter<RegenerationSystem>]                                             // registered first, runs last
public sealed class ClockSystem : EcsSystem
{
    protected override void OnCreate() => World.GetOrAddBattle();
    protected override void OnUpdate() => World.Battle.Tick++;
}
// #endregion projectiles

// #region replication
[UpdateInGroup<Replication>]
public class ReplicationSystem : EcsSystem                                    // only what changed since the last send
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

    [ForEach, Changed<Goal>]                                                  // the goals PlanningSystem changed, not
    static void SendGoal(in Goal goal, ref Replica replica, ref Battle battle)  // every goal it planned
    {
        replica.Waypoint = goal.Waypoint;
        battle.Replicated++;
    }

    [ForEach, Changed<DynamicBuffer<Loot>>]                                          // bags changed by a ref write
    static void SendBag(in DynamicBuffer<Loot> bag, ref Battle battle) => battle.Replicated += 1 + bag.Count;
}
// #endregion replication

// #region requests
/// <summary>
/// What a client asks of one unit: [ForEntity] writes the job <c>entity.Run</c> runs (one table lookup, Health its
/// WriteMask) and the entry point <c>Requests.TakeHit(unit, damage)</c>. The write is replicated with the next send.
/// </summary>
public static class Requests
{
    [ForEntity, Without<Dead>]
    static void TakeHit(ref Health hp, int damage) => hp.Value -= damage;
}
// #endregion requests

// #region reports
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

/// <summary>
/// [ForEach] methods outside systems: the generator writes the job and an entry point taking the world and the
/// caller's values (<c>Reports.SumMana(world, ref total)</c>), for passes the host runs between ticks.
/// </summary>
public static class Reports
{
    [ForEach, With<Player>, Without<Dead>]                                    // whole runs of rows, read-only
    static void SumHealth(ReadOnlySpan<Health> hp, ref long total)
    {
        // Value, Max, Value, Max, ...: sum every lane, keep the even ones (an int lane holds 4M rows of 500)
        var lanes = MemoryMarshal.Cast<Health, Vector<int>>(hp);
        var sum = Vector<int>.Zero;
        for (int i = 0; i < lanes.Length; i++) sum += lanes[i];
        total += Vector.Sum(sum & Vector.Equals(Vector<int>.Indices & Vector<int>.One, Vector<int>.Zero));
        for (int i = lanes.Length * Vector<int>.Count / 2; i < hp.Length; i++) total += hp[i].Value;  // left over
    }

    [ForEach]
    static void SumMana(in Mana mana, ref long total) => total += mana.Value;

    [ForEach, With<Player>, Without<Dead>]
    static void FindWeakest(Entity player, in Health hp, ref EntityId weakest, ref int lowest)
    {
        if (hp.Value < lowest) (lowest, weakest) = (hp.Value, player);
    }

    [ForEach(Parallel = true)]                                                // chunks on all cores: no shared state
    static void HealFully(ref Health hp) => hp.Value = hp.Max;

    [ForEach(Parallel = true), With<Npc>]                                     // NPCs start the round anywhere
    static void Scatter(Entity npc, ref Position p, long round)
    {
        uint r = Dice.Roll(round, npc.Id.Index);                              // per entity, the same on any thread
        p = new Position(r % 4096, (r >> 12) % 4096);
    }
}

/// <summary>What the server reports: computed by the runners of <see cref="Reports"/>, outside the tick.</summary>
public record struct Census(int Players, int Alive, long Health, long Mana, Entity Weakest, int ReadyToAttack);
// #endregion reports

/// <summary>
/// The host of one battle: owns its world on one thread, ticks it, and answers questions about it between ticks.
/// </summary>
public sealed class BattleServer : IDisposable
{
    // #region prefabs
    public const int ShotCost = 30;
    public const int BagSize = 8;

    private static readonly QueryFilter Players = QueryFilter.Create().With<Player>();     // build filters once
    private static readonly QueryFilter AlivePlayers = Players.Without<Dead>();
    private static readonly QueryFilter Alive = QueryFilter.Create().Without<Dead>();
    internal static readonly QueryFilter Afflicted = QueryFilter.Create().WithAny<Burning>().WithAny<Frozen>();
    private static readonly QueryFilter WrittenPlayers = Players.Changed<Health>(0);    // the tick is set per call
    internal static readonly QueryFilter[] Zones =                                        // one table each
        [Players.InZone(0), Players.InZone(1), Players.InZone(2), Players.InZone(3)];
    private static readonly QueryFilter ClientChanges =                           // any of three: one filter, a client's
        QueryFilter.Create().ChangedAny<Health, Burning, Goal>(0);                // tick set per call
    private static readonly QueryFilter FiresOut = QueryFilter.Create().Removed<Burning>(0);

    // Unit kinds: recipes that belong to no world (no query sees them), copied by Instantiate
    private static readonly Prefab Hero = new Prefab().Set(new Position(), new Velocity(1, 0))   // several at once
        .SetHealth(500, 500).SetReplica(500, false, 0).SetMana(100, 100).SetAttack(8, 6, 0)     // or one by name
        .SetZone(0).SetKills(0).SetPlayer().Set(new DynamicBuffer<Friend>())
        .SetLoot(new Loot(0));                                    // a starter item: each hero gets a bag of its own
    private static readonly Prefab Grunt = new Prefab().Set(new Position(), new Velocity())
        .SetHealth(120, 120).SetReplica(120, false, 0).SetAttack(6, 8, 0).SetGoal(0).SetSpawn(default, 0).SetNpc();
    private static readonly Prefab Brute = new Prefab(Grunt)                                    // a variant: a copy
        .SetHealth(200, 200).SetReplica(200, false, 0).SetAttack(9, 12, 0);
    internal static readonly Prefab Shot = new Prefab().SetLifetime(20);  // the rest is given per shot (Instantiate)
    // #endregion prefabs

    // #region setup
    public readonly World World;
    private readonly SingletonRef<Battle> _battle;                            // resolved once, no lookup per tick
    private readonly List<EntityId> _group = [];                              // reused by Transfer
    private WorldSnapshot _checkpoint;
    private ulong _checkpointAt;

    /// <summary>
    /// A battle of <paramref name="units"/> units, a third of them players: bots, beside the heroes clients bring
    /// (<see cref="Join"/>).
    /// </summary>
    public BattleServer(int units)
    {
        World = new World(new WorldOptions
        {
            InitialEntityCapacity = units * 2,
            ParallelSystems = true,                                           // unordered systems with disjoint data side by side
            MeasureSystems = true,                                            // per-system timings in system.Stats
            SystemErrors = SystemErrorPolicy.Disable,                         // a failing system goes off, the battle goes on
            DestroyLogCapacity = units,                                       // the ids gone while a client lags (CatchUp)
        });
        World.GroupBy<Zone>();                                                // a zone's players contiguous, in its table
        World.FriendIndex.WhenTargetDestroyed(TargetPolicy.Remove);   // a player who leaves leaves friend lists
        World.SetRules(ShotCost, BagSize, false);

        World.AddSystem<ClockSystem>();                                       // OnCreate adds the Battle singleton
        World.AddSystem<PlanningSystem>();                                    // and this one the Waypoints
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

        var players = new Entity[units / 3];
        World.Instantiate(Hero, players);                                     // in bulk: one table growth
        for (int i = 0; i < players.Length; i++)
        {
            uint r = Dice.Roll(0, i);
            players[i].Position = new Position(r % 4096, (r >> 12) % 4096);
            players[i].Velocity = new Velocity((int)(r % 5) - 2, (int)((r >> 8) % 5) - 2);
            players[i].Attack.Cooldown = (int)(r % 6);
            players[i].WriteLoot().EnsureCapacity(BagSize);                   // room once: a full bag never grows
            players[i].LinkFriend(players[(i + 1) % players.Length]); // each player has two friends
            players[i].LinkFriend(players[(i + 7) % players.Length]);
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

        World.Reserve(Grunt, npcs.Length / 4);                         // respawn waves never grow the tables
        World.Reserve(Brute, npcs.Length / 4);
    }

    public static void SpawnNpc(World world, Position at, int seed)
    {
        uint r = Dice.Roll(seed, seed);
        var npc = world.Instantiate((r & 1) == 0 ? Grunt : Brute, at);        // a copy with its position: one row copy
        npc.Attack.Cooldown = (int)(r % 10);                                  // then only what differs is written
    }
    // #endregion setup

    // #region tick
    public ref Battle Battle => ref _battle.Value;

    /// <summary>A system that threw: it was switched off and the battle went on (its Error is the failure, as data).</summary>
    public EcsSystem Failure
    {
        get
        {
            foreach (var system in World.Systems)
                if (system.Error != null) return system;
            return null;
        }
    }

    /// <summary>One server tick: the simulation every tick, replication every third.</summary>
    public void Tick()
    {
        World.Update();
        if (Battle.Tick % 3 == 0) World.Update<Replication>();
    }

    /// <summary>Whether players hold fire: a rule of the battle is data, so shots in flight still expire.</summary>
    public bool Ceasefire
    {
        get => World.Rules.Ceasefire;
        set => World.Rules.Ceasefire = value;
    }
    // #endregion tick

    // #region accounts
    /// <summary>
    /// A client logs in with its account: a hero of its own at <paramref name="at"/>, or the hero it already has (a
    /// reconnect). An account is a unique key: two heroes never share one.
    /// </summary>
    public Entity Join(int account, Position at)
    {
        if (World.AccountIndex.TryGet(account, out var hero)) return hero;    // one hash probe
        hero = World.Instantiate(Hero, at, new Account(account));             // the given values in the same row copy
        hero.WriteLoot().EnsureCapacity(BagSize);
        return hero;
    }

    /// <summary>An admin kicks an account out: its hero leaves (<see cref="Leave"/>).</summary>
    public bool Kick(int account) => World.AccountIndex.TryGet(account, out var hero) && Leave(hero);
    // #endregion accounts

    // #region queries
    public int PlayersIn(int zone) => World.Count<Player>(Zones[zone]);    // one table: its rows, not a scan

    public bool AnyoneBurning => World.Any<Burning>();

    public bool AnyoneAfflicted => World.Any<Health>(Afflicted);              // burning or frozen

    public int ShotsLeft(Entity player) => player.IsAlive && player.TryGetMana(out var mana) ? mana.Value / ShotCost : 0;

    /// <summary>
    /// A client's hit on the unit it names by id: the id turns back into a handle if that unit still lives, and the hit
    /// runs as a job on that one unit.
    /// </summary>
    public bool Hit(EntityId target, int damage) =>
        World.TryGetEntity(target, out var unit) && Requests.TakeHit(unit, damage);   // false: dead, or no Health
    // #endregion queries

    // #region catch-up
    /// <summary>
    /// What a client that acknowledged change tick <paramref name="acked"/> (the <see cref="World.ChangeTick"/> of the
    /// update it received) has missed: <paramref name="updates"/> units whose health, fire or goal was written plus
    /// fires that went out, and in <paramref name="gone"/> the ids destroyed since (room for
    /// <see cref="WorldOptions.DestroyLogCapacity"/> ids holds them all). False: the client lags further back than the destroy
    /// log reaches, and gets the whole battle again.
    /// </summary>
    public bool CatchUp(ulong acked, Span<EntityId> gone, out int updates, out int destroyed)
    {
        updates = 0;
        if (!World.DestroyedSince(acked, gone, out destroyed)) return false;  // too far behind: resynchronize
        updates = World.Count<Replica>(ClientChanges.Since(acked))            // each client its own tick, one filter
            + World.Count<Replica>(FiresOut.Since(acked));
        return true;
    }
    // #endregion catch-up

    // #region buffers-and-indexes
    /// <summary>
    /// A mirror image of a living unit as it is now (health, statuses, bag, zone): a copy of the entity, which the
    /// indexes, groups and the next send learn about. A client's hero is one of a kind (no copy may share its account):
    /// only bots and NPCs are mirrored.
    /// </summary>
    public Entity Mirror(Entity unit) => unit.IsAlive && !unit.HasAccount ? unit.Clone() : default;

    /// <summary>The items in a player's bag, summed: a read, never a change.</summary>
    public int LootValue(Entity player)
    {
        int value = 0;
        foreach (var loot in player.Loot) value += loot.Item;
        return value;
    }

    /// <summary>A client hands a player's oldest item to another player: two tracked writes, replicated next send.</summary>
    public bool Give(Entity from, Entity to)
    {
        // Check with reads first: a write counts as a change, and an unchanged bag must not be replicated
        if (!from.HasLoot || !to.HasLoot || from.Loot.Count == 0 || to.Loot.Count == BagSize) return false;
        ref var source = ref from.WriteLoot();
        to.WriteLoot().Add(source[0]);
        source.RemoveAt(0);
        return true;
    }

    /// <summary>The players with the most kills, best first: an ordered walk of the Kills index.</summary>
    public int TopKillers(Span<Entity> top)
    {
        int count = 0;
        foreach (var player in World.KillsIndex.Descending())
        {
            if (count == top.Length) break;
            top[count++] = player;
        }

        return count;
    }

    /// <summary>How many players list <paramref name="player"/> as a friend: the relation's index, by target.</summary>
    public int FriendsOf(Entity player) => World.FriendIndex.Count(player);
    // #endregion buffers-and-indexes

    // #region leave-and-transfer
    /// <summary>A player leaves: its projectiles go with it (children), and it drops out of friend lists (the policy).</summary>
    public bool Leave(Entity player)
    {
        if (!player.IsAlive || !player.IsPlayer) return false;
        player.Destroy();
        return true;
    }

    /// <summary>
    /// Move a player and its projectiles to another battle of this thread: they arrive as new entities, the shots
    /// still its children; links to entities left behind (friends) become null.
    /// </summary>
    public Entity Transfer(Entity player, BattleServer to)
    {
        if (!player.IsAlive || !player.IsPlayer) return default;
        _group.Clear();
        World.Descendants(player).AddTo(_group, includeRoot: true);           // the whole subtree moves together
        Entity arrived = default;
        World.MoveEntities(to.World, CollectionsMarshal.AsSpan(_group), new Span<Entity>(ref arrived));
        return arrived;
    }
    // #endregion leave-and-transfer

    // #region census
    /// <summary>The living player closest to <paramref name="at"/>: an ad-hoc query.</summary>
    public Entity NearestPlayer(Position at)
    {
        Entity nearest = default;
        float best = float.MaxValue;
        foreach (var item in World.Query<Position>(AlivePlayers))            // Position is not tracked: reads are free
        {
            float dx = item.Position.X - at.X, dy = item.Position.Y - at.Y, distance = dx * dx + dy * dy;
            if (distance < best) (best, nearest) = (distance, item.Entity);
        }

        return nearest;
    }

    public Census Census()
    {
        long health = 0, mana = 0;
        Reports.SumHealth(World, ref health);
        Reports.SumMana(World, ref mana);
        EntityId weakest = default;
        int lowest = int.MaxValue;
        Reports.FindWeakest(World, ref weakest, ref lowest);
        World.TryGetEntity(weakest, out var handle);                          // none when every player is dead
        return new Census(World.Count<Player>(), World.Count<Player>(AlivePlayers), health, mana, handle,
            World.Count<Attack>(Alive));                                      // a frozen unit's Attack is not counted
    }
    // #endregion census

    // #region new-round
    /// <summary>A new round: projectiles gone, statuses lifted, everyone healed, the NPCs scattered.</summary>
    public int NewRound()
    {
        int cleared = World.DestroyAll<Lifetime>();                      // one pass, however many
        World.RemoveAll<Burning>();                                           // in bulk: table by table, not per entity
        World.RemoveAll<Frozen>();
        World.EnableAll<Attack>();
        World.EnableAll<Velocity>();
        Reports.HealFully(World);                                             // on all cores
        Reports.Scatter(World, Battle.Tick);
        return cleared;
    }
    // #endregion new-round

    // #region snapshots-and-saves
    /// <summary>Keep the battle in memory; <see cref="WrittenSinceCheckpoint"/> counts the players written since.</summary>
    public void Checkpoint()
    {
        _checkpoint = World.Capture();
        _checkpointAt = World.ChangeTick;
    }

    /// <summary>Back to the last checkpoint.</summary>
    public void Rewind() => World.Restore(_checkpoint);

    public int WrittenSinceCheckpoint => World.Count<Health>(WrittenPlayers.Since(_checkpointAt));

    /// <summary>Save the battle (entities, components, singletons) as bytes: to disk, to another process.</summary>
    public void Save(IBufferWriter<byte> output) => World.Save(output);

    /// <summary>Replace the battle with a saved one: entities keep their ids, so links still point right.</summary>
    public void Load(ReadOnlySpan<byte> save) => World.Load(save);
    // #endregion snapshots-and-saves

    // #region diagnostics
    /// <summary>The world's tables: their components, rows and bytes, for a dashboard.</summary>
    public TableInfo[] Tables() => World.DescribeTables();

    /// <summary>
    /// How the systems run: every group's stages, what each system reads and writes, why it waits. Log it once at
    /// startup (<c>ToString</c>) or draw it (<c>ToMermaid</c>).
    /// </summary>
    public ScheduleInfo Schedule() => World.DescribeSchedule();

    /// <summary>The system that took the most time per run so far (<see cref="WorldOptions.MeasureSystems"/>).</summary>
    public EcsSystem Slowest()
    {
        EcsSystem slowest = null;
        foreach (var system in World.Systems)
            if (slowest == null || system.Stats.AverageTime > slowest.Stats.AverageTime) slowest = system;
        return slowest;
    }

    public void Dispose() => World.Dispose();                                 // OnDestroy of every system
    // #endregion diagnostics
}
