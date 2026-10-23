using System;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public class ChangedTests
{
    /// <summary>Counts, per entity, the runs in which its Armor had changed since the system's previous run.</summary>
    [EcsSystem]
    public ref struct ArmorWatch
    {
        [ForEach, Changed<Armor>]
        private static void Count(ref Seen seen) => seen.Count++;
    }

    /// <summary>Wears down the armor of stunned entities: a writer the watch runs after.</summary>
    [EcsSystem]
    public ref struct Wear
    {
        [ForEach, With<Stun>]
        private static void Apply(ref Armor armor) => armor = armor with { Value = armor.Value - 1 };
    }

    /// <summary>Reads armor only: an <c>in</c> parameter is not a write.</summary>
    [EcsSystem]
    public ref struct Inspect
    {
        [ForEach]
        private static void Read(in Armor armor, ref Health health) => health = new Health(armor.Value);
    }

    /// <summary>The batch form: runs of changed rows as spans.</summary>
    [EcsSystem]
    public ref struct ArmorWatchBatch
    {
        [ForEach, Changed<Armor>]
        private static void Count(System.Span<Seen> seen)
        {
            for (int i = 0; i < seen.Length; i++) seen[i].Count++;
        }
    }

    /// <summary>The parallel form: chunks of rows on several threads, changed rows only.</summary>
    [EcsSystem]
    public ref struct ArmorWatchParallel
    {
        [ForEach(Parallel = true), Changed<Armor>]
        private static void Count(ref Seen seen) => seen.Count++;
    }

    /// <summary>Changed combined with other filters, and with a second tracked component (both must have changed).</summary>
    [EcsSystem]
    public ref struct LivingArmorAndShieldWatch
    {
        [ForEach, Changed<Armor>, Changed<Shield>, Without<Dead>]
        private static void Count(ref Seen seen) => seen.Count++;
    }

    /// <summary>No component parameter: the Changed component is the queried one.</summary>
    [EcsSystem]
    public ref struct ArmorEntityWatch
    {
        [ForEach, Changed<Armor>]
#pragma warning disable EECS027 // no component parameter is what this system covers
        private static void Count(Entity entity) => entity.Get<Seen>().Count++;
#pragma warning restore EECS027
    }

    /// <summary>Destroys entities whose armor changed: structural changes inside a Changed pass.</summary>
    [EcsSystem]
    public ref struct BreakChanged
    {
        [ForEach, Changed<Armor>]
        private static void Break(Entity entity, in Armor armor)
        {
            if (armor.Value > 1) entity.Destroy();
        }
    }

    /// <summary>A watch that also writes what it watches: its own writes are not changes for its next run.</summary>
    [EcsSystem]
    public ref struct Regen
    {
        [ForEach, Changed<Armor>]
#pragma warning disable EECS026 // the write access is the point: it stamps the rows this system then must not see
        private static void Apply(ref Armor armor, ref Seen seen) => seen.Count++;
#pragma warning restore EECS026
    }

    /// <summary>A writer ordered after the watch.</summary>
    [EcsSystem, UpdateAfter<ArmorWatch>]
    public ref struct LateWear
    {
        [ForEach, With<Stun>]
        private static void Apply(ref Armor armor) => armor = armor with { Value = armor.Value - 1 };
    }

    /// <summary>A watch that runs every other update.</summary>
    [EcsSystem, UpdateInterval(2)]
    public ref struct SlowArmorWatch
    {
        [ForEach, Changed<Armor>]
        private static void Count(ref Seen seen) => seen.Count++;
    }

    /// <summary>Caps armor at 3 through a Mut: only the rows whose value changes are written (and stamped).</summary>
    [EcsSystem]
    public ref struct CapArmor
    {
        [ForEach]
        private static void Cap(Mut<Armor> armor) => armor.SetIfChanged(new Armor(Math.Min(armor.Value.Value, 3)));
    }

    /// <summary>The same cap through a ref: every visited row is stamped, changed or not.</summary>
    [EcsSystem]
    public ref struct CapArmorByRef
    {
        [ForEach]
        private static void Cap(ref Armor armor) => armor = new Armor(Math.Min(armor.Value, 3));
    }

    /// <summary>The cap on several threads.</summary>
    [EcsSystem]
    public ref struct CapArmorParallel
    {
        [ForEach(Parallel = true)]
        private static void Cap(Mut<Armor> armor) => armor.SetIfChanged(new Armor(Math.Min(armor.Value.Value, 3)));
    }

    /// <summary>The cap over the unstunned rows only: runs that start inside a table, and inside a 64-row word.</summary>
    [EcsSystem]
    public ref struct CapArmorUnstunned
    {
        [ForEach, Without<Stun>]
        private static void Cap(Mut<Armor> armor) => armor.SetIfChanged(new Armor(Math.Min(armor.Value.Value, 3)));
    }

    /// <summary>Reads armor through a Mut and writes the stunned rows' with Write().</summary>
    [EcsSystem]
    public ref struct PeekAndHit
    {
        [ForEach]
        private static void Apply(Mut<Armor> armor, ref Health health, Maybe<Stun> stun)
        {
            health = new Health(armor.Value.Value);
            if (stun.HasValue) armor.Write().Value--;
        }
    }

    /// <summary>A Mut in a method that changes structure: the shield moves the row, then the armor is written.</summary>
    [EcsSystem]
    public ref struct ShieldUp
    {
        [ForEach]
        private static void Raise(Entity entity, Mut<Armor> armor)
        {
            if (entity.Has<Shield>()) return;
            entity.Set(new Shield(1));
            armor.Write() = new Armor(9);
        }
    }

    /// <summary>A third tracked component, for the ChangedAny groups.</summary>
    public record struct Ward(int Value) : IComponent, ITracked;

    /// <summary>Visits the entities whose armor or shield changed (or both).</summary>
    [EcsSystem]
    public ref struct ArmorOrShieldWatch
    {
        [ForEach, ChangedAny<Armor, Shield>]
        private static void Count(ref Seen seen) => seen.Count++;
    }

    /// <summary>ChangedAny combined with Changed and Without: armor changed AND (shield OR ward changed) AND not dead.</summary>
    [EcsSystem]
    public ref struct MixedWatch
    {
        [ForEach, Changed<Armor>, ChangedAny<Shield, Ward>, Without<Dead>]
        private static void Count(ref Seen seen) => seen.Count++;
    }

    /// <summary>The parallel form of <see cref="ArmorOrShieldWatch"/>.</summary>
    [EcsSystem]
    public ref struct ArmorOrShieldWatchParallel
    {
        [ForEach(Parallel = true), ChangedAny<Armor, Shield>]
        private static void Count(ref Seen seen) => seen.Count++;
    }

    /// <summary>The batch form, two attributes joining one group.</summary>
    [EcsSystem]
    public ref struct ArmorOrShieldWatchBatch
    {
        [ForEach, ChangedAny<Armor, Shield>, ChangedAny<Shield, Ward>]
        private static void Count(System.Span<Seen> seen)
        {
            for (int i = 0; i < seen.Length; i++) seen[i].Count++;
        }
    }

#pragma warning disable EECS037 // the default WriteMask (every component) stamps the rows: what these jobs stand for
    /// <summary>A struct job that writes armor through a ref.</summary>
    private struct Polish : IForEach<Armor>
    {
        public void Execute(ref Armor armor) { }
    }

    /// <summary>A batch job that writes armor through a span.</summary>
    private struct PolishBatch : IForEachBatch<Armor>
    {
        public void Execute(EntitySpan entities, System.Span<Armor> armor) { }
    }
#pragma warning restore EECS037

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
    public void ASystemAddedForChangesFromNowSkipsWhatWasWrittenBefore()
    {
        using var world = new World();
        var a = world.CreateEntity(new Armor(5), new Seen());
        var b = world.CreateEntity(new Armor(5), new Seen());
        world.Update();
        b.Set(new Armor(4));                                       // written at the tick the system is added in
        world.AddSystem<ArmorWatch>(changesFromNow: true);

        world.Update(); // first run: nothing written since it was added
        Assert.That((SeenCount(a), SeenCount(b)), Is.EqualTo((0, 0)));

        a.Set(new Armor(3));                                       // right after: seen
        world.Update();
        Assert.That((SeenCount(a), SeenCount(b)), Is.EqualTo((1, 0)));
    }

    [Test]
    public void WriteXStampsAndTheReadPropertyDoesNot()
    {
        using var world = new World();
        world.AddSystem<ArmorWatch>();
        var e = world.CreateEntity(new Armor(5), new Seen());
        world.Update();

        Assert.That(e.Armor.Value, Is.EqualTo(5));   // ref readonly: no stamp
        world.Update();
        Assert.That(SeenCount(e), Is.EqualTo(1));

        e.WriteArmor().Value = 3;
        world.Update();
        Assert.That((SeenCount(e), e.Armor.Value), Is.EqualTo((2, 3)));
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
        var changed = QueryFilter.Create().Changed<Armor>(mark);
        Assert.That(world.Count<Armor>(changed), Is.Zero);

        entities[3].Get<Armor>() = new Armor(2);
        entities[70].Set(new Armor(2));
        foreach (var _ in world.Read<Armor>(QueryFilter.Create().With<Stun>())) { } // matches nothing: no stamp
        Assert.That(world.Count<Armor>(changed), Is.EqualTo(2));
        Assert.That(world.Any<Armor>(changed), Is.True);

        int visited = 0;
        foreach (var item in world.Read<Armor>(changed)) visited += item.Component1.Value;
        Assert.That(visited, Is.EqualTo(4));

        // Tick 0 selects every row having T; one filter has one tick; ChangedSince moves it.
        Assert.That(world.Count<Armor>(QueryFilter.Create().Changed<Armor>(0)), Is.EqualTo(100));
        Assert.Throws<ArgumentException>(() => QueryFilter.Create().Changed<Armor>(mark).Changed<Shield>(mark + 1));
        Assert.That(world.Count<Armor>(QueryFilter.Create().Changed<Armor>(0).Since(mark)), Is.EqualTo(world.Count<Armor>(changed)));
        Assert.That(world.DestroyAll<Armor>(changed), Is.EqualTo(2));
    }

    [Test]
    public void ReadStampsNothingAndQueryStampsWhatItVisits()
    {
        using var world = new World();
        var entities = new Entity[10];
        world.CreateEntities(entities, new Armor(1), new Seen(), new DynamicBuffer<DynamicBufferTests.Loot>());
        world.AddSystem<ArmorWatch>();
        world.Update();                                                       // the watch sees the creation once
        world.Update();
        ulong mark = world.ChangeTick;
        var changed = QueryFilter.Create().Changed<Armor>(mark);

        int sum = 0;
        foreach (var item in world.Read<Armor>()) sum += item.Armor.Value;
        foreach (var item in world.Read<Armor, DynamicBuffer<DynamicBufferTests.Loot>>(QueryFilter.Create().With<Seen>())) sum += item.Component2.Count;
        Assert.That(sum, Is.EqualTo(10));
        Assert.That(world.Count<Armor>(changed), Is.Zero);                    // reading wrote nothing
        Assert.That(world.Count<DynamicBuffer<DynamicBufferTests.Loot>>(QueryFilter.Create().Changed<DynamicBuffer<DynamicBufferTests.Loot>>(mark)), Is.Zero);
        world.Update();
        Assert.That(entities[0].Seen.Count, Is.EqualTo(1));                   // nor did the watch see a change

#pragma warning disable EECS046 // the stamping of a query that only reads is what this test covers
        foreach (var item in world.Query<Armor>()) sum += item.Component1.Value;
#pragma warning restore EECS046
        Assert.That(world.Count<Armor>(changed), Is.EqualTo(10));             // a query's items hand out refs: written
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
        var changed = QueryFilter.Create().Changed<Armor>(mark);
        Assert.That(world.Count<Armor>(changed), Is.EqualTo(1));

        e.Remove<Armor>();
        Assert.That(world.Count<Armor>(changed), Is.Zero);
        e.Set(new Armor()); // adding is a write
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
        var changed = QueryFilter.Create().Changed<Armor>(mark);
        Assert.That(world.Count<Armor>(changed), Is.EqualTo(1));
        foreach (var item in world.Read<Armor>(changed)) Assert.That(item.Entity, Is.EqualTo(entities[199]));
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
        var changed = QueryFilter.Create().Changed<Armor>(mark);

        // Reads: a copy, a read-only ref, a presence check, a missing component, a query over another component.
        entities[0].TryGet<Armor>(out _);
        Assert.That(entities[0].Read<Armor>().Value, Is.EqualTo(1));
        _ = entities[0].Has<Armor>();
        entities[0].TryGetRef<Position>(out bool found);
        Assert.That(found, Is.False);
        foreach (var _ in world.Query<Health>()) { }
        Assert.That(world.Count<Armor>(changed), Is.Zero);

        entities[1].GetOrAdd<Armor>();
        Assert.That(world.Count<Armor>(changed), Is.EqualTo(1));
        entities[2].TryGetRef<Armor>(out found).Value = 7;
        Assert.That(found, Is.True);
        Assert.That(entities[2].Read<Armor>().Value, Is.EqualTo(7));
        Assert.That(world.Count<Armor>(changed), Is.EqualTo(2));
        var spawned = world.CreateEntity(new Armor(2));
        var batch = new Entity[3];
        world.CreateEntities(batch, new Armor(3));
        Assert.That(world.Count<Armor>(changed), Is.EqualTo(6));

        var polish = new Polish();
        world.ForEach<Armor, Polish>(ref polish);
        Assert.That(world.Count<Armor>(changed), Is.EqualTo(world.Count<Armor>()));

        world.Update();
        mark = world.ChangeTick;
        changed = QueryFilter.Create().Changed<Armor>(mark);
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
        var changed = QueryFilter.Create().Changed<Armor>(mark);
        Assert.That(world.Count<Armor>(changed), Is.Zero);

        entities[15_000].Get<Armor>().Value++; // one row in block 3
        entities[19_999].Get<Armor>().Value++; // the last row, in block 4
        Assert.That(world.Count<Armor>(changed), Is.EqualTo(2));

        entities[0].Destroy(); // the last row moves into row 0 (block 0, not written since the mark): its summaries go with it
        var found = new System.Collections.Generic.List<Entity>();
        foreach (var item in world.Read<Armor>(changed)) found.Add(item.Entity);
        Assert.That(found, Is.EquivalentTo(new[] { entities[15_000], entities[19_999] }));
    }

    [Test]
    public void ARowMovedInWithAnOlderTickIsFoundBesideNewerWords()
    {
        // A block whose latest word is newer than a row that moves in later: the moved row's word keeps its own tick.
        using var world = new World();
        var plain = new Entity[200];
        var frozen = new Entity[200]; // words 0-3 of the frozen table, one block
        world.CreateEntities(plain, new Armor(1), new Seen());
        world.CreateEntities(frozen, new Armor(1), new Seen(), new Frozen());
        world.Update();
        plain[5].Get<Armor>().Value++;
        ulong older = world.ChangeTick;
        world.Update();
        frozen[0].Get<Armor>().Value++; // the block's latest, newer than the moving row's tick
        world.Update();
        plain[5].Set(new Frozen()); // row 200 (word 3) of the frozen table, with its older tick

        var found = new System.Collections.Generic.HashSet<Entity>();
        foreach (var item in world.Query<Seen>(QueryFilter.Create().With<Frozen>().Changed<Armor>(older))) found.Add(item.Entity);
        Assert.That(found, Is.EquivalentTo(new[] { frozen[0], plain[5] }));
    }

    [TestCase(600, 40)] // one block: rows of different ticks share words
    [TestCase(10_000, 12)] // three blocks of 4,096 rows: sparse writes, blocks whose words differ in their latest ticks
    public void ChangedFiltersMatchTheLastWriteOfEveryRowThroughMovesAndDestroys(int entities, int rounds)
    {
        // Rows of different ticks share words, older rows move into words written later, rows leave and fill holes,
        // a system writes runs of rows and single words: for every mark, the filter must select exactly the entities
        // written at or after it.
        using var world = new World(new WorldOptions { InitialEntityCapacity = 16 });
        var random = new System.Random(7);
        var written = new System.Collections.Generic.Dictionary<Entity, ulong>();
        var live = new System.Collections.Generic.List<Entity>();
        for (int i = 0; i < entities; i++)
        {
            // Half start stunned, in a table of their own: the writer covers it in long runs (whole words).
            var e = i % 2 == 0 ? world.CreateEntity(new Armor(i), new Seen(), new Stun(1)) : world.CreateEntity(new Armor(i), new Seen());
            live.Add(e);
            written[e] = world.ChangeTick;
        }

        world.AddSystem<Wear>(); // writes the armor of stunned entities: runs of rows and words of tested rows

        for (int round = 0; round < rounds; round++)
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
                foreach (var item in world.Query<Seen>(QueryFilter.Create().Changed<Armor>(mark)))
                    found.Add(item.Entity);
                Assert.That(found.SetEquals(expected), Is.True, $"round {round}, mark {mark}");
                Assert.That(world.Count<Seen>(QueryFilter.Create().Changed<Armor>(mark)), Is.EqualTo(expected.Count));
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
        var sinceBatch = QueryFilter.Create().Changed<Armor>(batch);
        var afterBatch = QueryFilter.Create().Changed<Armor>(batch + 1);
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
            var since = QueryFilter.Create().Changed<Armor>(mark);
            Assert.That(world.Count<Seen>(since), Is.EqualTo(300));
            world.Update();
            entities[200].Get<Armor>().Value++;
            Assert.That(world.Count<Seen>(QueryFilter.Create().Changed<Armor>(world.ChangeTick)), Is.EqualTo(1));
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
#pragma warning disable EECS051 // a tick inside the loop is what this checks
                world.Update(); // a new tick in the middle of a word
#pragma warning restore EECS051
                tick = world.ChangeTick;
            }

            item.Component1.Value++;
        }

        // Moving to a row stamps it: rows 11-100 were reached after the tick.
        Assert.That(world.Count<Seen>(QueryFilter.Create().Changed<Armor>(tick)), Is.EqualTo(90));
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
        var filter = QueryFilter.Create().Changed<Armor>(world.ChangeTick).WithAny<Stun>().WithAny<Frozen>();
        stunned.Get<Armor>().Value++;
        plain.Get<Armor>().Value++;
        Assert.That(world.Count<Armor>(filter), Is.EqualTo(1));
        frozen.Get<Armor>().Value++;
        var found = new System.Collections.Generic.List<Entity>();
        foreach (var item in world.Read<Armor>(filter)) found.Add(item.Entity);
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
            using var world = new World(new WorldOptions { ParallelSystems = parallel });
            world.AddSystem<Wear>();
            world.AddSystem<ArmorWatch>();
            world.AddSystem<Inspect>();
            var entities = new Entity[1_000];
            world.CreateEntities(entities, new Armor(9), new Seen(), new Health(0));
            for (int i = 0; i < entities.Length; i += 3) entities[i].Set(new Stun());
            for (int u = 0; u < 4; u++) world.Update();
            var seen = new int[entities.Length];
            for (int i = 0; i < seen.Length; i++) seen[i] = SeenCount(entities[i]);
            return seen;
        }

        Assert.That(Run(parallel: true), Is.EqualTo(Run(parallel: false)));
    }

    [Test]
    public void MutStampsOnlyTheRowsItWrites()
    {
        using var world = new World();
        world.AddSystem<CapArmor>();
        world.AddSystem<ArmorWatch>();
        var high = world.CreateEntity(new Armor(5), new Seen());
        var low = world.CreateEntity(new Armor(2), new Seen());
        world.Update(); // the cap writes high only; the watch's first run sees both
        Assert.That((high.Armor.Value, low.Armor.Value), Is.EqualTo((3, 2)));
        Assert.That((SeenCount(high), SeenCount(low)), Is.EqualTo((1, 1)));

        ulong mark = world.ChangeTick;
        world.Update(); // nothing changes: no row is stamped
        world.Update();
        Assert.That(world.Count<Armor>(QueryFilter.Create().Changed<Armor>(mark)), Is.Zero);
        Assert.That((SeenCount(high), SeenCount(low)), Is.EqualTo((1, 1)));

        low.SetArmor(8);
        world.Update(); // the external write and the cap's: one change for the watch
        Assert.That((low.Armor.Value, SeenCount(high), SeenCount(low)), Is.EqualTo((3, 1, 2)));

        // A ref parameter stamps every row it visits.
        world.RemoveSystem<CapArmor>();
        world.AddSystem<CapArmorByRef>();
        world.Update(); // the watch runs first now, the cap after it
        world.Update();
        Assert.That((SeenCount(high), SeenCount(low)), Is.EqualTo((2, 3)));
    }

    [Test]
    public void MutStampsTheRowsItWritesInRunsThatStartAnywhere()
    {
        using var world = new World();
        world.AddSystem<CapArmorUnstunned>();
        var entities = new Entity[1_000];
        world.CreateEntities(entities, new Armor(1));
        for (int i = 0; i < entities.Length; i += 3) entities[i].Set(new Stun(1)); // presence bits: runs between them
        world.Update();
        for (int i = 0; i < entities.Length; i += 5) entities[i].Get<Armor>() = new Armor(9); // stamped at this tick
        ulong mark = world.ChangeTick + 1; // the update's stage: the cap's writes only
        world.Update(); // the cap writes the unstunned rows of those: each its own row, word and block summaries
        var expected = new System.Collections.Generic.HashSet<Entity>();
        for (int i = 0; i < entities.Length; i += 5)
            if (i % 3 != 0) expected.Add(entities[i]);
        var changed = new System.Collections.Generic.HashSet<Entity>();
        foreach (var item in world.Read<Armor>(QueryFilter.Create().Changed<Armor>(mark))) changed.Add(item.Entity);
        Assert.That(changed, Is.EquivalentTo(expected));
        for (int i = 0; i < entities.Length; i++)
            Assert.That(entities[i].Armor.Value, Is.EqualTo(i % 5 == 0 && i % 3 != 0 ? 3 : i % 5 == 0 ? 9 : 1));
        mark = world.ChangeTick;
        world.Update(); // nothing to cap: nothing stamped
        Assert.That(world.Count<Armor>(QueryFilter.Create().Changed<Armor>(mark)), Is.Zero);
    }

    [Test]
    public void MutReadsDoNotStampAndWritesDo()
    {
        using var world = new World();
        world.AddSystem<PeekAndHit>();
        var calm = world.CreateEntity(new Armor(5), new Health(0));
        var stunned = world.CreateEntity(new Armor(5), new Health(0), new Stun());
        world.Update();
        ulong mark = world.ChangeTick;
        world.Update();
        Assert.That((calm.Health.Value, stunned.Health.Value, stunned.Armor.Value), Is.EqualTo((5, 4, 3)));
        int changed = 0;
        foreach (var item in world.Read<Armor>(QueryFilter.Create().Changed<Armor>(mark)))
        {
            Assert.That(item.Entity, Is.EqualTo(stunned));
            changed++;
        }

        Assert.That(changed, Is.EqualTo(1));
    }

    [Test]
    public void ParallelMutStampsOnlyTheRowsItWrites()
    {
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4 });
        world.AddSystem<CapArmorParallel>();
        var entities = new Entity[20_000];
        world.CreateEntities(entities, new Armor(1), new Seen());
        world.Update();
        ulong mark = world.ChangeTick;
        for (int i = 0; i < entities.Length; i += 7) entities[i].Get<Armor>() = new Armor(9);
        world.Update(); // each changed row only, written from the chunk that holds it
        var changed = QueryFilter.Create().Changed<Armor>(mark);
        Assert.That(world.Count<Armor>(changed), Is.EqualTo((entities.Length + 6) / 7));
        world.Update();
        mark = world.ChangeTick;
        world.Update();
        Assert.That(world.Count<Armor>(QueryFilter.Create().Changed<Armor>(mark)), Is.Zero);
        for (int i = 0; i < entities.Length; i++) Assert.That(entities[i].Armor.Value, Is.EqualTo(i % 7 == 0 ? 3 : 1));
    }

    [Test]
    public void MutWritesAfterAMoveReachTheEntitysNewRow()
    {
        using var world = new World();
        world.AddSystem<ArmorWatch>();
        var bare = world.CreateEntity(new Armor(1), new Seen());
        var shielded = world.CreateEntity(new Armor(1), new Seen(), new Shield(1));
        world.Update(); // the watch's first run
        world.AddSystem<ShieldUp>();
        world.Update(); // the watch runs first; ShieldUp moves bare, then writes its armor
        world.Update();
        Assert.That((bare.Armor.Value, bare.Has<Shield>()), Is.EqualTo((9, true)));
        Assert.That((SeenCount(bare), SeenCount(shielded)), Is.EqualTo((2, 1)));
    }

    [Test]
    public void SetIfChangedWritesOnlyANewValue()
    {
        using var world = new World();
        var e = world.CreateEntity(new Armor(1));
        var bare = world.CreateEntity(new Health(1));
        world.Update();
        ulong mark = world.ChangeTick;
        var changed = QueryFilter.Create().Changed<Armor>(mark);
        Assert.That(e.SetIfChanged(new Armor(1)), Is.False);
        Assert.That(e.SetArmorIfChanged(1), Is.False);
        Assert.That(world.Count<Armor>(changed), Is.Zero);
        Assert.That(e.SetArmorIfChanged(2), Is.True);
        Assert.That((e.Armor.Value, world.Count<Armor>(changed)), Is.EqualTo((2, 1)));
        Assert.That(bare.SetIfChanged(new Armor(0)), Is.True); // missing: added
        Assert.That(world.Count<Armor>(changed), Is.EqualTo(2));
        Assert.That(bare.SetIfChanged(new Health(1)), Is.False); // untracked works too
        e.Destroy();
        Assert.Throws<InvalidOperationException>(() => e.SetIfChanged(new Armor(3)));
    }

    public struct Heading : IComponent, ITracked { public float Angle; }          // compared by its bytes
    public record struct Bearing(float Angle) : IComponent, ITracked;             // compared as floats

    [Test]
    public void SetIfChangedComparesTheBytesOfATypeWithoutEquality()
    {
        using var world = new World();
        var e = world.CreateEntity(new Heading { Angle = 0f }, new Bearing(0f));
        world.Update();
        // -0.0 equals 0.0 as a float, not as bytes: a stamp too many, never a change missed.
        Assert.That(e.SetIfChanged(new Heading { Angle = -0f }), Is.True);
        Assert.That(e.SetIfChanged(new Bearing(-0f)), Is.False);
        Assert.That(e.SetIfChanged(new Bearing(float.NaN)), Is.True);
        Assert.That(e.SetIfChanged(new Bearing(float.NaN)), Is.False);  // a record compares NaN with float.Equals
    }

    [Test]
    public void ChangedAnyVisitsRowsWhereOneOfTheComponentsChanged()
    {
        using var world = new World();
        world.AddSystem<ArmorOrShieldWatch>();
        var armor = world.CreateEntity(new Armor(1), new Shield(1), new Seen());
        var shield = world.CreateEntity(new Armor(1), new Shield(1), new Seen());
        var both = world.CreateEntity(new Armor(1), new Shield(1), new Seen());
        var none = world.CreateEntity(new Armor(1), new Shield(1), new Seen());
        var armorOnly = world.CreateEntity(new Armor(1), new Seen());   // no Shield at all: its armor counts
        var neither = world.CreateEntity(new Health(1), new Seen());    // a table with neither column
        world.Update();
        var all = new[] { armor, shield, both, none, armorOnly, neither };
        Assert.That(Array.ConvertAll(all, SeenCount), Is.EqualTo(new[] { 1, 1, 1, 1, 1, 0 }));

        armor.SetArmor(2);
        shield.SetShield(2);
        both.SetArmor(2);
        both.SetShield(2);
        armorOnly.SetArmor(2);
        world.Update();
        world.Update(); // nothing new
        Assert.That(Array.ConvertAll(all, SeenCount), Is.EqualTo(new[] { 2, 2, 2, 1, 2, 0 }));

        shield.RemoveShield(); // a removal is not a write of a present component
        world.Update();
        Assert.That(SeenCount(shield), Is.EqualTo(2));
    }

    [Test]
    public void ChangedAnyCombinesWithChangedAndOtherFiltersWithAnd()
    {
        using var world = new World();
        world.AddSystem<MixedWatch>();
        var armorAndWard = world.CreateEntity(new Armor(1), new Shield(1), new Ward(1), new Seen());
        var armorAlone = world.CreateEntity(new Armor(1), new Shield(1), new Ward(1), new Seen());
        var shieldAlone = world.CreateEntity(new Armor(1), new Shield(1), new Ward(1), new Seen());
        var dead = world.CreateEntity(new Armor(1), new Shield(1), new Seen(), new Dead());
        world.Update();
        var all = new[] { armorAndWard, armorAlone, shieldAlone, dead };
        Assert.That(Array.ConvertAll(all, SeenCount), Is.EqualTo(new[] { 1, 1, 1, 0 }));

        armorAndWard.SetArmor(2);
        armorAndWard.SetWard(2);
        armorAlone.SetArmor(2);
        shieldAlone.SetShield(2);
        dead.SetArmor(2);
        dead.SetShield(2);
        world.Update();
        Assert.That(Array.ConvertAll(all, SeenCount), Is.EqualTo(new[] { 2, 1, 1, 0 }));
    }

    [Test]
    public void ChangedAnyFormsAgreeAcrossTablesWordsAndBlocks()
    {
        foreach (var form in new[] { 0, 1, 2 })
        {
            using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4 });
            if (form == 0) world.AddSystem<ArmorOrShieldWatch>();
            else if (form == 1) world.AddSystem<ArmorOrShieldWatchParallel>();
            else world.AddSystem<ArmorOrShieldWatchBatch>();
            var entities = new Entity[20_000]; // five blocks of 4,096 rows
            world.CreateEntities(entities, new Armor(1), new Shield(1), new Ward(1), new Seen());
            world.Update();
            // Armor writes in one block, shield writes in another, a word of each, and rows apart.
            int[] armor = [5, 70, 9_000, 9_001], shield = [12_000, 12_063, 19_999, 9_001];
            foreach (int i in armor) entities[i].Get<Armor>().Value++;
            foreach (int i in shield) entities[i].Get<Shield>().Value++;
            entities[15_000].Get<Ward>().Value++; // only the batch form's group has Ward
            world.Update();
            for (int i = 0; i < entities.Length; i++)
            {
                bool changed = Array.IndexOf(armor, i) >= 0 || Array.IndexOf(shield, i) >= 0 || form == 2 && i == 15_000;
                Assert.That(SeenCount(entities[i]), Is.EqualTo(changed ? 2 : 1), $"form {form}, entity {i}");
            }
        }
    }

    [Test]
    public void OneChangedAnyFilterServesClientsAtDifferentTicks()
    {
        using var world = new World(new WorldOptions { InitialEntityCapacity = 4 });
        var entities = new Entity[300];
        world.CreateEntities(entities, new Armor(1), new Shield(1), new Ward(1), new Health(0));
        var dirty = QueryFilter.Create().ChangedAny<Armor, Shield, Ward>(0); // kept once, run per client
        world.Update();
        ulong slow = world.ChangeTick;   // the client that acknowledged long ago
        entities[3].Get<Armor>().Value++;
        entities[100].Get<Ward>().Value++;
        world.Update();
        ulong fast = world.ChangeTick;   // the client that acknowledged just now
        entities[200].Get<Shield>().Value++;
        entities[3].Get<Shield>().Value++;

        int Dirty(ulong since)
        {
            int sum = 0;
            foreach (var item in world.Query<Health>(dirty.Since(since))) // Health: a query item of Armor would be a write of it
                sum += item.Entity.Id.Index + 1;
            return sum;
        }

        int Count(ulong since) => world.Count<Health>(dirty.Since(since));
        Assert.That(Count(slow), Is.EqualTo(3));
        Assert.That(Count(fast), Is.EqualTo(2));
        Assert.That(Dirty(slow), Is.EqualTo(entities[3].Id.Index + entities[100].Id.Index + entities[200].Id.Index + 3));
        Assert.That(Dirty(fast), Is.EqualTo(entities[3].Id.Index + entities[200].Id.Index + 2));
        Assert.That(Count(0), Is.EqualTo(entities.Length));

        // One tick per filter: every change filter of it names the same one.
        Assert.Throws<ArgumentException>(() => QueryFilter.Create().Changed<Armor>(fast).ChangedAny<Shield, Ward>(slow));
        Assert.Throws<ArgumentException>(() => QueryFilter.Create().ChangedAny<Shield, Ward>(slow).Removed<Armor>(fast));
        Assert.That(world.Count<Health>(QueryFilter.Create().ChangedAny<Shield, Ward>(slow).ChangedAny<Armor, Ward>(slow).Since(fast)), Is.EqualTo(2));
        Assert.That(world.Any<Health>(dirty.Since(world.ChangeTick + 1)), Is.False);

        // entity.Run tests one row like a query.
        var job = new Polish2();
        Assert.That(entities[100].Run<Armor, Polish2>(ref job, dirty.Since(slow)), Is.True);
        Assert.That(entities[100].Run<Armor, Polish2>(ref job, dirty.Since(fast)), Is.False);
    }

    private struct Polish2 : IForEachEntity<Armor>
    {
        public void Execute(Entity entity, ref Armor armor) { }

        static int IForEachEntity<Armor>.WriteMask => 0;
    }
}
