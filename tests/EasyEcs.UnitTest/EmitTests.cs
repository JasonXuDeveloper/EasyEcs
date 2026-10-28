using System;
using System.Linq;
using Nino.Core;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>
/// <see cref="Emit{T}"/>: a [ForEach] pass appends records to a list per chunk (per pass on one thread), joined in chunk
/// order into the <see cref="Emitted{T}"/> singleton when the pass ends.
/// </summary>
public class EmitTests
{
    public record struct Attack(int Power) : IComponent;
    public record struct Aim(EntityId Target) : IComponent;
    public record struct Hp(int Value) : IComponent;

    /// <summary>A hit: who takes it and how much (the scenario of a parallel combat pass, applied after it).</summary>
    public record struct Hit(EntityId Target, int Amount);

    /// <summary>An area-of-interest packet: who sees whom.</summary>
    public record struct Seen(EntityId Viewer, EntityId Subject);

    [EcsSystem]
    public ref struct ParallelStrikes
    {
        [ForEach(Parallel = true, ChunkSize = 64, SimdMode = SimdMode.Off)]
        static void Strike(in Attack attack, in Aim aim, Emit<Hit> hits)
        {
            if (attack.Power % 3 != 0) hits.Add(new Hit(aim.Target, attack.Power)); // some rows emit nothing
            if (attack.Power % 5 == 0) hits.Add(new Hit(aim.Target, 1)); // some emit twice
        }
    }

    [EcsSystem]
    public ref struct SerialStrikes
    {
        [ForEach(SimdMode = SimdMode.Off)]
        static void Strike(in Attack attack, in Aim aim, Emit<Hit> hits)
        {
            if (attack.Power % 3 != 0) hits.Add(new Hit(aim.Target, attack.Power));
            if (attack.Power % 5 == 0) hits.Add(new Hit(aim.Target, 1));
        }
    }

    /// <summary>The serial system the records are data for: applies each hit, then clears them.</summary>
    [EcsSystem, UpdateAfter<ParallelStrikes>]
    public ref struct ApplyHits
    {
        static void OnUpdate(World world)
        {
            ref var hits = ref world.GetSingleton<Emitted<Hit>>().Items;
            foreach (ref readonly var hit in hits.AsReadOnlySpan())
                if (world.TryGetEntity(hit.Target, out var target)) target.Get<Hp>().Value -= hit.Amount;
            hits.Clear();
        }
    }

    [EcsSystem]
    public ref struct ReadsHits
    {
        [ForEach]
        static void Read(ref Hp hp, in Emitted<Hit> hits) => hp.Value = hits.Items.Count;
    }

    public static class Runners
    {
        [ForEach(Parallel = true, SimdMode = SimdMode.Off)]
        public static void Strike(in Attack attack, in Aim aim, Emit<Hit> hits) => hits.Add(new Hit(aim.Target, attack.Power));

        [ForEach(Parallel = true, ChunkSize = 64, SimdMode = SimdMode.Off)]
        public static void Look(Entity viewer, in Aim aim, Emit<Seen> seen, Emit<Hit> hits)
        {
            seen.Add(new Seen(viewer.Id, aim.Target));
            hits.AddRange(new Hit(aim.Target, 1), new Hit(aim.Target, 2));
        }
    }

    /// <summary>Attackers over two tables, aiming at <paramref name="targets"/> targets.</summary>
    private static (World World, Entity[] Targets) Fight(int attackers, int targets, int threads)
    {
        var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        var aimed = new Entity[targets];
        world.CreateEntities(aimed, new Hp(1_000_000));
        var first = new Entity[attackers / 2];
        var second = new Entity[attackers - attackers / 2];
        world.CreateEntities(first, new Attack(0), new Aim(default));
        world.CreateEntities(second, new Attack(0), new Aim(default), new Hp(0));
        int n = 0;
        foreach (var e in first) e.Set(new Attack(n), new Aim(aimed[n++ % targets].Id));
        foreach (var e in second) e.Set(new Attack(n), new Aim(aimed[n++ % targets].Id));
        return (world, aimed);
    }

    private static Hit[] Expected(int attackers, Entity[] targets)
    {
        var hits = new System.Collections.Generic.List<Hit>();
        for (int n = 0; n < attackers; n++)
        {
            var target = targets[n % targets.Length].Id;
            if (n % 3 != 0) hits.Add(new Hit(target, n));
            if (n % 5 == 0) hits.Add(new Hit(target, 1));
        }

        return hits.ToArray();
    }

    [Test]
    public void AParallelPassEmitsInRowOrderForAnyThreadCount()
    {
        const int attackers = 20_000;
        foreach (int threads in new[] { 1, 2, 3, 4, 8 })
        {
            var (world, targets) = Fight(attackers, 37, threads);
            using (world)
            {
                var expected = Expected(attackers, targets);
                world.AddSystem<ParallelStrikes>();
                for (int run = 0; run < 30; run++)
                {
                    world.Update();
                    ref var hits = ref world.GetSingleton<Emitted<Hit>>().Items;
                    Assert.That(hits.AsReadOnlySpan().SequenceEqual(expected), Is.True, $"{threads} threads, run {run}: {hits.Count} hits");
                    hits.Clear();
                }
            }
        }
    }

    [Test]
    public void ASerialPassEmitsWhatTheParallelOneDoes()
    {
        const int attackers = 5000;
        var (world, targets) = Fight(attackers, 10, 4);
        using (world)
        {
            world.AddSystem<SerialStrikes>();
            world.Update();
            Assert.That(world.GetSingleton<Emitted<Hit>>().Items.AsReadOnlySpan().SequenceEqual(Expected(attackers, targets)), Is.True);
        }
    }

    [Test]
    public void PassesAppendUntilTheirReaderClears()
    {
        const int attackers = 3000;
        var (world, targets) = Fight(attackers, 10, 4);
        using (world)
        {
            world.AddSystem<ParallelStrikes>();
            world.Update();
            world.Update();
            var once = Expected(attackers, targets);
            ref var hits = ref world.GetSingleton<Emitted<Hit>>().Items;
            Assert.That(hits.Count, Is.EqualTo(once.Length * 2));
            Assert.That(hits.AsReadOnlySpan()[once.Length..].SequenceEqual(once), Is.True);
        }
    }

    [Test]
    public void ASerialSystemAppliesTheRecords()
    {
        const int attackers = 10_000;
        var (world, targets) = Fight(attackers, 7, 4);
        using (world)
        {
            world.AddSystem<ParallelStrikes>();
            world.AddSystem<ApplyHits>();
            var expected = new int[targets.Length];
            foreach (var hit in Expected(attackers, targets))
                expected[Array.FindIndex(targets, t => t.Id == hit.Target)] += hit.Amount;
            for (int run = 1; run <= 3; run++)
            {
                world.Update();
                for (int i = 0; i < targets.Length; i++) Assert.That(targets[i].Hp.Value, Is.EqualTo(1_000_000 - run * expected[i]));
                Assert.That(world.GetSingleton<Emitted<Hit>>().Items.Count, Is.Zero);
            }
        }
    }

    [Test]
    public void APassWithNoRowsAddsAnEmptySingleton()
    {
        using var world = new World();
        world.AddSystem<ParallelStrikes>();
        world.Update();
        Assert.That(world.HasSingleton<Emitted<Hit>>(), Is.True);
        Assert.That(world.GetSingleton<Emitted<Hit>>().Items.Count, Is.Zero);
    }

    [Test]
    public void ARunnerEmitsSeveralKindsOfRecords()
    {
        var (world, targets) = Fight(World.MaxChunkRows, 5, 4);
        using (world)
        {
            Runners.Look(world);
            ref var seen = ref world.GetSingleton<Emitted<Seen>>().Items;
            ref var hits = ref world.GetSingleton<Emitted<Hit>>().Items;
            Assert.That((seen.Count, hits.Count), Is.EqualTo((World.MaxChunkRows, World.MaxChunkRows * 2)));
            for (int i = 0; i < seen.Count; i++)
            {
                Assert.That(hits[2 * i].Target, Is.EqualTo(seen[i].Subject));
                Assert.That((hits[2 * i].Amount, hits[2 * i + 1].Amount), Is.EqualTo((1, 2)));
            }
            seen.Clear(); // the reader clears what it applied (EECS092)
            hits.Clear();
        }
    }

    [Test]
    public void TheEmitterWritesItsSingletonForTheScheduler()
    {
        using var world = new World(new WorldOptions { ParallelSystems = true });
        world.AddSystem<ParallelStrikes>();
        world.AddSystem<ReadsHits>();
        var stages = world.DescribeSchedule().Groups[0].Stages;
        Assert.That(stages.Length, Is.EqualTo(2), world.DescribeSchedule().ToString()); // the reader waits for the emitter
    }

    [Test]
    public void SnapshotsCopyTheRecords()
    {
        var (world, targets) = Fight(2000, 4, 4);
        using (world)
        {
            world.AddSystem<ParallelStrikes>();
            world.Update();
            var before = world.GetSingleton<Emitted<Hit>>().Items.AsReadOnlySpan().ToArray();
            var snapshot = world.Capture();
            world.Update(); // appends into the live list only
            Assert.That(world.GetSingleton<Emitted<Hit>>().Items.Count, Is.EqualTo(before.Length * 2));
            world.Restore(snapshot);
            Assert.That(world.GetSingleton<Emitted<Hit>>().Items.AsReadOnlySpan().SequenceEqual(before), Is.True);
        }
    }

    [Test]
    public void ReplaysFromASnapshotDoNotAllocate()
    {
        var (world, _) = Fight(World.MaxChunkRows * 4, 16, 4);
        using (world)
        {
            world.AddSystem<ParallelStrikes>();
            world.AddSystem<ApplyHits>();
            for (int i = 0; i < 30; i++) world.Update();
            var snapshot = world.Capture();                       // the records cleared: the copy holds none

            void Replay()
            {
                for (int i = 0; i < 10; i++)
                {
                    world.Update();
                    world.Restore(snapshot);                      // the singleton keeps its list, emptied
                }
            }

            Replay();
            Assert.That(Allocations.Of(Replay), Is.Zero);
        }
    }

    [Test]
    public void SavesWriteTheRecords()
    {
        var (world, _) = Fight(2000, 4, 4);
        using (world)
        {
            world.AddSystem<ParallelStrikes>();
            world.Update();
            var output = new NinoArrayBufferWriter();
            world.Save(output);
            using var loaded = new World();
            loaded.Load(output.WrittenSpan);
            Assert.That(loaded.GetSingleton<Emitted<Hit>>().Items.AsReadOnlySpan()
                .SequenceEqual(world.GetSingleton<Emitted<Hit>>().Items.AsReadOnlySpan()), Is.True);
        }
    }

    [Test]
    public void ParallelEmitsDoNotAllocateAfterWarmUp()
    {
        var (world, _) = Fight(World.MaxChunkRows * 4, 16, 4);
        using (world)
        {
            world.AddSystem<ParallelStrikes>();
            world.AddSystem<ApplyHits>();
            for (int i = 0; i < 30; i++) world.Update();
            Assert.That(Allocations.Of(() => { for (int i = 0; i < 100; i++) world.Update(); }), Is.Zero);
        }
    }

    [Test]
    public void ParallelEmitsDoNotAllocateOnHelperThreads()
    {
        // MeasureSystems adds what helper threads allocate during a pass to its system's stats.
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4, MeasureSystems = true });
        var aimed = world.CreateEntity(new Hp(0));
        world.CreateEntities(new Entity[World.MaxChunkRows * 4], new Attack(1), new Aim(aimed.Id));
        var strikes = world.AddSystem<ParallelStrikes>();
        world.AddSystem<ApplyHits>();
        for (int i = 0; i < 30; i++) world.Update();
        Assert.That(Allocations.Of(() => { for (int i = 0; i < 100; i++) world.Update(); }, () => strikes.Stats.TotalAllocated), Is.Zero);
    }

    // Segments: a parallel pass hands its chunks' lists to Emitted<T> (BufferStore.Segmented), joined only when the
    // records are read as one span or written; Count, Clear, [ForEachRecord] passes and ForParallel take them in place.

    /// <summary>A trap under every eleventh attacker: an emitter on one thread between two parallel ones.</summary>
    [EcsSystem, UpdateAfter<ParallelStrikes>]
    public ref struct SerialTraps
    {
        [ForEach(SimdMode = SimdMode.Off)]
        static void Trap(in Attack attack, in Aim aim, Emit<Hit> hits)
        {
            if (attack.Power % 11 == 0) hits.Add(new Hit(aim.Target, 11));
        }
    }

    /// <summary>Heals from every seventh attacker: a second parallel emitter of the same records.</summary>
    [EcsSystem, UpdateAfter<SerialTraps>]
    public ref struct ParallelHeals
    {
        [ForEach(Parallel = true, SimdMode = SimdMode.Off)]
        static void Heal(in Attack attack, in Aim aim, Emit<Hit> hits)
        {
            if (attack.Power % 7 == 0) hits.Add(new Hit(aim.Target, -attack.Power));
        }
    }

    /// <summary>Reads the records on several threads after a parallel emitter: the pass resolving Emitted joins them first.</summary>
    [EcsSystem, UpdateAfter<ParallelStrikes>]
    public ref struct ParallelLastHit
    {
        [ForEach(Parallel = true, SimdMode = SimdMode.Off)]
        static void Read(ref Hp hp, in Emitted<Hit> hits) => hp.Value = hits.Items[hits.Items.Count - 1].Amount + hits.Items.AsReadOnlySpan()[0].Amount;
    }

    public struct Tally : ISingletonComponent
    {
        public long Total;
        public long Order;
    }

    public static class Books
    {
        [ForEachRecord]
        public static void Book(in Hit hit, ref Tally tally)
        {
            tally.Total += hit.Amount;
            tally.Order = tally.Order * 31 + hit.Amount;
        }
    }

    private struct Number : IForSpan<Hit>
    {
        public void Execute(int start, Span<Hit> items)
        {
            for (int i = 0; i < items.Length; i++) items[i] = items[i] with { Amount = start + i };
        }
    }

    /// <summary>The records of a pass whose attacker n emits <paramref name="hit"/>(n, target) when it is not null.</summary>
    private static Hit[] Rows(int attackers, Entity[] targets, Func<int, EntityId, Hit?> hit)
    {
        var hits = new System.Collections.Generic.List<Hit>();
        for (int n = 0; n < attackers; n++)
            if (hit(n, targets[n % targets.Length].Id) is { } h)
                hits.Add(h);
        return hits.ToArray();
    }

    private static bool Segmented(ref DynamicBuffer<Hit> hits) => hits.Store is { Shares: BufferStore.Segmented };

    [Test]
    public void PassesOfOneUpdateKeepTheirOrderInSegments()
    {
        const int attackers = 20_000;
        foreach (int threads in new[] { 1, 4 })
        {
            var (world, targets) = Fight(attackers, 37, threads);
            using (world)
            {
                world.AddSystem<ParallelStrikes>(); // ChunkSize = 64: more chunks than segments a list holds
                world.AddSystem<SerialTraps>();     // joins the strikes' segments, then appends in place
                world.AddSystem<ParallelHeals>();   // its chunks' lists stay segments
                var expected = Expected(attackers, targets)
                    .Concat(Rows(attackers, targets, (n, t) => n % 11 == 0 ? new Hit(t, 11) : null))
                    .Concat(Rows(attackers, targets, (n, t) => n % 7 == 0 ? new Hit(t, -n) : null)).ToArray();
                for (int run = 0; run < 5; run++)
                {
                    world.Update();
                    ref var hits = ref world.GetSingleton<Emitted<Hit>>().Items;
                    Assert.That(Segmented(ref hits), Is.True, $"{threads} threads");
                    Assert.That(hits.Count, Is.EqualTo(expected.Length));
                    Assert.That(hits[expected.Length - 1], Is.EqualTo(expected[^1]));
                    Assert.That(Segmented(ref hits), Is.False); // the indexer joined them
                    Assert.That(hits.AsReadOnlySpan().SequenceEqual(expected), Is.True, $"{threads} threads, run {run}");
                    hits.Clear();
                }
            }
        }
    }

    [Test]
    public void CountAndClearLeaveTheSegmentsUncopied()
    {
        const int attackers = 20_000;
        var (world, targets) = Fight(attackers, 37, 4);
        using (world)
        {
            int expected = Rows(attackers, targets, (n, t) => n % 7 == 0 ? new Hit(t, -n) : null).Length;
            world.AddSystem<ParallelHeals>();
            void Ticks()
            {
                for (int i = 0; i < 20; i++)
                {
                    world.Update();
                    ref var hits = ref world.GetSingleton<Emitted<Hit>>().Items;
                    if (!Segmented(ref hits) || hits.Count != expected) throw new AssertionException($"{hits.Count} hits");
                    hits.Clear();
                }
            }

            Ticks();
            Assert.That(Allocations.Of(Ticks), Is.Zero);
            Assert.That(world.GetSingleton<Emitted<Hit>>().Items.Capacity, Is.LessThan(expected)); // never joined
        }
    }

    [Test]
    public void ARecordPassWalksTheSegmentsInOrder()
    {
        const int attackers = 20_000;
        Tally? first = null;
        foreach (int threads in new[] { 1, 4 })
        {
            var (world, targets) = Fight(attackers, 37, threads);
            using (world)
            {
                world.GetOrAddSingleton<Tally>();
                world.AddSystem<ParallelStrikes>();
                world.AddSystem<ParallelHeals>();
                var expected = new Tally();
                foreach (var hit in Expected(attackers, targets).Concat(Rows(attackers, targets, (n, t) => n % 7 == 0 ? new Hit(t, -n) : null)))
                    Books.Book(in hit, ref expected);
                world.Update();
                ref var hits = ref world.GetSingleton<Emitted<Hit>>().Items;
                Assert.That(Segmented(ref hits), Is.True);
                int capacity = hits.Capacity;
                Books.Book(world);
                Assert.That(world.GetSingleton<Tally>(), Is.EqualTo(expected));
                Assert.That((hits.Count, hits.Capacity), Is.EqualTo((0, capacity))); // walked in place, then cleared
                first ??= world.GetSingleton<Tally>();
                Assert.That(world.GetSingleton<Tally>(), Is.EqualTo(first)); // bit-identical at 1 and 4 threads
            }
        }
    }

    [Test]
    public void ARecordPassOverSegmentsDoesNotAllocate()
    {
        // One emitter: every chunk's list ends at the size its chunk needs. (Emitters of one record type whose chunks
        // differ in size share the pool, so a list keeps growing now and then until every list has served the larger
        // chunks, for good: steady state comes, after a number of updates the threads decide.)
        var (world, _) = Fight(20_000, 37, 4);
        using (world)
        {
            world.GetOrAddSingleton<Tally>();
            world.AddSystem<ParallelHeals>();
            void Ticks()
            {
                for (int i = 0; i < 10; i++)
                {
                    world.Update();
                    Books.Book(world);
                }
            }

            for (int i = 0; i < 3; i++) Ticks();
            Assert.That(Segmented(ref world.GetSingleton<Emitted<Hit>>().Items), Is.False); // cleared by the pass
            Assert.That(Allocations.Of(Ticks), Is.Zero);
        }
    }

    [Test]
    public void ForParallelRunsTheSegmentsInPlace()
    {
        const int attackers = 20_000;
        var (world, _) = Fight(attackers, 37, 4);
        using (world)
        {
            world.AddSystem<ParallelHeals>();
            world.Update();
            ref var hits = ref world.GetSingleton<Emitted<Hit>>().Items;
            Assert.That(Segmented(ref hits), Is.True);
            int count = hits.Count;
            var job = new Number();
            world.ForParallel(ref hits, ref job, chunkSize: 64); // chunks that span two segments: one call per segment
            Assert.That(Segmented(ref hits), Is.True);
            var numbered = hits.AsReadOnlySpan();
            Assert.That(numbered.Length, Is.EqualTo(count));
            for (int i = 0; i < numbered.Length; i++) Assert.That(numbered[i].Amount, Is.EqualTo(i));
        }
    }

    [Test]
    public void APassReadingTheRecordsOnSeveralThreadsFindsThemJoined()
    {
        const int attackers = 20_000;
        var (world, targets) = Fight(attackers, 37, 4);
        using (world)
        {
            world.AddSystem<ParallelStrikes>();
            world.AddSystem<ParallelLastHit>();
            var expected = Expected(attackers, targets);
            world.Update();
            foreach (var target in targets) Assert.That(target.Hp.Value, Is.EqualTo(expected[^1].Amount + expected[0].Amount));
            Assert.That(Segmented(ref world.GetSingleton<Emitted<Hit>>().Items), Is.False);
        }
    }

    [Test]
    public void SnapshotsAndSavesCopyTheSegments()
    {
        const int attackers = 20_000;
        var (world, targets) = Fight(attackers, 37, 4);
        using (world)
        {
            world.AddSystem<ParallelHeals>();
            var once = Rows(attackers, targets, (n, t) => n % 7 == 0 ? new Hit(t, -n) : null);
            world.Update();
            Assert.That(Segmented(ref world.GetSingleton<Emitted<Hit>>().Items), Is.True);
            var snapshot = world.Capture();
            var output = new NinoArrayBufferWriter();
            world.Update();
            Assert.That(Segmented(ref world.GetSingleton<Emitted<Hit>>().Items), Is.True);
            world.Save(output);
            using var loaded = new World();
            loaded.Load(output.WrittenSpan);
            Assert.That(loaded.GetSingleton<Emitted<Hit>>().Items.AsReadOnlySpan().SequenceEqual(once.Concat(once).ToArray()), Is.True);
            world.Update(); // segments again, which the restore drops
            world.Restore(snapshot);
            Assert.That(world.GetSingleton<Emitted<Hit>>().Items.AsReadOnlySpan().SequenceEqual(once), Is.True);
            world.Load(output.WrittenSpan);
            Assert.That(world.GetSingleton<Emitted<Hit>>().Items.AsReadOnlySpan().SequenceEqual(once.Concat(once).ToArray()), Is.True);
        }
    }

    [Test]
    public void WritesJoinTheSegmentsFirst()
    {
        const int attackers = 20_000;
        var (world, targets) = Fight(attackers, 37, 4);
        using (world)
        {
            world.AddSystem<ParallelHeals>();
            var once = Rows(attackers, targets, (n, t) => n % 7 == 0 ? new Hit(t, -n) : null);
            world.Update();
            ref var hits = ref world.GetSingleton<Emitted<Hit>>().Items;
            hits.Add(new Hit(default, 5));
            Assert.That(hits.AsReadOnlySpan().SequenceEqual(once.Append(new Hit(default, 5)).ToArray()), Is.True);
            world.Update();
            Assert.That(Segmented(ref hits), Is.True);
            hits.AsSpan()[0] = new Hit(default, 6); // a write: joined first
            Assert.That(hits[0], Is.EqualTo(new Hit(default, 6)));
            Assert.That(hits[once.Length], Is.EqualTo(new Hit(default, 5)));
            Assert.That(hits.AsReadOnlySpan()[(once.Length + 1)..].SequenceEqual(once), Is.True);
            hits.SetCount(1);
            hits.TrimExcess();
            Assert.That(hits.AsReadOnlySpan().ToArray(), Is.EqualTo(new[] { new Hit(default, 6) }));
        }
    }

    // EECS092: records nothing clears. The test compilation is a library, so the record types are internal: a public
    // one could be cleared by another assembly.
    private const string Strikes = """
        internal record struct Shot(EntityId Target, int Amount);
        public record struct Gun(int Power) : IComponent;
        public record struct Target(EntityId Id) : IComponent;
        [EcsSystem] public ref struct Fire { [ForEach] static void Pull(in Gun gun, in Target target, Emit<Shot> shots) => shots.Add(new Shot(target.Id, gun.Power)); }
        """;

    private static string[] Uncleared(string reader) =>
        GeneratorTests.Analyze([Strikes, reader]).Result.Where(d => d.Id == "EECS092").Select(d => d.GetMessage()).ToArray();

    [Test]
    public void RecordsNoCodeClearsAreReported()
    {
        var found = Uncleared("""
            public static class Host
            {
                public static void Apply(World world)
                {
                    foreach (var shot in world.GetSingleton<Emitted<Shot>>().Items)
                        if (world.TryGetEntity(shot.Target, out var target)) target.Destroy();
                }
            }
            [EcsSystem] public ref struct Fire2 { [ForEach] static void Again(in Gun gun, in Target target, Emit<Shot> shots) => shots.Add(new Shot(target.Id, 1)); }
            """);
        Assert.That(found, Is.EqualTo(new[] { "[ForEach] method 'Pull' emits Shot records, but no code clears Emitted<Shot>: each update adds its "
            + "records to all the earlier ones, so a reader applies them again and the list grows; clear "
            + "world.GetSingleton<Emitted<Shot>>().Items in the system that applies them, once applied" })); // once per record type
    }

    [TestCase("ref var shots = ref world.GetSingleton<Emitted<Shot>>().Items; shots.Clear();")]          // through a ref local
    [TestCase("world.GetSingleton<Emitted<Shot>>().Items.Clear();")]
    [TestCase("ref var shots = ref world.GetSingleton<Emitted<Shot>>().Items; while (shots.Count > 0) shots.RemoveAtSwapBack(0);")]
    [TestCase("world.RemoveSingleton<Emitted<Shot>>();")]
    [TestCase("world.GetSingleton<Emitted<Shot>>() = default;")]
    public void AClearAnywhereIsEnough(string clear)
    {
        Assert.That(Uncleared($"public static class Host {{ public static void Send(World world) {{ {clear} }} }}"), Is.Empty);
    }

    [Test]
    public void APublicRecordOfALibraryIsLeftToItsUsers()
    {
        var found = GeneratorTests.Analyze([Strikes.Replace("internal record struct Shot", "public record struct Shot")]).Result.Where(d => d.Id == "EECS092");
        Assert.That(found, Is.Empty);
    }
}
