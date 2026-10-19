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

    [TestCase(600, 40)] // one block: removals, writes and moves share words
    [TestCase(10_000, 12)] // three blocks of 4,096 rows: sparse removals among words that were only written
    public void RemovedFiltersMatchTheLastRemovalOfEveryRowThroughWritesAndMoves(int entities, int rounds)
    {
        // Rows lose and get back their armor, are written while they have it (writes are not removals), move between
        // tables with their ticks, are disabled and destroyed: for every mark, the filter must select exactly the
        // entities lacking armor whose removal is at or after it.
        using var world = new World(new WorldOptions { InitialEntityCapacity = 16 });
        var random = new Random(11);
        var removed = new System.Collections.Generic.Dictionary<Entity, ulong>(); // entities lacking armor: their removal
        var disabled = new System.Collections.Generic.HashSet<Entity>();
        var live = new System.Collections.Generic.List<Entity>();
#pragma warning disable EECS031 // no reservation on purpose: the tables grow under the change ticks the filter reads
        for (int i = 0; i < entities; i++) live.Add(world.CreateEntity(new Armor(i), new Seen()));
#pragma warning restore EECS031

        for (int round = 0; round < rounds; round++)
        {
            world.Update();
            for (int op = 0; op < 80; op++)
            {
                var e = live[random.Next(live.Count)];
                bool lacking = removed.ContainsKey(e);
                switch (random.Next(7))
                {
                    case 0:
                        if (lacking) break;
                        e.Remove<Armor>();
                        removed[e] = world.ChangeTick;
                        break;
                    case 1: // back (enabled with its kept value, or set), or a write when present
                        if (disabled.Remove(e)) Assert.That(e.Enable<Armor>(), Is.True);
                        else e.Set(new Armor(round));
                        removed.Remove(e);
                        break;
                    case 2:
                        if (!lacking) e.Get<Armor>().Value++; // a write: not a removal
                        break;
                    case 3: if (e.Has<Frozen>()) e.Remove<Frozen>(); else e.Set(new Frozen()); break; // moves, keeps its tick
                    case 4:
                        if (lacking) break;
                        Assert.That(e.Disable<Armor>(), Is.True);
                        disabled.Add(e);
                        removed[e] = world.ChangeTick; // disabled: removed too, its value kept
                        break;
                    case 5:
                        e.Destroy();
                        live.Remove(e);
                        removed.Remove(e);
                        disabled.Remove(e);
                        live.Add(world.CreateEntity(new Armor(round), new Seen()));
                        break;
                    default:
                        int at = live.IndexOf(e);
                        for (int k = 0; k < 64 && k < live.Count; k++) // a run of writes over entities created together
                            if (live[(at + k) % live.Count] is var w && !removed.ContainsKey(w)) w.Get<Armor>().Value++;
                        break;
                }
            }

            for (ulong mark = 1; mark <= world.ChangeTick; mark += 1 + (ulong)random.Next(3))
            {
                var expected = new System.Collections.Generic.HashSet<Entity>();
                foreach (var pair in removed)
                    if (pair.Value >= mark) expected.Add(pair.Key);
                var found = new System.Collections.Generic.HashSet<Entity>();
                foreach (var item in world.Query<Seen>(QueryFilter.Create().Removed<Armor>(mark))) found.Add(item.Entity);
                Assert.That(found.SetEquals(expected), Is.True, $"round {round}, mark {mark}");
                Assert.That(world.Count<Seen>(QueryFilter.Create().Removed<Armor>(mark)), Is.EqualTo(expected.Count));
            }
        }
    }
}
