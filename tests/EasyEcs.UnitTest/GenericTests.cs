using System;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>How many entities a counting system saw, per component it counts.</summary>
public record struct Tally(int Count) : ISingletonComponent;

/// <summary>A generic system: each closed type is a system of its own, with its own access set.</summary>
public class CountOf<T> : EcsSystem where T : struct, IComponent
{
    [ForEach]
    private static void Count(in T component, ref Tally tally) => tally.Count++;
}

/// <summary>A closed generic system as a base type: its code runs, and this type adds a pass of its own.</summary>
public class CountHealthy : CountOf<Health>
{
    [ForEach, Without<Dead>]
    private static void Healthy(in Health hp, ref Score score) => score.Points++;
}

/// <summary>Generic [ForEach] methods outside systems: the entry point takes the type arguments.</summary>
public static class Census
{
    [ForEach, Without<Dead>] // attribute type arguments cannot name type parameters (CS8968)
    private static void CountAlive<T>(in T component, ref int count) where T : struct, IComponent => count++;
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
    public void AGenericSystemRunsForItsClosedType()
    {
        _world.AddSystem<CountOf<Position>>();
        _world.Update();
        Assert.That(_world.GetSingleton<Tally>().Count, Is.EqualTo(3));
    }

    [Test]
    public void AClosedGenericBaseRunsBeforeItsSubclass()
    {
        _world.AddSystem<CountHealthy>();
        _world.Update();
        Assert.That(_world.GetSingleton<Tally>().Count, Is.EqualTo(2), "CountOf<Health>");
        Assert.That(_world.GetSingleton<Score>().Points, Is.EqualTo(1), "the subclass's own pass");
    }

    [Test]
    public void AClosedTypeTheAssemblyNeverNamesIsMadeAtRunTime()
    {
        // Built by reflection, so the generator never sees the closed type: its code is closed on first use.
        var system = (EcsSystem)Activator.CreateInstance(typeof(CountOf<>).MakeGenericType(typeof(Velocity)))!;
        _world.AddSystem(system);
        _world.Update();
        Assert.That(_world.GetSingleton<Tally>().Count, Is.EqualTo(1));
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

[UpdateInGroup<Thawed>]
public class CountThawed : EcsSystem
{
    [ForEach]
    private static void Count(in Position position, ref Tally tally) => tally.Count++;

    [ForEach, With<Health>]
    private static void Score(in Position position, ref Score score) => score.Points++;
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
