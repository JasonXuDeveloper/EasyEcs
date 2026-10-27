using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>A walker on a square map, its tables ordered along a Morton curve of its cell, boxes over (X, Y).</summary>
public partial record struct Walker(float X, float Y) : IComponent
{
    [OrderBy(nameof(X), nameof(Y), Interval = 3, Margin = 0.25f)]
    public readonly uint Cell => OrderTests.Morton(X, Y);
}

/// <summary>A rank ordered by a signed key, every update, without boxes.</summary>
public partial record struct OrderRank([OrderBy(Interval = 1)] int Value) : IComponent;

/// <summary>A depth ordered by a float key (negative ones first).</summary>
public partial record struct OrderDepth([OrderBy(Interval = 1)] float Value) : IComponent;

/// <summary>A deadline ordered by a 64-bit signed key.</summary>
public partial record struct OrderDeadline([OrderBy(Interval = 1)] long Tick) : IComponent;

public enum Tier : short { Low = -2, Mid = 0, High = 3 }

/// <summary>A tier ordered by an enum of short.</summary>
public partial record struct Tiered([OrderBy] Tier Tier) : IComponent;

/// <summary>A swimmer in 3D, boxes over a Vector3.</summary>
public partial struct Swimmer : IComponent
{
    public Vector3 At;

    [OrderBy(nameof(At), Interval = 1, Margin = 1)]
    public readonly ulong Cell => (ulong)OrderTests.Morton(At.X, At.Y) << 16 | (uint)Math.Clamp((int)At.Z, 0, 65535);
}

/// <summary>A mover with no declared order: a world asks for one (OrderRows), as a generated pair method's setup does.</summary>
public partial record struct Mover(float X, float Y) : IComponent;

/// <summary>What the generator writes for a pair method comparing Movers' positions: the Morton code by the cutoff.</summary>
public readonly struct MoverOrder : IRowOrder<Mover>
{
    public static int KeyBytes => 8;
    public static ulong Key(in Mover row, float cell) => RowKeys.Morton(Point(row), 2, cell);
    public static System.Numerics.Vector3 Point(in Mover row) => new(row.X, row.Y, 0);
}

/// <summary>A request for Walker, which [OrderBy] overrides.</summary>
public readonly struct WalkerByX : IRowOrder<Walker>
{
    public static int KeyBytes => 8;
    public static ulong Key(in Walker row, float cell) => RowKeys.Of(row.X);
    public static System.Numerics.Vector3 Point(in Walker row) => new(row.X, 0, 0);
}

public record struct Rank2(int Value) : IComponent;

public readonly struct Rank2Order : IRowOrder<Rank2>
{
    public static int KeyBytes => 4;
    public static ulong Key(in Rank2 row, float cell) => RowKeys.Of(row.Value);
    public static System.Numerics.Vector3 Point(in Rank2 row) => new(row.Value, 0, 0);
}

/// <summary>The team an entity's tables are grouped by (a group move is a move between ordered tables).</summary>
public partial record struct Team([GroupBy] int Id) : IComponent;

/// <summary>
/// Ordered tables ([OrderBy], RowOrder): the sort and its cadence, every column, presence bit, change tick and id
/// following its entity through it, the blocks and their boxes, churn between sorts (adds, removes, moves between
/// tables, group moves, recycled ids), saves and snapshots, parallel passes, the keys of every type, 0 B per update.
/// </summary>
public class OrderTests
{
    /// <summary>The Morton code of a point of [0, 4096)² quantized to 1/16.</summary>
    public static uint Morton(float x, float y) =>
        Spread((uint)Math.Clamp((int)(x * 16), 0, 65535)) | Spread((uint)Math.Clamp((int)(y * 16), 0, 65535)) << 1;

    private static uint Spread(uint v)
    {
        v = (v | v << 8) & 0x00FF00FF;
        v = (v | v << 4) & 0x0F0F0F0F;
        v = (v | v << 2) & 0x33333333;
        v = (v | v << 1) & 0x55555555;
        return v;
    }

    /// <summary>What each entity should hold: the test's own record, checked against the world after every step.</summary>
    private sealed class Expected
    {
        public readonly Dictionary<EntityId, (Walker Walker, Health Health, string Name)> Rows = new();

        public void Check(World world)
        {
            foreach (var (id, row) in Rows)
            {
                var entity = new Entity(world, id);
                Assert.That(entity.IsAlive, Is.True, $"{id}");
                Assert.That(entity.Get<Walker>(), Is.EqualTo(row.Walker), $"{id}");
                Assert.That(entity.Get<Health>(), Is.EqualTo(row.Health), $"{id}");
                Assert.That(entity.Get<Name>().Value, Is.EqualTo(row.Name), $"{id}");
            }

            Assert.That(world.Count<Walker>(), Is.EqualTo(Rows.Count));
        }
    }

    private static Walker RandomWalker(Random random) => new(random.NextSingle() * 1000, random.NextSingle() * 1000);

    /// <summary>Every ordered table of Walker: sorted rows in key order, every row's point inside its block's box.</summary>
    private static void CheckOrdered(World world)
    {
        int tables = 0;
        foreach (var blocks in world.Blocks<Walker>())
        {
            tables++;
            var walkers = blocks.Read<Walker>();
            Assert.That(blocks.SortedRows, Is.LessThanOrEqualTo(blocks.RowCount));
            for (int r = 1; r < blocks.SortedRows; r++)
                if (blocks.Has(r) && blocks.Has(r - 1))
                    Assert.That(walkers[r].Cell, Is.GreaterThanOrEqualTo(walkers[r - 1].Cell), $"row {r}");
            CheckBoxes(blocks);
        }

        Assert.That(tables, Is.GreaterThan(0));
    }

    private static void CheckBoxes(RowBlocks blocks)
    {
        var walkers = blocks.Read<Walker>();
        Assert.That(blocks.Count, Is.EqualTo((blocks.SortedRows + blocks.Size - 1) / blocks.Size));
        for (int b = 0; b < blocks.Count; b++)
            for (int r = blocks.Start(b); r < blocks.End(b); r++)
            {
                if (!blocks.Has(r)) continue;
                Assert.That(walkers[r].X, Is.InRange(blocks.Min(0)[b], blocks.Max(0)[b]), $"block {b}, row {r}");
                Assert.That(walkers[r].Y, Is.InRange(blocks.Min(1)[b], blocks.Max(1)[b]), $"block {b}, row {r}");
            }
    }

    private static Expected Populate(World world, int count, Random random)
    {
        var expected = new Expected();
        world.Reserve<Walker, Health, Name>(count);
        for (int i = 0; i < count; i++)
        {
            var walker = RandomWalker(random);
            var entity = world.CreateEntity(walker, new Health(i), new Name("n" + i));
            expected.Rows[entity.Id] = (walker, new Health(i), "n" + i);
        }

        return expected;
    }

    [Test]
    public void ASortOrdersTheRowsAndEveryColumnFollowsItsEntity()
    {
        using var world = new World();
        var expected = Populate(world, 1000, new Random(1));
        var before = world.Blocks<Walker>().GetEnumerator();
        Assert.That(before.MoveNext() && before.Current.Count == 0, Is.True, "no block before the first sort");

        world.SortRows<Walker>();
        CheckOrdered(world);
        expected.Check(world);
        foreach (var blocks in world.Blocks<Walker>())
        {
            Assert.That(blocks.SortedRows, Is.EqualTo(1000));
            Assert.That(blocks.Margin, Is.EqualTo(0.25f));
            Assert.That(blocks.Axes, Is.EqualTo(2));
            var ids = blocks.Ids;
            for (int r = 0; r < blocks.RowCount; r++) Assert.That(blocks[r].Id, Is.EqualTo(ids[r]));
        }
    }

    [Test]
    public void UpdatesSortOnTheDeclaredCadence()
    {
        using var world = new World();
        var random = new Random(2);
        Populate(world, 500, random);
        bool Sorted()
        {
            foreach (var blocks in world.Blocks<Walker>())
            {
                var walkers = blocks.Read<Walker>();
                for (int r = 1; r < blocks.RowCount; r++)
                    if (walkers[r].Cell < walkers[r - 1].Cell) return false;
            }

            return true;
        }

        void Scramble()
        {
            foreach (var item in world.Query<Walker>()) item.Component1 = RandomWalker(random);
        }

        var seen = new List<bool>();
        for (int update = 1; update <= 7; update++)
        {
            world.Update(); // Interval 3: the first update sorts (a new table), then 4, 7
            seen.Add(Sorted());
            Scramble();
        }

        Assert.That(seen, Is.EqualTo(new[] { true, false, false, true, false, false, true }));
    }

    [Test]
    public void ChurnBetweenSortsKeepsEveryEntityAndTheBoxesCoverTheirRows()
    {
        using var world = new World();
        var random = new Random(3);
        var expected = Populate(world, 2000, random);
        world.SortRows<Walker>();
        var ids = expected.Rows.Keys.ToList();
        for (int round = 0; round < 6; round++)
        {
            // Destroys (swap-removes move rows into blocks), adds (rows past the sorted ones), moves to another table
            // (a tag), group moves (a grouped component's value), recycled ids, and the ordered value itself.
            for (int i = 0; i < 150; i++)
            {
                int at = random.Next(ids.Count);
                new Entity(world, ids[at]).Destroy();
                expected.Rows.Remove(ids[at]);
                ids[at] = ids[^1];
                ids.RemoveAt(ids.Count - 1);
            }

            for (int i = 0; i < 150; i++)
            {
                var walker = RandomWalker(random);
                var entity = world.CreateEntity(walker, new Health(-i), new Name("r" + round + "." + i));
                expected.Rows[entity.Id] = (walker, new Health(-i), "r" + round + "." + i);
                ids.Add(entity.Id);
            }

            for (int i = 0; i < 100; i++)
            {
                var entity = new Entity(world, ids[random.Next(ids.Count)]);
                if (random.Next(2) == 0) entity.Set(new Team(random.Next(3)));
                else if (entity.Has<Frozen>()) entity.Remove<Frozen>();
                else entity.Set(new Frozen());
            }

            for (int i = 0; i < 100; i++)
            {
                var id = ids[random.Next(ids.Count)];
                var walker = RandomWalker(random);
                new Entity(world, id).Set(walker);
                expected.Rows[id] = expected.Rows[id] with { Walker = walker };
            }

            expected.Check(world);
            foreach (var blocks in world.Blocks<Walker>()) // between sorts: the boxes of untouched rows and widened ones
            {
                var walkers = blocks.Read<Walker>();
                for (int b = 0; b < blocks.Count; b++)
                    for (int r = blocks.Start(b); r < blocks.End(b); r++)
                    {
                        var id = blocks.Ids[r];
                        if (!expected.Rows.TryGetValue(id, out var row) || row.Walker != walkers[r]) continue; // moved since the sort
                    }
            }

            world.SortRows<Walker>();
            CheckOrdered(world);
            expected.Check(world);
        }
    }

    [Test]
    public void ARowThatMovesIntoABlockWidensItsBox()
    {
        using var world = new World();
        var random = new Random(4);
        var near = new List<Entity>();
        for (int i = 0; i < 64; i++) near.Add(world.CreateEntity(new Walker(random.NextSingle(), random.NextSingle()), new Health(i), new Name("")));
        world.SortRows<Walker>();
        var far = world.CreateEntity(new Walker(900, 900), new Health(99), new Name("far")); // past the sorted rows
        near[3].Destroy(); // the last row (far) moves into near[3]'s row, inside a block

        foreach (var blocks in world.Blocks<Walker>())
        {
            Assert.That(blocks.SortedRows, Is.EqualTo(64), "the table still has 64 rows: far took near[3]'s");
            int row = Array.IndexOf(blocks.Ids.ToArray(), far.Id);
            Assert.That(row, Is.LessThan(blocks.SortedRows));
            Assert.That(blocks.Max(0)[row / blocks.Size], Is.GreaterThanOrEqualTo(900f));
            CheckBoxes(blocks);
        }
    }

    [Test]
    public void ChangeTicksAndRemovalsFollowTheirRows()
    {
        using var world = new World();
        var random = new Random(5);
        var all = new List<Entity>();
        for (int i = 0; i < 3000; i++) all.Add(world.CreateEntity(RandomWalker(random), new Armor(i)));
        world.SortRows<Walker>();
        world.Update();
        world.Update();
        ulong written = world.ChangeTick;
        var wrote = new HashSet<EntityId>();
        for (int i = 0; i < all.Count; i += 7)
        {
            all[i].Set(new Armor(-i));
            wrote.Add(all[i].Id);
        }

        world.Update();
        ulong removedAt = world.ChangeTick;
        var removed = new HashSet<EntityId>();
        for (int i = 3; i < all.Count; i += 11)
        {
            if (wrote.Contains(all[i].Id)) continue;
            all[i].Remove<Armor>();
            removed.Add(all[i].Id);
        }

        foreach (var item in world.Query<Walker>()) item.Component1 = RandomWalker(random); // every row moves at the sort
        world.SortRows<Walker>();
        CheckOrdered(world);

        var ids = new EntityId[all.Count];
        int found = world.CollectIds<Armor>(ids, QueryFilter.Create().Changed<Armor>(written));
        Assert.That(ids.Take(found).ToHashSet(), Is.EquivalentTo(wrote));
        found = world.CollectIds<Walker>(ids, QueryFilter.Create().Removed<Armor>(removedAt));
        Assert.That(ids.Take(found).ToHashSet(), Is.EquivalentTo(removed));
        found = world.CollectIds<Armor>(ids, QueryFilter.Create().Changed<Armor>(removedAt + 1));
        Assert.That(found, Is.Zero, "nothing written since");
        foreach (var entity in all)
            Assert.That(entity.Has<Armor>(), Is.EqualTo(!removed.Contains(entity.Id)));
    }

    [Test]
    public void RecycledIdsAndStaleHandlesSurviveASort()
    {
        using var world = new World();
        var random = new Random(6);
        var first = new List<Entity>();
        for (int i = 0; i < 300; i++) first.Add(world.CreateEntity(RandomWalker(random), new Health(i), new Name("a")));
        foreach (var entity in first.Take(100)) entity.Destroy();
        var second = new List<Entity>();
        for (int i = 0; i < 100; i++) second.Add(world.CreateEntity(RandomWalker(random), new Health(1000 + i), new Name("b")));
        world.SortRows<Walker>();
        foreach (var entity in first.Take(100)) Assert.That(entity.IsAlive, Is.False);
        for (int i = 0; i < 100; i++) Assert.That(second[i].Get<Health>().Value, Is.EqualTo(1000 + i));
        for (int i = 100; i < 300; i++) Assert.That(first[i].Get<Health>().Value, Is.EqualTo(i));
        Assert.That(second.Select(e => e.Id.Index).Intersect(first.Take(100).Select(e => e.Id.Index)), Is.Not.Empty, "ids were recycled");
    }

    [Test]
    public void ARefTakenBeforeASortIsReadAgainAfterIt()
    {
        using var world = new World();
        var a = world.CreateEntity(new Walker(900, 900), new Health(1), new Name("a"));
        var b = world.CreateEntity(new Walker(1, 1), new Health(2), new Name("b"));
        ref var health = ref a.Get<Health>();
        Assert.That(health.Value, Is.EqualTo(1));
        world.SortRows<Walker>(); // b's key is lower: the rows swap
        Assert.That(health.Value, Is.EqualTo(2), "the old ref now reads the row's new entity");
        Assert.That(a.Get<Health>().Value, Is.EqualTo(1), "re-acquired, it is a's again");
        Assert.That(b.Get<Health>().Value, Is.EqualTo(2));
    }

    [Test]
    public void ASortDuringAPassThrows()
    {
        using var world = new World();
        world.CreateEntity(new Walker(1, 1), new Health(1), new Name(""));
        Assert.Throws<InvalidOperationException>(() =>
        {
#pragma warning disable EECS041 // the call this test proves throws
            foreach (var item in world.Query<Walker>()) world.SortRows<Walker>();
#pragma warning restore EECS041
        });
    }

    public static class Passes
    {
        [ForEach(Parallel = true, ChunkSize = 256)]
        public static void Weigh(in Walker walker, ref Health health) => health = new Health((int)(walker.X * 7 + walker.Y));
    }

    [Test]
    public void AParallelPassOverASortedTableMatchesTheSerialOne([Values(1, 4)] int threads)
    {
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        var expected = Populate(world, 20_000, new Random(7));
        world.SortRows<Walker>();
        Passes.Weigh(world);
        foreach (var (id, row) in expected.Rows)
            Assert.That(new Entity(world, id).Get<Health>().Value, Is.EqualTo((int)(row.Walker.X * 7 + row.Walker.Y)));
    }

    [Test]
    public void SavesAndSnapshotsKeepTheOrderAndTheNextUpdateDrawsTheBlocks()
    {
        using var world = new World();
        var random = new Random(8);
        var expected = new Dictionary<EntityId, (Walker, Health)>();
        for (int i = 0; i < 1000; i++)
        {
            var walker = RandomWalker(random);
            expected[world.CreateEntity(walker, new Health(i)).Id] = (walker, new Health(i));
        }

        void Check(World w)
        {
            foreach (var (id, (walker, health)) in expected)
            {
                Assert.That(new Entity(w, id).Get<Walker>(), Is.EqualTo(walker));
                Assert.That(new Entity(w, id).Get<Health>(), Is.EqualTo(health));
            }
        }

        world.SortRows<Walker>();
        var output = new System.Buffers.ArrayBufferWriter<byte>();
        world.Save(output);

        using var loaded = new World();
        loaded.Load(output.WrittenSpan);
        Check(loaded);
        foreach (var blocks in loaded.Blocks<Walker>()) Assert.That(blocks.Count, Is.Zero, "the boxes are not saved");
        var walkers = new List<uint>();
        foreach (var item in loaded.Query<Walker>()) walkers.Add(item.Component1.Cell);
        Assert.That(walkers, Is.Ordered, "the rows come back in their saved order");
        loaded.Update();
        CheckOrdered(loaded);
        foreach (var blocks in loaded.Blocks<Walker>()) Assert.That(blocks.SortedRows, Is.EqualTo(1000));

        var snapshot = world.Capture();
        foreach (var item in world.Query<Walker>()) item.Component1 = new Walker(5, 5);
        world.Restore(snapshot);
        Check(world);
        foreach (var blocks in world.Blocks<Walker>()) Assert.That(blocks.Count, Is.Zero);
        world.Update();
        CheckOrdered(world);
    }

    [Test]
    public void RowsLackingTheComponentSortLastAndOutsideTheBoxes()
    {
        using var world = new World();
        var random = new Random(9);
        var all = new List<Entity>();
        for (int i = 0; i < 200; i++) all.Add(world.CreateEntity(RandomWalker(random), new Health(i), new Name("")));
        for (int i = 0; i < 200; i += 5) all[i].Remove<Walker>();
        world.SortRows<Walker>();
        foreach (var blocks in world.Blocks<Walker>())
        {
            Assert.That(blocks.AllHave, Is.False);
            for (int r = 0; r < 160; r++) Assert.That(blocks.Has(r), Is.True, $"row {r}");
            for (int r = 160; r < 200; r++) Assert.That(blocks.Has(r), Is.False, $"row {r}");
            CheckBoxes(blocks);
            Assert.That(blocks.Min(0)[^1], Is.GreaterThan(blocks.Max(0)[^1]), "a block of rows without the component has an empty box");
        }

        for (int i = 0; i < 200; i++) Assert.That(all[i].Get<Health>().Value, Is.EqualTo(i));
    }

    [Test]
    public void KeysOfEveryTypeSortAsTheirValues()
    {
        using var world = new World();
        var random = new Random(10);
        var ints = new List<int>();
        var floats = new List<float>();
        var longs = new List<long>();
        var tiers = new List<Tier>();
        for (int i = 0; i < 500; i++)
        {
            int n = random.Next(int.MinValue, int.MaxValue);
            float f = (random.NextSingle() - 0.5f) * 1e6f;
            long l = random.NextInt64(long.MinValue, long.MaxValue);
            var tier = (Tier)new short[] { -2, 0, 3 }[random.Next(3)];
            ints.Add(n);
            floats.Add(f);
            longs.Add(l);
            tiers.Add(tier);
            world.CreateEntity(new OrderRank(n));
            world.CreateEntity(new OrderDepth(f));
            world.CreateEntity(new OrderDeadline(l));
            world.CreateEntity(new Tiered(tier));
        }

        world.Update(); // OrderRank, OrderDepth and OrderDeadline sort every update; Tiered's first update sorts its new table
        Assert.That(Rows<OrderRank>(world).Select(i => i.Value), Is.EqualTo(ints.OrderBy(x => x)));
        Assert.That(Rows<OrderDepth>(world).Select(i => i.Value), Is.EqualTo(floats.OrderBy(x => x)));
        Assert.That(Rows<OrderDeadline>(world).Select(i => i.Tick), Is.EqualTo(longs.OrderBy(x => x)));
        Assert.That(Rows<Tiered>(world).Select(i => i.Tier), Is.EqualTo(tiers.OrderBy(x => (short)x)));
    }

    [Test]
    public void AVector3BoxCoversThreeAxes()
    {
        using var world = new World();
        var random = new Random(11);
        for (int i = 0; i < 100; i++)
            world.CreateEntity(new Swimmer { At = new Vector3(random.NextSingle() * 50, random.NextSingle() * 50, random.NextSingle() * 50) });
        world.Update();
        foreach (var blocks in world.Blocks<Swimmer>())
        {
            Assert.That(blocks.Axes, Is.EqualTo(3));
            var swimmers = blocks.Read<Swimmer>();
            for (int b = 0; b < blocks.Count; b++)
                for (int r = blocks.Start(b); r < blocks.End(b); r++)
                    for (int a = 0; a < 3; a++)
                        Assert.That(swimmers[r].At[a], Is.InRange(blocks.Min(a)[b] + 1 - 1e-4f, blocks.Max(a)[b] - 1 + 1e-4f)); // the margin of 1, within rounding
        }
    }

    [Test]
    public void ABatchMethodFindsItsRowsInTheBlocks()
    {
        using var world = new World();
        Populate(world, 300, new Random(12));
        world.SortRows<Walker>();
        var job = new FindRows();
        world.ForEachBatch<Walker, FindRows>(ref job);
        Assert.That(job.Checked, Is.EqualTo(300));
    }

    [Test]
    public void BlockPairsHoldEveryPairOfRowsWithinReach([Values(0f, 3f, 40f)] float reach)
    {
        using var world = new World();
        var random = new Random(15);
        for (int i = 0; i < 700; i++)
            world.CreateEntity(new Walker(random.NextSingle() * 200, random.NextSingle() * 200), new Health(i), new Name(""));
        for (int i = 0; i < 300; i++)
            world.CreateEntity(new Walker(random.NextSingle() * 200, random.NextSingle() * 200), new Team(1));
        world.SortRows<Walker>();
        var tables = new List<(Walker[] Rows, int Sorted, Dictionary<(int, int), int> Pairs)>();
        int size = 0;
        foreach (var blocks in world.Blocks<Walker>())
        {
            if (blocks.RowCount == 0) continue; // a table of the group no entity is in
            var pairs = new BlockPair[4];
            int n = blocks.Pairs(reach, pairs);
            if (n > pairs.Length)
            {
                pairs = new BlockPair[n];
                Assert.That(blocks.Pairs(reach, pairs), Is.EqualTo(n));
            }

            var set = new Dictionary<(int, int), int>();
            foreach (var pair in pairs.AsSpan(0, n))
            {
                Assert.That(pair.First, Is.LessThanOrEqualTo(pair.Second));
                set.Add((pair.First, pair.Second), 0);
            }

            tables.Add((blocks.Read<Walker>().ToArray(), blocks.SortedRows, set));
            size = blocks.Size;
        }

        Assert.That(tables, Has.Count.EqualTo(2));
        foreach (var (rows, sorted, pairs) in tables)
            for (int i = 0; i < sorted; i++)
                for (int j = i; j < sorted; j++)
                    if (Math.Abs(rows[i].X - rows[j].X) <= reach && Math.Abs(rows[i].Y - rows[j].Y) <= reach)
                        Assert.That(pairs.ContainsKey((i / size, j / size)), $"rows {i}, {j}");

        // Across the two tables: every near pair of rows has its blocks' pair, the first table's block first.
        var e = world.Blocks<Walker>().GetEnumerator();
        while (e.MoveNext() && e.Current.RowCount == 0) { }
        var first = e.Current;
        while (e.MoveNext() && e.Current.RowCount == 0) { }
        var second = e.Current;
        var cross = new BlockPair[first.Count * second.Count];
        int found = first.Pairs(second, reach, cross);
        var crossSet = cross.Take(found).Select(p => (p.First, p.Second)).ToHashSet();
        Assert.That(crossSet, Has.Count.EqualTo(found));
        var (a, sa, _) = tables[0];
        var (b, sb, _) = tables[1];
        for (int i = 0; i < sa; i++)
            for (int j = 0; j < sb; j++)
                if (Math.Abs(a[i].X - b[j].X) <= reach && Math.Abs(a[i].Y - b[j].Y) <= reach)
                    Assert.That(crossSet.Contains((i / size, j / size)), $"rows {i}, {j}");
        Assert.That(found, Is.LessThan(first.Count * second.Count), "far blocks are not paired");
    }

    private struct FindRows : IForEachBatch<Walker>, INoStructuralChanges
    {
        public int Checked;

        public void Execute(EntitySpan entities, Span<Walker> walkers)
        {
            var all = entities.Blocks.Read<Walker>();
            for (int i = 0; i < walkers.Length; i++, Checked++)
                Assert.That(all[entities.FirstRow + i], Is.EqualTo(walkers[i]));
        }
    }

    private static List<T> Rows<T>(World world) where T : struct, IComponent
    {
        var rows = new List<T>();
        foreach (var item in world.Query<T>()) rows.Add(item.Component1);
        return rows;
    }

    [Test]
    public void ARequestOrdersTheTablesThereNowAndThoseToCome()
    {
        using var world = new World();
        var random = new Random(16);
        var ids = new Dictionary<EntityId, Mover>();
        for (int i = 0; i < 500; i++)
        {
            var mover = new Mover((random.NextSingle() - 0.5f) * 100, (random.NextSingle() - 0.5f) * 100);
            ids[world.CreateEntity(mover).Id] = mover;
        }

        foreach (var _ in world.Blocks<Mover>()) Assert.Fail("no order before the request");
        world.OrderRows<Mover, MoverOrder>(2, 1.5f, 5, 0.1f);
        for (int i = 0; i < 300; i++)
        {
            var mover = new Mover((random.NextSingle() - 0.5f) * 100, (random.NextSingle() - 0.5f) * 100);
            ids[world.CreateEntity(mover, new Health(i)).Id] = mover; // a table created after the request
        }

        world.Update();
        int tables = 0;
        foreach (var blocks in world.Blocks<Mover>())
        {
            tables++;
            var movers = blocks.Read<Mover>();
            Assert.That(blocks.SortedRows, Is.EqualTo(blocks.RowCount));
            for (int r = 1; r < blocks.RowCount; r++)
                Assert.That(MoverOrder.Key(movers[r], 1.5f), Is.GreaterThanOrEqualTo(MoverOrder.Key(movers[r - 1], 1.5f)));
            for (int b = 0; b < blocks.Count; b++)
                for (int r = blocks.Start(b); r < blocks.End(b); r++)
                {
                    Assert.That(movers[r].X, Is.InRange(blocks.Min(0)[b], blocks.Max(0)[b]));
                    Assert.That(movers[r].Y, Is.InRange(blocks.Min(1)[b], blocks.Max(1)[b]));
                }
        }

        Assert.That(tables, Is.EqualTo(2));
        foreach (var (id, mover) in ids) Assert.That(new Entity(world, id).Get<Mover>(), Is.EqualTo(mover));
    }

    [Test]
    public void OrderByOverridesARequest()
    {
        using var world = new World();
        var random = new Random(17);
        for (int i = 0; i < 200; i++) world.CreateEntity(new Walker(random.NextSingle() * 100, random.NextSingle() * 100));
        world.OrderRows<Walker, WalkerByX>(1, 1, 1, 0);
        world.Update();
        foreach (var blocks in world.Blocks<Walker>())
        {
            Assert.That(blocks.Axes, Is.EqualTo(2), "the declared order");
            var walkers = blocks.Read<Walker>();
            for (int r = 1; r < blocks.RowCount; r++) Assert.That(walkers[r].Cell, Is.GreaterThanOrEqualTo(walkers[r - 1].Cell));
        }
    }

    [TestCase(0)]
    [TestCase(4)]
    [TestCase(16)]
    public void ABlockHoldsTheRowsTheRequestAsksFor(int blockRows)
    {
        using var world = new World();
        var random = new Random(18);
        for (int i = 0; i < 301; i++) world.CreateEntity(new Mover(random.NextSingle() * 50, random.NextSingle() * 50));
        world.OrderRows<Mover, MoverOrder>(2, 1, 1, 0, blockRows);
        world.Update();
        foreach (var blocks in world.Blocks<Mover>())
        {
            int size = blockRows == 0 ? (System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated ? 8 : 4) : blockRows;
            Assert.That(blocks.Size, Is.EqualTo(size));
            Assert.That(blocks.Count, Is.EqualTo((301 + size - 1) / size));
            Assert.That(blocks.End(blocks.Count - 1), Is.EqualTo(301));
            var movers = blocks.Read<Mover>();
            for (int b = 0; b < blocks.Count; b++)
                for (int r = blocks.Start(b); r < blocks.End(b); r++)
                    Assert.That(movers[r].X, Is.InRange(blocks.Min(0)[b], blocks.Max(0)[b]));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => world.OrderRows<Rank2, Rank2Order>(1, 1, 1, 0, 6));
    }

    [Test]
    public void ARequestIsCheckedAndOutsidePasses()
    {
        using var world = new World();
        world.CreateEntity(new Mover(1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => world.OrderRows<Mover, MoverOrder>(4, 1, 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => world.OrderRows<Mover, MoverOrder>(2, 0, 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => world.OrderRows<Mover, MoverOrder>(2, 1, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => world.OrderRows<Mover, MoverOrder>(2, 1, 1, float.NaN));
        Assert.Throws<InvalidOperationException>(() =>
        {
#pragma warning disable EECS041 // the call this test proves throws
            foreach (var item in world.Query<Mover>()) world.OrderRows<Mover, MoverOrder>(2, 1, 1, 0);
#pragma warning restore EECS041
        });
    }

    [Test]
    public void MortonKeysKeepNearCellsNear()
    {
        // Each axis orders on its own (the other fixed), negative coordinates first, and the cell quantizes.
        Assert.That(RowKeys.Morton(new(-3, 5, 0), 2, 1), Is.LessThan(RowKeys.Morton(new(-2, 5, 0), 2, 1)));
        Assert.That(RowKeys.Morton(new(4, -1, 0), 2, 1), Is.LessThan(RowKeys.Morton(new(4, 0, 0), 2, 1)));
        Assert.That(RowKeys.Morton(new(0.2f, 0.7f, 0), 2, 1), Is.EqualTo(RowKeys.Morton(new(0.9f, 0.1f, 0), 2, 1)));
        Assert.That(RowKeys.Morton(new(1, 1, -7), 3, 2), Is.LessThan(RowKeys.Morton(new(1, 1, 7), 3, 2)));
        Assert.That(RowKeys.Morton(new(-1e30f, 0, 0), 1, 1), Is.LessThan(RowKeys.Morton(new(1e30f, 0, 0), 1, 1)));
        Assert.That(RowKeys.Of(-1.5f), Is.LessThan(RowKeys.Of(0.5f)));
    }

    [Test]
    public void AComponentWithoutAnOrderHasNoBlocks()
    {
        using var world = new World();
        world.CreateEntity(new Health(1));
        world.SortRows<Health>();
        foreach (var _ in world.Blocks<Health>()) Assert.Fail("no table is ordered by Health");
    }

    [Test]
    public void TrimKeepsTheBlocks()
    {
        using var world = new World();
        Populate(world, 4000, new Random(13));
        world.SortRows<Walker>();
        var all = new List<Entity>();
        foreach (var item in world.Query<Walker>()) all.Add(item.Entity);
        foreach (var entity in all.Skip(100)) entity.Destroy();
        world.TrimExcess();
        CheckOrdered(world);
        foreach (var blocks in world.Blocks<Walker>()) Assert.That(blocks.SortedRows, Is.EqualTo(100));
    }

    [Test]
    public void SortingUpdatesAllocateNothing()
    {
        using var world = new World();
        var random = new Random(14);
        Populate(world, 5000, random);
        for (int i = 0; i < 1000; i++) world.CreateEntity(new OrderRank(random.Next()), new Swimmer { At = new Vector3(i) });
        void Tick()
        {
            foreach (var item in world.Query<Walker>()) item.Component1 = new Walker(item.Component1.Y, item.Component1.X); // the order changes
            foreach (var item in world.Query<OrderRank>()) item.Component1 = new OrderRank(-item.Component1.Value);
            world.Update();
            world.SortRows<Walker>();
        }

        for (int i = 0; i < 5; i++) Tick();
        Assert.That(Allocations.Of(Tick), Is.Zero);
    }
}
