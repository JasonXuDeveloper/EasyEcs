using System;
using System.Buffers;
using System.Collections.Generic;
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
public partial record struct Zone([GroupBy] int Id) : IComponent;            // every world: a table per zone,
                                                                              // its players contiguous
public partial record struct Kills([IndexKey] int Count) : IComponent;            // indexed, ordered: a leaderboard
public partial record struct Account([UniqueKey] int Id) : IComponent;       // one hero per account
public record struct Spawn(Position At) : ICleanupComponent;                  // outlives its NPC: where the next appears
public record struct Loot(int Item);                                          // players carry a DynamicBuffer<Loot>
public record struct Kill(EntityId Victim);                                   // a record, not a component: the kill feed
[WhenTargetDestroyed(TargetPolicy.Remove)]                                    // a player who leaves leaves friend lists
public record struct Friend(EntityId Target) : IRelation;                     // and a DynamicBuffer<Friend> of links
public struct Player : IComponent;                                            // tags: empty components
public struct Npc : IComponent;
public struct Dead : IComponent;
// #endregion components

// #region singletons
// The battle's totals so far
public record struct Battle(long Tick, int Hits, int Deaths, int Shots, int Replicated, int Afflicted) : ISingletonComponent;

// The hits of one CombatSystem pass (a ref Reduce<T> parameter): each run of rows counts in a register, and the
// combined count replaces the singleton when the pass ends, so TallySystem adds it to the total. [Sum] on a field: the
// generator writes how two partial counts combine, and a vector loop folds its lanes into it
public partial struct Strikes : ISingletonComponent { [Sum] public int Count; }

// The streams of EntityRandom: one per draw of an entity in a tick, so no two draws share their numbers
public static class Draws
{
    public const uint Setup = 0, Status = 1, Shot = 2, Spawn = 3, Hunter = 4, Loot = 5, Kind = 6, Respawn = 7, Scatter = 8;
}

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
[EcsSystem]
public ref struct PlanningSystem                                          // where each NPC heads next
{
    static void OnCreate(World world)                                        // the system sets up the data it plans with
    {
        ref var waypoints = ref world.GetOrAddWaypoints();
        for (int i = 0; i < Waypoints.Count; i++) waypoints.Points[i] = new Position(512 + i * 400, 512 + i * 1777 % 3000);
    }

    static void OnDestroy(World world) => world.RemoveWaypoints();

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

[EcsSystem]
public ref struct SteeringSystem                                          // heavy per-entity math: on all cores
{
    // Row by row: the waypoint is read by index (a gather), which a vector loop does not take, and the generator would
    // say so (EECS055); Off keeps the method scalar without the message
    [ForEach(Parallel = true, SimdMode = SimdMode.Off), With<Npc>, Without<Dead>, Without<Frozen>]
    static void Steer(in Position p, in Goal goal, ref Velocity v, in Waypoints waypoints)
    {
        var target = waypoints.Points[goal.Waypoint];
        float dx = target.X - p.X, dy = target.Y - p.Y, length = MathF.Sqrt(dx * dx + dy * dy) + 1;
        v = new Velocity(dx / length * 2, dy / length * 2);
    }
}

[EcsSystem]
public ref struct MovementSystem
{
    // Written for one row, run as a vector loop: the generator writes a Vector<float> loop from the body (two floats
    // in, two out). Require makes losing it a compile error (EECS056), so the hottest loop of the tick stays vectorized
    [ForEach(SimdMode = SimdMode.Require), Without<Dead>]
    static void Move(ref Position p, in Velocity v) => p = new Position(p.X + v.X, p.Y + v.Y);
}

[UpdateInterval(10, Phase = 5)]                                               // every 10th update, five updates after
[EcsSystem]
public ref struct ManaSystem                                              // RegenerationSystem: their work spreads;
{                                                                             // data no earlier system touches: it runs
                                                                              // beside PlanningSystem

    [ForEach]
    static void Regenerate(ref Mana mana) => mana.Value = Math.Min(mana.Max, mana.Value + 10);
}

[EcsSystem]
public ref struct ZoneSystem                                              // a new zone moves the row to its table
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
[EcsSystem]
public ref struct CombatSystem                                            // damage only: statuses are StatusSystem's
{
    [ForEach, Without<Dead>]                                                  // a frozen unit's Attack is disabled
    static void Fight(ref Attack attack, Mut<Health> hp, ref Reduce<Strikes> strikes)
    {
        if (--attack.Cooldown > 0) return;
        attack.Cooldown = attack.Interval;
        // The fight is abstracted: a unit pays for the hits it trades from its own Health. A hit is a change of Health,
        // so only the units hit this tick are stamped.
        hp.Write().Value -= attack.Damage;
        strikes.Value.Count++;                                                // a count in a register, not a singleton
    }                                                                         // written per row
}

[EcsSystem]
public ref struct StatusSystem
{
    [ForEach, Without<Dead>]                                                  // the units that attacked this tick
    static void Afflict(Entity unit, in Attack attack, in EntityRandom random)
    {
        if (attack.Cooldown != attack.Interval) return;                      // CombatSystem just reset it
        float roll = random.Float(unit.Id, Draws.Status);                    // one draw: a fire or a frost, never both
        if (roll < 1 / 50f) unit.GetOrAddBurning() = new Burning(10);         // applied at once; a new fire restarts it
        else if (roll < 1 / 50f + 1 / 70f)
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

[EcsSystem]
public ref struct TallySystem                                             // registered after StatusSystem
{
    static void OnUpdate(World world)
    {
        ref var battle = ref world.Battle;
        battle.Hits += world.Strikes.Count;                                   // CombatSystem's result: one add
        battle.Afflicted += world.Count<Health>(BattleServer.Afflicted);      // a count, not a loop: Count reads
    }                                                                         // presence bits, 64 rows per load
}

[EcsSystem]
public ref struct DeathSystem
{
    [ForEach, Without<Dead>]                                                  // a plain scan: most healths change often
    static void Die(Entity unit, in Health hp, Emit<Kill> kills)
    {
        if (hp.Value > 0) return;
        unit.SetDead();                                                       // whoever cares queries Dead
        kills.Add(new Kill(unit.Id));                                         // for the kill feed: KillFeedSystem sends it
    }
}
// #endregion combat

// #region respawn
[EcsSystem]
public ref struct RespawnSystem                                           // DeathSystem does not know it exists
{
    [ForEach, With<Player>, With<Dead>]
    static void Respawn(Entity player, ref Health hp, ref DynamicBuffer<Loot> bag, in DynamicBuffer<Friend> friends,
        in EntityRandom random, Scratch<List<Position>> spots, World world)
    {
        foreach (var shot in world.Children(player)) shot.Destroy();          // recall its projectiles
        hp.Value = hp.Max;
        bag.Clear();                                                          // a death empties the bag
        var near = spots.Value ??= new List<Position>();                      // a list per thread, not one per row
        near.Clear();                                                         // what the last row left in it
        foreach (var link in friends)                                         // next to a living friend, one at random
            if (world.TryGetEntity(link.Target, out var friend) && !friend.IsDead)
                near.Add(friend.Position);
        var at = near.Count == 0 ? new Position(2048, 2048) : near[random.Range(player.Id, Draws.Respawn, 0, near.Count)];

        player.Set(at, new Mana(0, 100), new Kills(0));                       // several components, one call; a death
                                                                              // ends the kill streak
        player.Remove<Dead, Burning, Frozen>();                               // back without statuses: one transition
        player.EnableAttack();                                                // false when it was not frozen
        player.EnableVelocity();
    }

    // [Consume]: each NPC is destroyed after the method, which retires it (Spawn stays, with the Retired tag)
    [ForEach, Consume, With<Npc>, With<Dead>]
    static void Despawn(Entity npc, ref Spawn spawn, in Rules rules, in EntityRandom random, World world)
    {
        // Rolled per NPC and tick: the same hunter, spot and item on every run (EntityRandom is a pure function)
        spawn.At = BattleServer.Spot(random.Bits(npc.Id, Draws.Spawn));       // where the next one appears: anywhere,
        Entity hunter = default;                                              // or by the hunter
        foreach (var item in world.Query<Position>(BattleServer.Zones[random.Range(npc.Id, Draws.Hunter, 0, 4)]))
        {                                                                     // a player of some zone: one table
            hunter = item.Entity;
            spawn.At = item.Position;
            break;
        }

        if (hunter.IsAlive)
        {
            ref var bag = ref hunter.WriteLoot();                             // the hunter gets the loot
            if (bag.Count == rules.BagSize) bag.RemoveAt(0);                  // a full bag drops its oldest item
            bag.Add(new Loot(random.Range(npc.Id, Draws.Loot, 0, 16)));
            hunter.SetKills(hunter.Kills.Count + 1);                          // the leaderboard follows
        }
    }

    [ForEach, With<Retired>]                                                  // what retired NPCs left behind
    static void Revive(Entity old, in Spawn spawn, in EntityRandom random, World world)
    {
        BattleServer.SpawnNpc(world, spawn.At, random.Bits(old.Id, Draws.Kind));
        old.RemoveSpawn();                                                    // its last cleanup component: freed
    }
}
// #endregion respawn

// #region projectiles
[EcsSystem]
public ref struct ProjectileSystem
{
    [ForEach, With<Player>, Without<Dead>]                                    // a frozen player's Velocity is disabled
    static void Shoot(Entity player, in Position p, in Velocity v, ref Mana mana, ref Battle battle, in Rules rules,
        in EntityRandom random, World world)
    {
        if (rules.Ceasefire || mana.Value < rules.ShotCost || !random.Chance(player.Id, Draws.Shot, 1 / 20f)) return;
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
[EcsSystem]
public ref struct RegenerationSystem
{
    [ForEach, With<Player>, Without<Dead>]
    static void Regenerate(Mut<Health> hp, ReadOnlyMaybe<Burning> burning)    // burning or not: one method
    {
        var (value, max) = hp.Value;                                          // a read: nothing stamped
        if (value < max) hp.Write().Value = Math.Min(max, value + (burning.HasValue ? 5 : 20));
    }
}
// #endregion projectiles

// #region replication
[EcsSystem, UpdateInGroup<Replication>]
public ref struct ReplicationSystem                                       // only what changed since the last send
{
    // The totals are written row by row here, not reduced: a send visits the rows written since the last one, a few
    // scattered rows per run, where a Reduce<T>'s partial result per run of rows costs more than the add it saves
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

    [ForEach, Changed<DynamicBuffer<Loot>>]                                   // bags changed by a ref write
    static void SendBag(in DynamicBuffer<Loot> bag, ref Battle battle) => battle.Replicated += 1 + bag.Count;
}

[EcsSystem, UpdateInGroup<Replication>]
public ref struct KillFeedSystem                                          // the kills since the last send
{
    static void OnUpdate(World world)
    {
        ref var kills = ref world.GetOrAddSingleton<Emitted<Kill>>().Items;  // DeathSystem's records, in row order
        ref var battle = ref world.Battle;
        battle.Deaths += kills.Count;
        battle.Replicated += kills.Count;
        kills.Clear();                                                        // sent once
    }
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
// The census's totals: reductions into singletons of their own, which the census reads after its passes. Int sums of
// int fields: a lane per row, so the passes are vector loops (4M players of 500 health still fit)
public partial struct Vitality : ISingletonComponent { [Sum] public int Health; }
public partial struct ManaPool : ISingletonComponent { [Sum] public int Mana; }

/// <summary>
/// The weakest living player: two fields that combine together (the lower health keeps its id), so the reduction is
/// written by hand. Combine keeps the left one on a tie: the earlier row, as on one thread.
/// </summary>
public record struct Weakest(EntityId Player, int Health) : ISingletonComponent, IReduction<Weakest>
{
    public static Weakest Identity => new(EntityId.Null, int.MaxValue);
    public static Weakest Combine(Weakest left, Weakest right) => right.Health < left.Health ? right : left;
}

/// <summary>
/// [ForEach] methods outside systems: the generator writes the job and an entry point taking the world and the
/// caller's values (<c>Reports.Scatter(world, round)</c>), for passes the host runs between ticks. A reduction lands in the
/// world it is called with.
/// </summary>
public static class Reports
{
    [ForEach(Parallel = true), With<Player>, Without<Dead>]                   // a vector loop per chunk of rows
    static void SumHealth(in Health hp, ref Reduce<Vitality> total) => total.Value.Health += hp.Value;

    [ForEach(Parallel = true)]
    static void SumMana(in Mana mana, ref Reduce<ManaPool> total) => total.Value.Mana += mana.Value;

    [ForEach(Parallel = true), With<Player>, Without<Dead>]
    static void FindWeakest(Entity player, in Health hp, ref Reduce<Weakest> weakest)
    {
        if (hp.Value < weakest.Value.Health) weakest.Value = new Weakest(player.Id, hp.Value);
    }

    [ForEach(Parallel = true)]                                                // chunks on all cores: no shared state
    static void HealFully(ref Health hp) => hp.Value = hp.Max;

    // NPCs start the round anywhere: drawn for the round the caller names (EntityRandom's static form takes the seed
    // and the tick), per entity, the same on any thread
    [ForEach(Parallel = true), With<Npc>]
    static void Scatter(Entity npc, ref Position p, ulong round) =>
        p = BattleServer.Spot(EntityRandom.Bits(BattleServer.Seed, npc.Id, round, Draws.Scatter));
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
    public const ulong Seed = 1;

    private static readonly QueryFilter Players = QueryFilter.Create().With<Player>();     // build filters once
    private static readonly QueryFilter AlivePlayers = Players.Without<Dead>();
    private static readonly QueryFilter Alive = QueryFilter.Create().Without<Dead>();
    internal static readonly QueryFilter Afflicted = QueryFilter.Create().WithAny<Burning, Frozen>();
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
        .SetHealth(120, 120).SetReplica(120, false, 0).SetAttack(6, 8, 0).SetGoal(0).SetSpawn(default).SetNpc();
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
        World.SetRules(ShotCost, BagSize, false);
        World.SetEntityRandom(Seed);                                          // one seed: one battle, on every run
        World.GetOrAddBattle();                                               // the totals start at zero

        World.AddSystem<PlanningSystem>();                                    // OnCreate adds the Waypoints
        World.AddSystem<SteeringSystem>();
        World.AddSystem<MovementSystem>();
        World.AddSystem<ManaSystem>();
        World.AddSystem<ZoneSystem>();
        World.AddSystem<CombatSystem>();
        World.AddSystem<StatusSystem>();
        World.AddSystem<TallySystem>();
        World.AddSystem<DeathSystem>();
        World.AddSystem<RespawnSystem>();
        World.AddSystem<ProjectileSystem>();
        World.AddSystem<RegenerationSystem>();
        World.AddSystem<ReplicationSystem>();
        World.AddSystem<KillFeedSystem>();
        _battle = World.SingletonRef<Battle>();

        var random = World.EntityRandom;
        var players = new Entity[units / 3];
        World.Instantiate(Hero, players);                                     // in bulk: one table growth
        for (int i = 0; i < players.Length; i++)
        {
            ulong r = random.Bits(players[i].Id, Draws.Setup);                // 64 bits: a field from each slice
            players[i].Position = Spot(r);
            players[i].Velocity = new Velocity((int)(r >> 24 & 0xFF) % 5 - 2, (int)(r >> 32 & 0xFF) % 5 - 2);
            players[i].Attack.Cooldown = (int)(r >> 40 & 0xFF) % 6;
            players[i].WriteLoot().EnsureCapacity(BagSize);                   // room once: a full bag never grows
            players[i].LinkFriend(players[(i + 1) % players.Length]); // each player has two friends
            players[i].LinkFriend(players[(i + 7) % players.Length]);
        }

        var npcs = new Entity[units - players.Length];
        World.Instantiate(Grunt, npcs.AsSpan(0, npcs.Length / 2));              // copies of a recipe, in bulk
        World.Instantiate(Brute, npcs.AsSpan(npcs.Length / 2));
        for (int i = 0; i < npcs.Length; i++)
        {
            ulong r = random.Bits(npcs[i].Id, Draws.Setup);
            npcs[i].Position = Spot(r);
            npcs[i].Attack.Cooldown = (int)(r >> 40 & 0xFF) % 10;
        }

        World.Reserve(Grunt, npcs.Length / 4);                         // respawn waves never grow the tables
        World.Reserve(Brute, npcs.Length / 4);
    }

    /// <summary>A spot on the 4096 × 4096 map from random bits: 12 bits per coordinate.</summary>
    public static Position Spot(ulong bits) => new(bits & 4095, bits >> 12 & 4095);

    /// <summary>An NPC at <paramref name="at"/>, a grunt or a brute: <paramref name="roll"/> picks it and its cooldown.</summary>
    public static void SpawnNpc(World world, Position at, ulong roll)
    {
        var npc = world.Instantiate((roll & 1) == 0 ? Grunt : Brute, at);     // a copy with its position: one row copy
        npc.Attack.Cooldown = (int)(roll >> 8 & 0xFF) % 10;                   // then only what differs is written
    }
    // #endregion setup

    // #region tick
    public ref Battle Battle => ref _battle.Value;

    /// <summary>A system that threw: it was switched off and the battle went on (its Error is the failure, as data).</summary>
    public SystemHandle? Failure
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
        World.Update();                                                       // EntityRandom draws new numbers:
        if (++Battle.Tick % 3 == 0) World.Update<Replication>();              // a group's update draws the same ones
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
        Reports.SumHealth(World);                                             // on all cores, each into its singleton
        Reports.SumMana(World);
        Reports.FindWeakest(World);
        World.TryGetEntity(World.Weakest.Player, out var weakest);            // none when every player is dead
        return new Census(World.Count<Player>(), World.Count<Player>(AlivePlayers), World.Vitality.Health,
            World.ManaPool.Mana, weakest, World.Count<Attack>(Alive));        // a frozen unit's Attack is not counted
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
        Reports.Scatter(World, (ulong)Battle.Tick);
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
    public SystemHandle? Slowest()
    {
        SystemHandle? slowest = null;
        foreach (var system in World.Systems)
            if (slowest is not { } other || system.Stats.AverageTime > other.Stats.AverageTime) slowest = system;
        return slowest;
    }

    public void Dispose() => World.Dispose();                                 // OnDestroy of every system
    // #endregion diagnostics
}
