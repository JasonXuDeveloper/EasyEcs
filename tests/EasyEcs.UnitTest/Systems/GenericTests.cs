using System;
using System.Reflection;
using EasyEcs.Aot.Library;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>How many entities a counting system saw, per component it counts.</summary>
public record struct Tally(int Count) : ISingletonComponent;

/// <summary>A generic system: each closed type is a system of its own, with its own access set.</summary>
[EcsSystem]
public ref struct CountOf<T> where T : struct, IComponent
{
    [ForEach]
    private static void Count(in T component, ref Tally tally) => tally.Count++;
}

/// <summary>A component of this assembly that a library's generic system (Regen&lt;T&gt;) heals.</summary>
public record struct Vigor(int Value) : ILife;

/// <summary>Generic [ForEach] methods outside systems: the entry point takes the type arguments.</summary>
public static class Census
{
    [ForEach, Without<Dead>] // attribute type arguments cannot name type parameters (CS8968)
    private static void CountAlive<T>(in T component, ref int count) where T : struct, IComponent => count++;
}

/// <summary>
/// The actor of a type, as a server framework declares it: one tag type per actor type, and one type that is at once
/// the actor's key component (indexed by itself) and the system group of its type, through one interface.
/// </summary>
public struct In<TActor> : IComponent;
public interface IActor<TSelf> : IComponent, IIndexed<TSelf>, ISystemGroup, ISystemGroupWith<In<TSelf>>
    where TSelf : struct, IActor<TSelf>, IEquatable<TSelf>;
public readonly partial record struct MapActor(long Id) : IActor<MapActor> { MapActor IIndexed<MapActor>.Key => this; }
public readonly partial record struct ShopActor(long Id) : IActor<ShopActor> { ShopActor IIndexed<ShopActor>.Key => this; }

[EcsSystem, UpdateInGroup<MapActor>]
public ref struct TickMaps
{
    [ForEach]
    private static void Tick(ref Health health) => health.Value++;
}

/// <summary>A generic runner type: its entry points are members of each closed type.</summary>
public static class Census<T> where T : struct, IComponent
{
    [ForEach]
    private static void Positions(in Position position, in T component, ref float x) => x += position.X;
}

public class GenericTests
{
    private World _world;

    [SetUp]
    public void SetUp()
    {
        _world = new World();
        _world.CreateEntity(new Position(1, 0), new Health(10));
        _world.CreateEntity(new Position(2, 0), new Health(0), new Dead());
        _world.CreateEntity(new Position(4, 0), new Velocity(1, 0));
        _world.AddSingleton<Tally>();
        _world.AddSingleton<Score>();
    }

    [TearDown]
    public void TearDown() => _world.Dispose();

    [Test]
    public void AnActorGroupSeesOnlyTheEntitiesOfItsActorType()
    {
        using var world = new World();
        world.AddSystem<TickMaps>();
        var map = world.CreateEntity(new Health(0), new MapActor(7), new In<MapActor>());
        var shop = world.CreateEntity(new Health(0), new ShopActor(7), new In<ShopActor>());
        var stray = world.CreateEntity(new Health(0), new MapActor(8));   // a key without the actor tag
        world.Update<MapActor>();
        world.Update<ShopActor>();
        Assert.That((map.Read<Health>().Value, shop.Read<Health>().Value, stray.Read<Health>().Value), Is.EqualTo((1, 0, 0)));

        // The group type is also the actor's key component, with its generated members and index.
        int found = 0;
        foreach (var e in world.MapActorIndex.Find(new MapActor(7))) found += e == map ? 1 : 100;
        Assert.That((map.MapActor.Id, map.HasMapActor, shop.HasMapActor, found), Is.EqualTo((7L, true, false, 1)));
        map.SetMapActor(9);
        Assert.That((world.MapActorIndex.Count(new MapActor(7)), world.MapActorIndex.Count(new MapActor(9))), Is.EqualTo((0, 1)));
    }

    [Test]
    public void AGenericSystemRunsForItsClosedType()
    {
        _world.AddSystem<CountOf<Position>>();
        _world.Update();
        Assert.That(_world.GetSingleton<Tally>().Count, Is.EqualTo(3));
    }

    [Test]
    public void AnotherAssemblysGenericSystemRunsClosedHere()
    {
        // Regen<T> is EasyEcs.Aot.Library's; this assembly names Regen<Vigor>, so its generated set holds the code.
        var vigor = _world.CreateEntity(new Vigor(0));
        var regen = _world.AddSystem<Regen<Vigor>>();
        for (int i = 0; i < 4; i++) _world.Update();
        Assert.That((regen.RunCount, vigor.Vigor.Value), Is.EqualTo((2L, 2)), "every second update ([UpdateInterval(2)])");
        Assert.That(regen.Type, Is.EqualTo(typeof(Regen<Vigor>)));
    }

    [Test]
    public void AClosedTypeNoAssemblyNamesThrows()
    {
        // Made by reflection: no assembly's generated code names CountOf<Velocity>, so there is no code to run it.
        var add = typeof(World).GetMethod(nameof(World.AddSystem))!.MakeGenericMethod(typeof(CountOf<>).MakeGenericType(typeof(Velocity)));
        var thrown = Assert.Throws<TargetInvocationException>(() => add.Invoke(_world, [false]));
        Assert.That(thrown!.InnerException, Is.TypeOf<NotSupportedException>());
        Assert.That(_world.Systems.Count, Is.Zero, "nothing was added");
    }

    [Test]
    public void EachClosedTypeIsASystemOfItsOwn()
    {
        // Both write Tally, so they run one after the other; each counts its own component.
        _world.AddSystem<CountOf<Position>>();
        _world.AddSystem<CountOf<Health>>();
        _world.Update();
        Assert.That(_world.GetSingleton<Tally>().Count, Is.EqualTo(5));
    }

    [Test]
    public void GenericRunnersTakeTheirTypeArguments()
    {
        int alive = 0;
        Census.CountAlive<Health>(_world, ref alive);
        Assert.That(alive, Is.EqualTo(1));

        int positions = 0;
        Census.CountAlive<Position>(_world, ref positions);
        Assert.That(positions, Is.EqualTo(2));

        float x = 0;
        Census<Velocity>.Positions(_world, ref x);
        Assert.That(x, Is.EqualTo(4));
        x = 0;
        Census<Health>.Positions(_world, ref x);
        Assert.That(x, Is.EqualTo(3));
    }
}

/// <summary>A group whose filter joins every [ForEach] method of its systems: frozen entities are out of it.</summary>
[Without<Frozen>]
public struct Thawed : ISystemGroup;

[EcsSystem, UpdateInGroup<Thawed>]
public ref struct CountThawed
{
    [ForEach, With<Position>]
    private static void Count(ref Tally tally) => tally.Count++;

    [ForEach, With<Position>, With<Health>]
    private static void Score(ref Score score) => score.Points++;
}

public class GroupFilterTests
{
    [Test]
    public void AGroupsFilterJoinsEveryMethodOfItsSystems()
    {
        using var world = new World();
        world.CreateEntity(new Position(0, 0), new Health(1));
        world.CreateEntity(new Position(1, 0), new Health(1), new Frozen());
        world.CreateEntity(new Position(2, 0));
        world.AddSingleton<Tally>();
        world.AddSingleton<Score>();
        world.AddSystem<CountThawed>();

        world.Update<Thawed>();
        Assert.That(world.GetSingleton<Tally>().Count, Is.EqualTo(2), "the frozen entity is out");
        Assert.That(world.GetSingleton<Score>().Points, Is.EqualTo(1), "with the method's own filter too");
    }
}
