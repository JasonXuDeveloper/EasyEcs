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
        var clusters = world.GetSingleton<Crowd.Clusters>().Rows.Clusters;
        for (int s = 1; s < clusters.Count; s++)
            Assert.That(clusters.Keys[s], Is.GreaterThanOrEqualTo(clusters.Keys[s - 1]), "rows in Morton order");
        float widest = 0;
        for (int b = 0; b < clusters.ClusterCount; b++)
            widest += clusters.MaxX[0][b] - clusters.MinX[0][b] + clusters.MaxY[0][b] - clusters.MinY[0][b];
        // Clusters of 8 consecutive rows on the curve are small: on average about the side of the square 8 rows cover.
        float rowsPerUnit2 = clusters.Count / (world.GetSingleton<Crowd.Map>().Extent * world.GetSingleton<Crowd.Map>().Extent);
        Assert.That(widest / clusters.ClusterCount / 2, Is.LessThan(4 * MathF.Sqrt(8 / rowsPerUnit2)), "cluster boxes");
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

    // The cluster search finds a player at the same distance as the ring walk (the ids differ only on ties).
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
        ZombieHunt.PickInClusters(world);
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
            ZombieHunt.PickInClusters(world);
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

    [TestCase(Density.Crowd)]
    [TestCase(Density.Sparse)]
    public void ClustersOnTheCoreOrderMatchTheReference(Density density)
    {
        using var world = Crowd.Create(3_000, density);
        CoreClusters.Order(world, int.MaxValue);
        var expected = Crowd.Reference(world);
        var clusters = new CoreClusters();
        foreach (var tick in new Action[] { () => clusters.Interact(world), () => clusters.Interact16(world) })
        {
            tick();
            Assert.That(Crowd.FirstMismatch(Crowd.Pushes(world), expected), Is.EqualTo(-1));
        }

        clusters.Interact(world);
        Assert.That(Allocations.Of(() => clusters.Interact(world)), Is.Zero);
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
                     into => Selection.ShuffleTable(values, limit, into),
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
        foreach (int distance in new[] { 1, 16, 2_000 })
        {
            Array.Clear(got);
            table.Prefetched(queries, got, distance);
            Assert.That(got, Is.EqualTo(expected), $"prefetched {distance} ahead");
        }
    }

    // borrowed-ideas.md item 1: the chase jobs, with and without prefetches, write what the plain method writes, bit
    // for bit (runs shorter than the distance, an odd tail of rows, dead and recycled targets), and allocate nothing.
    [TestCase(1)]
    [TestCase(13)]
    [TestCase(1_000)]
    [TestCase(20_011)]
    public void ChaseVersionsAgree(int entities)
    {
        using var world = Chase.Create(entities);
        // Dead targets (a version that no longer matches) and a recycled id whose new entity lacks Position.
        if (entities > 10)
        {
            Entity hunter = default;
            foreach (var e in world.Entities)
                if (e.Index == 3) hunter = e;
            world.GetEntity(hunter.Read<Chase.Prey>().Target).Destroy();
            world.CreateEntity(new Chase.Heading());
        }

        Chase.Hunt(world);
        var expected = Chase.Headings(world);
        foreach (var (name, run) in new (string, Action<World>)[]
                 {
                     ("rows", w => Chase.Rows(w, 0)), ("prefetched 1", w => Chase.Rows(w, 1)),
                     ("prefetched 16", w => Chase.Rows(w, 16)), ("records only", w => Chase.Rows(w, 16, rows: false)),
                     ("generated gathers", Chase.HuntGathered), ("gathers", w => Chase.Gathered(w, 0)),
                     ("gathers prefetched", w => Chase.Gathered(w, 4)),
                     ("loads ahead 2", w => Chase.LoadAhead(w, 2)), ("loads ahead 8", w => Chase.LoadAhead(w, 8)),
                     ("loads ahead 64", w => Chase.LoadAhead(w, 64)),
                 })
        {
            Clear(world);
            run(world);
            Assert.That(Chase.Headings(world), Is.EqualTo(expected), name);
            Assert.That(Allocations.Of(() => run(world)), Is.Zero, name);
        }

        static void Clear(World world)
        {
            var job = new ClearHeadings();
            world.ForEachBatch<Chase.Heading, ClearHeadings>(ref job);
        }
    }

    private struct ClearHeadings : IForEachBatch<Chase.Heading>
    {
        public void Execute(EntitySpan entities, Span<Chase.Heading> heading) => heading.Clear();
    }

    [Test]
    public void PrefetchesOfAnyIdAreHarmless()
    {
        using var world = Chase.Create(10);
        var lookup = new Lookup<Chase.Position>(world, null);
        var dead = world.CreateEntity(new Chase.Position(1, 1));
        var id = dead.Id;
        dead.Destroy();
        var tagOnly = world.CreateEntity(new Chase.Heading()).Id;
        foreach (var target in new[] { EntityId.Null, id, tagOnly, new EntityId(int.MaxValue, 1), new EntityId(-1, 1) })
        {
            lookup.Prefetch(target);
            lookup.PrefetchRow(target);
        }

        Assert.That(LookupLanes.CanPrefetch, Is.EqualTo(Prefetching.IsSupported));
    }

    // Streaming stores write what plain stores write, every length (whole vectors, pairs of them, a tail).
    [TestCase(0), TestCase(1), TestCase(15), TestCase(16), TestCase(33), TestCase(1000)]
    public unsafe void StreamingStoresWriteWhatPlainStoresWrite(int n)
    {
        var src = (float*)System.Runtime.InteropServices.NativeMemory.AlignedAlloc((nuint)Math.Max(n, 1) * sizeof(float), 64);
        var plain = (float*)System.Runtime.InteropServices.NativeMemory.AlignedAlloc((nuint)Math.Max(n, 1) * sizeof(float), 64);
        var streamed = (float*)System.Runtime.InteropServices.NativeMemory.AlignedAlloc((nuint)Math.Max(n, 1) * sizeof(float), 64);
        try
        {
            for (int i = 0; i < n; i++) src[i] = i * 0.37f - 5;
            Streams.Plain(src, plain, n, 1.5f, 2f);
            Streams.NonTemporal(src, streamed, n, 1.5f, 2f);
            Assert.That(new ReadOnlySpan<float>(streamed, n).SequenceEqual(new ReadOnlySpan<float>(plain, n)), Is.True);
        }
        finally
        {
            System.Runtime.InteropServices.NativeMemory.AlignedFree(src);
            System.Runtime.InteropServices.NativeMemory.AlignedFree(plain);
            System.Runtime.InteropServices.NativeMemory.AlignedFree(streamed);
        }
    }
}
