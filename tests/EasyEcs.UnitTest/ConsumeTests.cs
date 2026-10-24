using System;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>
/// [ForEach, Consume]: the entities a pass visits are destroyed when it ends, in bulk for a method without structural
/// changes and one by one for a method that may make them; never an entity that matches later.
/// </summary>
public class ConsumeTests
{
    public record struct Hit(int Amount, EntityId Target) : IComponent;     // an event: applied once
    public record struct Spawn(int Count) : IComponent;                     // a command that creates entities
    public record struct Tally(int Hits, int Total) : ISingletonComponent;
    public struct Held : IComponent;                                        // events that wait
    public record struct Score(int Value) : IComponent, ITracked;
    public record struct Ledger(int Slot) : ICleanupComponent;

    // No structural change: its rows go at once after the pass.
    [EcsSystem]
    public ref struct ApplyHits
    {
        [ForEach, Consume, Without<Held>]
        static void Apply(in Hit hit, ref Tally tally)
        {
            tally.Hits++;
            tally.Total += hit.Amount;
        }
    }

    // Creates entities that match its own query: they are not destroyed, the next run visits them.
    [EcsSystem]
    public ref struct Spawner
    {
        [ForEach, Consume]
        static void Run(in Spawn spawn, World world, ref Tally tally)
        {
            tally.Hits++;
            if (spawn.Count > 1) world.CreateEntity(new Spawn(spawn.Count - 1));
        }
    }

    // Visits rows whose Score changed since its last run, and consumes only those.
    [EcsSystem]
    public ref struct ScoreChanges
    {
        [ForEach, Consume, Changed<Score>]
        static void Count(in Score score, ref Tally tally) => tally.Total += score.Value;
    }

    // A batch method: no structural change can happen, its rows go in bulk.
    [EcsSystem]
    public ref struct BatchHits
    {
        [ForEach, Consume]
        static void Apply(ReadOnlySpan<Hit> hits, ref Tally tally)
        {
            for (int i = 0; i < hits.Length; i++) tally.Total += hits[i].Amount;
        }
    }

    [EcsSystem]
    public ref struct ParallelHits
    {
        [ForEach(Parallel = true), Consume]
        static void Apply(ref Hit hit) => hit.Amount++;
    }

    // Throws on a negative amount: the pass ends early, and destroys nothing.
    [EcsSystem]
    public ref struct Strict
    {
        [ForEach, Consume]
        static void Apply(in Hit hit, ref Tally tally)
        {
            if (hit.Amount < 0) throw new InvalidOperationException("negative");
            tally.Total += hit.Amount;
        }
    }

    [ForEach, Consume]
    private static void Drain(in Hit hit, ref int total) => total += hit.Amount;

    [Test]
    public void TheVisitedRowsAreDestroyedAndTheOthersStay()
    {
        using var world = new World();
        world.AddSingleton<Tally>();
        world.AddSystem<ApplyHits>();
        for (int i = 0; i < 100; i++) world.CreateEntity(new Hit(i, default));
        var held = world.CreateEntity(new Hit(1000, default), new Held());
        var other = world.CreateEntity(new Health(1));
        world.Update();
        Assert.That(world.GetSingleton<Tally>(), Is.EqualTo(new Tally(100, 4950)));
        Assert.That(world.Count<Hit>(), Is.EqualTo(1));
        Assert.That((held.IsAlive, other.IsAlive), Is.EqualTo((true, true)));
        world.Update();                                                       // nothing left to visit
        Assert.That(world.GetSingleton<Tally>().Hits, Is.EqualTo(100));
    }

    [Test]
    public void EntitiesThatMatchDuringThePassAreNotConsumed()
    {
        using var world = new World();
        world.AddSingleton<Tally>();
        world.AddSystem<Spawner>();
        world.CreateEntity(new Spawn(3));
        world.Update();                                                       // visits Spawn(3), creates Spawn(2)
        Assert.That(world.Count<Spawn>(), Is.EqualTo(1));
        world.Update();
        world.Update();
        Assert.That(world.Count<Spawn>(), Is.Zero);
        Assert.That(world.GetSingleton<Tally>().Hits, Is.EqualTo(3));
    }

    [Test]
    public void AChangeFilterConsumesOnlyTheChangedRows()
    {
        using var world = new World();
        world.AddSingleton<Tally>();
        world.AddSystem<ScoreChanges>();
        var a = world.CreateEntity(new Score(1));
        var b = world.CreateEntity(new Score(2));
        world.Update();                                                       // first run: every row with Score
        Assert.That((a.IsAlive, b.IsAlive), Is.EqualTo((false, false)));
        var c = world.CreateEntity(new Score(3));
        var d = world.CreateEntity(new Score(4));
        world.Update();
        Assert.That((c.IsAlive, d.IsAlive), Is.EqualTo((false, false)));
        var e = world.CreateEntity(new Score(5));
        world.Update();
        world.Update();                                                       // nothing changed: nothing visited
        Assert.That(e.IsAlive, Is.False);
        Assert.That(world.GetSingleton<Tally>().Total, Is.EqualTo(15));
    }

    [Test]
    public void BatchAndParallelMethodsConsumeInBulk()
    {
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4 });
        world.AddSingleton<Tally>();
        world.AddSystem<BatchHits>();
        world.CreateEntities(new Entity[500], new Hit(2, default));
        world.Update();
        Assert.That((world.Count<Hit>(), world.GetSingleton<Tally>().Total), Is.EqualTo((0, 1000)));

        using var parallel = new World(new WorldOptions { MaxDegreeOfParallelism = 4 });
        parallel.AddSystem<ParallelHits>();
        parallel.CreateEntities(new Entity[World.ParallelChunkRows * 4], new Hit(0, default));
        parallel.Update();
        Assert.That(parallel.Count<Hit>(), Is.Zero);
    }

    [Test]
    public void CleanupComponentsRetireTheConsumedAndTheDestroyLogRecordsThem()
    {
        using var world = new World(new WorldOptions { DestroyLogCapacity = 16 });
        world.AddSingleton<Tally>();
        world.AddSystem<ApplyHits>();
        ulong tick = world.ChangeTick;
        var leased = world.CreateEntity(new Hit(1, default), new Ledger(7));
        var plain = world.CreateEntity(new Hit(2, default));
        world.Update();
        // The leased one is retired: alive, its Hit gone, so no pass visits it again; the plain one is freed and logged.
        Assert.That((leased.IsAlive, leased.Has<Hit>(), leased.Has<Retired>(), plain.IsAlive), Is.EqualTo((true, false, true, false)));
        Span<EntityId> gone = stackalloc EntityId[16];
        Assert.That(world.DestroyedSince(tick, gone, out int count), Is.True);
        Assert.That(gone[..count].ToArray(), Is.EqualTo(new[] { plain.Id }));
        world.Update();
        Assert.That(world.GetSingleton<Tally>().Hits, Is.EqualTo(2));
        leased.Remove<Ledger>();                                              // released: now it goes, and is logged
        Assert.That(world.DestroyedSince(tick, gone, out count) && count == 2, Is.True);
    }

    [Test]
    public void APassThatThrowsDestroysNothing()
    {
        using var world = new World();
        world.AddSingleton<Tally>();
        world.AddSystem<Strict>();
        world.CreateEntity(new Hit(1, default));
        world.CreateEntity(new Hit(-1, default));
        Assert.Throws<InvalidOperationException>(world.Update);
        Assert.That(world.Count<Hit>(), Is.EqualTo(2));                    // both are visited again on the next run
    }

    [Test]
    public void ARunnerConsumesThroughItsEntryPoint()
    {
        using var world = new World();
        world.CreateEntities(new Entity[10], new Hit(3, default));
        int total = 0;
        ConsumeTests.Drain(world, ref total);
        Assert.That((total, world.Count<Hit>()), Is.EqualTo((30, 0)));
        ConsumeTests.Drain(world, ref total);                                // an empty world: nothing to do
        Assert.That(total, Is.EqualTo(30));
    }

    [Test]
    public void ConsumingDoesNotAllocate()
    {
        using var world = new World();
        world.AddSingleton<Tally>();
        world.AddSystem<ApplyHits>();
        world.AddSystem<Spawner>();
        var hits = new Entity[256];
        void Fill()
        {
            world.CreateEntities(hits, new Hit(1, default));
            world.CreateEntity(new Spawn(2));
        }

        for (int i = 0; i < 20; i++)
        {
            Fill();
            world.Update();
            world.Update();
        }

        Assert.That(Allocations.Of(() =>
        {
            for (int i = 0; i < 50; i++)
            {
                Fill();
                world.Update();
                world.Update();
            }
        }), Is.Zero);
        Assert.That(world.Count<Hit>(), Is.Zero);
    }
}
