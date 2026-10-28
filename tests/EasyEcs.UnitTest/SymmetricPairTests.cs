using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public partial record struct BodyAt(float X, float Y) : IComponent;
public partial record struct BodyForce(float X, float Y) : IComponent;
public partial record struct BodySight(float Range) : IComponent;
public partial record struct BodyContacts(int Count) : IComponent, ITracked;
public partial record struct BodyHeavy : IComponent;
public partial record struct ItemStack(int Kind, int Count) : IComponent, ITracked;

/// <summary>The unordered pairs a symmetric pass met: their count.</summary>
[Sum]
public partial struct BodyPairs : ISingletonComponent
{
    public long Pairs;
}

/// <summary>
/// Pair methods that write both sides of their pairs (a component by ref of the row and of the neighbor): each unordered
/// pair once, against the walk of the half pairs in the tables' order.
/// </summary>
public static class SymmetricPairs
{
    public const float Range = 1f;
    public const float MergeRange = 0.6f;

    /// <summary>
    /// Newton's third law: what the row is pushed by, the neighbor is pushed back by. Each unordered pair once, one at a
    /// time (a write of the neighbor keeps the pairs one at a time).
    /// </summary>
    [ForEach]
    public static void Repel(in BodyAt p, ref BodyForce f, [Neighbor] in BodyAt q, [Neighbor] ref BodyForce g)
    {
        float dx = p.X - q.X, dy = p.Y - q.Y, d2 = dx * dx + dy * dy;
        if (d2 >= Range * Range) return;
        float w = Range * Range - d2;
        f.X += w * dx;
        f.Y += w * dy;
        g.X -= w * dx;
        g.Y -= w * dy;
    }

    /// <summary>
    /// Counts of both sides, weighted by the side of the pair, under the default SimdMode (<see cref="MeetRows"/> with
    /// SimdMode.Off): the same pass.
    /// </summary>
    [ForEach]
    public static void Meet(in BodyAt p, ref BodyContacts c, [Neighbor] in BodyAt q, [Neighbor] ref BodyContacts k)
    {
        float dx = p.X - q.X, dy = p.Y - q.Y;
        if (dx * dx + dy * dy >= Range * Range) return;
        c.Count += dx > 0 ? 3 : 1;
        k.Count += dx > 0 ? 1 : 2;
    }

    /// <summary><see cref="Meet"/> one pair at a time.</summary>
    [ForEach(SimdMode = SimdMode.Off)]
    public static void MeetRows(in BodyAt p, ref BodyContacts c, [Neighbor] in BodyAt q, [Neighbor] ref BodyContacts k)
    {
        float dx = p.X - q.X, dy = p.Y - q.Y;
        if (dx * dx + dy * dy >= Range * Range) return;
        c.Count += dx > 0 ? 3 : 1;
        k.Count += dx > 0 ? 1 : 2;
    }

    /// <summary>The same, one pair at a time.</summary>
    [ForEach(SimdMode = SimdMode.Off)]
    public static void RepelRows(in BodyAt p, ref BodyForce f, [Neighbor] in BodyAt q, [Neighbor] ref BodyForce g)
    {
        float dx = p.X - q.X, dy = p.Y - q.Y, d2 = dx * dx + dy * dy;
        if (d2 >= Range * Range) return;
        float w = Range * Range - d2;
        f.X += w * dx;
        f.Y += w * dy;
        g.X -= w * dx;
        g.Y -= w * dy;
    }

    /// <summary>
    /// The row's own sight is the cutoff (of the earlier row of each pair), a branch of the body adds into the neighbor
    /// under a condition, and a count of contacts on both sides.
    /// </summary>
    [ForEach]
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

    /// <summary>The same, one pair at a time.</summary>
    [ForEach(SimdMode = SimdMode.Off)]
    public static void SenseRows(in BodyAt p, in BodySight s, ref BodyForce f, ref BodyContacts c, [Neighbor] in BodyAt q,
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

    /// <summary>The unordered pairs within range, counted once each into a result of the pass.</summary>
    [ForEach]
    public static void Touch(in BodyAt p, ref BodyContacts c, [Neighbor] in BodyAt q, [Neighbor] ref BodyContacts k, ref Reduce<BodyPairs> pairs)
    {
        float dx = p.X - q.X, dy = p.Y - q.Y;
        if (dx * dx + dy * dy >= Range * Range) return;
        c.Count++;
        k.Count++;
        pairs.Value.Pairs++;
    }

    /// <summary>
    /// Two stacks of one kind within range merge into the one of the lower entity index, the other left empty: each
    /// unordered pair once, in the order of the tables' rows.
    /// </summary>
    [ForEach]
    public static void Merge(Entity self, in BodyAt p, ref ItemStack s, [Neighbor] in BodyAt q, [Neighbor] ref ItemStack t, [Neighbor] Entity other)
    {
        float dx = p.X - q.X, dy = p.Y - q.Y;
        if (dx * dx + dy * dy >= MergeRange * MergeRange || s.Count == 0 || t.Count == 0 || s.Kind != t.Kind) return;
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

    [ForEach]
    public static void ResetForce(ref BodyForce f) => f = default;

    [ForEach]
    public static void ResetContacts(ref BodyContacts c) => c = default;
}

/// <summary>Pair methods writing both sides as a system's, for what its loops say.</summary>
[EcsSystem]
public ref struct SymmetricRules
{
    [ForEach]
    static void Push(in BodyAt p, ref BodyForce f, [Neighbor] in BodyAt q, [Neighbor] ref BodyForce g)
    {
        float dx = p.X - q.X, dy = p.Y - q.Y;
        if (dx * dx + dy * dy >= 1f) return;
        f.X += dx;
        g.X -= dx;
    }

    [ForEach]
    static void Gather(Entity self, in BodyAt p, ref ItemStack s, [Neighbor] in BodyAt q, [Neighbor] ref ItemStack t, [Neighbor] Entity other)
    {
        float dx = p.X - q.X, dy = p.Y - q.Y;
        if (dx * dx + dy * dy >= 1f) return;
        if (self.Id.Index < other.Id.Index) (s.Count, t.Count) = (s.Count + t.Count, 0);
        else (t.Count, s.Count) = (s.Count + t.Count, 0);
    }
}

public class SymmetricPairTests
{
    private static World Bodies(int count, float extent, int seed, bool tables = false, bool sight = false, int threads = 0)
    {
        var world = threads > 0 ? new World(new WorldOptions { MaxDegreeOfParallelism = threads }) : new World();
        var random = new Random(seed);
        world.Reserve<BodyAt, BodyForce>(count);
        for (int i = 0; i < count; i++)
        {
            var e = world.CreateEntity(new BodyAt((float)random.NextDouble() * extent, (float)random.NextDouble() * extent), new BodyForce());
            if (sight) e.Set(new BodySight(0.3f + (float)random.NextDouble()), new BodyContacts());
            if (tables && i % 3 == 0) e.Set(new BodyHeavy()); // a second table
        }

        return world;
    }

    /// <summary>The tables the pass walks (the rows of <typeparamref name="T1"/> and <typeparamref name="T2"/>), in its order, each its rows in order.</summary>
    private static List<List<Entity>> Tables<T1, T2>(World world) where T1 : struct, IComponent where T2 : struct, IComponent
    {
        var tables = new List<List<Entity>>();
        Archetype last = null;
        foreach (var item in world.Read<T1, T2>())
        {
            var table = world.Meta[item.Entity.Id.Index].Archetype;
            if (!ReferenceEquals(table, last)) tables.Add([]);
            last = table;
            tables[^1].Add(item.Entity);
        }

        return tables;
    }

    /// <summary>
    /// The half pairs in the pass's order: each table against itself (each row against the rows after it), then against
    /// each table after it (every row against every row).
    /// </summary>
    private static void HalfWalk(List<List<Entity>> tables, Action<Entity, Entity> pair)
    {
        for (int a = 0; a < tables.Count; a++)
            for (int b = a; b < tables.Count; b++)
                for (int i = 0; i < tables[a].Count; i++)
                    for (int j = a == b ? i + 1 : 0; j < tables[b].Count; j++)
                        pair(tables[a][i], tables[b][j]);
    }

    /// <summary>Repel over the half walk from forces at zero.</summary>
    private static Dictionary<EntityId, BodyForce> Reference(World world)
    {
        var tables = Tables<BodyAt, BodyForce>(world);
        var forces = tables.SelectMany(t => t).ToDictionary(e => e.Id, _ => new BodyForce());
        HalfWalk(tables, (e, o) =>
        {
            var f = forces[e.Id];
            var g = forces[o.Id];
            SymmetricPairs.Repel(e.Read<BodyAt>(), ref f, o.Read<BodyAt>(), ref g);
            forces[e.Id] = f;
            forces[o.Id] = g;
        });
        return forces;
    }

    private static Dictionary<EntityId, BodyForce> Forces(World world)
    {
        var result = new Dictionary<EntityId, BodyForce>();
        foreach (var item in world.Read<BodyAt, BodyForce>()) result[item.Entity.Id] = item.Component2;
        return result;
    }

    private static Dictionary<EntityId, BodyForce> Repelled(World world, bool rows)
    {
        SymmetricPairs.ResetForce(world);
        if (rows) SymmetricPairs.RepelRows(world);
        else SymmetricPairs.Repel(world);
        return Forces(world);
    }

    // Equal entry by entry, bit for bit: the first difference named.
    private static void Same<T>(Dictionary<EntityId, T> got, Dictionary<EntityId, T> expected, string what) where T : IEquatable<T>
    {
        Assert.That(got.Count, Is.EqualTo(expected.Count), what);
        int wrong = 0;
        string first = "";
        foreach (var (id, value) in expected)
            if (!got.TryGetValue(id, out var other) || !other.Equals(value))
                if (wrong++ == 0) first = $"{id}: {other} instead of {value}";
        Assert.That(wrong, Is.Zero, $"{what}: {wrong} rows differ, first {first}");
    }

    [TestCase(1, 4f, false)]
    [TestCase(7, 2f, false)]   // one cluster, every pair within range
    [TestCase(200, 10f, false)]
    [TestCase(2000, 40f, false)]
    [TestCase(2000, 8f, false)] // dense
    [TestCase(3000, 30f, true)]
    public void EachUnorderedPairOnceBitForBitTheHalfWalk(int count, float extent, bool tables)
    {
        using var world = Bodies(count, extent, count, tables);
        SymmetricPairs.Repel(world); // sorts the rows: the reference walks them in that order
        var auto = Repelled(world, rows: false);
        Same(auto, Reference(world), "SimdMode.Auto");
        Same(Repelled(world, rows: true), auto, "SimdMode.Off");
        // Newton's third law: the forces cancel (up to rounding).
        Assert.That(Math.Abs(auto.Values.Sum(f => (double)f.X)), Is.LessThan(1e-2 * Math.Max(1, count)));
    }

    /// <summary>Integer sums into both sides: the half walk's, under either SimdMode.</summary>
    [TestCase(7, 2f, false)]
    [TestCase(2000, 8f, false)]
    [TestCase(3000, 30f, true)]
    public void IntegerSumsOfBothSidesAreExact(int count, float extent, bool tables)
    {
        using var world = Bodies(count, extent, count + 5, tables, sight: true);
        SymmetricPairs.Meet(world); // sorted
        var t = Tables<BodyAt, BodyContacts>(world);
        var expected = t.SelectMany(x => x).ToDictionary(e => e.Id, _ => new BodyContacts());
        HalfWalk(t, (e, o) =>
        {
            var (c, k) = (expected[e.Id], expected[o.Id]);
            SymmetricPairs.Meet(e.Read<BodyAt>(), ref c, o.Read<BodyAt>(), ref k);
            (expected[e.Id], expected[o.Id]) = (c, k);
        });
        foreach (var rows in new[] { false, true })
        {
            SymmetricPairs.ResetContacts(world);
            if (rows) SymmetricPairs.MeetRows(world);
            else SymmetricPairs.Meet(world);
            var got = new Dictionary<EntityId, BodyContacts>();
            foreach (var item in world.Read<BodyContacts>()) got[item.Entity.Id] = item.Component1;
            Same(got, expected, rows ? "SimdMode.Off" : "SimdMode.Auto");
        }
    }

    [Test]
    public void ARowNeverMeetsItselfAndAPairOnce()
    {
        using var world = new World();
        var a = world.CreateEntity(new BodyAt(0, 0), new BodyForce());
        var b = world.CreateEntity(new BodyAt(0.5f, 0), new BodyForce());
        world.CreateEntity(new BodyAt(5, 5), new BodyForce()); // far from both
        SymmetricPairs.Repel(world);
        // One pair: w = 1 - 0.25, dx = -0.5 for the row a (or +0.5 for b): each pushed once, apart.
        var (fa, fb) = (a.Read<BodyForce>(), b.Read<BodyForce>());
        Assert.That(Math.Abs(fa.X), Is.EqualTo(0.375f));
        Assert.That(fb.X, Is.EqualTo(-fa.X));
        Assert.That(fa.X, Is.LessThan(0), "a is pushed away from b");
    }

    [Test]
    public void TombstonesAbsentRowsAndTablesAreRight()
    {
        using var world = Bodies(1500, 12f, 5, tables: true);
        var all = new List<Entity>();
        foreach (var item in world.Query<BodyAt>()) all.Add(item.Entity);
        for (int i = 0; i < all.Count; i += 7) all[i].Destroy();
        for (int i = 3; i < all.Count; i += 11)
            if (all[i].IsAlive) all[i].Disable<BodyForce>(); // fails the filter: neither side of a pair
        for (int pass = 0; pass < 3; pass++)
        {
            var auto = Repelled(world, rows: false);
            Same(auto, Reference(world), $"pass {pass}");
            Same(Repelled(world, rows: true), auto, $"pass {pass}, SimdMode.Off");
        }
    }

    [Test]
    public void MovingRowsKeepEveryPairWithinTheCutoff()
    {
        using var world = Bodies(1200, 20f, 9, tables: true);
        var random = new Random(1);
        for (int tick = 0; tick < 40; tick++)
        {
            // Some rows jump far (their clusters' boxes are left), the others drift; the cadence re-sorts every 10.
            foreach (var item in world.Query<BodyAt>())
            {
                ref var p = ref item.Component1;
                if (random.Next(50) == 0) p = new BodyAt((float)random.NextDouble() * 20, (float)random.NextDouble() * 20);
                else p = new BodyAt(p.X + 0.05f * ((float)random.NextDouble() - 0.5f), p.Y + 0.05f);
            }
            world.Update();
            var auto = Repelled(world, rows: false);
            Same(auto, Reference(world), $"tick {tick}");
            Same(Repelled(world, rows: true), auto, $"tick {tick}, SimdMode.Off");
        }
    }

    [Test]
    public void ListsOfNeighborsHoldTheRowsAfterTheRow()
    {
        using var world = Bodies(3000, 60f, 4, tables: true);
        var before = PairState.Lists;
        try
        {
            for (int tick = 0; tick < 12; tick++)
            {
                foreach (var item in world.Query<BodyAt>()) item.Component1 = new BodyAt(item.Component1.X + 0.01f, item.Component1.Y);
                world.Update();
                PairState.Lists = PairState.ListMode.On;
                var listed = Repelled(world, rows: true);
                PairState.Lists = PairState.ListMode.Off;
                Same(listed, Repelled(world, rows: true), $"tick {tick}: lists against ranges");
                Same(listed, Reference(world), $"tick {tick}: the half walk");
            }
        }
        finally
        {
            PairState.Lists = before;
        }
    }

    [Test]
    public void NaNPositionsMeetEveryRow()
    {
        using var world = Bodies(300, 5f, 2);
        foreach (var item in world.Query<BodyAt>())
        {
            item.Component1 = new BodyAt(float.NaN, 1);
            break;
        }
        SymmetricPairs.Repel(world);
        var auto = Repelled(world, rows: false);
        Same(auto, Reference(world), "SimdMode.Auto");
        Same(Repelled(world, rows: true), auto, "SimdMode.Off");
    }

    [TestCase(800, 6f, false)]
    [TestCase(2000, 20f, true)]
    public void ACutoffPerRowABranchAndCountsOfBothSides(int count, float extent, bool tables)
    {
        using var world = Bodies(count, extent, 13, tables, sight: true);
        SymmetricPairs.Sense(world);
        var t = Tables<BodyAt, BodySight>(world);
        var forces = t.SelectMany(x => x).ToDictionary(e => e.Id, _ => new BodyForce());
        var contacts = t.SelectMany(x => x).ToDictionary(e => e.Id, _ => new BodyContacts());
        HalfWalk(t, (e, o) =>
        {
            var (f, g, c, k) = (forces[e.Id], forces[o.Id], contacts[e.Id], contacts[o.Id]);
            SymmetricPairs.Sense(e.Read<BodyAt>(), e.Read<BodySight>(), ref f, ref c, o.Read<BodyAt>(), ref g, ref k);
            (forces[e.Id], forces[o.Id], contacts[e.Id], contacts[o.Id]) = (f, g, c, k);
        });

        foreach (var form in new[] { false, true })
        {
            SymmetricPairs.ResetForce(world);
            SymmetricPairs.ResetContacts(world);
            if (form) SymmetricPairs.SenseRows(world);
            else SymmetricPairs.Sense(world);
            var got = new Dictionary<EntityId, BodyForce>();
            var counted = new Dictionary<EntityId, BodyContacts>();
            foreach (var item in world.Read<BodyForce, BodyContacts>())
                (got[item.Entity.Id], counted[item.Entity.Id]) = (item.Component1, item.Component2);
            Same(got, forces, form ? "SimdMode.Off" : "SimdMode.Auto");
            Same(counted, contacts, form ? "contacts, SimdMode.Off" : "contacts, SimdMode.Auto");
        }
    }

    [Test]
    public void BothSidesAreStampedAsRowsTheyAre()
    {
        using var world = Bodies(1000, 15f, 21, tables: true, sight: true);
        SymmetricPairs.Sense(world);
        world.Update();
        ulong mark = world.ChangeTick;
        SymmetricPairs.Sense(world);
        var since = QueryFilter.Create().Changed<BodyContacts>(mark);
        Assert.That(world.Count<BodyContacts>(since), Is.EqualTo(world.Count<BodyContacts>()), "every row the pass ran for, either side of its pairs");
    }

    [TestCase(500, 6f)]
    [TestCase(3000, 25f)]
    public void AResultOfThePassCountsEachPairOnce(int count, float extent)
    {
        using var world = Bodies(count, extent, 17, tables: true, sight: true);
        SymmetricPairs.Touch(world);
        world.GetOrAddSingleton<BodyPairs>() = default;
        SymmetricPairs.ResetContacts(world);
        SymmetricPairs.Touch(world);
        var points = new List<(EntityId Id, BodyAt P)>();
        foreach (var item in world.Read<BodyAt, BodyContacts>()) points.Add((item.Entity.Id, item.Component1));
        long pairs = 0;
        var near = points.ToDictionary(p => p.Id, _ => 0);
        for (int i = 0; i < points.Count; i++)
            for (int j = i + 1; j < points.Count; j++)
            {
                float dx = points[i].P.X - points[j].P.X, dy = points[i].P.Y - points[j].P.Y;
                if (dx * dx + dy * dy >= SymmetricPairs.Range * SymmetricPairs.Range) continue;
                pairs++;
                near[points[i].Id]++;
                near[points[j].Id]++;
            }
        Assert.That(world.GetSingleton<BodyPairs>().Pairs, Is.EqualTo(pairs));
        foreach (var item in world.Read<BodyContacts>()) Assert.That(item.Component1.Count, Is.EqualTo(near[item.Entity.Id]));
    }

    private static World Items(int count, float extent, int kinds, int seed, bool tables)
    {
        var world = new World();
        var random = new Random(seed);
        world.Reserve<BodyAt, ItemStack>(count);
        for (int i = 0; i < count; i++)
        {
            var e = world.CreateEntity(new BodyAt((float)random.NextDouble() * extent, (float)random.NextDouble() * extent),
                new ItemStack(random.Next(kinds), 1 + random.Next(9)));
            if (tables && i % 4 == 0) e.Set(new BodyHeavy());
        }

        return world;
    }

    private static Dictionary<EntityId, ItemStack> Stacks(World world)
    {
        var result = new Dictionary<EntityId, ItemStack>();
        foreach (var item in world.Read<BodyAt, ItemStack>()) result[item.Entity.Id] = item.Component2;
        return result;
    }

    [TestCase(600, 8f, 3, false)]
    [TestCase(2000, 12f, 4, true)]
    public void ItemsMergeIntoTheLowerEntityInTheOrderOfTheTables(int count, float extent, int kinds, bool tables)
    {
        using var world = Items(count, extent, kinds, count, tables);
        using var twin = Items(count, extent, kinds, count, tables);
        var before = Stacks(world);
        var totals = before.Values.GroupBy(s => s.Kind).ToDictionary(g => g.Key, g => g.Sum(s => s.Count));
        // The pass sorts the rows first: the reference walks them in that order. A twin world merged by the pass equals.
        world.Update();
        twin.Update();
        var t = Tables<BodyAt, ItemStack>(world);
        var stacks = Stacks(world);
        Assume.That(stacks, Is.EqualTo(before));
        SymmetricPairs.Merge(world); // sorts, and merges: the reference starts from the sorted rows before it
        SymmetricPairs.Merge(twin);
        Same(Stacks(twin), Stacks(world), "the same world merges the same way");

        // A pass over the sorted rows against the half walk of them.
        using var again = Items(count, extent, kinds, count + 1, tables);
        SymmetricPairs.Merge(again); // sorted
        foreach (var item in again.Query<ItemStack>()) item.Component1 = new ItemStack(item.Entity.Id.Index % kinds, 1 + item.Entity.Id.Index % 5);
        var tablesAgain = Tables<BodyAt, ItemStack>(again);
        var expected = Stacks(again);
        HalfWalk(tablesAgain, (e, o) =>
        {
            var (s, u) = (expected[e.Id], expected[o.Id]);
            SymmetricPairs.Merge(e, e.Read<BodyAt>(), ref s, o.Read<BodyAt>(), ref u, o);
            (expected[e.Id], expected[o.Id]) = (s, u);
        });
        SymmetricPairs.Merge(again);
        Same(Stacks(again), expected, "the half walk");

        // Nothing is lost, and each kind's items gathered into fewer stacks.
        var after = Stacks(world);
        foreach (var (kind, total) in totals)
            Assert.That(after.Values.Where(s => s.Kind == kind).Sum(s => s.Count), Is.EqualTo(total), $"kind {kind}");
        Assert.That(after.Values.Count(s => s.Count > 0), Is.LessThanOrEqualTo(before.Count));
    }

    [Test]
    public void TwoStacksOfOneKindWithinRangeMergeIntoTheLowerEntity()
    {
        using var world = new World();
        var low = world.CreateEntity(new BodyAt(0, 0), new ItemStack(7, 3));
        var high = world.CreateEntity(new BodyAt(0.5f, 0), new ItemStack(7, 4));
        var other = world.CreateEntity(new BodyAt(0.2f, 0.2f), new ItemStack(8, 5)); // within range, another kind
        var far = world.CreateEntity(new BodyAt(9, 9), new ItemStack(7, 6)); // the same kind, out of range
        SymmetricPairs.Merge(world);
        Assert.That((low.Read<ItemStack>().Count, high.Read<ItemStack>().Count, other.Read<ItemStack>().Count, far.Read<ItemStack>().Count),
            Is.EqualTo((7, 0, 5, 6)));
    }

    [Test]
    public void ItemsWrittenAreStamped()
    {
        using var world = Items(500, 6f, 2, 3, true);
        SymmetricPairs.Merge(world);
        world.Update();
        ulong mark = world.ChangeTick;
        foreach (var item in world.Query<ItemStack>()) item.Component1 = new ItemStack(0, 1); // all mergeable again
        world.Update();
        mark = world.ChangeTick;
        SymmetricPairs.Merge(world);
        var since = QueryFilter.Create().Changed<ItemStack>(mark);
        Assert.That(world.Count<ItemStack>(since), Is.EqualTo(world.Count<ItemStack>()));
    }

    [Test]
    public void EmptyWorldsAndOneRowRunNothing()
    {
        using var world = new World();
        SymmetricPairs.Repel(world);
        var only = world.CreateEntity(new BodyAt(1, 1), new BodyForce());
        SymmetricPairs.Repel(world);
        SymmetricPairs.RepelRows(world);
        Assert.That(only.Read<BodyForce>(), Is.EqualTo(new BodyForce()));
    }

    [Test]
    public void APassAllocatesNothingOnceWarm()
    {
        using var world = Bodies(5000, 50f, 6, tables: true, sight: true);
        using var items = Items(2000, 20f, 3, 8, true);
        for (int i = 0; i < 200; i++) Passes(world, items); // re-sorts of the cadence, past tiering and OSR under every ISA
        Assert.That(Allocations.Of(() =>
        {
            for (int i = 0; i < 20; i++) Passes(world, items);
        }), Is.EqualTo(0));

        static void Passes(World world, World items)
        {
            SymmetricPairs.ResetForce(world);
            SymmetricPairs.Repel(world);
            SymmetricPairs.RepelRows(world);
            SymmetricPairs.Meet(world);
            SymmetricPairs.Sense(world);
            SymmetricPairs.Touch(world);
            world.Update();
            SymmetricPairs.Merge(items);
            items.Update();
        }
    }

    [Test]
    public void TheLoopsSayWhatTheyRun()
    {
        using var world = new World();
        world.AddSystem<SymmetricRules>();
        var lines = world.DescribeSchedule().Groups[0].Stages[0].Systems[0].Loops;
        string push = lines.Single(l => l.StartsWith("Push:")), merge = lines.Single(l => l.StartsWith("Gather:"));
        Assert.That(push, Does.Contain("each unordered pair once, both sides written").And.Contain("one pair at a time (it writes both sides of its pairs"));
        Assert.That(merge, Does.Contain("each unordered pair once").And.Contain("one pair at a time"));
    }
}
