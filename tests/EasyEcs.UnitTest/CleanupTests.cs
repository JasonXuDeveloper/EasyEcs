using System;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public record struct Lease(int Slot) : ICleanupComponent;   // a resource outside the world, released exactly once
public record struct Ticket(int Seat) : ICleanupComponent;  // a second one

/// <summary>Releases leases of retired entities: the system a cleanup component asks for.</summary>
public class ReleaseLeases : EcsSystem
{
    [ForEach, With<Retired>, With<Lease>]
    static void Release(Entity entity, ref Released released)
    {
        released.Count++;
        entity.Remove<Lease>();
    }
}

public record struct Released(int Count) : ISingletonComponent;

public class CleanupTests
{
    [Test]
    public void DestroyKeepsCleanupComponentsUntilTheyAreRemoved()
    {
        using var world = new World();
        var e = world.CreateEntity(new Lease(3), new Health(10), new Position(1, 2));
        e.Destroy();
        Assert.That((e.IsAlive, e.Has<Retired>(), e.Get<Lease>().Slot), Is.EqualTo((true, true, 3)));
        Assert.That((e.Has<Health>(), e.Has<Position>(), world.Count<Health>()), Is.EqualTo((false, false, 0)));

        e.Destroy();                                                     // retired already: nothing changes
        Assert.That((e.IsAlive, e.Get<Lease>().Slot), Is.EqualTo((true, 3)));
        e.Remove<Lease>();                                               // the last cleanup component: freed
        Assert.That((e.IsAlive, world.EntityCount), Is.EqualTo((false, 0)));
    }

    [Test]
    public void EveryCleanupComponentMustGo()
    {
        using var world = new World();
        var e = world.CreateEntity(new Lease(1), new Ticket(2));
        e.Destroy();
        e.Remove<Lease>();
        Assert.That((e.IsAlive, e.Has<Ticket>()), Is.EqualTo((true, true)));
        e.Remove<Lease, Ticket>();                                       // RemoveBits frees as well
        Assert.That(e.IsAlive, Is.False);
    }

    [Test]
    public void WithoutCleanupComponentsDestroyFrees()
    {
        using var world = new World();
        var kept = world.CreateEntity(new Lease(1), new Health(1));
        var plain = world.CreateEntity(new Health(2));
        plain.Destroy();
        kept.Remove<Lease>();                                            // not destroyed: removing keeps it alive
        Assert.That(kept.IsAlive, Is.True);
        kept.Destroy();                                                  // a table with the column, a row without it
        Assert.That((plain.IsAlive, kept.IsAlive), Is.EqualTo((false, false)));
    }

    [Test]
    public void ACleanupSystemReleasesEntitiesRetiredInABatch()
    {
        using var world = new World();
        world.AddSingleton<Released>();
        world.AddSystem<ReleaseLeases>();
        for (int i = 0; i < 100; i++) world.CreateEntity(new Lease(i), new Health(i));
        world.CreateEntity(new Health(-1));
        Assert.That(world.DestroyAll<Health>(), Is.EqualTo(101));
        Assert.That(world.DestroyAll<Health>(), Is.Zero);
        Assert.That((world.EntityCount, world.Count<Lease>(QueryFilter.Create().With<Retired>())), Is.EqualTo((100, 100)));
        Assert.That(world.DestroyAll<Lease>(), Is.Zero);            // retired already
        world.Update();
        Assert.That((world.EntityCount, world.GetSingleton<Released>().Count), Is.EqualTo((0, 100)));
    }

    [Test]
    public void RetiringUnlinksIndexesAndClearsDisabledValues()
    {
        using var world = new World();
        var parent = world.CreateEntity();
        var e = world.CreateEntity(new Lease(1), new ChildOf(parent), new Callsign("x"));
        e.Disable<Callsign>();
        e.Destroy();
        Assert.That(world.Index<ChildOf, EntityId>().Count(parent), Is.Zero);
        Assert.That((e.IsDisabled<Callsign>(), e.Enable<Callsign>()), Is.EqualTo((false, false)));
    }

    [Test]
    public void ARetiredEntityCannotBeCloned()
    {
        using var world = new World();
        var e = world.CreateEntity(new Lease(1), new Health(1));
        var copy = e.Clone();                                            // alive: cloned with its cleanup component
        Assert.That(copy.Read<Lease>().Slot, Is.EqualTo(1));
        e.Destroy();
        var error = Assert.Throws<InvalidOperationException>(() => e.Clone());
        Assert.That(error!.Message, Does.Contain("retired"));
        Assert.That((world.EntityCount, world.Count<Lease>(QueryFilter.Create().With<Retired>())), Is.EqualTo((2, 1)));
    }

    [Test]
    public void BulkWritesLeaveRetiredEntitiesOut()
    {
        using var world = new World();
        var retired = world.CreateEntity(new Lease(1), new Health(1));
        var live = world.CreateEntity(new Lease(2), new Health(2));
        retired.Destroy();

        Assert.That(world.SetAll(new Health(5)), Is.EqualTo(1));        // the retired entity does not come back
        Assert.That((retired.Has<Health>(), live.Read<Health>().Value, world.Count<Health>()), Is.EqualTo((false, 5, 1)));
        Assert.That(world.SetAll(new Lease(9)), Is.EqualTo(1));
        Assert.That((retired.Read<Lease>().Slot, live.Read<Lease>().Slot), Is.EqualTo((1, 9)));

        // Named in the filter, the tag reaches them; RemoveAll always does (removing is how they are released).
        Assert.That(world.SetAll(new Lease(7), QueryFilter.Create().With<Retired>()), Is.EqualTo(1));
        Assert.That(retired.Read<Lease>().Slot, Is.EqualTo(7));
        Assert.That(world.RemoveAll<Lease>(), Is.EqualTo(2));
        Assert.That((retired.IsAlive, live.IsAlive, world.EntityCount), Is.EqualTo((false, true, 1)));
    }
}
