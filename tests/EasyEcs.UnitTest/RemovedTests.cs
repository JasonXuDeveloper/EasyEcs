using System;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public class RemovedTests
{
    /// <summary>Counts, per entity, the runs in which it had lost its Armor since the system's previous run.</summary>
    [EcsSystem]
    public ref struct ArmorLoss
    {
        [ForEach, Removed<Armor>]
        private static void Count(ref Seen seen) => seen.Count++;
    }

    /// <summary>The batch form, with the entities of each run.</summary>
    [EcsSystem]
    public ref struct ArmorLossBatch
    {
        [ForEach, Removed<Armor>]
        private static void Count(EntitySpan entities, Span<Seen> seen)
        {
            for (int i = 0; i < seen.Length; i++) seen[i].Count += entities[i].Has<Armor>() ? 100 : 1;
        }
    }

    /// <summary>Watches changes of the same component: removed rows are not changed rows.</summary>
    [EcsSystem]
    public ref struct ArmorChange
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
    public void RetiringIsADespawnNotARemoval()
    {
        using var world = new World(new WorldOptions { DestroyLogCapacity = 8 });
        var kept = world.CreateEntity(new Lease(1), new Armor(1));
        var removedFirst = world.CreateEntity(new Lease(2), new Armor(2));
        var plain = world.CreateEntity(new Armor(3));
        ulong tick = world.ChangeTick;
        removedFirst.Remove<Armor>();                                // a removal, then the destroy
        Assert.That(world.Count<Lease>(QueryFilter.Create().Removed<Armor>(tick)), Is.EqualTo(1));
        kept.Destroy();
        removedFirst.Destroy();
        plain.Destroy();

        // A retired entity reports no removal, as a freed one does not: no Removed filter visits it.
        var removed = QueryFilter.Create().Removed<Armor>(tick);
        Assert.That((world.Count<Lease>(removed), world.Count<Lease>(removed.With<Retired>())), Is.EqualTo((0, 0)));
        Assert.That(world.Count<Lease>(QueryFilter.Create().RemovedAny<Armor, Shield>(tick)), Is.Zero);
        Span<EntityId> gone = stackalloc EntityId[8];
        world.DestroyedSince(tick, gone, out int count);
        Assert.That(count, Is.EqualTo(1));                           // the freed one; the retired ones once released
        Assert.That(world.RemoveAll<Lease>(), Is.EqualTo(2));
        world.DestroyedSince(tick, gone, out count);
        Assert.That(count, Is.EqualTo(3));
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

    // ---- The destroy log (WorldOptions.DestroyLogCapacity, world.DestroyedSince) ----

    private static EntityId[] DestroyedSince(World world, ulong since)
    {
        var into = new EntityId[64];
        Assert.That(world.DestroyedSince(since, into, out int count), Is.True);
        return into.AsSpan(0, count).ToArray();
    }

    [Test]
    public void ReadersAtDifferentTicksEachSeeTheirDestroys()
    {
        using var world = new World(new WorldOptions { DestroyLogCapacity = 16 });
        var units = new Entity[6];
        world.CreateEntities(units, new Health(1));
        ulong first = world.ChangeTick;                              // reader A acknowledged here
        units[0].Destroy();
        world.Update();
        ulong second = world.ChangeTick;                             // reader B acknowledged here
        units[1].Destroy();
        units[2].Destroy();
        world.Update();
        ulong now = world.ChangeTick;

        Assert.That(DestroyedSince(world, first), Is.EqualTo(new[] { units[0].Id, units[1].Id, units[2].Id }));
        Assert.That(DestroyedSince(world, second), Is.EqualTo(new[] { units[1].Id, units[2].Id }));
        Assert.That(DestroyedSince(world, now), Is.Empty);           // nothing since
        Assert.That(world.DestroyLogOldestTick, Is.Zero);            // nothing overwritten: every reader is served

        var recycled = world.CreateEntity(new Health(2));            // the index comes back with another version
        recycled.Destroy();
        var ids = DestroyedSince(world, now);
        Assert.That((ids.Length, ids[0], ids[0] == units[2].Id), Is.EqualTo((1, recycled.Id, false)));

        Span<EntityId> small = stackalloc EntityId[2];               // too small: the oldest that fit, the full count
        Assert.That((world.DestroyedSince(first, small, out int count), count, small[1]), Is.EqualTo((true, 4, units[1].Id)));
    }

    [Test]
    public void AnOverwrittenLogTellsOldReadersToResync()
    {
        using var world = new World(new WorldOptions { DestroyLogCapacity = 4 });
        var units = new Entity[6];
        world.CreateEntities(units, new Health(1));
        ulong old = world.ChangeTick;
        units[0].Destroy();
        units[1].Destroy();
        world.Update();
        ulong recent = world.ChangeTick;
        for (int i = 2; i < 6; i++) units[i].Destroy();              // the ring of 4 drops units 0 and 1

        Assert.That(world.DestroyLogOldestTick, Is.EqualTo(old + 1));
        var into = new EntityId[4];
        Assert.That((world.DestroyedSince(old, into, out int count), count), Is.EqualTo((false, 0)));
        Assert.That(DestroyedSince(world, recent), Is.EqualTo(new[] { units[2].Id, units[3].Id, units[4].Id, units[5].Id }));
    }

    [Test]
    public void BulkDestroysCascadesAndMovesAreLogged()
    {
        using var world = new World(new WorldOptions { DestroyLogCapacity = 64 });
        using var other = new World();
        var shots = new Entity[3];
        world.CreateEntities(shots, new Stun(1));                    // a whole table: freed at once
        var tracked = world.CreateEntity(new Stun(1), new Armor(1)); // a table destroyed by a Changed filter
        var parent = world.CreateEntity(new Health(1));
        var child = world.CreateEntity(new ChildOf(parent));
        var grandchild = world.CreateEntity(new ChildOf(child));
        var traveller = world.CreateEntity(new Health(2));
        ulong mark = world.ChangeTick;

        Assert.That(world.DestroyAll<Stun>(), Is.EqualTo(4));
        Assert.That(DestroyedSince(world, mark), Is.EquivalentTo(new[] { shots[0].Id, shots[1].Id, shots[2].Id, tracked.Id }));

        mark = world.ChangeTick + 1;
        world.Update();
        parent.Destroy();                                            // the children follow
        Assert.That(DestroyedSince(world, mark), Is.EquivalentTo(new[] { parent.Id, child.Id, grandchild.Id }));

        mark = world.ChangeTick + 1;
        world.Update();
        world.MoveEntities(other, [traveller.Id]);                   // gone from this world
        Assert.That(DestroyedSince(world, mark), Is.EqualTo(new[] { traveller.Id }));
    }

    [Test]
    public void ARetiredEntityIsLoggedWhenItsLastCleanupComponentGoes()
    {
        using var world = new World(new WorldOptions { DestroyLogCapacity = 8 });
        var session = world.CreateEntity(new Lease(1), new Ticket(2), new Health(1));
        ulong mark = world.ChangeTick;
        session.Destroy();                                           // retired: still alive, Retired tag
        Assert.That(DestroyedSince(world, mark), Is.Empty);
        session.Remove<Lease>();
        Assert.That(DestroyedSince(world, mark), Is.Empty);          // a cleanup component is left
        world.Update();
        session.Remove<Ticket>();                                    // the last one: the id dies now
        var ids = DestroyedSince(world, mark);
        Assert.That((ids.Length, ids[0], DestroyedSince(world, world.ChangeTick).Length), Is.EqualTo((1, session.Id, 1)));
    }

    [Test]
    public void RestoreAndLoadLogTheEntitiesTheyTakeAway()
    {
        using var world = new World(new WorldOptions { DestroyLogCapacity = 16 });
        var kept = world.CreateEntity(new Health(1));
        var gone = world.CreateEntity(new Health(2));
        var snapshot = world.Capture();
        var later = world.CreateEntity(new Health(3));               // not in the snapshot
        gone.Destroy();
        var reborn = world.CreateEntity(new Health(4));              // gone's index, another version
        ulong mark = world.ChangeTick + 1;
        world.Update();

        world.Restore(snapshot);                                     // gone alive again, later and reborn not
        Assert.That(DestroyedSince(world, mark), Is.EquivalentTo(new[] { later.Id, reborn.Id }));
        Assert.That((kept.IsAlive, gone.IsAlive), Is.EqualTo((true, true)));

        var save = new System.Buffers.ArrayBufferWriter<byte>();
        using (var source = new World())
        {
            source.CreateEntity(new Health(8)).Destroy();
            source.CreateEntity(new Health(9));                      // 0v2: not kept (0v1)
            source.Save(save);
        }

        mark = world.ChangeTick + 1;
        world.Update();
        world.Load(save.WrittenSpan);                                // a replacement: what was alive is gone
        Assert.That(DestroyedSince(world, mark), Is.EquivalentTo(new[] { kept.Id, gone.Id }));
    }

    [Test]
    public void TheLogIsOffByDefault()
    {
        using var world = new World();
        world.CreateEntity(new Health(1)).Destroy();
        Assert.That(world.DestroyLogOldestTick, Is.EqualTo(ulong.MaxValue));
        Assert.Throws<InvalidOperationException>(() => world.DestroyedSince(0, default, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new World(new WorldOptions { DestroyLogCapacity = -1 }));
    }

    [Test]
    public void DestroysAndReadsOfTheLogDoNotAllocate()
    {
        using var world = new World(new WorldOptions { DestroyLogCapacity = 256 });
        var churned = new Entity[64];
        var into = new EntityId[256];
        var parent = world.CreateEntity(new Health(1));
        int wrong = 0;
        void Frame()
        {
            ulong mark = world.ChangeTick;
            for (int i = 0; i < churned.Length; i++) churned[i] = world.CreateEntity(new Position(), new Health(1));
            for (int i = 0; i < churned.Length / 2; i++) churned[i].Destroy();
            world.DestroyAll<Position>();
            var child = world.CreateEntity(new ChildOf(parent));
            world.CreateEntity(new ChildOf(child));
            parent.Destroy();                                        // a cascade of three
            parent = world.CreateEntity(new Health(1));
            world.Update();
            if (!world.DestroyedSince(mark, into, out int count) || count != churned.Length + 3) wrong++;
        }

        for (int i = 0; i < 10; i++) Frame(); // warm-up: tables, edges, JIT
        long before = Allocations.Baseline();
        for (int i = 0; i < 100; i++) Frame();
        Assert.That((GC.GetAllocatedBytesForCurrentThread() - before, wrong), Is.EqualTo((0L, 0)));
    }

    /// <summary>Visits the entities that lost their armor or their shield.</summary>
    [EcsSystem]
    public ref struct ArmorOrShieldLoss
    {
        [ForEach, RemovedAny<Armor, Shield>]
        private static void Count(ref Seen seen) => seen.Count++;
    }

    [Test]
    public void RemovedAnyVisitsEntitiesThatLostOneOfTheComponents()
    {
        using var world = new World();
        var armor = world.CreateEntity(new Armor(1), new Shield(1), new Seen());
        var shield = world.CreateEntity(new Armor(1), new Shield(1), new Seen());
        var kept = world.CreateEntity(new Armor(1), new Shield(1), new Seen());
        var back = world.CreateEntity(new Armor(1), new Shield(1), new Seen());
        var armorOnly = world.CreateEntity(new Armor(1), new Seen());      // a table without a Shield column
        var never = world.CreateEntity(new Seen());
        world.AddSystem<ArmorOrShieldLoss>();
        world.Update();
        var all = new[] { armor, shield, kept, back, armorOnly, never };
        Assert.That(Array.ConvertAll(all, e => e.Get<Seen>().Count), Is.EqualTo(new[] { 0, 0, 0, 0, 0, 0 }));

        armor.Remove<Armor>();
        shield.Remove<Shield>();
        kept.Get<Armor>().Value++;                                          // a write is not a removal
        back.Remove<Shield>();
        back.Set(new Shield(2));                                            // back again: not removed
        armorOnly.Remove<Armor>();
        world.Update();
        world.Update();
        Assert.That(Array.ConvertAll(all, e => e.Get<Seen>().Count), Is.EqualTo(new[] { 1, 1, 0, 0, 1, 0 }));

        var lost = QueryFilter.Create().RemovedAny<Armor, Shield>(0);
        Assert.That(world.Count<Seen>(lost), Is.EqualTo(3));
        Assert.That(world.Count<Seen>(lost.With<Shield>()), Is.EqualTo(1));                  // AND with the other filters
    }
}
