using NUnit.Framework;

namespace EasyEcs.UnitTest;

public partial class ChangedTests
{
    /// <summary>Counts, per entity, the runs in which its Armor had changed since the system's previous run.</summary>
    public partial class ArmorWatch : EcsSystem
    {
        [ForEach, Changed<Armor>]
        private static void Count(ref Seen seen) => seen.Count++;
    }

    /// <summary>Wears down the armor of stunned entities: a writer the watch runs after.</summary>
    public partial class Wear : EcsSystem
    {
        [ForEach, With<Stun>]
        private static void Apply(ref Armor armor) => armor = armor with { Value = armor.Value - 1 };
    }

    /// <summary>Reads armor only: an <c>in</c> parameter is not a write.</summary>
    public partial class Inspect : EcsSystem
    {
        [ForEach]
        private static void Read(in Armor armor, ref Health health) => health = new Health(armor.Value);
    }

    private static int SeenCount(Entity e) => e.Get<Seen>().Count;

    [Test]
    public void ChangedMethodsSeeRowsWrittenSinceTheirLastRun()
    {
        using var world = new World();
        world.AddSystem<ArmorWatch>();
        var a = world.CreateEntity(new Armor(5), new Seen());
        var b = world.CreateEntity(new Armor(5), new Seen());

        world.Update(); // first run: every row is new
        Assert.That((SeenCount(a), SeenCount(b)), Is.EqualTo((1, 1)));

        world.Update(); // nothing written
        Assert.That((SeenCount(a), SeenCount(b)), Is.EqualTo((1, 1)));

        a.Get<Armor>() = new Armor(3); // a ref access between updates
        world.Update();
        Assert.That((SeenCount(a), SeenCount(b)), Is.EqualTo((2, 1)));

        b.Set(new Armor(1));
        world.Update();
        Assert.That((SeenCount(a), SeenCount(b)), Is.EqualTo((2, 2)));
    }

    [Test]
    public void RefParametersStampAndInParametersDoNot()
    {
        using var world = new World();
        world.AddSystem<Wear>();
        world.AddSystem<Inspect>();
        world.AddSystem<ArmorWatch>();
        var worn = world.CreateEntity(new Armor(5), new Seen(), new Health(0), new Stun(1));
        var read = world.CreateEntity(new Armor(5), new Seen(), new Health(0));

        world.Update();
        world.Update(); // Wear writes worn's armor every update; Inspect only reads both
        Assert.That(SeenCount(worn), Is.EqualTo(2));
        Assert.That(SeenCount(read), Is.EqualTo(1));
        Assert.That(read.Get<Health>().Value, Is.EqualTo(5));
    }

    [Test]
    public void QueryFilterSelectsRowsWrittenAtOrAfterATick()
    {
        using var world = new World(new WorldOptions { InitialEntityCapacity = 4 });
        var entities = new Entity[100];
        world.CreateEntities(entities, new Armor(1));
        world.Update();

        ulong mark = world.ChangeTick;
        var changed = QueryFilter.Create().Changed<Armor>().ChangedSince(mark);
        Assert.That(world.Count<Armor>(changed), Is.Zero);

        entities[3].Get<Armor>() = new Armor(2);
        entities[70].Set(new Armor(2));
        foreach (var _ in world.Query<Armor>(QueryFilter.Create().With<Stun>())) { } // matches nothing: no stamp
        Assert.That(world.Count<Armor>(changed), Is.EqualTo(2));
        Assert.That(world.Any<Armor>(changed), Is.True);

        int visited = 0;
        foreach (var item in world.Query<Armor>(changed)) visited += item.Component1.Value;
        Assert.That(visited, Is.EqualTo(4));

        // Without a tick, Changed<T> selects every row having T.
        Assert.That(world.Count<Armor>(QueryFilter.Create().Changed<Armor>()), Is.EqualTo(100));
        Assert.That(world.DestroyEntities<Armor>(changed), Is.EqualTo(2));
    }

    [Test]
    public void TicksFollowEntitiesThatMove()
    {
        using var world = new World();
        var e = world.CreateEntity(new Armor(1));
        world.Update();
        ulong mark = world.ChangeTick;
        e.Get<Armor>() = new Armor(2);
        e.Set(new Frozen()); // moves to another table: the tick goes with it
        var changed = QueryFilter.Create().Changed<Armor>().ChangedSince(mark);
        Assert.That(world.Count<Armor>(changed), Is.EqualTo(1));

        e.Remove<Armor>();
        Assert.That(world.Count<Armor>(changed), Is.Zero);
        e.Add<Armor>(); // adding is a write
        Assert.That(world.Count<Armor>(changed), Is.EqualTo(1));
    }

    [Test]
    public void TicksFollowRowsThatFillHoles()
    {
        using var world = new World();
        var entities = new Entity[200];
        world.CreateEntities(entities, new Armor(1));
        world.Update();
        ulong mark = world.ChangeTick;
        entities[199].Get<Armor>() = new Armor(2); // the last row, in the last 64-row word
        entities[0].Destroy();                     // the last row moves into row 0, whose word was not written
        var changed = QueryFilter.Create().Changed<Armor>().ChangedSince(mark);
        Assert.That(world.Count<Armor>(changed), Is.EqualTo(1));
        foreach (var item in world.Query<Armor>(changed)) Assert.That(item.Entity, Is.EqualTo(entities[199]));
    }
}
