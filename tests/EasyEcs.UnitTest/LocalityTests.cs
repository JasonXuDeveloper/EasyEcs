using System;
using EasyEcs.Benchmark.Locality;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>
/// The phase 0 prototypes of docs/plans/locality.md and borrowed-ideas.md item 2 (benchmarks/EasyEcs.Benchmark/Locality,
/// linked here): every version computes what the plain form computes, and a tick allocates nothing once warm.
/// </summary>
public class LocalityTests
{
    // The pair sums: every version within 1e-5 * (1 + the sum of the terms' magnitudes) of a double-precision sum over
    // the same pairs (each term is the same float math; only the order of the sum differs).
    [TestCase(1_000, Density.Crowd)]
    [TestCase(3_000, Density.Crowd)]
    [TestCase(3_000, Density.Sparse)]
    public void PairVersionsAgree(int entities, Density density)
    {
        using var world = Crowd.Create(entities, density);
        var expected = Crowd.Reference(world);

        Crowd.GridTick(world);
        Assert.That(Crowd.FirstMismatch(Crowd.Pushes(world), expected), Is.EqualTo(-1), "grid through lookups");
        Crowd.CopiedGridTick(world);
        Assert.That(Crowd.FirstMismatch(Crowd.Pushes(world), expected), Is.EqualTo(-1), "grid with copies");
        Crowd.BuildLists(world);
        Crowd.Interact(world);
        Assert.That(Crowd.FirstMismatch(Crowd.Pushes(world), expected), Is.EqualTo(-1), "neighbor lists");
        Crowd.Sort(world);
        world.GetSingleton<Crowd.Clusters>().Rows.Interact();
        Crowd.WriteBack(world);
        Assert.That(Crowd.FirstMismatch(Crowd.Pushes(world), expected), Is.EqualTo(-1), "cluster kernel");
        Crowd.ClusterTickWithCopies(world);
        Assert.That(Crowd.FirstMismatch(Crowd.Pushes(world), expected), Is.EqualTo(-1), "cluster kernel with copies");
    }

    [Test]
    public void PairTicksAllocateNothing()
    {
        using var world = Crowd.Create(2_000, Density.Crowd);
        var rows = world.GetSingleton<Crowd.Clusters>().Rows;
        Action tick = () =>
        {
            Crowd.GridTick(world);
            Crowd.CopiedGridTick(world);
            Crowd.BuildLists(world);
            Crowd.Interact(world);
            Crowd.Sort(world);
            rows.Interact();
            Crowd.ClusterTickWithCopies(world);
        };
        tick();
        tick();
        Assert.That(Allocations.Of(tick), Is.Zero);
    }

    // The block search finds a player at the same distance as the ring walk (the ids differ only on ties).
    [TestCase(50, Layout.Packed)]
    [TestCase(50, Layout.Spread)]
    [TestCase(400, Layout.Packed)]
    [TestCase(400, Layout.Spread)]
    public void ZombieSearchesAgree(int players, Layout layout)
    {
        using var world = ZombieHunt.Create(players, layout);
        ZombieHunt.Pick(world);
        var expected = ZombieHunt.Targets(world);
        ZombieHunt.SortPlayers(world);
        ZombieHunt.PickInBlocks(world);
        var got = ZombieHunt.Targets(world);
        int found = 0;
        for (int i = 0; i < got.Length; i++)
        {
            Assert.That(got[i].Distance, Is.EqualTo(expected[i].Distance), $"zombie {i}");
            if (got[i].Distance >= 0) found++;
        }

        Assert.That(found, Is.GreaterThan(0));
        Assert.That(Allocations.Of(() =>
        {
            ZombieHunt.Pick(world);
            ZombieHunt.SortPlayers(world);
            ZombieHunt.PickInBlocks(world);
        }), Is.Zero);
    }

    [Test]
    public void NearestPlayerSearchesAgree()
    {
        using var world = PlayerSearch.Create(3_000);
        PlayerSearch.Sort(world);
        var random = new Random(3);
        for (int q = 0; q < 200; q++)
        {
            var at = new PlayerSearch.Position(random.Next(4096), random.Next(4096));
            Assert.That(PlayerSearch.DistanceTo(world, PlayerSearch.Search(world, at), at),
                Is.EqualTo(PlayerSearch.DistanceTo(world, PlayerSearch.Scan(world, at).Id, at)));
        }
    }

    // Every way of waking the waiting entities does the same work on the same ticks.
    [Test]
    public void TimerVersionsAgree()
    {
        int[] expected = null;
        foreach (var tick in new Action<World>[] { Timers.ScanTick, Timers.ScanScalarTick, Timers.CountdownTick, Timers.WheelTick, Timers.WheelTagTick })
        {
            using var world = Timers.Create(5_000);
            for (int t = 0; t < 400; t++) tick(world);
            var got = Timers.Counts(world);
            Assert.That(got, Has.Some.GreaterThan(1));
            expected ??= got;
            Assert.That(got, Is.EqualTo(expected), tick.Method.Name);
            Assert.That(Allocations.Of(() => tick(world)), Is.Zero, tick.Method.Name);
        }
    }

    [TestCase(10)]
    [TestCase(50)]
    [TestCase(90)]
    public void SelectionFormsAgree(int percent)
    {
        var values = Selection.Column(1_003);
        float limit = percent / 100f;
        var expected = Selection.RowList(values.Length);
        int n = Selection.Branchy(values, limit, expected);
        var numbers = new int[values.Length];
        for (int i = 0; i < numbers.Length; i++) numbers[i] = i;
        foreach (var form in new Func<int[], int>[]
                 {
                     into => Selection.Branchless(values, limit, into),
                     into => Selection.PermutationTable(values, limit, into),
                     into => Selection.Compress512(values, limit, into),
                     into => Selection.CoreCompact(values, limit, numbers, into),
                 })
        {
            var got = Selection.RowList(values.Length);
            Assert.That(form(got), Is.EqualTo(n));
            Assert.That(got.AsSpan(0, n).ToArray(), Is.EqualTo(expected.AsSpan(0, n).ToArray()));
        }
    }

    [TestCase(1_000)]
    [TestCase(20_000)]
    public void ProbeFormsAgree(int keys)
    {
        var (table, queries) = ProbeTable.Create(keys, 1_027);
        var expected = new int[queries.Length];
        table.OneAtATime(queries, expected);
        var got = new int[queries.Length];
        table.Vertical(queries, got);
        Assert.That(got, Is.EqualTo(expected));
        Array.Clear(got);
        table.VerticalRefill(queries, got, new int[queries.Length + 8], new int[queries.Length + 8]);
        Assert.That(got, Is.EqualTo(expected));
    }
}
