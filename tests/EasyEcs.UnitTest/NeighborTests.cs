using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public partial record struct PairSpot(float X, float Y) : IComponent;
public partial record struct PairDrift(float X, float Y) : IComponent;
public partial record struct PairShove(float X, float Y) : IComponent;
public partial record struct PairSight(float Range) : IComponent;
public partial record struct PairPrey(int Index, float Distance) : IComponent;
public partial record struct PairHunter : IComponent;
public partial record struct PairQuarry : IComponent;
public partial record struct PairSpot3(System.Numerics.Vector3 At) : IComponent;
public partial record struct PairCount(int Value) : IComponent;
public partial record struct PairReach(float Value) : ISingletonComponent;
public partial record struct PairTarget(EntityId Id, float Distance, bool Held) : IComponent;
public partial record struct PairLoot(bool Taken, int By, int Order) : IComponent, ITracked;
public partial record struct PairBag(int Count) : IComponent;
public partial record struct PairHeat(int Value) : IComponent, ITracked;

/// <summary>Pair methods ([Neighbor]): what the tests run against the pairs a loop over both sides would visit.</summary>
public static class Neighbors
{
    public const float Range = 1f;
    public const float Drag = 0.5f;

    /// <summary>The crowd's separation: a vector loop of a window of rows against each neighbor.</summary>
    [ForEach]
    public static void Separate(in PairSpot p, in PairDrift v, ref PairShove push, [Neighbor] in PairSpot q, [Neighbor] in PairDrift u)
    {
        float dx = p.X - q.X, dy = p.Y - q.Y, d2 = dx * dx + dy * dy;
        if (d2 >= Range * Range || d2 == 0) return;
        float w = Range * Range - d2;
        push.X += w * (dx + Drag * (u.X - v.X));
        push.Y += w * (dy + Drag * (u.Y - v.Y));
    }

    /// <summary>The same, one pair at a time (it takes the neighbor's entity): bit for bit the vector loop's.</summary>
    [ForEach]
    public static void SeparateRows(in PairSpot p, in PairDrift v, ref PairShove push, [Neighbor] in PairSpot q, [Neighbor] in PairDrift u, [Neighbor] Entity other)
    {
        float dx = p.X - q.X, dy = p.Y - q.Y, d2 = dx * dx + dy * dy;
        if (d2 >= Range * Range || d2 == 0 || other.Id.Index < 0) return;
        float w = Range * Range - d2;
        push.X += w * (dx + Drag * (u.X - v.X));
        push.Y += w * (dy + Drag * (u.Y - v.Y));
    }

    /// <summary>The same with no cutoff test the generator reads: every pair runs (the test is a call).</summary>
#pragma warning disable EECS106 // the point: every pair runs
    [ForEach]
    public static void SeparateAll(in PairSpot p, in PairDrift v, ref PairShove push, [Neighbor] in PairSpot q, [Neighbor] in PairDrift u)
    {
        float dx = p.X - q.X, dy = p.Y - q.Y, d2 = dx * dx + dy * dy;
        if (Far(d2) || d2 == 0) return;
        float w = Range * Range - d2;
        push.X += w * (dx + Drag * (u.X - v.X));
        push.Y += w * (dy + Drag * (u.Y - v.Y));
    }

#pragma warning restore EECS106

    private static bool Far(float d2) => d2 >= Range * Range;

    /// <summary>The test as the condition of the rest of the body, the cutoff a singleton.</summary>
    [ForEach]
    public static void Count(in PairSpot p, ref PairCount near, [Neighbor] in PairSpot q, in PairReach reach)
    {
        float dx = q.X - p.X;
        float dy = q.Y - p.Y;
        if (dx * dx + dy * dy < reach.Value * reach.Value) near.Value++;
    }

    /// <summary>A hunter's nearest quarry within its own sight (a cutoff per row), ties to the lower entity index.</summary>
    [ForEach]
    [With<PairHunter>]
    public static void Hunt(in PairSpot p, in PairSight sight, ref PairPrey prey, [Neighbor] in PairSpot q, [Neighbor] in PairQuarry _, [Neighbor] Entity other)
    {
        float dx = p.X - q.X, dy = p.Y - q.Y, d2 = dx * dx + dy * dy;
        if (d2 >= sight.Range * sight.Range || d2 > prey.Distance || d2 == prey.Distance && other.Id.Index >= prey.Index) return;
        prey = new PairPrey(other.Id.Index, d2);
    }

    /// <summary>Three axes through Vector3.DistanceSquared.</summary>
    [ForEach]
    public static void Near3(in PairSpot3 p, ref PairCount near, [Neighbor] in PairSpot3 q)
    {
        if (System.Numerics.Vector3.DistanceSquared(p.At, q.At) >= 4f) return;
        near.Value++;
    }

    /// <summary>
    /// Keep the current target while it lives within sight: a pass before the pair method reads it through a lookup and
    /// leaves the hint in the target component (a distance below any pair's), so the pair method keeps it; otherwise
    /// the target is dropped and the pair method picks the nearest.
    /// </summary>
    [ForEach]
    public static void Keep(in PairSpot p, in PairSight sight, ref PairTarget target, Lookup<PairSpot> spots, Lookup<PairQuarry> quarries)
    {
        if (target.Held && quarries.Has(target.Id))
        {
            ref readonly var q = ref spots.Read(target.Id);
            float dx = p.X - q.X, dy = p.Y - q.Y;
            if (dx * dx + dy * dy < sight.Range * sight.Range)
            {
                target.Distance = -1;
                return;
            }
        }

        target = new PairTarget(default, float.PositiveInfinity, false);
    }

    /// <summary>The nearest quarry within sight unless the hint keeps the current one; ties to the lower entity index.</summary>
    [ForEach]
    [With<PairHunter>]
    public static void Acquire(in PairSpot p, in PairSight sight, ref PairTarget target, [Neighbor] in PairSpot q, [Neighbor] in PairQuarry _, [Neighbor] Entity other)
    {
        float dx = p.X - q.X, dy = p.Y - q.Y, d2 = dx * dx + dy * dy;
        if (d2 >= sight.Range * sight.Range || d2 > target.Distance || d2 == target.Distance && other.Id.Index >= target.Id.Index) return;
        target = new PairTarget(other.Id, d2, true);
    }

    /// <summary>
    /// A pickup: each collector takes the loot within range nobody took before it, a write of the neighbor
    /// (<c>[Neighbor] ref</c>), so the pairs run one at a time in the order of the tables' rows.
    /// </summary>
    [ForEach]
    [With<PairHunter>]
    public static void Pick(Entity self, in PairSpot p, ref PairBag bag, [Neighbor] in PairSpot q, [Neighbor] ref PairLoot loot)
    {
        float dx = p.X - q.X, dy = p.Y - q.Y, d2 = dx * dx + dy * dy;
        if (d2 >= Range * Range || loot.Taken) return;
        loot = new PairLoot(true, self.Id.Index, bag.Count);
        bag.Count++;
    }

    /// <summary><see cref="Separate"/> on several threads (the vector loop over each chunk's windows).</summary>
    [ForEach(Parallel = true)]
    public static void SeparateParallel(in PairSpot p, in PairDrift v, ref PairShove push, [Neighbor] in PairSpot q, [Neighbor] in PairDrift u)
    {
        float dx = p.X - q.X, dy = p.Y - q.Y, d2 = dx * dx + dy * dy;
        if (d2 >= Range * Range || d2 == 0) return;
        float w = Range * Range - d2;
        push.X += w * (dx + Drag * (u.X - v.X));
        push.Y += w * (dy + Drag * (u.Y - v.Y));
    }

    /// <summary><see cref="SeparateRows"/> on several threads (one pair at a time).</summary>
    [ForEach(Parallel = true)]
    public static void SeparateRowsParallel(in PairSpot p, in PairDrift v, ref PairShove push, [Neighbor] in PairSpot q, [Neighbor] in PairDrift u, [Neighbor] Entity other)
    {
        float dx = p.X - q.X, dy = p.Y - q.Y, d2 = dx * dx + dy * dy;
        if (d2 >= Range * Range || d2 == 0 || other.Id.Index < 0) return;
        float w = Range * Range - d2;
        push.X += w * (dx + Drag * (u.X - v.X));
        push.Y += w * (dy + Drag * (u.Y - v.Y));
    }

    /// <summary><see cref="Hunt"/> on several threads: a cutoff per row, the neighbor's entity.</summary>
    [ForEach(Parallel = true)]
    [With<PairHunter>]
    public static void HuntParallel(in PairSpot p, in PairSight sight, ref PairPrey prey, [Neighbor] in PairSpot q, [Neighbor] in PairQuarry _, [Neighbor] Entity other)
    {
        float dx = p.X - q.X, dy = p.Y - q.Y, d2 = dx * dx + dy * dy;
        if (d2 >= sight.Range * sight.Range || d2 > prey.Distance || d2 == prey.Distance && other.Id.Index >= prey.Index) return;
        prey = new PairPrey(other.Id.Index, d2);
    }

    /// <summary><see cref="Count"/> on several threads: the cutoff from a singleton.</summary>
    [ForEach(Parallel = true)]
    public static void CountParallel(in PairSpot p, ref PairCount near, [Neighbor] in PairSpot q, in PairReach reach)
    {
        float dx = q.X - p.X;
        float dy = q.Y - p.Y;
        if (dx * dx + dy * dy < reach.Value * reach.Value) near.Value++;
    }

    /// <summary>A tracked component of the row written on several threads: every row the pass runs for is stamped.</summary>
#pragma warning disable EECS053 // the point: a ref parameter stamps every row the pass runs for
    [ForEach(Parallel = true)]
    public static void Heat(in PairSpot p, ref PairHeat heat, [Neighbor] in PairSpot q)
    {
        float dx = p.X - q.X, dy = p.Y - q.Y;
        if (dx * dx + dy * dy >= Range * Range) return;
        heat.Value++;
    }
#pragma warning restore EECS053

    [ForEach]
    public static void ResetLoot(ref PairLoot loot) => loot = default;

    [ForEach]
    public static void ResetBag(ref PairBag bag) => bag = default;

    [ForEach]
    public static void Reset(ref PairShove push) => push = default;

    [ForEach]
    public static void ResetCount(ref PairCount near) => near = default;

    [ForEach]
    public static void ResetPrey(ref PairPrey prey) => prey = new PairPrey(-1, float.PositiveInfinity);
}

/// <summary>
/// Pair methods ([Neighbor] parameters): the sums of the pass equal a loop over every pair of rows bit for bit (the
/// pairs outside the cutoff add nothing), the vector loop equals the method per pair, the cutoff per row, a cutoff
/// from a singleton, three axes, entities moving between passes (boxes left, lists drawn again), several tables,
/// presence bits and tombstones, NaN positions, no allocation once warm.
/// </summary>
public class NeighborTests
{
    private static World Crowd(int count, float extent, int seed, bool tables = false, int threads = 0)
    {
        var world = threads > 0 ? new World(new WorldOptions { MaxDegreeOfParallelism = threads }) : new World();
        var random = new Random(seed);
        world.Reserve<PairSpot, PairDrift, PairShove>(count);
        for (int i = 0; i < count; i++)
        {
            var e = world.CreateEntity(new PairSpot((float)random.NextDouble() * extent, (float)random.NextDouble() * extent),
                new PairDrift((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f), new PairShove());
            if (tables && i % 3 == 0) e.Set(new PairCount(i)); // a second table
        }

        return world;
    }

    /// <summary>The method's sums over every pair, rows in the order the pass keeps them (its tables after the sort).</summary>
    private static Dictionary<EntityId, PairShove> Reference(World world)
    {
        var rows = new List<(EntityId Id, PairSpot P, PairDrift V)>();
        foreach (var item in world.Query<PairSpot, PairDrift>()) rows.Add((item.Entity.Id, item.Component1, item.Component2));
        var result = new Dictionary<EntityId, PairShove>();
        foreach (var (id, p, v) in rows)
        {
            var push = new PairShove();
            foreach (var (_, q, u) in rows) Neighbors.Separate(in p, in v, ref push, in q, in u);
            result[id] = push;
        }

        return result;
    }

    private static List<Entity> Entities<T>(World world) where T : struct, IComponent
    {
        var list = new List<Entity>();
        foreach (var item in world.Query<T>()) list.Add(item.Entity);
        return list;
    }

    private static Dictionary<EntityId, PairShove> Pushes(World world)
    {
        var result = new Dictionary<EntityId, PairShove>();
        foreach (var item in world.Query<PairShove>()) result[item.Entity.Id] = item.Component1;
        return result;
    }

    [TestCase(1, 4f)]
    [TestCase(7, 4f)]
    [TestCase(200, 10f)]
    [TestCase(2000, 40f)]
    [TestCase(2000, 2f)]
    public void SumsEqualEveryPairBitForBit(int count, float extent)
    {
        using var world = Crowd(count, extent, count);
        Neighbors.Separate(world); // sorts the rows: the reference walks them in that order
        Neighbors.Reset(world);
        Neighbors.Separate(world);
        var expected = Reference(world);
        var got = Pushes(world);
        Assert.That(got.Count, Is.EqualTo(count));
        foreach (var (id, push) in expected) Assert.That(got[id], Is.EqualTo(push), $"entity {id}");
    }

    [Test]
    public void VectorLoopEqualsPairsOneAtATimeAndEveryPair()
    {
        using var world = Crowd(3000, 30f, 3, tables: true);
        Neighbors.Separate(world);
        Neighbors.Reset(world);
        Neighbors.Separate(world);
        var vector = Pushes(world);
        Neighbors.Reset(world);
        Neighbors.SeparateRows(world);
        Assert.That(Pushes(world), Is.EqualTo(vector));
        Neighbors.Reset(world);
        Neighbors.SeparateAll(world);
        Assert.That(Pushes(world), Is.EqualTo(vector));
    }

    [Test]
    public void SeveralTablesTombstonesAndAbsentRowsAreRight()
    {
        using var world = Crowd(1500, 12f, 5, tables: true);
        var all = Entities<PairSpot>(world);
        for (int i = 0; i < all.Count; i += 7) all[i].Destroy();
        for (int i = 3; i < all.Count; i += 11)
            if (all[i].IsAlive) all[i].Disable<PairDrift>(); // fails both filters: no row of it runs, no neighbor reads it
        for (int pass = 0; pass < 3; pass++)
        {
            Neighbors.Reset(world);
            Neighbors.Separate(world);
            var got = Pushes(world);
            var expected = Reference(world);
            foreach (var (id, push) in expected) Assert.That(got[id], Is.EqualTo(push), $"entity {id}");
            Neighbors.Reset(world);
            Neighbors.SeparateRows(world);
            Assert.That(Pushes(world), Is.EqualTo(got), "the vector loop equals the pairs one at a time");
        }
    }

    [Test]
    public void MovingRowsKeepEveryPairWithinTheCutoff()
    {
        using var world = Crowd(1200, 20f, 9);
        var random = new Random(1);
        for (int tick = 0; tick < 40; tick++)
        {
            // Some rows jump far (their windows' boxes are left), the others drift: every pass equals every pair.
            foreach (var item in world.Query<PairSpot>())
            {
                ref var p = ref item.Component1;
                if (random.Next(50) == 0) p = new PairSpot((float)random.NextDouble() * 20, (float)random.NextDouble() * 20);
                else p = new PairSpot(p.X + 0.05f * ((float)random.NextDouble() - 0.5f), p.Y + 0.05f);
            }
            world.Update(); // the order's cadence sorts every 10 updates
            Neighbors.Reset(world);
            Neighbors.Separate(world);
            var got = Pushes(world);
            Neighbors.Reset(world);
            Neighbors.SeparateAll(world);
            Assert.That(got, Is.EqualTo(Pushes(world)), $"tick {tick}");
        }
    }

    [Test]
    public void CutoffFromASingletonAndTheTestAsTheBodysCondition()
    {
        using var world = Crowd(800, 10f, 11);
        foreach (var e in Entities<PairSpot>(world)) e.Set(new PairCount());
        world.GetOrAddSingleton<PairReach>() = new PairReach(0.75f);
        Neighbors.Count(world);
        var spots = Entities<PairSpot>(world).Select(e => e.Get<PairSpot>()).ToList();
        foreach (var item in world.Query<PairSpot, PairCount>())
        {
            var p = item.Component1;
            int expected = spots.Count(s => (s.X - p.X) * (s.X - p.X) + (s.Y - p.Y) * (s.Y - p.Y) < 0.75f * 0.75f);
            Assert.That(item.Component2.Value, Is.EqualTo(expected));
        }
    }

    [Test]
    public void NearestWithinEachRowsSightAcrossTwoKindsOfEntity()
    {
        using var world = new World();
        var random = new Random(4);
        var quarries = new List<Entity>();
        for (int i = 0; i < 600; i++)
            quarries.Add(world.CreateEntity(new PairSpot((float)random.NextDouble() * 30, (float)random.NextDouble() * 30), new PairQuarry()));
        // Two quarries at one point: the lower index wins the tie, whatever the order.
        quarries.Add(world.CreateEntity(quarries[5].Get<PairSpot>(), new PairQuarry()));
        var hunters = new List<Entity>();
        for (int i = 0; i < 300; i++)
            hunters.Add(world.CreateEntity(new PairSpot((float)random.NextDouble() * 30, (float)random.NextDouble() * 30),
                new PairSight(0.5f + (float)random.NextDouble() * (i % 10 == 0 ? 20 : 2)), new PairPrey(), new PairHunter()));
        hunters.Add(world.CreateEntity(quarries[5].Get<PairSpot>(), new PairSight(1), new PairPrey(), new PairHunter()));
        for (int pass = 0; pass < 2; pass++)
        {
            Neighbors.ResetPrey(world);
            Neighbors.Hunt(world);
            foreach (var hunter in hunters)
            {
                var p = hunter.Get<PairSpot>();
                float sight = hunter.Get<PairSight>().Range;
                int best = -1;
                float bestD = float.PositiveInfinity;
                foreach (var quarry in quarries)
                {
                    var q = quarry.Get<PairSpot>();
                    float dx = p.X - q.X, dy = p.Y - q.Y, d2 = dx * dx + dy * dy;
                    if (d2 >= sight * sight || d2 > bestD || d2 == bestD && quarry.Id.Index >= best) continue;
                    (best, bestD) = (quarry.Id.Index, d2);
                }
                Assert.That(hunter.Get<PairPrey>().Index, Is.EqualTo(best));
            }
        }
        Assert.That(hunters[^1].Get<PairPrey>().Index, Is.EqualTo(Math.Min(quarries[5].Id.Index, quarries[^1].Id.Index)));
    }

    [Test]
    public void TheCurrentTargetIsKeptWhileInSightThroughAHint()
    {
        using var world = new World();
        var random = new Random(9);
        var quarries = new List<Entity>();
        for (int i = 0; i < 400; i++)
            quarries.Add(world.CreateEntity(new PairSpot((float)random.NextDouble() * 20, (float)random.NextDouble() * 20), new PairQuarry()));
        var hunters = new List<Entity>();
        for (int i = 0; i < 200; i++)
            hunters.Add(world.CreateEntity(new PairSpot((float)random.NextDouble() * 20, (float)random.NextDouble() * 20),
                new PairSight(1.5f), new PairTarget(default, float.PositiveInfinity, false), new PairHunter()));
        var held = new Dictionary<EntityId, EntityId>();
        for (int pass = 0; pass < 6; pass++)
        {
            Neighbors.Keep(world);
            Neighbors.Acquire(world);
            foreach (var hunter in hunters)
            {
                var p = hunter.Get<PairSpot>();
                var target = hunter.Get<PairTarget>();
                float r2 = 1.5f * 1.5f;
                // Kept: the target of the last pass when it still lives within sight, whatever is nearer now.
                if (held.TryGetValue(hunter.Id, out var before) && world.IsAlive(before) && Within(world.GetEntity(before)))
                {
                    Assert.That(target.Id, Is.EqualTo(before));
                    continue;
                }

                EntityId best = default;
                float bestD = float.PositiveInfinity;
                bool any = false;
                foreach (var quarry in quarries)
                {
                    if (!quarry.IsAlive) continue;
                    var q = quarry.Get<PairSpot>();
                    float dx = p.X - q.X, dy = p.Y - q.Y, d2 = dx * dx + dy * dy;
                    if (d2 >= r2 || d2 > bestD || d2 == bestD && quarry.Id.Index >= best.Index) continue;
                    (best, bestD, any) = (quarry.Id, d2, true);
                }
                Assert.That(target.Held, Is.EqualTo(any));
                if (any) Assert.That(target.Id, Is.EqualTo(best));

                bool Within(Entity quarry)
                {
                    var q = quarry.Get<PairSpot>();
                    float dx = p.X - q.X, dy = p.Y - q.Y;
                    return dx * dx + dy * dy < r2;
                }
            }

            held.Clear();
            foreach (var hunter in hunters)
                if (hunter.Get<PairTarget>() is { Held: true } t) held[hunter.Id] = t.Id;
            // The world moves on: quarries step, a few die, so targets leave sight and nearer ones appear.
            foreach (var quarry in quarries)
                if (quarry.IsAlive) quarry.Get<PairSpot>() = new PairSpot(quarry.Get<PairSpot>().X + (float)random.NextDouble() - 0.5f, quarry.Get<PairSpot>().Y + (float)random.NextDouble() - 0.5f);
            for (int i = pass; i < quarries.Count; i += 37)
                if (quarries[i].IsAlive) quarries[i].Destroy();
        }
    }

    [Test]
    public void ThreeAxesThroughDistanceSquared()
    {
        using var world = new World();
        var random = new Random(8);
        for (int i = 0; i < 1000; i++)
            world.CreateEntity(new PairSpot3(new System.Numerics.Vector3((float)random.NextDouble() * 12, (float)random.NextDouble() * 12, (float)random.NextDouble() * 12)), new PairCount());
        Neighbors.Near3(world);
        var all = Entities<PairSpot3>(world).Select(e => e.Get<PairSpot3>().At).ToList();
        foreach (var e in Entities<PairSpot3>(world))
        {
            var at = e.Get<PairSpot3>().At;
            Assert.That(e.Get<PairCount>().Value, Is.EqualTo(all.Count(q => System.Numerics.Vector3.DistanceSquared(at, q) < 4f)));
        }
    }

    [Test]
    public void NaNPositionsMeetEveryRow()
    {
        using var world = Crowd(300, 5f, 2);
        Entities<PairSpot>(world)[0].Set(new PairSpot(float.NaN, 1));
        Neighbors.Separate(world);
        var got = Pushes(world);
        Neighbors.Reset(world);
        Neighbors.SeparateAll(world);
        Assert.That(got, Is.EqualTo(Pushes(world)));
    }

    [Test]
    public void EmptyWorldsAndWorldsWithoutNeighborsRunNothing()
    {
        using var world = new World();
        Neighbors.Separate(world);
        world.CreateEntity(new PairSpot(1, 1), new PairShove()); // no PairDrift: neither side
        Neighbors.Separate(world);
        Assert.That(Entities<PairShove>(world)[0].Get<PairShove>(), Is.EqualTo(new PairShove()));
    }

    [Test]
    public void APassAllocatesNothingOnceWarm()
    {
        using var world = Crowd(5000, 50f, 6, tables: true);
        for (int i = 0; i < 12; i++) // through one re-sort of the order's cadence (10 updates)
        {
            Neighbors.Reset(world);
            Neighbors.Separate(world);
            world.Update();
        }

        Assert.That(Allocations.Of(() =>
        {
            for (int i = 0; i < 20; i++)
            {
                Neighbors.Reset(world);
                Neighbors.Separate(world);
                world.Update();
            }
        }), Is.EqualTo(0));
    }

    // Equal entry by entry: the first difference named (a large dictionary's mismatch message takes minutes).
    private static void Same(Dictionary<EntityId, PairShove> got, Dictionary<EntityId, PairShove> expected, string what)
    {
        Assert.That(got.Count, Is.EqualTo(expected.Count), what);
        int wrong = 0;
        string first = "";
        foreach (var (id, push) in expected)
            if (!got.TryGetValue(id, out var other) || other != push)
            {
                if (wrong++ == 0) first = $"{id}: {other} instead of {push}";
            }
        Assert.That(wrong, Is.Zero, $"{what}: {wrong} rows differ, first {first}");
    }

    [TestCase(1, 30000, 60f)]
    [TestCase(4, 30000, 60f)]
    [TestCase(4, 20000, 6f)]   // dense: heavy windows
    [TestCase(4, 900, 10f)]    // one chunk
    public void ParallelPassesEqualTheSerialOnesBitForBit(int threads, int count, float extent)
    {
        using var world = Crowd(count, extent, count, tables: true, threads: threads);
        for (int pass = 0; pass < 3; pass++)
        {
            Neighbors.Reset(world);
            Neighbors.Separate(world);
            var serial = Pushes(world);
            Neighbors.Reset(world);
            Neighbors.SeparateParallel(world);
            Same(Pushes(world), serial, "the vector loop");
            Neighbors.Reset(world);
            Neighbors.SeparateRowsParallel(world);
            Same(Pushes(world), serial, "one pair at a time");
            foreach (var item in world.Query<PairSpot>()) item.Component1 = new PairSpot(item.Component1.X + 0.2f, item.Component1.Y);
            world.Update();
        }
    }

    [Test]
    public void ParallelCutoffsPerRowAndFromASingleton()
    {
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4 });
        var random = new Random(12);
        world.Reserve<PairSpot, PairQuarry>(20000);
        world.Reserve<PairSpot, PairSight, PairPrey, PairHunter, PairCount>(12000);
        for (int i = 0; i < 20000; i++)
            world.CreateEntity(new PairSpot((float)random.NextDouble() * 80, (float)random.NextDouble() * 80), new PairQuarry());
        for (int i = 0; i < 12000; i++)
            world.CreateEntity(new PairSpot((float)random.NextDouble() * 80, (float)random.NextDouble() * 80),
                new PairSight(0.5f + (float)random.NextDouble() * 3), new PairPrey(), new PairHunter(), new PairCount());
        world.GetOrAddSingleton<PairReach>() = new PairReach(0.75f);
        Neighbors.ResetPrey(world);
        Neighbors.Hunt(world);
        Neighbors.Count(world);
        var serial = new Dictionary<EntityId, (PairPrey, PairCount)>();
        foreach (var item in world.Query<PairPrey, PairCount>()) serial[item.Entity.Id] = (item.Component1, item.Component2);
        Neighbors.ResetPrey(world);
        Neighbors.ResetCount(world);
        Neighbors.HuntParallel(world);
        Neighbors.CountParallel(world);
        foreach (var item in world.Query<PairPrey, PairCount>())
            Assert.That((item.Component1, item.Component2), Is.EqualTo(serial[item.Entity.Id]));
    }

    [Test]
    public void AParallelPassStampsEveryRowItRunsFor()
    {
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4 });
        var random = new Random(13);
        world.Reserve<PairSpot, PairHeat>(30000);
        var all = new List<Entity>();
        for (int i = 0; i < 30000; i++)
            all.Add(world.CreateEntity(new PairSpot((float)random.NextDouble() * 60, (float)random.NextDouble() * 60), new PairHeat()));
        Neighbors.Heat(world); // sorts
        for (int i = 0; i < all.Count; i += 5) all[i].Disable<PairHeat>(); // absent rows: not run for, not stamped
        world.Update();
        ulong mark = world.ChangeTick;
        var since = QueryFilter.Create().Changed<PairHeat>(mark);
        Assert.That(world.Count<PairHeat>(since), Is.Zero);
        Neighbors.Heat(world);
        Assert.That(world.Count<PairHeat>(since), Is.EqualTo(world.Count<PairHeat>()));
    }

    [Test]
    public void AParallelPassAllocatesNothingOnceWarm()
    {
        using var world = Crowd(30000, 60f, 6, tables: true, threads: 4);
        for (int i = 0; i < 12; i++)
        {
            Neighbors.Reset(world);
            Neighbors.SeparateParallel(world);
            world.Update();
        }

        Assert.That(Allocations.Of(() =>
        {
            for (int i = 0; i < 20; i++)
            {
                Neighbors.Reset(world);
                Neighbors.SeparateParallel(world);
                world.Update();
            }
        }), Is.EqualTo(0));
    }

    private static World Pickup(int collectors, int loot, float extent, int seed, bool tables)
    {
        var world = new World();
        var random = new Random(seed);
        world.Reserve<PairSpot, PairBag, PairHunter>(collectors);
        world.Reserve<PairSpot, PairLoot>(loot);
        for (int i = 0; i < collectors; i++)
            world.CreateEntity(new PairSpot((float)random.NextDouble() * extent, (float)random.NextDouble() * extent), new PairBag(), new PairHunter());
        for (int i = 0; i < loot; i++)
        {
            var e = world.CreateEntity(new PairSpot((float)random.NextDouble() * extent, (float)random.NextDouble() * extent), new PairLoot());
            if (tables && i % 3 == 0) e.Set(new PairCount(i)); // a second table of loot
        }

        return world;
    }

    private static void ResetPickup(World world)
    {
        Neighbors.ResetLoot(world);
        Neighbors.ResetBag(world);
    }

    [TestCase(1, 1, 1f)]
    [TestCase(300, 900, 20f)]
    [TestCase(500, 500, 4f)]
    public void WritesOfTheNeighborRunInTheOrderOfALoopOverBothTables(int collectors, int loot, float extent)
    {
        using var world = Pickup(collectors, loot, extent, collectors + loot, false);
        Neighbors.Pick(world); // sorts the rows: the reference walks them in that order
        ResetPickup(world);

        var rows = new List<(Entity E, PairSpot P)>();
        foreach (var item in world.Query<PairSpot, PairBag>()) rows.Add((item.Entity, item.Component1));
        var items = new List<(Entity E, PairSpot P)>();
        foreach (var item in world.Read<PairSpot, PairLoot>()) items.Add((item.Entity, item.Component1));
        var expected = new Dictionary<EntityId, PairLoot>();
        foreach (var (e, _) in items) expected[e.Id] = default;
        var bags = new Dictionary<EntityId, PairBag>();
        foreach (var (e, p) in rows)
        {
            var bag = new PairBag();
            foreach (var (o, q) in items)
            {
                var l = expected[o.Id];
                Neighbors.Pick(e, in p, ref bag, in q, ref l);
                expected[o.Id] = l;
            }
            bags[e.Id] = bag;
        }

        world.Update();
        ulong mark = world.ChangeTick;
        var since = QueryFilter.Create().Changed<PairLoot>(mark);
        Assert.That(world.Count<PairLoot>(since), Is.Zero);
        Neighbors.Pick(world);
        foreach (var (e, _) in items) Assert.That(e.Read<PairLoot>(), Is.EqualTo(expected[e.Id]), $"loot {e.Id}");
        foreach (var (e, _) in rows) Assert.That(e.Get<PairBag>(), Is.EqualTo(bags[e.Id]), $"collector {e.Id}");
        // The loot written is stamped: a reader of the changes sees every taken one.
        var changed = new HashSet<EntityId>();
        foreach (var item in world.Read<PairLoot>(since)) changed.Add(item.Entity.Id);
        foreach (var (e, _) in items)
            if (e.Read<PairLoot>().Taken) Assert.That(changed, Does.Contain(e.Id));
    }

    [Test]
    public void WritesOfTheNeighborAcrossTablesTakeEachLootOnce()
    {
        using var world = Pickup(400, 1200, 25f, 3, true);
        for (int pass = 0; pass < 3; pass++)
        {
            ResetPickup(world);
            Neighbors.Pick(world);
            var collectors = new List<(Entity E, PairSpot P)>();
            foreach (var item in world.Query<PairSpot, PairBag>()) collectors.Add((item.Entity, item.Component1));
            int taken = 0;
            var orders = new HashSet<(int, int)>();
            foreach (var item in world.Read<PairSpot, PairLoot>())
            {
                var (q, l) = (item.Component1, item.Component2);
                bool near = collectors.Any(c => (c.P.X - q.X) * (c.P.X - q.X) + (c.P.Y - q.Y) * (c.P.Y - q.Y) < Neighbors.Range * Neighbors.Range);
                Assert.That(l.Taken, Is.EqualTo(near), "loot within a collector's range is taken, other loot is not");
                if (!l.Taken) continue;
                taken++;
                var by = collectors.Single(c => c.E.Id.Index == l.By);
                Assert.That((by.P.X - q.X) * (by.P.X - q.X) + (by.P.Y - q.Y) * (by.P.Y - q.Y), Is.LessThan(Neighbors.Range * Neighbors.Range));
                Assert.That(orders.Add((l.By, l.Order)), Is.True, "each collector numbers its loot once");
            }
            Assert.That(collectors.Sum(c => c.E.Get<PairBag>().Count), Is.EqualTo(taken));
            // The world moves on.
            foreach (var item in world.Query<PairSpot>())
                item.Component1 = new PairSpot(item.Component1.X + 0.3f, item.Component1.Y - 0.2f);
            world.Update();
        }
    }

    [Test]
    public void AWritingPassAllocatesNothingOnceWarm()
    {
        using var world = Pickup(1000, 3000, 40f, 6, true);
        for (int i = 0; i < 12; i++)
        {
            ResetPickup(world);
            Neighbors.Pick(world);
            world.Update();
        }

        Assert.That(Allocations.Of(() =>
        {
            for (int i = 0; i < 20; i++)
            {
                ResetPickup(world);
                Neighbors.Pick(world);
                world.Update();
            }
        }), Is.EqualTo(0));
    }
}
