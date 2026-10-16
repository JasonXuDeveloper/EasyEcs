using NUnit.Framework;

namespace EasyEcs.UnitTest;

public partial class ChangedTests
{
    /// <summary>Counts, per entity, the runs in which its Armor had changed since the system's previous run.</summary>
    public partial class ArmorWatch : EcsSystem
    {
        [ForEach, Changed<Armor>]
        private static void Count(ref Seen seen) => seen.Count++;
    }

    /// <summary>Wears down the armor of stunned entities: a writer the watch runs after.</summary>
    public partial class Wear : EcsSystem
    {
        [ForEach, With<Stun>]
        private static void Apply(ref Armor armor) => armor = armor with { Value = armor.Value - 1 };
    }

    /// <summary>Reads armor only: an <c>in</c> parameter is not a write.</summary>
    public partial class Inspect : EcsSystem
    {
        [ForEach]
        private static void Read(in Armor armor, ref Health health) => health = new Health(armor.Value);
    }

    /// <summary>The batch form: runs of changed rows as spans.</summary>
    public partial class ArmorWatchBatch : EcsSystem
    {
        [ForEach, Changed<Armor>]
        private static void Count(System.ReadOnlySpan<Armor> armor, System.Span<Seen> seen)
        {
            for (int i = 0; i < seen.Length; i++) seen[i].Count++;
        }
    }

    /// <summary>The parallel form: chunks of rows on several threads, changed rows only.</summary>
    public partial class ArmorWatchParallel : EcsSystem
    {
        [ForEach(Parallel = true), Changed<Armor>]
        private static void Count(ref Seen seen) => seen.Count++;
    }

    /// <summary>Changed combined with other filters, and with a second tracked component (both must have changed).</summary>
    public partial class LivingArmorAndShieldWatch : EcsSystem
    {
        [ForEach, Changed<Armor>, Changed<Shield>, Without<Dead>]
        private static void Count(ref Seen seen) => seen.Count++;
    }

    /// <summary>No component parameter: the Changed component is the queried one.</summary>
    public partial class ArmorEntityWatch : EcsSystem
    {
        [ForEach, Changed<Armor>]
        private static void Count(Entity entity) => entity.Get<Seen>().Count++;
    }

    /// <summary>Destroys entities whose armor changed: structural changes inside a Changed pass.</summary>
    public partial class BreakChanged : EcsSystem
    {
        [ForEach, Changed<Armor>]
        private static void Break(Entity entity, in Armor armor)
        {
            if (armor.Value > 1) entity.Destroy();
        }
    }

    /// <summary>A watch that also writes what it watches: its own writes are not changes for its next run.</summary>
    public partial class Regen : EcsSystem
    {
        [ForEach, Changed<Armor>]
        private static void Apply(ref Armor armor, ref Seen seen) => seen.Count++;
    }

    /// <summary>A writer ordered after the watch.</summary>
    public partial class LateWear : EcsSystem
    {
        public override int Priority => 1;

        [ForEach, With<Stun>]
        private static void Apply(ref Armor armor) => armor = armor with { Value = armor.Value - 1 };
    }

    /// <summary>A watch that runs every other update.</summary>
    public partial class SlowArmorWatch : EcsSystem
    {
        public override int UpdateInterval => 2;

        [ForEach, Changed<Armor>]
        private static void Count(ref Seen seen) => seen.Count++;
    }

    /// <summary>A struct job that writes armor through a ref.</summary>
    private struct Polish : IForEach<Armor>
    {
        public void Execute(int entityId, ref Armor armor) { }
    }

    /// <summary>A batch job that writes armor through a span.</summary>
    private struct PolishBatch : IForEachBatch<Armor>
    {
        public void Execute(System.Span<Armor> armor) { }
    }

    private static int SeenCount(Entity e) => e.Get<Seen>().Count;

    [Test]
    public void ChangedMethodsSeeRowsWrittenSinceTheirLastRun()
    {
        using var world = new World();
        world.AddSystem<ArmorWatch>();
        var a = world.CreateEntity(new Armor(5), new Seen());
        var b = world.CreateEntity(new Armor(5), new Seen());

        world.Update(); // first run: every row is new
        Assert.That((SeenCount(a), SeenCount(b)), Is.EqualTo((1, 1)));

        world.Update(); // nothing written
        Assert.That((SeenCount(a), SeenCount(b)), Is.EqualTo((1, 1)));

        a.Get<Armor>() = new Armor(3); // a ref access between updates
        world.Update();
        Assert.That((SeenCount(a), SeenCount(b)), Is.EqualTo((2, 1)));

        b.Set(new Armor(1));
        world.Update();
        Assert.That((SeenCount(a), SeenCount(b)), Is.EqualTo((2, 2)));
    }

    [Test]
    public void RefParametersStampAndInParametersDoNot()
    {
        using var world = new World();
        world.AddSystem<Wear>();
        world.AddSystem<Inspect>();
        world.AddSystem<ArmorWatch>();
        var worn = world.CreateEntity(new Armor(5), new Seen(), new Health(0), new Stun(1));
        var read = world.CreateEntity(new Armor(5), new Seen(), new Health(0));

        world.Update();
        world.Update(); // Wear writes worn's armor every update; Inspect only reads both
        Assert.That(SeenCount(worn), Is.EqualTo(2));
        Assert.That(SeenCount(read), Is.EqualTo(1));
        Assert.That(read.Get<Health>().Value, Is.EqualTo(5));
    }

    [Test]
    public void QueryFilterSelectsRowsWrittenAtOrAfterATick()
    {
        using var world = new World(new WorldOptions { InitialEntityCapacity = 4 });
        var entities = new Entity[100];
        world.CreateEntities(entities, new Armor(1));
        world.Update();

        ulong mark = world.ChangeTick;
        var changed = QueryFilter.Create().Changed<Armor>().ChangedSince(mark);
        Assert.That(world.Count<Armor>(changed), Is.Zero);

        entities[3].Get<Armor>() = new Armor(2);
        entities[70].Set(new Armor(2));
        foreach (var _ in world.Query<Armor>(QueryFilter.Create().With<Stun>())) { } // matches nothing: no stamp
        Assert.That(world.Count<Armor>(changed), Is.EqualTo(2));
        Assert.That(world.Any<Armor>(changed), Is.True);

        int visited = 0;
        foreach (var item in world.Query<Armor>(changed)) visited += item.Component1.Value;
        Assert.That(visited, Is.EqualTo(4));

        // Without a tick, Changed<T> selects every row having T.
        Assert.That(world.Count<Armor>(QueryFilter.Create().Changed<Armor>()), Is.EqualTo(100));
        Assert.That(world.DestroyEntities<Armor>(changed), Is.EqualTo(2));
    }

    [Test]
    public void TicksFollowEntitiesThatMove()
    {
        using var world = new World();
        var e = world.CreateEntity(new Armor(1));
        world.Update();
        ulong mark = world.ChangeTick;
        e.Get<Armor>() = new Armor(2);
        e.Set(new Frozen()); // moves to another table: the tick goes with it
        var changed = QueryFilter.Create().Changed<Armor>().ChangedSince(mark);
        Assert.That(world.Count<Armor>(changed), Is.EqualTo(1));

        e.Remove<Armor>();
        Assert.That(world.Count<Armor>(changed), Is.Zero);
        e.Add<Armor>(); // adding is a write
        Assert.That(world.Count<Armor>(changed), Is.EqualTo(1));
    }

    [Test]
    public void TicksFollowRowsThatFillHoles()
    {
        using var world = new World();
        var entities = new Entity[200];
        world.CreateEntities(entities, new Armor(1));
        world.Update();
        ulong mark = world.ChangeTick;
        entities[199].Get<Armor>() = new Armor(2); // the last row, in the last 64-row word
        entities[0].Destroy();                     // the last row moves into row 0, whose word was not written
        var changed = QueryFilter.Create().Changed<Armor>().ChangedSince(mark);
        Assert.That(world.Count<Armor>(changed), Is.EqualTo(1));
        foreach (var item in world.Query<Armor>(changed)) Assert.That(item.Entity, Is.EqualTo(entities[199]));
    }

    [Test]
    public void BatchAndParallelMethodsSeeOnlyChangedRows()
    {
        using var world = new World();
        world.AddSystem<ArmorWatchBatch>();
        var entities = new Entity[10_000];
        world.CreateEntities(entities, new Armor(1), new Seen());
        world.Update();
        for (int i = 0; i < entities.Length; i += 97) entities[i].Get<Armor>().Value++;
        world.Update();
        world.Update();
        for (int i = 0; i < entities.Length; i++)
            Assert.That(SeenCount(entities[i]), Is.EqualTo(i % 97 == 0 ? 2 : 1), $"entity {i}");

        world.RemoveSystem<ArmorWatchBatch>();
        world.AddSystem<ArmorWatchParallel>();
        world.Update(); // its first run: every row
        for (int i = 0; i < entities.Length; i += 89) entities[i].Get<Armor>().Value++;
        world.Update();
        for (int i = 0; i < entities.Length; i++)
            Assert.That(SeenCount(entities[i]), Is.EqualTo((i % 97 == 0 ? 2 : 1) + 1 + (i % 89 == 0 ? 1 : 0)), $"entity {i}");
    }

    [Test]
    public void ChangedCombinesWithOtherFiltersAndOtherChangedFilters()
    {
        using var world = new World();
        world.AddSystem<LivingArmorAndShieldWatch>();
        var both = world.CreateEntity(new Armor(1), new Shield(1), new Seen());
        var armorOnly = world.CreateEntity(new Armor(1), new Shield(1), new Seen());
        var dead = world.CreateEntity(new Armor(1), new Shield(1), new Seen(), new Dead());
        world.Update();
        Assert.That((SeenCount(both), SeenCount(armorOnly), SeenCount(dead)), Is.EqualTo((1, 1, 0)));

        foreach (var e in new[] { both, armorOnly, dead }) e.Get<Armor>().Value++;
        both.Get<Shield>().Value++;
        dead.Get<Shield>().Value++;
        world.Update();
        Assert.That((SeenCount(both), SeenCount(armorOnly), SeenCount(dead)), Is.EqualTo((2, 1, 0)));
    }

    [Test]
    public void EveryRefAccessIsAWriteAndCopiesAreNot()
    {
        using var world = new World();
        var entities = new Entity[8];
        world.CreateEntities(entities, new Armor(1), new Health(0));
        world.Update();
        ulong mark = world.ChangeTick;
        var changed = QueryFilter.Create().Changed<Armor>().ChangedSince(mark);

        // Reads: a copy, a presence check, a query over another component.
        entities[0].TryGet<Armor>(out _);
        _ = entities[0].Has<Armor>();
        foreach (var _ in world.Query<Health>()) { }
        Assert.That(world.Count<Armor>(changed), Is.Zero);

        entities[1].GetOrAdd<Armor>();
        Assert.That(world.Count<Armor>(changed), Is.EqualTo(1));
        var spawned = world.CreateEntity(new Armor(2));
        var batch = new Entity[3];
        world.CreateEntities(batch, new Armor(3));
        Assert.That(world.Count<Armor>(changed), Is.EqualTo(5));

        var polish = new Polish();
        world.ForEach<Armor, Polish>(ref polish);
        Assert.That(world.Count<Armor>(changed), Is.EqualTo(world.Count<Armor>()));

        world.Update();
        mark = world.ChangeTick;
        changed = QueryFilter.Create().Changed<Armor>().ChangedSince(mark);
        var polishBatch = new PolishBatch();
        world.ForEachBatch<Armor, PolishBatch>(ref polishBatch);
        Assert.That(world.Count<Armor>(changed), Is.EqualTo(world.Count<Armor>()));
        _ = spawned;
    }

    [Test]
    public void RestoreCountsEveryRowAsChanged()
    {
        using var world = new World();
        world.AddSystem<ArmorWatch>();
        var e = world.CreateEntity(new Armor(1), new Seen());
        world.Update();
        var snapshot = world.Capture();
        world.Update();
        Assert.That(SeenCount(e), Is.EqualTo(1));

        world.Restore(snapshot); // restored values may differ from what any system last saw
        world.Update();
        Assert.That(SeenCount(e), Is.EqualTo(2));
    }

    [Test]
    public void SparseWritesAreFoundAcrossBlocksOfRows()
    {
        using var world = new World();
        var entities = new Entity[20_000]; // five blocks of 4,096 rows
        world.CreateEntities(entities, new Armor(1));
        world.Update();
        ulong mark = world.ChangeTick;
        var changed = QueryFilter.Create().Changed<Armor>().ChangedSince(mark);
        Assert.That(world.Count<Armor>(changed), Is.Zero);

        entities[15_000].Get<Armor>().Value++; // one row in block 3
        entities[19_999].Get<Armor>().Value++; // the last row, in block 4
        Assert.That(world.Count<Armor>(changed), Is.EqualTo(2));

        entities[0].Destroy(); // the last row moves into row 0 (block 0, not written since the mark): its summaries go with it
        var found = new System.Collections.Generic.List<Entity>();
        foreach (var item in world.Query<Armor>(changed)) found.Add(item.Entity);
        Assert.That(found, Is.EquivalentTo(new[] { entities[15_000], entities[19_999] }));
    }

    [Test]
    public void ChangedFiltersMatchTheLastWriteOfEveryRowThroughMovesAndDestroys()
    {
        // Rows of different ticks share words, older rows move into words written later, rows leave and fill holes,
        // a system writes runs of rows and single words: for every mark, the filter must select exactly the entities
        // written at or after it.
        using var world = new World(new WorldOptions { InitialEntityCapacity = 16 });
        var random = new System.Random(7);
        var written = new System.Collections.Generic.Dictionary<Entity, ulong>();
        var live = new System.Collections.Generic.List<Entity>();
        for (int i = 0; i < 600; i++)
        {
            // Half start stunned, in a table of their own: the writer covers it in long runs (whole words).
            var e = i % 2 == 0 ? world.CreateEntity(new Armor(i), new Seen(), new Stun(1)) : world.CreateEntity(new Armor(i), new Seen());
            live.Add(e);
            written[e] = world.ChangeTick;
        }

        world.AddSystem<Wear>(); // writes the armor of stunned entities: runs of rows and words of tested rows

        for (int round = 0; round < 40; round++)
        {
            ulong batch = world.ChangeTick + 1; // the tick of the update's one batch
            world.Update();
            foreach (var e in live)
                if (e.Has<Stun>()) written[e] = batch;
            for (int op = 0; op < 60; op++)
            {
                var e = live[random.Next(live.Count)];
                switch (random.Next(6))
                {
                    case 0: e.Get<Armor>().Value++; written[e] = world.ChangeTick; break;
                    case 1: if (e.Has<Frozen>()) e.Remove<Frozen>(); else e.Set(new Frozen()); break; // moves, keeps its tick
                    case 2: e.Set(new Stun(1)); break;
                    case 3:
                        e.Destroy();
                        live.Remove(e);
                        written.Remove(e);
                        var created = world.CreateEntity(new Armor(round), new Seen());
                        live.Add(created);
                        written[created] = world.ChangeTick;
                        break;
                    default: if (e.Has<Stun>()) e.Remove<Stun>(); break; // its row stays: a presence bit flips
                }
            }

            for (ulong mark = 1; mark <= world.ChangeTick; mark += 1 + (ulong)random.Next(4))
            {
                var expected = new System.Collections.Generic.HashSet<Entity>();
                foreach (var pair in written)
                    if (pair.Value >= mark) expected.Add(pair.Key);
                var found = new System.Collections.Generic.HashSet<Entity>();
                // Through Seen: a query handing out ref Armor would stamp the rows it visits.
                foreach (var item in world.Query<Seen>(QueryFilter.Create().Changed<Armor>().ChangedSince(mark)))
                    found.Add(item.Entity);
                Assert.That(found.SetEquals(expected), Is.True, $"round {round}, mark {mark}");
                Assert.That(world.Count<Seen>(QueryFilter.Create().Changed<Armor>().ChangedSince(mark)), Is.EqualTo(expected.Count));
            }
        }
    }

    [Test]
    public void RunsOfWrittenRowsAreSelectedExactly()
    {
        // A system writes a whole table (whole 64-row words and partial ones); later single writes split words again.
        using var world = new World();
        var entities = new Entity[1_000];
        world.CreateEntities(entities, new Armor(1), new Seen(), new Stun(1));
        world.AddSystem<Wear>();
        ulong batch = world.ChangeTick + 1;
        world.Update();
        var sinceBatch = QueryFilter.Create().Changed<Armor>().ChangedSince(batch);
        var afterBatch = QueryFilter.Create().Changed<Armor>().ChangedSince(batch + 1);
        Assert.That(world.Count<Seen>(sinceBatch), Is.EqualTo(1_000));
        Assert.That(world.Count<Seen>(afterBatch), Is.Zero);

        entities[130].Get<Armor>().Value++;
        entities[999].Get<Armor>().Value++;
        Assert.That(world.Count<Seen>(sinceBatch), Is.EqualTo(1_000));
        Assert.That(world.Count<Seen>(afterBatch), Is.EqualTo(2));
    }

    [Test]
    public void QueryWritesAreFoundInEveryWordAndTick()
    {
        // A query hands out refs row after row, over several 64-row words, at two ticks; a single write splits a word.
        using var world = new World();
        var entities = new Entity[300];
        world.CreateEntities(entities, new Armor(1), new Seen());
        for (int round = 0; round < 2; round++)
        {
            ulong mark = world.ChangeTick;
            int visited = 0;
            foreach (var item in world.Query<Armor>())
                if (++visited % 2 == 0) item.Component1.Value++;
            var since = QueryFilter.Create().Changed<Armor>().ChangedSince(mark);
            Assert.That(world.Count<Seen>(since), Is.EqualTo(300));
            world.Update();
            entities[200].Get<Armor>().Value++;
            Assert.That(world.Count<Seen>(QueryFilter.Create().Changed<Armor>().ChangedSince(world.ChangeTick)), Is.EqualTo(1));
            world.Update();
        }
    }

    [Test]
    public void QueryWritesAfterATickInsideTheLoopCarryTheNewTick()
    {
        using var world = new World();
        var entities = new Entity[100];
        world.CreateEntities(entities, new Armor(1), new Seen());
        int visited = 0;
        ulong tick = 0;
        foreach (var item in world.Query<Armor>())
        {
            if (++visited == 10)
            {
                world.Update(); // a new tick in the middle of a word
                tick = world.ChangeTick;
            }

            item.Component1.Value++;
        }

        // Moving to a row stamps it: rows 11-100 were reached after the tick.
        Assert.That(world.Count<Seen>(QueryFilter.Create().Changed<Armor>().ChangedSince(tick)), Is.EqualTo(90));
    }

    [Test]
    public void ChangedWithoutComponentParametersQueriesTheChangedComponent()
    {
        using var world = new World();
        world.AddSystem<ArmorEntityWatch>();
        var a = world.CreateEntity(new Armor(1), new Seen());
        var b = world.CreateEntity(new Armor(1), new Seen());
        world.CreateEntity(new Seen()); // no armor: never visited
        world.Update();
        a.Get<Armor>().Value++;
        world.Update();
        Assert.That((SeenCount(a), SeenCount(b)), Is.EqualTo((2, 1)));
    }

    [Test]
    public void ChangedCombinesWithWithAny()
    {
        using var world = new World();
        var stunned = world.CreateEntity(new Armor(1), new Stun(1));
        var frozen = world.CreateEntity(new Armor(1), new Frozen());
        var plain = world.CreateEntity(new Armor(1));
        world.Update();
        var filter = QueryFilter.Create().Changed<Armor>().ChangedSince(world.ChangeTick).WithAny<Stun>().WithAny<Frozen>();
        stunned.Get<Armor>().Value++;
        plain.Get<Armor>().Value++;
        Assert.That(world.Count<Armor>(filter), Is.EqualTo(1));
        frozen.Get<Armor>().Value++;
        var found = new System.Collections.Generic.List<Entity>();
        foreach (var item in world.Query<Armor>(filter)) found.Add(item.Entity);
        Assert.That(found, Is.EquivalentTo(new[] { stunned, frozen }));
    }

    [Test]
    public void DestroyingInsideAChangedPassKeepsTheOtherRows()
    {
        using var world = new World();
        world.AddSystem<BreakChanged>();
        var entities = new Entity[10_000];
        world.CreateEntities(entities, new Armor(1));
        world.Update(); // every row is new, none is broken
        Assert.That(world.Count<Armor>(), Is.EqualTo(entities.Length));

        // Every 97th row written: the pass destroys them (tombstones) and skips the words nobody wrote.
        int written = 0;
        for (int i = 0; i < entities.Length; i += 97, written++) entities[i].Get<Armor>().Value++;
        world.Update();
        Assert.That(world.Count<Armor>(), Is.EqualTo(entities.Length - written));
        for (int i = 0; i < entities.Length; i++) Assert.That(entities[i].IsAlive, Is.EqualTo(i % 97 != 0));
        world.Update();
        Assert.That(world.Count<Armor>(), Is.EqualTo(entities.Length - written));
    }

    [Test]
    public void ASystemsOwnWritesAreNotChangesForItsNextRun()
    {
        using var world = new World();
        world.AddSystem<Regen>();
        var e = world.CreateEntity(new Armor(1), new Seen());
        world.Update();
        world.Update();
        Assert.That(SeenCount(e), Is.EqualTo(1));
    }

    [Test]
    public void WritesOrderedAfterTheWatchAreSeenNextUpdate()
    {
        using var world = new World();
        world.AddSystem<ArmorWatch>();
        world.AddSystem<LateWear>();
        var e = world.CreateEntity(new Armor(5), new Seen(), new Stun(1));
        world.Update();               // new row: seen; then LateWear writes it
        Assert.That(SeenCount(e), Is.EqualTo(1));
        world.Update();               // LateWear's write of the previous update
        Assert.That(SeenCount(e), Is.EqualTo(2));
    }

    [Test]
    public void WritesBetweenRunsOfAnIntervalSystemAreSeenOnce()
    {
        using var world = new World();
        world.AddSystem<SlowArmorWatch>();
        var e = world.CreateEntity(new Armor(5), new Seen());
        world.Update(); // update 1: skipped
        world.Update(); // update 2: runs, the row is new
        Assert.That(SeenCount(e), Is.EqualTo(1));
        e.Get<Armor>().Value++;
        world.Update(); // update 3: skipped
        e.Get<Armor>().Value++;
        world.Update(); // update 4: both writes, one change
        world.Update();
        world.Update(); // update 6: nothing new
        Assert.That(SeenCount(e), Is.EqualTo(2));
    }

    [Test]
    public void ParallelWritersAndWatchersAgreeWithSequentialRuns()
    {
        int[] Run(bool parallel)
        {
            using var world = new World(new WorldOptions { Parallel = parallel });
            world.AddSystem<Wear>();
            world.AddSystem<ArmorWatch>();
            world.AddSystem<Inspect>();
            var entities = new Entity[1_000];
            world.CreateEntities(entities, new Armor(9), new Seen(), new Health(0));
            for (int i = 0; i < entities.Length; i += 3) entities[i].Add<Stun>();
            for (int u = 0; u < 4; u++) world.Update();
            var seen = new int[entities.Length];
            for (int i = 0; i < seen.Length; i++) seen[i] = SeenCount(entities[i]);
            return seen;
        }

        Assert.That(Run(parallel: true), Is.EqualTo(Run(parallel: false)));
    }
}
