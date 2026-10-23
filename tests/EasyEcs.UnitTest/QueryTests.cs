using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public class QueryTests
{
    private World _world;

    [SetUp]
    public void SetUp()
    {
        _world = new World();
        // 10 movers (2 frozen, 1 dead), 5 static entities, 3 empty ones.
        for (int i = 0; i < 10; i++)
        {
            var e = _world.CreateEntity(new Position(i, 0), new Velocity(1, 2));
            if (i < 2) e.Set(new Frozen());
            if (i == 9) e.Set(new Dead());
        }

        for (int i = 0; i < 5; i++) _world.CreateEntity(new Position(100 + i, 0));
        for (int i = 0; i < 3; i++) _world.CreateEntity();
    }

    [TearDown]
    public void TearDown() => _world.Dispose();

    private static readonly QueryFilter Active = QueryFilter.Create().Without<Frozen>().Without<Dead>();

    [Test]
    public void ManyFiltersOfOneSignatureEachMatchTheirRowsAndSeeNewTables()
    {
        // More filters than a signature remembers: the rest take the query cache, with the same results.
        var world = new World();
        var filters = new[]
        {
            QueryFilter.Create(), QueryFilter.Create().With<C1>(), QueryFilter.Create().With<C2>(),
            QueryFilter.Create().With<C3>(), QueryFilter.Create().With<C4>(), QueryFilter.Create().With<C5>(),
            QueryFilter.Create().Without<C1>(),
        };
        var tags = new Action<Entity>[] { e => e.Set(new C1()), e => e.Set(new C2()), e => e.Set(new C3()),
            e => e.Set(new C4()), e => e.Set(new C5()) };
        for (int round = 0; round < 2; round++)
        {
            // Second round: every table is new to filters that were remembered in the first one.
            for (int i = 0; i < tags.Length; i++) tags[i](world.CreateEntity(new Position(round, i)));
            int total = 5 * (round + 1);
            for (int f = 0; f < filters.Length; f++)
            {
                int expected = f == 0 ? total : f == filters.Length - 1 ? total - (round + 1) : round + 1;
                Assert.That(world.Count<Position>(filters[f]), Is.EqualTo(expected), $"filter {f}, round {round}");
                int visited = 0;
#pragma warning disable EECS032 // the rows the query visits, counted one by one to check Count against them
                foreach (var _ in world.Query<Position>(filters[f])) visited++;
#pragma warning restore EECS032
                Assert.That(visited, Is.EqualTo(expected));
            }
        }

        world.Dispose();
    }

    [Test]
    public void CollectIdsOfWholeWordsMatchesTheQuery()
    {
        // Tables of many 64-row words whose rows pass or not by presence bits and change ticks: a word with room for
        // all of its rows is compacted at once, the last words of a bounded batch one row at a time.
        using var world = new World();
        var all = new Entity[3000];
        world.CreateEntities(all, new Health(1), new Armor(1));
        var random = new Random(11);
        for (int i = 0; i < all.Length; i++)
        {
            if (random.Next(2) == 0) all[i].Remove<Health>();
            if (i % 9 == 0) all[i].Set(new Stun(i));
        }

        ulong since = world.ChangeTick + 1;
        world.Update();
        for (int i = 0; i < all.Length; i += 1 + random.Next(4)) all[i].WriteArmor() = new Armor(2);
        var filters = new[] { default, QueryFilter.Create().Changed<Armor>(since), QueryFilter.Create().Without<Stun>() };
        foreach (var filter in filters)
        {
            var expected = new List<EntityId>();
            foreach (var item in world.Read<Health, Armor>(filter)) expected.Add(item.Entity);
            foreach (int size in new[] { expected.Count + 64, expected.Count, 64, 65, 127, 1 })
            {
                var batch = new EntityId[size];
                int count = world.CollectIds<Health, Armor>(batch, filter);
                Assert.That(count, Is.EqualTo(Math.Min(size, expected.Count)));
                Assert.That(batch[..count], Is.EqualTo(expected.GetRange(0, count).ToArray()));
            }
        }
    }

    [Test]
    public void CollectIdsTakesABoundedBatchInQueryOrder()
    {
        using var world = new World();
        var all = new Entity[200];
        world.CreateEntities(all, new Health(1));
        for (int i = 0; i < all.Length; i++)
        {
            if (i % 3 == 0) all[i].Set(new Stun(i));               // a second table
            if (i % 5 == 0) all[i].Set(new Frozen());              // a third
            if (i % 7 == 0) all[i].Remove<Health>();               // a presence bit: rows to test
        }

        all[11].Destroy();
        var awake = QueryFilter.Create().Without<Frozen>();
        var expected = new List<EntityId>();
        foreach (var item in world.Read<Health>(awake)) expected.Add(item.Entity);

        Span<EntityId> batch = stackalloc EntityId[expected.Count + 10];
        int count = world.CollectIds<Health>(batch, awake);      // room for every one: all of them, in query order
        Assert.That(batch[..count].ToArray(), Is.EqualTo(expected.ToArray()));

        for (int size = 0; size < 70; size += 13)                   // a bounded batch: the first ones
        {
            var some = new EntityId[size];
            Assert.That(world.CollectIds<Health>(some, awake), Is.EqualTo(size));
            Assert.That(some, Is.EqualTo(expected.GetRange(0, size).ToArray()));
        }

        Span<EntityId> two = stackalloc EntityId[2];                // two components, and a table every row of which passes
        Assert.That(world.CollectIds<Health, Stun>(two), Is.EqualTo(2));
        Assert.That(world.CollectIds<Marked>(two), Is.Zero);        // no entity
        Assert.That(world.DestroyEntities(batch[..count]), Is.EqualTo(count)); // the use it is for: a batch of work
        Assert.That(world.Count<Health>(awake), Is.Zero);
    }

    [Test]
    public void CollectIdsDoesNotAllocate()
    {
        using var world = new World();
        world.CreateEntities(new Entity[300], new Health(1));
        var filter = QueryFilter.Create().With<Health>();
        var into = new EntityId[100];
        world.CollectIds<Health>(into, filter);
        world.CollectIds<Health>(into);                                       // each query's match is cached by its first call
        long before = GC.GetAllocatedBytesForCurrentThread();
        int count = world.CollectIds<Health>(into, filter) + world.CollectIds<Health>(into);
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
        Assert.That(count, Is.EqualTo(200));
    }

    [Test]
    public void WithAnyNeedsOneOfTheComponents()
    {
        var halted = QueryFilter.Create().WithAny<Frozen>().WithAny<Dead>();
        Assert.That(_world.Count<Position>(halted), Is.EqualTo(3));
        Assert.That(_world.Count<Position>(QueryFilter.Create().WithAny<Health>()), Is.Zero);

        // Removing the component keeps the row (a presence bit): the filter tests the bits.
        int removed = 0;
        foreach (var item in _world.Query<Position>(QueryFilter.Create().With<Frozen>()))
            if (removed++ == 0) item.Entity.Remove<Frozen>();
        Assert.That(_world.Count<Position>(halted), Is.EqualTo(2));
        Assert.That(_world.Any<Position>(QueryFilter.Create().WithAny<Frozen>()), Is.True);

        int visited = 0;
#pragma warning disable EECS032 // the rows the query visits, counted one by one to check the filter's presence test
        foreach (var _ in _world.Query<Position>(halted)) visited++;
#pragma warning restore EECS032
        Assert.That(visited, Is.EqualTo(2));
    }

    [Test]
    public void CountAndAnyMatchArchetypes()
    {
        Assert.That(_world.Count<Position>(), Is.EqualTo(15));
        Assert.That(_world.Count<Position, Velocity>(), Is.EqualTo(10));
        Assert.That(_world.Count<Position, Velocity>(Active), Is.EqualTo(7));
        Assert.That(_world.Count<Position>(QueryFilter.Create().With<Frozen>()), Is.EqualTo(2));
        Assert.That(_world.Any<Health>(), Is.False);
        Assert.That(_world.Any<Velocity>(Active), Is.True);
    }

    [Test]
    public void QueryYieldsEntitiesAndWritableRefs()
    {
        foreach (var item in _world.Query<Position, Velocity>(Active))
            item.Component1.X += item.Component2.X;

        var moved = new List<float>();
        foreach (var item in _world.Query<Position>())
            if (item.Entity.Has<Velocity>()) moved.Add(item.Component1.X);

        moved.Sort();
        Assert.That(moved, Is.EqualTo(new float[] { 0, 1, 3, 4, 5, 6, 7, 8, 9, 9 }));
        Assert.That(_world.Count<Position, Velocity>(Active), Is.EqualTo(7));
        Assert.That(_world.Any<Health>(), Is.False);
    }

    private struct Integrate : IForEach<Position, Velocity>
    {
        public int Visited;

        public void Execute(ref Position p, ref Velocity v)
        {
            p.Y += v.Y;
            Visited++;
        }
    }

    [Test]
    public void ForEachRunsAStructJobOverMatches()
    {
        var job = new Integrate();
        _world.ForEach<Position, Velocity, Integrate>(ref job, Active);
        Assert.That(job.Visited, Is.EqualTo(7));
        Assert.That(_world.Count<Position>(), Is.EqualTo(15));
    }

    private struct KillAndSpawn(World world) : IForEachEntity<Velocity>
    {
        public int Visited;

        public void Execute(Entity e, ref Velocity v)
        {
            Visited++;
            e.Set(new Dead());                                   // moves the entity out of the matching archetype
            world.CreateEntity(new Position(), new Velocity());  // appended after the pass started: not visited
            if (Visited > 1000) Assert.Fail("iteration did not terminate");
        }
    }

    [Test]
    public void StructuralChangesDuringIterationAreSafe()
    {
        var job = new KillAndSpawn(_world);
        _world.ForEachEntity<Velocity, KillAndSpawn>(ref job, Active);

        Assert.That(job.Visited, Is.EqualTo(7)); // entities created during the pass are not visited
        Assert.That(_world.Count<Velocity>(QueryFilter.Create().With<Dead>()), Is.EqualTo(job.Visited + 1));
    }

    [Test]
    public void RemovalOutsidePassesKeepsTablesDense()
    {
        var spawned = new List<Entity>();
        for (int i = 0; i < 50; i++) spawned.Add(_world.CreateEntity(new Health(i)));
        for (int i = 0; i < 50; i += 2) spawned[i].Destroy(); // the last row fills each hole at once

        Assert.That(_world.RowStats().HoleRatio, Is.Zero);
        for (int i = 1; i < 50; i += 2) Assert.That(spawned[i].Get<Health>().Value, Is.EqualTo(i));
        Assert.That(_world.Count<Health>(), Is.EqualTo(25));
    }

    [Test]
    public void TombstonesLeftByAPassAreClosedWhenItEnds()
    {
        using var world = new World();
        for (int i = 0; i < 1_000; i++) world.CreateEntity(new Health(i));
        world.AddSystem<FreezeHalf>();
        for (int i = 0; i < 20; i++)
        {
            world.Update(); // 500 entities change archetype inside the pass: tombstones until it ends
            Assert.That(world.RowStats(), Is.EqualTo((1_000, 1_000, 0f)));
        }

        int visited = 0;
        foreach (var item in world.Query<Health>())
        {
            visited++;
            Assert.That(item.Component1.Value, Is.EqualTo(item.Entity.Id.Index), "data follows its entity");
        }

        Assert.That(visited, Is.EqualTo(1_000));
    }

    [Test]
    public void AStablePassInsideAStructuralOneSkipsItsTombstones()
    {
        using var world = new World();
        var entities = new Entity[300];
        world.CreateEntities(entities, new Health(1), new Frozen());
        for (int i = 0; i < entities.Length; i += 2) entities[i].Remove<Frozen>(); // rows to test: every word mixed
        var outer = new DestroyThenCount();
        world.ForEachEntity<Health, DestroyThenCount>(ref outer);
        // At the last row, every third row is a tombstone; of the rest, the even ones lack Frozen.
        Assert.That(outer.Counted, Is.EqualTo(100));
    }

    private static readonly QueryFilter NotFrozen = QueryFilter.Create().Without<Frozen>();

    private struct DestroyThenCount : IForEachEntity<Health>
    {
        public int Counted;

        public void Execute(Entity entity, ref Health health)
        {
            if (entity.Id.Index % 3 == 0) entity.Destroy();
            if (entity.Id.Index != 299) return;
            var count = new CountRows();
            entity.World.ForEach<Health, CountRows>(ref count, NotFrozen);
            Counted = count.Rows;
        }
    }

    private struct CountRows : IForEach<Health>, INoStructuralChanges
    {
        public int Rows;
        public static int WriteMask => 0;
        public void Execute(ref Health health) => Rows += health.Value;
    }

    [EcsSystem]
    internal ref struct FreezeHalf
    {
        [ForEach]
        private static void Toggle(Entity entity, in Health health)
        {
            if (health.Value % 2 != 0) return;
            if (entity.Has<Frozen>()) entity.Remove<Frozen>();
            else entity.Set(new Frozen());
        }
    }

    private struct DestroyThenFail : IForEachEntity<Health>
    {
        public int Visited;

        public void Execute(Entity entity, ref Health hp)
        {
            entity.Destroy();                                    // a tombstone: the pass is still open
            if (++Visited == 5) throw new InvalidOperationException("a bug in the job");
        }
    }

    [Test]
    public void AJobThatThrowsStillEndsItsPass()
    {
        using var world = new World();
        for (int i = 0; i < 20; i++) world.CreateEntity(new Health(i));
        var job = new DestroyThenFail();
        Assert.Throws<InvalidOperationException>(() => world.ForEachEntity<Health, DestroyThenFail>(ref job));
        Assert.That(world.RowStats().HoleRatio, Is.Zero);   // the tombstones were closed
        var values = new List<int>();
        foreach (var item in world.Query<Health>()) values.Add(item.Component1.Value);
        values.Sort();
        Assert.That(values, Is.EqualTo(new[] { 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19 }));
        world.CreateEntity(new Health(99)).Destroy();                            // rows are reused again
        Assert.That(world.RowStats().HoleRatio, Is.Zero);
    }

    [Test]
    public void UpdateClosesThePassOfAnAbandonedQuery()
    {
        using var world = new World();
        for (int i = 0; i < 20; i++) world.CreateEntity(new Health(i));
        var walk = world.Query<Health>().GetEnumerator();                        // never disposed
        Assert.That(walk.MoveNext(), Is.True);
        walk.Current.Entity.Destroy();                                            // a tombstone: the pass is open
        Assert.That(world.RowStats().HoleRatio, Is.Positive);
        world.Update();
        Assert.That(world.RowStats().HoleRatio, Is.Zero);
        Assert.That(world.Count<Health>(), Is.EqualTo(19));
    }

    [Test]
    public void AnUpdateDuringALoopLeavesNoPassBehind()
    {
        using var world = new World();
        for (int i = 0; i < 20; i++) world.CreateEntity(new Health(i));
#pragma warning disable EECS051 // the mistake under test
        foreach (var item in world.Query<Health>())
        {
            if (item.Health.Value == 3) item.Entity.Destroy();                   // a tombstone: the pass is open
            if (item.Health.Value == 5) world.Update();                          // compacts and restarts the count
        }
#pragma warning restore EECS051
        Assert.That(world.PassDepth, Is.Zero);
        Assert.DoesNotThrow(() => world.TrimExcess());                           // no pass is counted as open
        Assert.That(world.Count<Health>(), Is.EqualTo(19));
    }

    [Test]
    public void RowsToTestAreVisitedOnceAcrossWordsWhileTheLoopDestroys()
    {
        using var world = new World();
        var all = new Entity[1_000];                                            // 16 words of one table
        world.CreateEntities(all, new Health(0), new Stun(1));
        for (int i = 0; i < all.Length; i += 3) all[i].Remove<Stun>();          // rows without Stun: rows are tested
        var visited = new HashSet<Entity>();
        var destroyed = new HashSet<Entity>();
        foreach (var item in world.Query<Health, Stun>())
        {
            var e = item.Entity;
            Assert.That(e.IsAlive && e.Has<Stun>(), Is.True);
            Assert.That(visited.Add(e), Is.True, "visited twice");
            if (e.Id.Index + 1 < all.Length && all[e.Id.Index + 1].IsAlive && e.Id.Index % 7 == 0)
            {
                destroyed.Add(all[e.Id.Index + 1]);
                all[e.Id.Index + 1].Destroy();                                       // a later row becomes a tombstone
            }
        }

        for (int i = 0; i < all.Length; i++)
            Assert.That(visited.Contains(all[i]), Is.EqualTo(i % 3 != 0 && !destroyed.Contains(all[i])), $"row {i}");
    }
}
