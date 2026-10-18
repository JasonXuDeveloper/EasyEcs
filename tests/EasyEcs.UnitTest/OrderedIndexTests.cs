using System.Collections.Generic;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public record struct Rating(int Points) : IComponent, IIndexed<int>
{
    public int Key => Points;
}

public record struct Deadline(string At) : IComponent, IIndexed<string> // a managed, comparable key
{
    public string Key => At;
}

public class OrderedIndexTests
{
    private static List<int> Keys(ValueIndex<Rating, int>.Ordered walk)
    {
        var keys = new List<int>();
        foreach (var e in walk) keys.Add(e.Read<Rating>().Points);
        return keys;
    }

    [Test]
    public void WalksKeysInOrderAndWithinRanges()
    {
        using var world = new World();
        foreach (int points in new[] { 50, 10, 30, 30, 70, 20 }) world.CreateEntity(new Rating(points));
        var scores = world.Index<Rating, int>();
        Assert.That(Keys(scores.Ascending()), Is.EqualTo(new[] { 10, 20, 30, 30, 50, 70 }));
        Assert.That(Keys(scores.Descending()), Is.EqualTo(new[] { 70, 50, 30, 30, 20, 10 }));
        Assert.That(Keys(scores.Range(20, 50)), Is.EqualTo(new[] { 20, 30, 30, 50 }));
        Assert.That(Keys(scores.Range(21, 49)), Is.EqualTo(new[] { 30, 30 }));
        Assert.That(Keys(scores.RangeDescending(15, 60)), Is.EqualTo(new[] { 50, 30, 30, 20 }));
        Assert.That(Keys(scores.Range(80, 90)), Is.Empty);
        Assert.That(Keys(scores.Range(50, 10)), Is.Empty);
    }

    [Test]
    public void KeepsOrderAsValuesChange()
    {
        using var world = new World();
        var scores = world.Index<Rating, int>();
        var a = world.CreateEntity(new Rating(5));
        Assert.That(Keys(scores.Ascending()), Is.EqualTo(new[] { 5 }));   // sorted now; kept from here
        var b = world.CreateEntity(new Rating(1));
        world.CreateEntity(new Rating(9));
        a.Set(new Rating(12));
        b.Remove<Rating>();
        Assert.That(Keys(scores.Ascending()), Is.EqualTo(new[] { 9, 12 }));
        for (int i = 0; i < 100; i++) world.CreateEntity(new Rating(i * 7 % 101));
        var keys = Keys(scores.Ascending());
        for (int i = 1; i < keys.Count; i++) Assert.That(keys[i], Is.GreaterThanOrEqualTo(keys[i - 1]));
        Assert.That(keys, Has.Count.EqualTo(102));
    }

    [Test]
    public void DestroyingDuringAWalkVisitsTheRest()
    {
        using var world = new World();
        for (int i = 0; i < 10; i++) world.CreateEntity(new Rating(i));
        var scores = world.Index<Rating, int>();
        var seen = new List<int>();
        foreach (var e in scores.Ascending())
        {
            seen.Add(e.Read<Rating>().Points);
            e.Destroy();                                                   // its key leaves the sorted keys
        }

        Assert.That(seen, Is.EqualTo(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }));
        seen.Clear();
        for (int i = 0; i < 10; i++) world.CreateEntity(new Rating(i));
        foreach (var e in scores.Descending())
        {
            seen.Add(e.Read<Rating>().Points);
            e.Destroy();
        }

        Assert.That(seen, Is.EqualTo(new[] { 9, 8, 7, 6, 5, 4, 3, 2, 1, 0 }));
    }

    [Test]
    public void ManagedKeysAndRestore()
    {
        using var world = new World();
        world.CreateEntity(new Deadline("b"));
        world.CreateEntity(new Deadline("a"));
        var deadlines = world.Index<Deadline, string>();
        var snapshot = world.Capture();
        world.CreateEntity(new Deadline("c"));
        var order = new List<string>();
        foreach (var e in deadlines.Ascending()) order.Add(e.Read<Deadline>().At);
        Assert.That(order, Is.EqualTo(new[] { "a", "b", "c" }));
        world.Restore(snapshot);
        order.Clear();
        foreach (var e in deadlines.Descending()) order.Add(e.Read<Deadline>().At);
        Assert.That(order, Is.EqualTo(new[] { "b", "a" }));
    }
}
