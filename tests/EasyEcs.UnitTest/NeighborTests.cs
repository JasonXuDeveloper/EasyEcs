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
    private static World Crowd(int count, float extent, int seed, bool tables = false)
    {
        var world = new World();
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
}
