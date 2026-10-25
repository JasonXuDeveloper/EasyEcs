using System;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public class SnapshotTests
{
    [Test]
    public void RestorePutsEntitiesComponentsAndSingletonsBack()
    {
        using var world = new World(new WorldOptions { InitialEntityCapacity = 2 });
        var a = world.CreateEntity(new Position(1, 1), new Velocity(1, 0));
        var b = world.CreateEntity(new Position(2, 2), new Name("b"));
        var c = world.CreateEntity(new Health(3));
        c.Remove<Health>();                       // a presence bit, captured too
        world.AddSingleton<Score>() = new Score(1, 2);
        var score = world.SingletonRef<Score>();

        var snapshot = world.Capture();
        Assert.That(snapshot.EntityCount, Is.EqualTo(3));

        a.Get<Position>() = new Position(9, 9);
        a.Set(new Frozen());
        b.Destroy();
        var later = world.CreateEntity(new Position(5, 5));
        var reused = world.CreateEntity(new Health(7));   // may reuse b's id
        c.Set(new Health(4));
        world.AddSingleton<Clock>();
        score.Value = new Score(5, 5);

        world.Restore(snapshot);

        Assert.That(world.EntityCount, Is.EqualTo(3));
        Assert.That(a.Get<Position>(), Is.EqualTo(new Position(1, 1)));
        Assert.That(a.Has<Frozen>(), Is.False);
        Assert.That(b.IsAlive, Is.True);
        Assert.That(b.Get<Name>().Value, Is.EqualTo("b"));
        Assert.That(c.Has<Health>(), Is.False);
        Assert.That(later.IsAlive, Is.False);
        Assert.That(reused.IsAlive, Is.False);
        Assert.That(score.Value, Is.EqualTo(new Score(1, 2)));
        Assert.That(world.HasSingleton<Clock>(), Is.False);
        Assert.That(world.Count<Position>(), Is.EqualTo(2));
        Assert.That(world.Count<Health>(), Is.Zero);
    }

    [Test]
    public void HandlesMadeAfterTheCaptureNeverMatchLaterEntities()
    {
        using var world = new World();
        var snapshot = world.Capture();
        var after = world.CreateEntity(new Health(1));
        world.Restore(snapshot);

        var fresh = world.CreateEntity(new Health(2));   // reuses the id of `after`
        Assert.That(fresh.Id.Index, Is.EqualTo(after.Id.Index));
        Assert.That(after.IsAlive, Is.False);
        Assert.Throws<InvalidOperationException>(() => after.Get<Health>());
        Assert.That(fresh.Get<Health>().Value, Is.EqualTo(2));
    }

    [Test]
    public void ASnapshotRestoresAnyNumberOfTimesAndTheWorldKeepsWorking()
    {
        using var world = new World();
        for (int i = 0; i < 100; i++) world.CreateEntity(new Position(i, 0), new Velocity(1, 0));
        var snapshot = world.Capture();

        for (int round = 0; round < 3; round++)
        {
            int destroyed = world.DestroyAll<Position>(QueryFilter.Create().With<Velocity>());
            Assert.That(destroyed, Is.EqualTo(100));
            for (int i = 0; i < 50; i++) world.CreateEntity(new Health(i));
            world.Restore(snapshot);

            float sum = 0;
            foreach (var item in world.Query<Position, Velocity>()) sum += item.Component1.X;
            Assert.That(sum, Is.EqualTo(4950));
            Assert.That(world.Count<Health>(), Is.Zero);
            world.CreateEntity(new Position(0, 0)).Destroy();   // ids and tables still consistent
        }
    }

    [Test]
    public void SnapshotsBelongToTheirWorldAndNeverToAPass()
    {
        using var world = new World();
        using var other = new World();
        world.CreateEntity(new Health(1));
        Assert.Throws<ArgumentException>(() => other.Restore(world.Capture()));
        foreach (var _ in world.Query<Health>())
            Assert.Throws<InvalidOperationException>(() => world.Capture());
    }

    [Test]
    public void CapturingIntoASnapshotReusesItAndAllocatesNothing()
    {
        using var world = new World();
        var entities = new Entity[100];
        world.CreateEntities(entities, new Health(1), new Position(0, 0));
        world.AddSingleton<Released>();
        var snapshot = world.Capture();
        for (int round = 0; round < 3; round++)                                // warm-up
            Assert.That(world.Capture(snapshot), Is.SameAs(snapshot));

        long before = Allocations.Baseline();
        for (int round = 0; round < 50; round++)
        {
            entities[round].Get<Health>().Value = round + 10;
            world.Capture(snapshot);
        }

        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
        entities[0].Destroy();
        world.GetSingleton<Released>().Count = 5;
        world.Restore(snapshot);                                                // the last capture
        Assert.That((entities[0].IsAlive, entities[49].Get<Health>().Value, world.GetSingleton<Released>().Count),
            Is.EqualTo((true, 59, 0)));

        world.DestroyAll<Health>();                                        // fewer rows: the copy shrinks to them
        world.Capture(snapshot);
        world.CreateEntity(new Health(3));
        world.Restore(snapshot);
        Assert.That(world.EntityCount, Is.Zero);
        using var other = new World();
        Assert.Throws<ArgumentException>(() => other.Capture(snapshot));
    }
}
