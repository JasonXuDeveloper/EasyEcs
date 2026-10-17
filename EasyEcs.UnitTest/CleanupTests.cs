using NUnit.Framework;

namespace EasyEcs.UnitTest;

public record struct Lease(int Slot) : ICleanupComponent;   // a resource outside the world, released exactly once
public record struct Ticket(int Seat) : ICleanupComponent;  // a second one

/// <summary>Releases leases of destroyed entities: the system a cleanup component asks for.</summary>
public partial class ReleaseLeases : EcsSystem
{
    [ForEach, With<Destroyed>]
    static void Release(Entity entity, in Lease lease, ref Released released)
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
        Assert.That((e.IsAlive, e.Has<Destroyed>(), e.Get<Lease>().Slot), Is.EqualTo((true, true, 3)));
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
    public void ACleanupSystemReleasesBatchDestroyedEntities()
    {
        using var world = new World();
        world.AddSingleton<Released>();
        world.AddSystem<ReleaseLeases>();
        for (int i = 0; i < 100; i++) world.CreateEntity(new Lease(i), new Health(i));
        world.CreateEntity(new Health(-1));
        Assert.That(world.DestroyEntities<Health>(), Is.EqualTo(101));
        Assert.That(world.DestroyEntities<Health>(), Is.Zero);
        Assert.That((world.EntityCount, world.Count<Lease>(QueryFilter.Create().With<Destroyed>())), Is.EqualTo((100, 100)));
        Assert.That(world.DestroyEntities<Lease>(), Is.Zero);            // retired already
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
}
