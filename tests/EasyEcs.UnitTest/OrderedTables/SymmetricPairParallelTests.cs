using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>
/// <see cref="SymmetricPairs"/>' methods on several threads: chunks of a fixed number of rows kept off each other's rows
/// by colors or by buffers of the neighbors of other chunks.
/// </summary>
public static class SymmetricParallelPairs
{
    /// <summary><see cref="SymmetricPairs.Repel"/> in parallel: its writes of the neighbor add (buffers may take them).</summary>
    [ForEach(Parallel = true)]
    public static void Repel(in BodyAt p, ref BodyForce f, [Neighbor] in BodyAt q, [Neighbor] ref BodyForce g)
    {
        float dx = p.X - q.X, dy = p.Y - q.Y, d2 = dx * dx + dy * dy;
        if (d2 >= SymmetricPairs.Range * SymmetricPairs.Range) return;
        float w = SymmetricPairs.Range * SymmetricPairs.Range - d2;
        f.X += w * dx;
        f.Y += w * dy;
        g.X -= w * dx;
        g.Y -= w * dy;
    }

    /// <summary><see cref="SymmetricPairs.Meet"/> in parallel: integer sums, exact in any order.</summary>
    [ForEach(Parallel = true)]
    public static void Meet(in BodyAt p, ref BodyContacts c, [Neighbor] in BodyAt q, [Neighbor] ref BodyContacts k)
    {
        float dx = p.X - q.X, dy = p.Y - q.Y;
        if (dx * dx + dy * dy >= SymmetricPairs.Range * SymmetricPairs.Range) return;
        c.Count += dx > 0 ? 3 : 1;
        k.Count += dx > 0 ? 1 : 2;
    }

    /// <summary><see cref="SymmetricPairs.Sense"/> in parallel: a cutoff per row, a branch, increments of the neighbor.</summary>
    [ForEach(Parallel = true)]
    public static void Sense(in BodyAt p, in BodySight s, ref BodyForce f, ref BodyContacts c, [Neighbor] in BodyAt q,
        [Neighbor] ref BodyForce g, [Neighbor] ref BodyContacts k)
    {
        float dx = p.X - q.X, dy = p.Y - q.Y, d2 = dx * dx + dy * dy;
        if (d2 >= s.Range * s.Range) return;
        c.Count++;
        k.Count++;
        if (dx > 0)
        {
            f.X += d2;
            g.X -= d2;
        }
        else f.Y -= dy;
    }

    /// <summary><see cref="SymmetricPairs.Touch"/> in parallel: each chunk's count of pairs combined in chunk order.</summary>
    [ForEach(Parallel = true)]
    public static void Touch(in BodyAt p, ref BodyContacts c, [Neighbor] in BodyAt q, [Neighbor] ref BodyContacts k, ref Reduce<BodyPairs> pairs)
    {
        float dx = p.X - q.X, dy = p.Y - q.Y;
        if (dx * dx + dy * dy >= SymmetricPairs.Range * SymmetricPairs.Range) return;
        c.Count++;
        k.Count++;
        pairs.Value.Pairs++;
    }

    /// <summary><see cref="SymmetricPairs.Merge"/> in parallel: it reads the neighbor's stack, so its chunks are colored.</summary>
    [ForEach(Parallel = true)]
    public static void Merge(Entity self, in BodyAt p, ref ItemStack s, [Neighbor] in BodyAt q, [Neighbor] ref ItemStack t, [Neighbor] Entity other)
    {
        float dx = p.X - q.X, dy = p.Y - q.Y;
        if (dx * dx + dy * dy >= SymmetricPairs.MergeRange * SymmetricPairs.MergeRange || s.Count == 0 || t.Count == 0 || s.Kind != t.Kind) return;
        if (self.Id.Index < other.Id.Index)
        {
            s.Count += t.Count;
            t.Count = 0;
        }
        else
        {
            t.Count += s.Count;
            s.Count = 0;
        }
    }
}

public class SymmetricPairParallelTests
{
    private int _rows;

    [SetUp]
    public void Cut()
    {
        _rows = PairHalves.ChunkRows;
        PairHalves.ChunkRows = 64; // many chunks of small tables
    }

    [TearDown]
    public void Restore() => PairHalves.ChunkRows = _rows;

    private static World Bodies(int count, float extent, int seed, bool tables, int threads)
    {
        var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        var random = new Random(seed);
        world.Reserve<BodyAt, BodyForce>(count);
        for (int i = 0; i < count; i++)
        {
            var e = world.CreateEntity(new BodyAt((float)random.NextDouble() * extent, (float)random.NextDouble() * extent), new BodyForce(),
                new BodySight(0.3f + (float)random.NextDouble()), new BodyContacts());
            if (tables && i % 3 == 0) e.Set(new BodyHeavy()); // a second table
        }

        return world;
    }

    private static Dictionary<EntityId, (BodyForce, BodyContacts)> Rows(World world)
    {
        var result = new Dictionary<EntityId, (BodyForce, BodyContacts)>();
        foreach (var item in world.Read<BodyForce, BodyContacts>()) result[item.Entity.Id] = (item.Component1, item.Component2);
        return result;
    }

    /// <summary>Every parallel pass, from forces and counts at zero; the rows after each.</summary>
    private static List<Dictionary<EntityId, (BodyForce, BodyContacts)>> Passes(World world)
    {
        var results = new List<Dictionary<EntityId, (BodyForce, BodyContacts)>>();
        foreach (var pass in new Action<World>[] { SymmetricParallelPairs.Repel, SymmetricParallelPairs.Meet, SymmetricParallelPairs.Sense })
        {
            SymmetricPairs.ResetForce(world);
            SymmetricPairs.ResetContacts(world);
            pass(world);
            results.Add(Rows(world));
        }

        return results;
    }

    /// <summary>The same pass on 1 and on 4 threads, bit for bit (equal values, NaN equal to NaN).</summary>
    private static void SameOnAnyThreads(World one, World four, string what)
    {
        var (a, b) = (Passes(one), Passes(four));
        for (int k = 0; k < a.Count; k++)
        {
            Assert.That(b[k].Count, Is.EqualTo(a[k].Count), what);
            foreach (var (id, value) in a[k])
                Assert.That(b[k][id], Is.EqualTo(value), $"{what}, pass {k}, {id}");
        }
    }

    /// <summary>
    /// The serial pass of the same method: integer sums equal, float sums within rounding (a chunk adds the pairs of a
    /// row in another order than the serial pass).
    /// </summary>
    private static void AsTheSerialPass(World world, string what)
    {
        SymmetricPairs.ResetForce(world);
        SymmetricPairs.ResetContacts(world);
        SymmetricPairs.Sense(world);
        var serial = Rows(world);
        SymmetricPairs.ResetForce(world);
        SymmetricPairs.ResetContacts(world);
        SymmetricParallelPairs.Sense(world);
        foreach (var (id, (force, contacts)) in Rows(world))
        {
            var (f, c) = serial[id];
            Assert.That(contacts, Is.EqualTo(c), $"{what}: {id}'s count");
            double tolerance = 1e-4 * (1 + Math.Abs(f.X) + Math.Abs(f.Y));
            Assert.That(Math.Abs(force.X - f.X) + Math.Abs(force.Y - f.Y), Is.LessThanOrEqualTo(tolerance), $"{what}: {id}'s force");
        }
    }

    [TestCase(3000, 30f, false)]
    [TestCase(3000, 12f, true)] // dense, two tables
    [TestCase(6000, 60f, true)]
    public void OneResultOnAnyThreadsAndTheSerialPassesSums(int count, float extent, bool tables)
    {
        using var one = Bodies(count, extent, count, tables, threads: 1);
        using var four = Bodies(count, extent, count, tables, threads: 4);
        SymmetricPairs.Repel(one); // sorts the rows of both alike
        SymmetricPairs.Repel(four);
        SameOnAnyThreads(one, four, "sorted");
        AsTheSerialPass(four, "4 threads");
    }

    [Test]
    public void IntegerSumsAreTheHalfWalks()
    {
        using var world = Bodies(4000, 25f, 3, tables: true, threads: 4);
        SymmetricPairs.Meet(world); // sorted
        SymmetricPairs.ResetContacts(world);
        SymmetricPairs.Meet(world);
        var serial = Rows(world);
        SymmetricPairs.ResetContacts(world);
        SymmetricParallelPairs.Meet(world);
        foreach (var (id, (_, contacts)) in Rows(world)) Assert.That(contacts, Is.EqualTo(serial[id].Item2), $"{id}");
    }

    [Test]
    public void MovingRowsTombstonesAndAbsentRows()
    {
        using var one = Bodies(2500, 20f, 9, tables: true, threads: 1);
        using var four = Bodies(2500, 20f, 9, tables: true, threads: 4);
        foreach (var world in new[] { one, four })
        {
            var all = new List<Entity>();
            foreach (var item in world.Query<BodyAt>()) all.Add(item.Entity);
            for (int i = 0; i < all.Count; i += 7) all[i].Destroy();
            for (int i = 3; i < all.Count; i += 11)
                if (all[i].IsAlive) all[i].Disable<BodyForce>(); // neither side of a pair
        }

        var random = (new Random(1), new Random(1));
        for (int tick = 0; tick < 25; tick++)
        {
            Move(one, random.Item1);
            Move(four, random.Item2);
            SameOnAnyThreads(one, four, $"tick {tick}");
            AsTheSerialPass(four, $"tick {tick}");
        }

        static void Move(World world, Random random)
        {
            foreach (var item in world.Query<BodyAt>())
            {
                ref var p = ref item.Component1;
                if (random.Next(50) == 0) p = new BodyAt((float)random.NextDouble() * 20, (float)random.NextDouble() * 20);
                else p = new BodyAt(p.X + 0.05f * ((float)random.NextDouble() - 0.5f), p.Y + 0.05f);
            }

            world.Update();
        }
    }

    [Test]
    public void NaNPositions()
    {
        using var one = Bodies(2000, 15f, 2, tables: false, threads: 1);
        using var four = Bodies(2000, 15f, 2, tables: false, threads: 4);
        foreach (var world in new[] { one, four })
            foreach (var item in world.Query<BodyAt>())
            {
                if (item.Entity.Id.Index % 97 == 0) item.Component1 = new BodyAt(float.NaN, 1);
            }

        SymmetricPairs.Repel(one);
        SymmetricPairs.Repel(four);
        SameOnAnyThreads(one, four, "NaN");
    }

    [Test]
    public void SmallTablesRunTheSerialPass()
    {
        PairHalves.ChunkRows = 1024;
        using var world = Bodies(2000, 15f, 4, tables: false, threads: 4); // two chunks: fewer than PairHalves.Least
        SymmetricPairs.Repel(world);
        SymmetricPairs.ResetForce(world);
        SymmetricPairs.Repel(world);
        var serial = Rows(world);
        SymmetricPairs.ResetForce(world);
        SymmetricParallelPairs.Repel(world);
        foreach (var (id, value) in Rows(world)) Assert.That(value, Is.EqualTo(serial[id]), $"{id}");
    }

    [Test]
    public void AResultOfThePassCountsEachPairOnce()
    {
        using var world = Bodies(3000, 18f, 17, tables: true, threads: 4);
        SymmetricPairs.Touch(world); // sorted
        world.GetOrAddSingleton<BodyPairs>() = default;
        SymmetricPairs.ResetContacts(world);
        SymmetricPairs.Touch(world);
        long serial = world.GetSingleton<BodyPairs>().Pairs;
        var counts = Rows(world);
        world.GetOrAddSingleton<BodyPairs>() = default;
        SymmetricPairs.ResetContacts(world);
        SymmetricParallelPairs.Touch(world);
        Assert.That(world.GetSingleton<BodyPairs>().Pairs, Is.EqualTo(serial));
        foreach (var (id, (_, contacts)) in Rows(world)) Assert.That(contacts, Is.EqualTo(counts[id].Item2), $"{id}");
    }

    [Test]
    public void BothSidesAreStamped()
    {
        using var world = Bodies(3000, 18f, 21, tables: true, threads: 4);
        SymmetricParallelPairs.Sense(world);
        world.Update();
        ulong mark = world.ChangeTick;
        SymmetricParallelPairs.Sense(world);
        var since = QueryFilter.Create().Changed<BodyContacts>(mark);
        Assert.That(world.Count<BodyContacts>(since), Is.EqualTo(world.Count<BodyContacts>()));
    }

    private static World Items(int count, float extent, int kinds, int seed, int threads)
    {
        var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        var random = new Random(seed);
        world.Reserve<BodyAt, ItemStack>(count);
        for (int i = 0; i < count; i++)
        {
            var e = world.CreateEntity(new BodyAt((float)random.NextDouble() * extent, (float)random.NextDouble() * extent),
                new ItemStack(random.Next(kinds), 1 + random.Next(9)));
            if (i % 4 == 0) e.Set(new BodyHeavy());
        }

        return world;
    }

    [Test]
    public void ItemsMergeAlikeOnAnyThreadsAndLoseNothing()
    {
        using var one = Items(4000, 20f, 3, 5, threads: 1);
        using var four = Items(4000, 20f, 3, 5, threads: 4);
        var totals = new Dictionary<int, int>();
        foreach (var item in one.Read<ItemStack>()) totals[item.Component1.Kind] = totals.GetValueOrDefault(item.Component1.Kind) + item.Component1.Count;
        for (int pass = 0; pass < 3; pass++)
        {
            SymmetricParallelPairs.Merge(one);
            SymmetricParallelPairs.Merge(four);
            var a = new Dictionary<EntityId, ItemStack>();
            foreach (var item in one.Read<ItemStack>()) a[item.Entity.Id] = item.Component1;
            foreach (var item in four.Read<ItemStack>()) Assert.That(item.Component1, Is.EqualTo(a[item.Entity.Id]), $"pass {pass}");
            foreach (var (kind, total) in totals)
                Assert.That(a.Values.Where(s => s.Kind == kind).Sum(s => s.Count), Is.EqualTo(total), $"pass {pass}, kind {kind}");
        }
    }

    [Test]
    public void APassAllocatesNothingOnceWarm()
    {
        using var world = Bodies(5000, 40f, 6, tables: true, threads: 4);
        using var items = Items(3000, 20f, 3, 8, threads: 4);
        for (int i = 0; i < 200; i++) Run(world, items);
        Assert.That(Allocations.Of(() =>
        {
            for (int i = 0; i < 20; i++) Run(world, items);
        }), Is.EqualTo(0));

        static void Run(World world, World items)
        {
            SymmetricPairs.ResetForce(world);
            SymmetricParallelPairs.Repel(world);
            SymmetricParallelPairs.Meet(world);
            SymmetricParallelPairs.Sense(world);
            SymmetricParallelPairs.Touch(world);
            world.Update();
            SymmetricParallelPairs.Merge(items);
            items.Update();
        }
    }
}
