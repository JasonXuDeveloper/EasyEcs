using System;
using System.Collections.Generic;
using EasyEcs.Benchmark.Showcase;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>
/// The battle server of the README and BattleServerBenchmark, run as a whole: every feature it uses must keep its
/// promise while the others churn the world (changes replicated, indexes in sync, relations cleaned up, snapshots
/// replaying, nothing allocated).
/// </summary>
public class BattleServerTests
{
    private const int Units = 3_000;

    [Test]
    public void TheBattleIsBusy()
    {
        using var world = Run(300);
        var battle = world.GetSingleton<Battle>();
        Assert.That(new[] { battle.Hits, battle.Deaths, battle.Shots, battle.Replicated, battle.Afflicted }, Is.All.Positive);
    }

    [Test]
    public void ReplicationSendsEveryChangeAndOnlyChanges()
    {
        using var world = Run(30);
        for (int sends = 0; sends < 30;)
        {
            BattleServer.Tick(world);
            if (world.GetSingleton<Battle>().Tick % 3 != 0) continue;
            sends++;
            int sent = world.GetSingleton<Battle>().Replicated;
            world.Update<Replication>();                                      // nothing changed since the send
            Assert.That(world.GetSingleton<Battle>().Replicated, Is.EqualTo(sent));
            foreach (var item in world.Query<Health, Replica>())             // every client copy is current
                Assert.That(item.Component2.Health, Is.EqualTo(item.Component1.Value));
        }
    }

    [Test]
    public void TheZoneIndexMatchesAScan()
    {
        using var world = Run(200);
        var zones = world.Index<Zone, int>();
        var counts = new int[4];
        foreach (var item in world.Query<Zone>()) counts[item.Component1.Id]++;
        for (int zone = 0; zone < counts.Length; zone++) Assert.That(zones.Count(zone), Is.EqualTo(counts[zone]));
        Assert.That(counts, Has.Some.Not.EqualTo(counts[0]));                // players spread over the zones
    }

    [Test]
    public void ProjectilesBelongToLivingPlayersAndTheRelationListsThem()
    {
        using var world = Run(200);
        var shots = world.Index<OwnedBy, Entity>();
        var owned = new Dictionary<Entity, int>();
        foreach (var item in world.Query<OwnedBy>())
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
    public void RestoringASnapshotReplaysTheSameBattle()
    {
        using var world = Run(100);
        var save = world.Capture();
        for (int i = 0; i < 60; i++) BattleServer.Tick(world);
        var first = world.GetSingleton<Battle>();
        world.Restore(save);
        for (int i = 0; i < 60; i++) BattleServer.Tick(world);
        var replay = world.GetSingleton<Battle>();
        // A restore counts every tracked row as written: the first send after it brings clients the whole state.
        Assert.That(replay with { Replicated = 0 }, Is.EqualTo(first with { Replicated = 0 }));
        Assert.That(replay.Replicated, Is.GreaterThan(first.Replicated));
    }

    [Test]
    public void ATickAllocatesNothing()
    {
        using var world = Run(300);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) BattleServer.Tick(world);
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    private static World Run(int ticks)
    {
        var world = BattleServer.Create(Units);
        for (int i = 0; i < ticks; i++) BattleServer.Tick(world);
        return world;
    }
}
