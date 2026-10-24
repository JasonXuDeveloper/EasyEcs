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
        [ForEach(Parallel = true, ChunkRows = 64)]
        static void Strike(in Attack attack, in Aim aim, Emit<Hit> hits)
        {
            if (attack.Power % 3 != 0) hits.Add(new Hit(aim.Target, attack.Power)); // some rows emit nothing
            if (attack.Power % 5 == 0) hits.Add(new Hit(aim.Target, 1)); // some emit twice
        }
    }

    [EcsSystem]
    public ref struct SerialStrikes
    {
        [ForEach]
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
        [ForEach(Parallel = true)]
        public static void Strike(in Attack attack, in Aim aim, Emit<Hit> hits) => hits.Add(new Hit(aim.Target, attack.Power));

        [ForEach(Parallel = true, ChunkRows = 64)]
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
        var (world, targets) = Fight(World.ParallelChunkRows, 5, 4);
        using (world)
        {
            Runners.Look(world);
            ref var seen = ref world.GetSingleton<Emitted<Seen>>().Items;
            ref var hits = ref world.GetSingleton<Emitted<Hit>>().Items;
            Assert.That((seen.Count, hits.Count), Is.EqualTo((World.ParallelChunkRows, World.ParallelChunkRows * 2)));
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
        var (world, _) = Fight(World.ParallelChunkRows * 4, 16, 4);
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
        var (world, _) = Fight(World.ParallelChunkRows * 4, 16, 4);
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
        world.CreateEntities(new Entity[World.ParallelChunkRows * 4], new Attack(1), new Aim(aimed.Id));
        var strikes = world.AddSystem<ParallelStrikes>();
        world.AddSystem<ApplyHits>();
        for (int i = 0; i < 30; i++) world.Update();
        Assert.That(Allocations.Of(() => { for (int i = 0; i < 100; i++) world.Update(); }, () => strikes.Stats.TotalAllocated), Is.Zero);
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
