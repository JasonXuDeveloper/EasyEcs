# Example: a battle server

[Docs index](README.md) · Related: [Getting started](getting-started.md) · [Features](features.md) ·
[ECS principles](ecs-principles.md)

A battle server using every feature: plain data, one job per system, systems that never know about each other, and a
host that ticks the world and answers questions between ticks. It is the
[benchmark world](https://github.com/JasonXuDeveloper/EasyEcs/blob/master/EasyEcs.Benchmark/BattleServer.cs) measured
below and the world of the
[scenario tests](https://github.com/JasonXuDeveloper/EasyEcs/blob/master/EasyEcs.UnitTest/BattleServerTests.cs). Coming
from another ECS? [Their features in EasyEcs](comparison.md#their-features-in-easyecs) maps each one to its place here.

| Feature | Where |
|---|---|
| Components, tags, singletons | `Position`, `Health`, `Player`, `Dead`; `Battle` (counters), `Rules` (the battle's rules), `Waypoints` |
| Generated members | `players[i].Position = …`, `SetZone`, `SetFrozen`, `RemoveBurning`, `SetDead`, `IsDead`, `TryGetMana`, `WriteHealth()` (a tracked write), `World.Battle`, `World.Rules`, `SetRules`, `KillsIndex`; buffers `player.Loot`, `HasLoot`, `WriteLoot()`, `player.Friend`; `DisableAttack` / `EnableAttack`, `IsAttackDisabled`; on prefabs `SetHealth`, `SetZone`, `SetSpawn`, `SetNpc` |
| `[ForEach]` systems, filters (`With`, `Without`, `WithAny`) | every system; `AfflictionSystem` counts burning **or** frozen units with `World.Count` (no loop) |
| `[ForEach(Parallel = true)]` | `SteeringSystem`: NPC steering on every core; `Reports.HealFully` and `Reports.Scatter` in `NewRound` |
| Parallel systems (`WorldOptions.ParallelSystems`) | `MovementSystem` and `ManaSystem`: disjoint data, run at once |
| Batch methods (`Span<T>`), SIMD | `MovementSystem`: whole runs of rows as `Vector<float>`; `Reports.SumHealth` sums health as `Vector<int>` lanes |
| `[ForEach]` runners (outside systems) | `Reports`: the generator writes an entry point per method (`Reports.SumMana(World, ref mana)`), read-only ones declare no writes |
| `Maybe<T>` | `RegenerationSystem`: one method heals burning players less |
| One system per job | `CombatSystem` deals damage, `StatusSystem` applies statuses to the units that just attacked, burns and thaws |
| Disabling components (`Disable` / `Enable`, `DisableAll` / `EnableAll`) | a frozen unit's `Attack` and `Velocity` are disabled: no system sees them, their values come back on `Thaw`; `NewRound` enables every one in bulk |
| Cleanup components (`ICleanupComponent`, `Destroyed`) | `Spawn`: a destroyed NPC leaves it behind, `RespawnSystem.Revive` spawns the next one and removes it, which frees the old entity |
| Hierarchy (`ChildOf`, `Children`, cascade) | projectiles are children of their player: recalled on respawn, destroyed with a player who `Leave`s, moved with one who `Transfer`s |
| Immediate structural changes | statuses added and removed mid-pass, deaths (`SetDead`), respawns (`Set` of three components, `Remove` of three), projectiles spawned and destroyed |
| Prefabs (`Prefab`, `Instantiate`, `EnsureCapacity`) | `Hero` for players (with their buffers), `Grunt` and `Brute` (a variant: `new Prefab(Grunt)`) for NPCs, instantiated in bulk and one by one by `SpawnNpc`; `EnsureCapacity` sizes the respawn waves |
| Cloning (`Clone`) | `Mirror` copies a living unit as it is now (health, statuses, bag, zone); indexes, groups and the next send learn about the copy |
| Change tracking (`ITrackChanges`, `[Changed<T>]`, `Since`) | `ReplicationSystem` sends only the healths written since its last run (`Send`) and fires lit or burning (`SendFire`); `WrittenSinceCheckpoint` counts players written since the checkpoint |
| Removal detection (`[Removed<T>]`) | `ReplicationSystem.SendFireOut` tells clients that a fire went out |
| Lists per entity (`DynamicBuffer<T>`) | every player's bag of `Loot`: filled in `Despawn`, emptied in `Respawn`, sent by `SendBag` only when it changed; `LootValue` reads it, `Give` moves an item between two bags |
| Groups (`IGroup`, `GroupBy`, `InGroup`) | `Zone`: a table per zone; `PlayersIn` counts one table, `Despawn` hunts a player in one |
| Ordered index (`IIndexed<int>`, `Descending`) | `Kills`: the kill streak of a player, `TopKillers` walks the leaderboard best first |
| Relations (`IRelation`, `AddRelation`, `Relations<T>().Count`) | `Friend`: every player has two; a respawning player comes back next to a living friend; `FriendsOf` asks the index |
| Target policies (`WhenTargetDestroyed`) | links to a player who leaves are removed from friend lists |
| System groups, `UpdateInterval`, `UpdatePhase` | `Replication` runs every third tick; `RegenerationSystem` and `ManaSystem` every tenth update, five updates apart |
| Ordering (`[UpdateAfter<T>]`) | `ClockSystem` is registered first and runs last |
| System lifecycle (`OnCreate`, `OnDestroy`) | `SteeringSystem` sets up and removes its `Waypoints`; `ClockSystem` adds `Battle` |
| Ad-hoc queries (`Query`, `Count`, `Any`, `TryGet`) | `NearestPlayer`, `Census`, `AnyoneBurning`, `AnyoneAfflicted`, `ShotsLeft` with `static readonly` filters |
| Entity handles (`TryGetEntity`, `IsAlive`, `TryGetRef`) | `Hit` turns a client's id back into the living unit and takes its `Health` by `ref` only if it has one; `ShotsLeft` of a stale handle is 0 |
| Bulk operations | `Instantiate(prefab, span)`, `DestroyEntities`, `RemoveAll`, `EnableAll` |
| Snapshots | `Checkpoint` / `Rewind` (`Capture` / `Restore`) |
| Saves and migration (`Save`, `Load`, `MoveEntities`) | `Save` / `Load` write and read the battle as bytes; `Transfer` moves a player and its projectiles to another battle |
| Introspection (`Describe`) | `Tables` lists every table with its rows and bytes |
| Diagnostics | `MeasureSystems` and `Slowest`; `SystemErrors` switches a failing system off |
| Handles that stay valid | `SingletonRef<Battle>` in the host |
| One world per thread | the end of the usage below |

**Data** is plain structs:

```csharp
using System;
using System.Buffers;
using System.Collections.Generic;
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
public record struct Frozen(int Ticks) : IComponent;                          // while it lasts, Attack and Velocity
                                                                              // are disabled: no query sees them
public record struct Lifetime(int Ticks) : IComponent;
public record struct Zone(int Id) : IComponent, IGroup { public int Group => Id; }  // a table per zone
public record struct Kills(int Count) : IComponent, IIndexed<int> { public int Key => Count; } // ordered: a leaderboard
public record struct Spawn(Position At, int Seed) : ICleanupComponent;        // outlives its NPC: where the next appears
public record struct Loot(int Item);                                          // players carry a DynamicBuffer<Loot>
public record struct Friend(EntityId Target) : IRelation;                     // and a DynamicBuffer<Friend> of links
public struct Player : IComponent;                                            // tags: empty components
public struct Npc : IComponent;
public struct Dead : IComponent;

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
```

**Systems** do one job each, in registration order (`[UpdateAfter<T>]` moves one):

```csharp
public class SteeringSystem : EcsSystem                                       // heavy per-entity math: on all cores
{
    protected override void OnCreate()                                        // the system sets up the data it steers by
    {
        ref var waypoints = ref World.GetOrAddWaypoints();
        for (int i = 0; i < Waypoints.Count; i++) waypoints.Points[i] = new Position(512 + i * 400, 512 + i * 1777 % 3000);
    }

    protected override void OnDestroy() => World.RemoveWaypoints();

    [ForEach(Parallel = true), With<Npc>, Without<Dead>, Without<Frozen>]
    static void Steer(in Position p, ref Velocity v, in Waypoints waypoints)
    {
        var target = waypoints.Points[(int)(p.X + p.Y) & (Waypoints.Count - 1)];
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

public class ManaSystem : EcsSystem                                           // other data than MovementSystem: both
{                                                                             // run at once
    public override int UpdateInterval => 10;                                 // every 10th update, five updates after
    public override int UpdatePhase => 5;                                     // RegenerationSystem: their work spreads

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

public class CombatSystem : EcsSystem                                         // damage only: statuses are StatusSystem's
{
    [ForEach, Without<Dead>]                                                  // a frozen unit's Attack is disabled
    static void Fight(Entity unit, ref Attack attack, ref Battle battle)
    {
        if (--attack.Cooldown > 0) return;
        attack.Cooldown = attack.Interval;
        unit.WriteHealth().Value -= attack.Damage;                            // a hit is a change of Health
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

public class RespawnSystem : EcsSystem                                        // DeathSystem does not know it exists
{
    [ForEach, With<Player>, With<Dead>]
    static void Respawn(Entity player, ref Health hp, ref DynamicBuffer<Loot> bag, World world)
    {
        foreach (var shot in world.Children(player)) shot.Destroy();          // recall its projectiles
        hp.Value = hp.Max;
        bag.Clear();                                                          // a death empties the bag
        var at = new Position(2048, 2048);
        foreach (var link in player.Friend)                                   // next to a living friend, if any
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

    [ForEach, With<Npc>, With<Dead>]
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

        npc.Destroy();                                                        // Spawn stays, with the Destroyed tag
    }

    [ForEach, With<Destroyed>]                                                // what destroyed NPCs left behind
    static void Revive(Entity old, in Spawn spawn, World world)
    {
        BattleServer.SpawnNpc(world, spawn.At, spawn.Seed);
        old.RemoveSpawn();                                                    // its last cleanup component: freed
    }
}

public class ProjectileSystem : EcsSystem
{
    [ForEach, With<Player>, Without<Dead>]                                    // a frozen player's Velocity is disabled
    static void Shoot(Entity player, in Position p, in Velocity v, ref Mana mana, ref Battle battle, in Rules rules, World world)
    {
        if (rules.Ceasefire || mana.Value < rules.ShotCost || Dice.Roll(battle.Tick, player.Id.Index) % 20 != 0) return;
        mana.Value -= rules.ShotCost;
        battle.Shots++;
        world.CreateEntity(p, new Velocity(v.X * 4, v.Y * 4), new Lifetime(20), new ChildOf(player));  // a child: gone
    }                                                                         // with its player

    [ForEach]
    static void Expire(Entity shot, ref Lifetime life)
    {
        if (--life.Ticks == 0) shot.Destroy();
    }
}

public class RegenerationSystem : EcsSystem
{
    public override int UpdateInterval => 10;                                 // every 10th update

    [ForEach, With<Player>, Without<Dead>]
    static void Regenerate(Entity player, in Health hp, Maybe<Burning> burning)  // burning or not: one method
    {
        if (hp.Value < hp.Max) player.WriteHealth().Value = Math.Min(hp.Max, hp.Value + (burning.HasValue ? 5 : 20));
    }
}

[UpdateAfter<RegenerationSystem>]                                             // registered first, runs last
public sealed class ClockSystem : EcsSystem
{
    protected override void OnCreate() => World.GetOrAddBattle();
    protected override void OnUpdate() => World.Battle.Tick++;
}

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

    [ForEach, Changed<DynamicBuffer<Loot>>]                                          // bags changed by a ref write
    static void SendBag(in DynamicBuffer<Loot> bag, ref Battle battle) => battle.Replicated += 1 + bag.Count;
}
```

**The host** owns the world, ticks it, and answers questions between ticks:

```csharp
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

/// <summary>
/// The host of one battle: owns its world on one thread, ticks it, and answers questions about it between ticks.
/// </summary>
public sealed class BattleServer : IDisposable
{
    public const int ShotCost = 30;
    public const int BagSize = 8;

    private static readonly QueryFilter Players = QueryFilter.Create().With<Player>();     // build filters once
    private static readonly QueryFilter AlivePlayers = Players.Without<Dead>();
    private static readonly QueryFilter Alive = QueryFilter.Create().Without<Dead>();
    internal static readonly QueryFilter Afflicted = QueryFilter.Create().WithAny<Burning>().WithAny<Frozen>();
    private static readonly QueryFilter WrittenPlayers = Players.Changed<Health>(0);    // the tick is set per call
    internal static readonly QueryFilter[] Zones =                                        // one table each
        [Players.InZone(0), Players.InZone(1), Players.InZone(2), Players.InZone(3)];

    // Unit kinds: recipes that belong to no world (no query sees them), copied by Instantiate
    private static readonly Prefab Hero = new Prefab().Set(new Position(), new Velocity(1, 0))   // several at once
        .SetHealth(500, 500).SetReplica(500, false).SetMana(100, 100).SetAttack(8, 6, 0)        // or one by name
        .SetZone(0).SetKills(0).SetPlayer().Set(new DynamicBuffer<Friend>())
        .SetLoot(new Loot(0));                                    // a starter item: each hero gets a bag of its own
    private static readonly Prefab Grunt = new Prefab().Set(new Position(), new Velocity())
        .SetHealth(120, 120).SetReplica(120, false).SetAttack(6, 8, 0).SetSpawn(default, 0).SetNpc();
    private static readonly Prefab Brute = new Prefab(Grunt)                                    // a variant: a copy
        .SetHealth(200, 200).SetReplica(200, false).SetAttack(9, 12, 0);

    public readonly World World;
    private readonly SingletonRef<Battle> _battle;                            // resolved once, no lookup per tick
    private readonly List<EntityId> _group = [];                              // reused by Transfer
    private WorldSnapshot _checkpoint;
    private ulong _checkpointAt;

    /// <summary>A battle of <paramref name="units"/> units, a third of them players.</summary>
    public BattleServer(int units)
    {
        World = new World(new WorldOptions
        {
            InitialEntityCapacity = units * 2,
            ParallelSystems = true,                                           // unordered systems with disjoint data side by side
            MeasureSystems = true,                                            // per-system timings in system.Stats
            SystemErrors = SystemErrorPolicy.Disable,                         // a failing system goes off, the battle goes on
        });
        World.GroupBy<Zone>();                                                // a zone's players contiguous, in its table
        World.FriendIndex.WhenTargetDestroyed(TargetPolicy.Remove);   // a player who leaves leaves friend lists
        World.SetRules(ShotCost, BagSize, false);

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

        var players = new Entity[units / 3];
        World.Instantiate(Hero, players);                                     // in bulk: one table growth
        for (int i = 0; i < players.Length; i++)
        {
            uint r = Dice.Roll(0, i);
            players[i].Position = new Position(r % 4096, (r >> 12) % 4096);
            players[i].Velocity = new Velocity((int)(r % 5) - 2, (int)((r >> 8) % 5) - 2);
            players[i].Attack.Cooldown = (int)(r % 6);
            players[i].WriteLoot().EnsureCapacity(BagSize);                   // room once: a full bag never grows
            players[i].AddFriend(players[(i + 1) % players.Length]);  // each player has two friends
            players[i].AddFriend(players[(i + 7) % players.Length]);
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

        World.EnsureCapacity(Grunt, npcs.Length / 4);                         // respawn waves never grow the tables
        World.EnsureCapacity(Brute, npcs.Length / 4);
    }

    public static void SpawnNpc(World world, Position at, int seed)
    {
        uint r = Dice.Roll(seed, seed);
        var npc = world.Instantiate((r & 1) == 0 ? Grunt : Brute);            // then only what differs is written
        npc.Position = at;
        npc.Attack.Cooldown = (int)(r % 10);
    }

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

    public int PlayersIn(int zone) => World.Count<Player>(Zones[zone]);    // one table: its rows, not a scan

    public bool AnyoneBurning => World.Any<Burning>();

    public bool AnyoneAfflicted => World.Any<Health>(Afflicted);              // burning or frozen

    public int ShotsLeft(Entity player) => player.IsAlive && player.TryGetMana(out var mana) ? mana.Value / ShotCost : 0;

    /// <summary>A client's hit on the unit it names by id: the id turns back into a handle if that unit still lives.</summary>
    public bool Hit(EntityId target, int damage)
    {
        if (!World.TryGetEntity(target, out var unit) || unit.IsDead) return false;
        ref var hp = ref unit.TryGetRef<Health>(out bool found);             // optional access by ref
        if (!found) return false;
        hp.Value -= damage;                                                   // a write: replicated with the next send
        return true;
    }

    /// <summary>
    /// A mirror image of a living unit as it is now (health, statuses, bag, zone): a copy of the entity, which the
    /// indexes, groups and the next send learn about.
    /// </summary>
    public Entity Mirror(Entity unit) => unit.IsAlive ? unit.Clone() : default;

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
        _group.Add(player);
        foreach (var shot in World.Children(player)) _group.Add(shot);        // a hierarchy moves together
        Entity arrived = default;
        World.MoveEntities(to.World, CollectionsMarshal.AsSpan(_group), new Span<Entity>(ref arrived));
        return arrived;
    }

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

    /// <summary>A new round: projectiles gone, statuses lifted, everyone healed, the NPCs scattered.</summary>
    public int NewRound()
    {
        int cleared = World.DestroyEntities<Lifetime>();                      // one pass, however many
        World.RemoveAll<Burning>();                                           // in bulk: table by table, not per entity
        World.RemoveAll<Frozen>();
        World.EnableAll<Attack>();
        World.EnableAll<Velocity>();
        Reports.HealFully(World);                                             // on all cores
        Reports.Scatter(World, Battle.Tick);
        return cleared;
    }

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

    /// <summary>The world's tables: their components, rows and bytes, for a dashboard.</summary>
    public TableInfo[] Tables() => World.Describe();

    /// <summary>The system that took the most time per run so far (<see cref="WorldOptions.MeasureSystems"/>).</summary>
    public EcsSystem Slowest()
    {
        EcsSystem slowest = null;
        foreach (var system in World.Systems)
            if (slowest == null || system.Stats.Average > slowest.Stats.Average) slowest = system;
        return slowest;
    }

    public void Dispose() => World.Dispose();                                 // OnDestroy of every system
}
```

**Running it:**

```csharp
using var server = new BattleServer(100_000);
server.Checkpoint();                                      // e.g. before a risky admin command

while (running)
{
    server.Tick();                                        // 0 B per tick once warmed up
    if (server.WrittenSinceCheckpoint > 1_000) server.Checkpoint();
}

var census = server.Census();                             // between ticks: runners, Count, groups
Console.WriteLine($"{census.Alive}/{census.Players} alive, {server.PlayersIn(0)} in zone 0, slowest {server.Slowest()}");
server.Ceasefire = true;                                  // a rule as data: players hold fire, shots in flight expire
server.NewRound();
server.Rewind();                                          // roll the battle back in memory

var save = new ArrayBufferWriter<byte>();
server.Save(save);                                        // to disk, or to another process: Load(save.WrittenSpan)

using var other = new BattleServer(10_000);
server.Transfer(server.NearestPlayer(default), other);    // a player and its projectiles change battles

// One world per thread scales across cores: worlds share nothing.
var threads = new Thread[4];
for (int i = 0; i < threads.Length; i++)
    (threads[i] = new Thread(() => { using var battle = new BattleServer(25_000); while (running) battle.Tick(); })).Start();
```

Every system can be removed, reordered or tested alone: they share components and singletons, nothing else. The
step-by-step tutorial is [Getting started](getting-started.md).
