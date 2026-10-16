using System;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;

namespace EasyEcs.UnitTest.Showcase;

using EasyEcs.Benchmark.Showcase;   // inside the namespace: wins over the shared test fixtures

/// <summary>
/// The battle server of the README and BattleServerBenchmark, run as a whole: every feature it uses must keep its
/// promise while the others churn the world (changes replicated, indexes in sync, relations cleaned up, struct jobs
/// and queries agreeing with scans, snapshots replaying, failures isolated, nothing allocated).
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
        Assert.That(server.Failure.System, Is.Null);
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
                Assert.That(item.Component2.Health, Is.EqualTo(item.Component1.Value));
        }
    }

    [Test]
    public void TheZoneIndexMatchesAScan()
    {
        using var server = Run(200);
        var counts = new int[4];
        foreach (var item in server.World.Query<Zone>()) counts[item.Component1.Id]++;
        for (int zone = 0; zone < counts.Length; zone++) Assert.That(server.PlayersIn(zone), Is.EqualTo(counts[zone]));
        Assert.That(counts, Has.Some.Not.EqualTo(counts[0]));                // players spread over the zones
    }

    [Test]
    public void ProjectilesBelongToLivingPlayersAndTheRelationListsThem()
    {
        using var server = Run(200);
        var shots = server.World.Index<OwnedBy, Entity>();
        var owned = new Dictionary<Entity, int>();
        foreach (var item in server.World.Query<OwnedBy>())
        {
            var owner = item.Component1.Owner;
            Assert.That(owner.IsAlive && owner.Has<Player>() && !owner.Has<Dead>(), Is.True);
            owned[owner] = owned.GetValueOrDefault(owner) + 1;
        }

        Assert.That(owned, Is.Not.Empty);
        foreach (var (owner, count) in owned)
        {
            int found = 0;
            foreach (var shot in shots.Find(owner)) found++;
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
        server.Save();
        server.Census();                                                      // struct jobs that declare no writes
        Assert.That(server.Unsaved, Is.Zero);
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
        Assert.That(server.World.Index<OwnedBy, Entity>().Count(server.NearestPlayer(default)), Is.Zero);
        Assert.That(server.World.GetFragmentationStats().FragmentationRatio, Is.LessThanOrEqualTo(0.1f));
    }

    [Test]
    public void UnsavedCountsThePlayersWrittenSinceTheSave()
    {
        using var server = Run(30);
        server.Save();
        Assert.That(server.Unsaved, Is.Zero);
        for (int i = 0; i < 10; i++) server.Tick();
        int written = 0;
        foreach (var e in server.World.Entities)
            if (e.Has<Player>() && e.TryGet<Health>(out var hp) && e.TryGet<Replica>(out var replica) && hp.Value != replica.Health)
                written++;
        Assert.That(server.Unsaved, Is.Positive.And.AtLeast(written));      // hits since the last send at least
        Assert.That(server.Unsaved, Is.LessThanOrEqualTo(server.Census().Players));
    }

    [Test]
    public void RestoringASnapshotReplaysTheSameBattle()
    {
        using var server = Run(100);
        server.Save();
        for (int i = 0; i < 60; i++) server.Tick();
        var first = server.Battle;
        server.Load();
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
        Assert.That(server.Failure.System, Is.InstanceOf<FaultySystem>());
        Assert.That(server.Failure.System.Enabled, Is.False);
        Assert.That(server.Battle.Tick, Is.EqualTo(tick + 10));
    }

    [Test]
    public void SystemsAreMeasuredAndOwnTheirSingletons()
    {
        using var server = Run(20);
        Assert.That(server.Slowest().Stats.Runs, Is.Positive);
        Assert.That(server.World.GetSystem<RegenerationSystem>().Stats.Runs, Is.EqualTo(2));   // every 10th update
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
        using var server = Run(300);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) server.Tick();
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
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
