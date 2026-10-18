using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;

namespace EasyEcs.UnitTest.Showcase;

using EasyEcs.Benchmark.Showcase;   // inside the namespace: wins over the shared test fixtures

/// <summary>
/// The battle server example (doc/example.md) and BattleServerBenchmark, run as a whole: every feature it uses must keep its
/// promise while the others churn the world (changes replicated, indexes and groups in sync, links cleaned up,
/// runners and queries agreeing with scans, snapshots replaying, saves loading, failures isolated, nothing allocated).
/// </summary>
public class BattleServerTests
{
    private const int Units = 3_000;

    [Test]
    public void TheBattleIsBusy()
    {
        using var server = Run(300);
        var battle = server.Battle;
        Assert.That(new[] { battle.Hits, battle.Deaths, battle.Shots, battle.Replicated, battle.Afflicted }, Is.All.Positive);
        Assert.That(server.Failure, Is.Null);
    }

    [Test]
    public void PlayersCollectLootInBoundedBags()
    {
        using var server = Run(300);
        int looted = 0;
        foreach (var item in server.World.Query<DynamicBuffer<Loot>>())
        {
            Assert.That(item.Component1.Count, Is.InRange(0, BattleServer.BagSize));
            foreach (var loot in item.Component1) Assert.That(loot.Item, Is.InRange(0, 15));
            looted += item.Component1.Count;
        }

        Assert.That(looted, Is.Positive);
        Assert.That(server.World.Count<DynamicBuffer<Loot>>(), Is.EqualTo(server.World.Count<Player>()));
    }

    [Test]
    public void LootChangesHandsAndReadsChangeNothing()
    {
        using var server = Run(300);
        Entity from = default, to = default;
        foreach (var item in server.World.Query<DynamicBuffer<Loot>>())
        {
            if (from == default && item.Component1.Count > 0) from = item.Entity;
            else if (to == default && item.Component1.Count < BattleServer.BagSize) to = item.Entity;
        }

        int value = server.LootValue(from) + server.LootValue(to), items = from.Loot.Count;
        var oldest = from.Loot[0];
        Assert.That(server.Give(from, to), Is.True);
        Assert.That(from.Loot.Count, Is.EqualTo(items - 1));
        var bag = to.Loot.AsReadOnlySpan();
        Assert.That(bag[^1], Is.EqualTo(oldest));
        Assert.That(server.LootValue(from) + server.LootValue(to), Is.EqualTo(value));
        Assert.That(server.Give(default, to), Is.False);                     // a stale handle has no bag
        Assert.That(server.AnyoneAfflicted, Is.EqualTo(server.World.Any<Burning>() || server.World.Any<Frozen>()));
    }

    [Test]
    public void ReplicationSendsEveryChangeAndOnlyChanges()
    {
        using var server = Run(30);
        for (int sends = 0; sends < 30;)
        {
            server.Tick();
            if (server.Battle.Tick % 3 != 0) continue;
            sends++;
            int sent = server.Battle.Replicated;
            server.World.Update<Replication>();                               // nothing changed since the send
            Assert.That(server.Battle.Replicated, Is.EqualTo(sent));
            foreach (var item in server.World.Query<Health, Replica>())      // every client copy is current
            {
                Assert.That(item.Component2.Health, Is.EqualTo(item.Component1.Value));
                Assert.That(item.Component2.Burning, Is.EqualTo(item.Entity.Has<Burning>()));  // fires lit and gone out
            }
        }
    }

    [Test]
    public void AMirrorIsACopyOfTheUnitAsItIsNow()
    {
        using var server = Run(30);
        var player = server.NearestPlayer(new Position(2048, 2048));
        int zone = player.Zone.Id, inZone = server.PlayersIn(zone);
        var mirror = server.Mirror(player);
        Assert.That(mirror, Is.Not.EqualTo(player));
        Assert.That(mirror.Read<Health>(), Is.EqualTo(player.Read<Health>()));
        Assert.That(server.LootValue(mirror), Is.EqualTo(server.LootValue(player)));
        Assert.That(server.PlayersIn(zone), Is.EqualTo(inZone + 1));         // the copy went to its zone's table
        mirror.WriteLoot().Add(new Loot(99));                                // a bag of its own
        Assert.That(server.LootValue(mirror), Is.EqualTo(server.LootValue(player) + 99));
        Assert.That(server.Mirror(default), Is.EqualTo(default(Entity)));
    }

    [Test]
    public void TheZoneGroupsMatchAScan()
    {
        using var server = Run(200);
        var counts = new int[4];
        foreach (var item in server.World.Query<Zone>()) counts[item.Component1.Id]++;
        for (int zone = 0; zone < counts.Length; zone++) Assert.That(server.PlayersIn(zone), Is.EqualTo(counts[zone]));
        Assert.That(counts, Has.Some.Not.EqualTo(counts[0]));                // players spread over the zones
    }

    [Test]
    public void ProjectilesAreChildrenOfLivingPlayers()
    {
        using var server = Run(200);
        var owned = new Dictionary<Entity, int>();
        foreach (var item in server.World.Query<ChildOf>())
        {
            Assert.That(server.World.TryGetEntity(item.Component1.Parent, out var owner), Is.True);
            Assert.That(owner.Has<Player>() && !owner.Has<Dead>(), Is.True);
            owned[owner] = owned.GetValueOrDefault(owner) + 1;
        }

        Assert.That(owned, Is.Not.Empty);
        foreach (var (owner, count) in owned)
        {
            int found = 0;
            foreach (var shot in server.World.Children(owner)) found++;
            Assert.That(found, Is.EqualTo(count));
        }
    }

    [Test]
    public void TheCensusMatchesAScan()
    {
        using var server = Run(200);
        var census = server.Census();
        int players = 0, alive = 0, ready = 0, lowest = int.MaxValue;
        long health = 0, mana = 0;
        foreach (var e in server.World.Entities)
        {
            if (e.TryGet<Mana>(out var m)) mana += m.Value;
            if (e.Has<Attack>() && !e.Has<Dead>() && !e.Has<Frozen>()) ready++;
            if (!e.Has<Player>()) continue;
            players++;
            if (e.Has<Dead>()) continue;
            alive++;
            e.TryGet<Health>(out var hp);
            health += hp.Value;
            lowest = Math.Min(lowest, hp.Value);
        }

        Assert.That(census with { Weakest = default }, Is.EqualTo(new Census(players, alive, health, mana, default, ready)));
        census.Weakest.TryGet<Health>(out var weakest);
        Assert.That(weakest.Value, Is.EqualTo(lowest));
    }

    [Test]
    public void TheCensusIsNotAChange()
    {
        using var server = Run(30);
        server.Checkpoint();
        server.Census();                                                      // runners that only read
        Assert.That(server.WrittenSinceCheckpoint, Is.Zero);
    }

    [Test]
    public void TheNearestPlayerIsTheClosestLivingOne()
    {
        using var server = Run(100);
        var at = new Position(1000, 3000);
        var nearest = server.NearestPlayer(at);
        float best = float.MaxValue;
        foreach (var e in server.World.Entities)
            if (e.Has<Player>() && !e.Has<Dead>() && e.TryGet<Position>(out var p))
                best = Math.Min(best, (p.X - at.X) * (p.X - at.X) + (p.Y - at.Y) * (p.Y - at.Y));

        var found = nearest.Get<Position>();
        Assert.That((found.X - at.X) * (found.X - at.X) + (found.Y - at.Y) * (found.Y - at.Y), Is.EqualTo(best));
    }

    [Test]
    public void ShootingCostsManaAndACeasefireStopsIt()
    {
        using var server = Run(100);
        var census = server.Census();
        Assert.That(census.Mana, Is.LessThan(100L * census.Players));        // shots were paid for
        server.Ceasefire = true;
        int shots = server.Battle.Shots;
        for (int i = 0; i < 50; i++) server.Tick();
        Assert.That(server.Battle.Shots, Is.EqualTo(shots));
        Assert.That(server.World.Count<Lifetime>(), Is.Zero);                // shots in flight expired meanwhile
        server.Ceasefire = false;
        for (int i = 0; i < 50; i++) server.Tick();
        Assert.That(server.Battle.Shots, Is.GreaterThan(shots));
    }

    [Test]
    public void ANewRoundClearsProjectilesAndHealsEveryone()
    {
        using var server = Run(100);
        int projectiles = server.World.Count<Lifetime>();
        Assert.That(projectiles, Is.Positive);
        Assert.That(server.NewRound(), Is.EqualTo(projectiles));
        Assert.That(server.World.Any<Lifetime>(), Is.False);
        foreach (var item in server.World.Query<Health>()) Assert.That(item.Component1.Value, Is.EqualTo(item.Component1.Max));
        Assert.That(server.World.Children(server.NearestPlayer(default)).GetEnumerator().MoveNext(), Is.False);
        Assert.That(server.AnyoneAfflicted, Is.False);                       // statuses lifted in bulk
        Assert.That(server.Census().ReadyToAttack, Is.EqualTo(server.World.Count<Attack>(QueryFilter.Create().Without<Dead>())));
        Assert.That(server.World.RowStats().HoleRatio, Is.LessThanOrEqualTo(0.1f));
    }

    [Test]
    public void ANewRoundScattersTheNpcsTheSameWayOnEveryThread()
    {
        using var one = Run(50);
        using var other = Run(50);
        var before = Positions(one);
        one.NewRound();
        other.NewRound();
        var after = Positions(one);
        Assert.That(after, Is.EqualTo(Positions(other)));                    // chunks on any threads: one result
        Assert.That(after, Is.Not.EqualTo(before));
    }

    [Test]
    public void AHitByIdReachesOnlyALivingUnit()
    {
        using var server = Run(30);
        var player = server.NearestPlayer(default);
        int hp = player.Get<Health>().Value;
        Assert.That(server.Hit(player.Id, 7), Is.True);
        Assert.That(player.Get<Health>().Value, Is.EqualTo(hp - 7));
        Assert.That(server.ShotsLeft(player), Is.EqualTo(player.Get<Mana>().Value / BattleServer.ShotCost));

        player.Destroy();
        Assert.That(server.ShotsLeft(player), Is.Zero);                       // a stale handle has no mana
        Assert.That(server.Hit(player.Id, 7), Is.False);                     // its id names no living entity now
        Assert.That(server.Hit(EntityId.Null, 7), Is.False);                // an empty reference
    }

    private static List<(int, float, float)> Positions(BattleServer server)
    {
        var positions = new List<(int, float, float)>();
        foreach (var item in server.World.Query<Position>(QueryFilter.Create().With<Npc>()))
            positions.Add((item.Entity.Id.Index, item.Component1.X, item.Component1.Y));
        positions.Sort();
        return positions;
    }

    [Test]
    public void PlayersWrittenSinceTheCheckpointAreCounted()
    {
        using var server = Run(30);
        server.Checkpoint();
        Assert.That(server.WrittenSinceCheckpoint, Is.Zero);
        for (int i = 0; i < 10; i++) server.Tick();
        int written = 0;
        foreach (var e in server.World.Entities)
            if (e.Has<Player>() && e.TryGet<Health>(out var hp) && e.TryGet<Replica>(out var replica) && hp.Value != replica.Health)
                written++;
        Assert.That(server.WrittenSinceCheckpoint, Is.Positive.And.AtLeast(written));  // hits since the last send at least
        Assert.That(server.WrittenSinceCheckpoint, Is.LessThanOrEqualTo(server.Census().Players));
    }

    [Test]
    public void RestoringASnapshotReplaysTheSameBattle()
    {
        using var server = Run(100);
        server.Checkpoint();
        for (int i = 0; i < 60; i++) server.Tick();
        var first = server.Battle;
        server.Rewind();
        for (int i = 0; i < 60; i++) server.Tick();
        var replay = server.Battle;
        // A restore counts every tracked row as written: the first send after it brings clients the whole state.
        Assert.That(replay with { Replicated = 0 }, Is.EqualTo(first with { Replicated = 0 }));
        Assert.That(replay.Replicated, Is.GreaterThan(first.Replicated));
    }

    [Test]
    public void AFailingSystemIsSwitchedOffAndTheBattleGoesOn()
    {
        using var server = Run(10);
        server.World.AddSystem<FaultySystem>();
        long tick = server.Battle.Tick;
        for (int i = 0; i < 10; i++) server.Tick();
        Assert.That(server.Failure, Is.InstanceOf<FaultySystem>());
        Assert.That(server.Failure.Enabled, Is.False);
        Assert.That(server.Battle.Tick, Is.EqualTo(tick + 10));
    }

    [Test]
    public void SystemsAreMeasuredAndOwnTheirSingletons()
    {
        using var server = Run(20);
        Assert.That(server.Slowest().Stats.Runs, Is.Positive);
        Assert.That(server.World.GetSystem<RegenerationSystem>().Stats.Runs, Is.EqualTo(2));   // every 10th update
        Assert.That(server.World.GetSystem<ManaSystem>().Stats.Runs, Is.EqualTo(2));           // the same, phase 5
        Assert.That(server.World.HasSingleton<Waypoints>(), Is.True);         // SteeringSystem.OnCreate
        server.World.RemoveSystem<SteeringSystem>();
        Assert.That(server.World.HasSingleton<Waypoints>(), Is.False);        // SteeringSystem.OnDestroy
    }

    [Test]
    public void BattlesOnSeparateThreadsMatchOneRunAlone()
    {
        Battle alone;
        using (var server = Run(100)) alone = server.Battle;

        var results = new Battle[4];
        var threads = new Thread[results.Length];
        for (int i = 0; i < threads.Length; i++)
        {
            int slot = i;
            threads[i] = new Thread(() => { using var server = Run(100); results[slot] = server.Battle; });  // a world per thread
            threads[i].Start();
        }

        foreach (var thread in threads) thread.Join();
        Assert.That(results, Is.All.EqualTo(alone));
    }

    [Test]
    public void ATickAllocatesNothing()
    {
        using var server = Run(1_200);                                        // tables grown to their peak populations
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) server.Tick();
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    [Test]
    public void FrozenUnitsKeepTheirAttackAndVelocityDisabled()
    {
        using var server = Run(300);
        int frozen = 0;
        foreach (var item in server.World.Query<Frozen>())
        {
            Assert.That(item.Entity.IsAttackDisabled && item.Entity.IsVelocityDisabled, Is.True);
            Assert.That(item.Entity.HasAttack || item.Entity.HasVelocity, Is.False);  // no query sees them
            frozen++;
        }

        Assert.That(frozen, Is.Positive);
        foreach (var item in server.World.Query<Attack>()) Assert.That(item.Entity.Has<Frozen>(), Is.False);
    }

    [Test]
    public void DestroyedNpcsComeBackThroughTheirCleanupComponent()
    {
        using var server = Run(300);
        Assert.That(server.World.Any<Destroyed>(), Is.False);                 // revived within the tick
        Assert.That(server.World.Count<Npc>(), Is.EqualTo(Units - Units / 3)); // as many NPCs as ever
        Assert.That(server.Battle.Deaths, Is.Positive);
    }

    [Test]
    public void TheLeaderboardIsOrderedByKills()
    {
        using var server = Run(300);
        Span<Entity> top = new Entity[10];
        Assert.That(server.TopKillers(top), Is.EqualTo(10));
        int most = int.MaxValue;
        foreach (var player in top)
        {
            Assert.That(player.Kills.Count, Is.LessThanOrEqualTo(most));
            most = player.Kills.Count;
        }

        int best = 0;
        foreach (var item in server.World.Query<Kills>()) best = Math.Max(best, item.Component1.Count);
        Assert.That(top[0].Kills.Count, Is.EqualTo(best).And.Positive);
    }

    [Test]
    public void APlayerWhoLeavesTakesItsShotsAndLeavesFriendLists()
    {
        using var server = Run(100);
        Entity player = default;
        foreach (var item in server.World.Query<ChildOf>())
            if (server.World.TryGetEntity(item.Component1.Parent, out player)) break;
        Assert.That(server.FriendsOf(player), Is.EqualTo(2));                 // two players list it
        int shots = server.World.Count<Lifetime>(), own = 0;
        foreach (var shot in server.World.Children(player)) own++;
        Assert.That(own, Is.Positive);

        Assert.That(server.Leave(player), Is.True);
        Assert.That(server.World.Count<Lifetime>(), Is.EqualTo(shots - own)); // its children went with it
        Assert.That(server.FriendsOf(player), Is.Zero);                       // the policy dropped the links
        foreach (var item in server.World.Query<DynamicBuffer<Friend>>())
            foreach (var link in item.Component1) Assert.That(link.Target, Is.Not.EqualTo(player.Id));
        Assert.That(server.Leave(player), Is.False);
    }

    [Test]
    public void APlayerTransfersWithItsShots()
    {
        using var server = Run(100);
        using var other = new BattleServer(30);
        Entity player = default;
        foreach (var item in server.World.Query<ChildOf>())
            if (server.World.TryGetEntity(item.Component1.Parent, out player)) break;
        int own = 0, players = server.World.Count<Player>();
        foreach (var shot in server.World.Children(player)) own++;
        int value = server.LootValue(player);

        var arrived = server.Transfer(player, other);
        Assert.That(player.IsAlive, Is.False);
        Assert.That(server.World.Count<Player>(), Is.EqualTo(players - 1));
        Assert.That(arrived.World, Is.SameAs(other.World));
        Assert.That(other.LootValue(arrived), Is.EqualTo(value));
        int children = 0;
        foreach (var shot in other.World.Children(arrived)) children++;
        Assert.That(children, Is.EqualTo(own));                               // still its children over there
        foreach (var link in arrived.Friend) Assert.That(link.Target, Is.EqualTo(EntityId.Null));  // friends stayed
    }

    [Test]
    public void ASavedBattleLoadsAndPlaysOnTheSame()
    {
        using var server = Run(100);
        var save = new ArrayBufferWriter<byte>();
        server.Save(save);
        using var copy = new BattleServer(30);
        copy.Load(save.WrittenSpan);                                          // the copy's own units are replaced
        Assert.That(copy.Battle, Is.EqualTo(server.Battle));                  // singletons
        Assert.That(copy.Census(), Is.EqualTo(server.Census() with { Weakest = copy.World.GetEntity(server.Census().Weakest.Id) }));
        Assert.That(copy.TopKillers(new Entity[1]), Is.EqualTo(1));          // indexes rebuilt
        for (int zone = 0; zone < 4; zone++) Assert.That(copy.PlayersIn(zone), Is.EqualTo(server.PlayersIn(zone)));  // groups
        for (int i = 0; i < 60; i++) copy.Tick();                             // and it plays on
        Assert.That(copy.Battle.Tick, Is.EqualTo(server.Battle.Tick + 60));
        Assert.That(copy.Failure, Is.Null);
    }

    [Test]
    public void TheTablesDescribeEveryEntity()
    {
        using var server = Run(30);
        int entities = 0;
        foreach (var table in server.Tables()) entities += table.Entities;
        int scanned = 0;
        foreach (var e in server.World.Entities) scanned++;
        Assert.That(entities, Is.EqualTo(scanned));
    }

    private static BattleServer Run(int ticks)
    {
        var server = new BattleServer(Units);
        for (int i = 0; i < ticks; i++) server.Tick();
        return server;
    }

    private sealed class FaultySystem : EcsSystem
    {
        protected override void OnUpdate() => throw new InvalidOperationException("a bug in one system");
    }
}
