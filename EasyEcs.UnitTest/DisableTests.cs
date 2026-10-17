using NUnit.Framework;

namespace EasyEcs.UnitTest;

public class DisableTests
{
    [Test]
    public void ADisabledComponentKeepsItsValueButIsAbsent()
    {
        using var world = new World();
        var e = world.CreateEntity(new Health(42), new Position(1, 2));
        Assert.That(e.Disable<Health>(), Is.True);
        Assert.That((e.Has<Health>(), e.IsDisabled<Health>(), e.Has<Disabled<Health>>()), Is.EqualTo((false, true, true)));
        Assert.That(world.Count<Health>(), Is.Zero);
        Assert.That(world.Count<Position>(QueryFilter.Create().With<Disabled<Health>>()), Is.EqualTo(1)); // the tag is data
        Assert.That(e.Disable<Health>(), Is.False);                           // disabled already

        Assert.That(e.Enable<Health>(), Is.True);
        Assert.That((e.Get<Health>().Value, e.IsDisabled<Health>()), Is.EqualTo((42, false)));
        Assert.That(e.Enable<Health>(), Is.False);                            // enabled already
        Assert.That(world.Count<Health>(), Is.EqualTo(1));
    }

    [Test]
    public void SetReplacesAndRemoveDropsADisabledComponent()
    {
        using var world = new World();
        var a = world.CreateEntity(new Health(1));
        var b = world.CreateEntity(new Health(2));
        a.Disable<Health>();
        b.Disable<Health>();
        a.Set(new Health(5));                                                 // a new value: enabled, the kept one is gone
        Assert.That((a.Get<Health>().Value, a.IsDisabled<Health>()), Is.EqualTo((5, false)));
        b.Remove<Health>();                                                   // removed: nothing to enable
        Assert.That((b.IsDisabled<Health>(), b.Enable<Health>(), b.Has<Health>()), Is.EqualTo((false, false, false)));
    }

    [Test]
    public void DisablingIsARemovalAndEnablingAWrite()
    {
        using var world = new World();
        var e = world.CreateEntity(new Armor(3));
        world.Update();
        ulong mark = world.ChangeTick;
        e.Disable<Armor>();
        var removed = QueryFilter.Create().Removed<Armor>(mark);
        var changed = QueryFilter.Create().Changed<Armor>(mark);
        Assert.That(world.Count<Disabled<Armor>>(removed), Is.EqualTo(1));
        world.Update();
        mark = world.ChangeTick;
        e.Enable<Armor>();
        Assert.That((world.Count<Armor>(changed.Since(mark)), world.Count<Disabled<Armor>>(removed.Since(mark))), Is.EqualTo((1, 0)));
    }

    [Test]
    public void ADisabledIndexedComponentLeavesItsIndexUntilEnabled()
    {
        using var world = new World();
        var parent = world.CreateEntity();
        var children = world.Index<ChildOf, EntityId>();
        var kid = world.CreateEntity(new ChildOf(parent));
        Assert.That(children.Count(parent), Is.EqualTo(1));
        kid.Disable<ChildOf>();
        Assert.That(children.Count(parent), Is.Zero);
        kid.Enable<ChildOf>();
        Assert.That(children.Count(parent), Is.EqualTo(1));
    }

    [Test]
    public void ManagedValuesSurviveMovesClonesAndSnapshots()
    {
        using var world = new World();
        var e = world.CreateEntity(new Name("kept"), new Health(1));
        e.Disable<Name>();
        e.Set(new Position(0, 0));                                            // a move to another table
        var copy = e.Clone();
        var snapshot = world.Capture();
        e.Enable<Name>();
        e.Set(new Name("changed"));
        world.Restore(snapshot);
        foreach (var x in new[] { e, copy })
        {
            Assert.That(x.IsDisabled<Name>(), Is.True);
            x.Enable<Name>();
            Assert.That(x.Get<Name>().Value, Is.EqualTo("kept"));
        }
    }
}
