using System;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public class RemovedTests
{
    /// <summary>Counts, per entity, the runs in which it had lost its Armor since the system's previous run.</summary>
    public class ArmorLoss : EcsSystem
    {
        [ForEach, Removed<Armor>]
        private static void Count(ref Seen seen) => seen.Count++;
    }

    /// <summary>The batch form, with the entities of each run.</summary>
    public class ArmorLossBatch : EcsSystem
    {
        [ForEach, Removed<Armor>]
        private static void Count(EntitySpan entities, Span<Seen> seen)
        {
            for (int i = 0; i < seen.Length; i++) seen[i].Count += entities[i].Has<Armor>() ? 100 : 1;
        }
    }

    /// <summary>Watches changes of the same component: removed rows are not changed rows.</summary>
    public class ArmorChange : EcsSystem
    {
        [ForEach, Changed<Armor>]
        private static void Count(ref Health seen) => seen.Value++;
    }

    [Test]
    public void AMethodVisitsEachRemovalOnce()
    {
        using var world = new World();
        var kept = world.CreateEntity(new Armor(1), new Seen());
        var lost = world.CreateEntity(new Armor(1), new Seen());
        var never = world.CreateEntity(new Seen());                  // never had Armor: not a removal
        world.AddSystem<ArmorLoss>();
        world.Update();
        Assert.That(lost.Get<Seen>().Count, Is.Zero);                // nothing removed yet

        lost.Remove<Armor>();
        world.Update();
        world.Update();
        Assert.That(lost.Get<Seen>().Count, Is.EqualTo(1));          // once, not again on the next run
        Assert.That(kept.Get<Seen>().Count + never.Get<Seen>().Count, Is.Zero);

        lost.Set(new Armor(2));                                      // back again: no longer removed
        world.Update();
        Assert.That(lost.Get<Seen>().Count, Is.EqualTo(1));
        lost.Remove<Armor>();
        world.Update();
        Assert.That(lost.Get<Seen>().Count, Is.EqualTo(2));          // a second removal is a second visit
    }

    [Test]
    public void ARemovalFollowsTheEntityToAnotherTable()
    {
        using var world = new World();
        var e = world.CreateEntity(new Armor(1), new Seen());
        world.AddSystem<ArmorLoss>();
        world.Update();
        e.Remove<Armor>();
        e.Set(new Frozen());                                         // a new column: the row moves with its tick
        world.Update();
        Assert.That(e.Get<Seen>().Count, Is.EqualTo(1));
    }

    [Test]
    public void ChangedFiltersSkipRemovedRowsAndDestroyedEntitiesAreNotVisited()
    {
        using var world = new World();
        var removed = world.CreateEntity(new Armor(1), new Seen(), new Health(0));
        var destroyed = world.CreateEntity(new Armor(1), new Seen(), new Health(0));
        world.AddSystem<ArmorLoss>();
        world.AddSystem<ArmorChange>();
        world.Update();
        int changed = removed.Get<Health>().Value;
        removed.Remove<Armor>();
        destroyed.Remove<Armor>();
        destroyed.Destroy();
        world.Update();
        Assert.That(removed.Get<Health>().Value, Is.EqualTo(changed)); // not a change
        Assert.That(removed.Get<Seen>().Count, Is.EqualTo(1));
        Assert.That(world.EntityCount, Is.EqualTo(1));
    }

    [Test]
    public void BatchMethodsReceiveRunsOfRemovedRows()
    {
        using var world = new World();
        var entities = new Entity[200];
        world.CreateEntities(entities, new Armor(1), new Seen());
        world.AddSystem<ArmorLossBatch>();
        world.Update();
        for (int i = 0; i < entities.Length; i += 3) entities[i].Remove<Armor>();
        world.Update();
        for (int i = 0; i < entities.Length; i++)
            Assert.That(entities[i].Get<Seen>().Count, Is.EqualTo(i % 3 == 0 ? 1 : 0));
    }

    [Test]
    public void QueryFiltersTakeTheTickToLookFrom()
    {
        using var world = new World();
        var a = world.CreateEntity(new Armor(1), new Seen());
        var b = world.CreateEntity(new Armor(1), new Seen());
        a.Remove<Armor>();
        world.Update();                                              // the tick moves on
        ulong since = world.ChangeTick;
        b.Remove<Armor>();
        var recent = QueryFilter.Create().Removed<Armor>(since);
        Assert.That(world.Count<Seen>(recent), Is.EqualTo(1));
        foreach (var item in world.Query<Seen>(recent)) Assert.That(item.Entity, Is.EqualTo(b));
        Assert.That(world.Count<Seen>(recent.Since(1)), Is.EqualTo(2));
        Assert.That(world.Any<Seen>(recent.Since(world.ChangeTick + 1)), Is.False);
        Assert.Throws<ArgumentException>(() => QueryFilter.Create().Changed<Shield>(1).Removed<Armor>(2));
    }

    [Test]
    public void ARestoreCountsAbsentRowsAsRemoved()
    {
        using var world = new World();
        var e = world.CreateEntity(new Armor(1), new Seen());
        e.Remove<Armor>();
        var snapshot = world.Capture();
        world.Update();
        ulong since = world.ChangeTick;
        Assert.That(world.Count<Seen>(QueryFilter.Create().Removed<Armor>(since)), Is.Zero);
        world.Restore(snapshot);                                     // like Changed: a restore re-sends everything
        Assert.That(world.Count<Seen>(QueryFilter.Create().Removed<Armor>(since)), Is.EqualTo(1));
    }
}
