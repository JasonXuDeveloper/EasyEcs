using System;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary><c>entity.Run</c>: one entity's job with the refs and guarantees of a pass.</summary>
public class RunTests
{
    // Heals by its amount and, once healthy, takes armor (a structural change in the middle of the job).
#pragma warning disable EECS037 // Armor left out of WriteMask on purpose: the test checks it is neither carried over nor stamped
    private struct Heal(int amount) : IForEachEntity<Health, Armor>
    {
        public int Runs;

        public void Execute(Entity entity, ref Health health, ref Armor armor)
        {
            Runs++;
            if (health.Value >= 100) entity.Set(new Frozen()); // the row moves: the refs still point at the old one
            health.Value += amount;
            armor.Value += amount;
            if (amount < 0) throw new InvalidOperationException("refused");
        }

        public static int WriteMask => 1; // Health only: Armor is read (its writes are not carried over nor stamped)
    }
#pragma warning restore EECS037

    private struct Read : IForEachEntity<Health>, INoStructuralChanges
    {
        public int Seen;

        public void Execute(Entity entity, ref Health health)
        {
            Seen = health.Value;
            if (Seen < 0) entity.Set(new Frozen()); // breaks its promise
        }

        public static int WriteMask => 0;
    }

    [Test]
    public void RunsOnlyOnAnEntityThatHasTheComponentsAndPassesTheFilter()
    {
        using var world = new World();
        var both = world.CreateEntity(new Health(1), new Armor(1));
        var bare = world.CreateEntity(new Health(1));
        var frozen = world.CreateEntity(new Health(1), new Armor(1), new Frozen());
        var job = new Heal(2);
        var awake = QueryFilter.Create().Without<Frozen>();

        Assert.That((both.Run<Health, Armor, Heal>(ref job), bare.Run<Health, Armor, Heal>(ref job)), Is.EqualTo((true, false)));
        Assert.That((frozen.Run<Health, Armor, Heal>(ref job, awake), both.Run<Health, Armor, Heal>(ref job, awake)), Is.EqualTo((false, true)));
        frozen.Destroy();
        Assert.That((frozen.Run<Health, Armor, Heal>(ref job), default(Entity).Run<Health, Armor, Heal>(ref job)), Is.EqualTo((false, false)));
        Assert.That((job.Runs, both.Read<Health>().Value), Is.EqualTo((2, 5)));
    }

    [Test]
    public void TheWritesCountAsWrittenAndSurviveTheJobsOwnStructuralChange()
    {
        using var world = new World();
        var e = world.CreateEntity(new Health(100), new Armor(7));
        world.Update();
        ulong tick = world.ChangeTick;
        var job = new Heal(5);

        Assert.That(e.Run<Health, Armor, Heal>(ref job), Is.True);
        Assert.That((e.Has<Frozen>(), e.Read<Health>().Value, e.Read<Armor>().Value), Is.EqualTo((true, 105, 7))); // Armor is not in WriteMask
        Assert.That(world.Count<Armor>(QueryFilter.Create().Changed<Armor>(tick)), Is.Zero);

        e.Remove<Frozen>();
        var refused = new Heal(-1);
        Assert.Throws<InvalidOperationException>(() => e.Run<Health, Armor, Heal>(ref refused));
        Assert.That((e.Has<Frozen>(), e.Read<Health>().Value), Is.EqualTo((true, 104)));          // moved before the throw
    }

    [Test]
    public void AJobWithoutStructuralChangesRunsFrozen()
    {
        using var world = new World();
        var e = world.CreateEntity(new Health(3));
        var job = new Read();
        Assert.That((e.Run<Health, Read>(ref job), job.Seen), Is.EqualTo((true, 3)));
        e.Get<Health>().Value = -1;
        Assert.Throws<InvalidOperationException>(() => e.Run<Health, Read>(ref job));
        Assert.That((e.Has<Frozen>(), world.Count<Health>(QueryFilter.Create())), Is.EqualTo((false, 1)));
    }

    [Test]
    public void AChangedFilterSelectsOnlyAFreshWrite()
    {
        using var world = new World();
        var e = world.CreateEntity(new Health(1), new Armor(1));
        world.Update();
        var job = new Read();
        var fresh = QueryFilter.Create().Changed<Armor>(world.ChangeTick);
        Assert.That(e.Run<Health, Read>(ref job, fresh), Is.False);
        e.Get<Armor>().Value++;
        Assert.That(e.Run<Health, Read>(ref job, fresh), Is.True);
    }

    // Runs a job on the entity it visits, from the threads of a parallel pass.
    private struct RunFromChunks : IForEachEntity<Health>, INoStructuralChanges
    {
        public void Execute(Entity entity, ref Health health)
        {
            var job = new Read();
            if (!entity.Run<Health, Read>(ref job) || job.Seen != health.Value) throw new InvalidOperationException("missed");
        }
    }

    [Test]
    public void RunOnTheThreadsOfAParallelPassTakesThePassesFreeze()
    {
        // The pass froze the gate, so Run on a helper thread neither freezes nor thaws it (no plain store races).
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4 });
        world.CreateEntities(new Entity[World.ParallelChunkRows * 4], new Health(7));
        var job = new RunFromChunks();
        for (int i = 0; i < 20; i++) world.ForEachEntityParallel<Health, RunFromChunks>(ref job);
        var bare = world.CreateEntity(new Health(1));
        Assert.That(bare.Has<Frozen>(), Is.False);
        bare.Set(new Frozen());                                   // the gate is free again after the passes
        Assert.That(bare.Has<Frozen>(), Is.True);
    }
}
