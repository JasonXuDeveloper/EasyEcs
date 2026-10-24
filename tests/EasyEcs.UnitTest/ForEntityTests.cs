using System;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>
/// [ForEntity] methods: the generator writes the job entity.Run runs and an entry point that takes the entity and the
/// caller's values, returning whether the method ran.
/// </summary>
public static class Orders
{
    public record struct Stock(int Items) : IComponent, ITracked;
    public record struct Sales(int Count) : IComponent, ITracked;
    public struct Closed : IComponent;
    public record struct Fees(int PerItem) : ISingletonComponent;

    // Writes both: both are stamped, and both follow the shop when it closes (a structural change in the middle).
    [ForEntity, Without<Closed>]
    static void Buy(Entity shop, ref Stock stock, ref Sales sales, int quantity)
    {
        stock = stock with { Items = stock.Items - quantity };
        if (stock.Items == 0) shop.Set(new Closed());
        sales = sales with { Count = sales.Count + quantity };
    }

    // Reads only: nothing is stamped; returns its result through a ref value.
    [ForEntity]
    static void Count(in Stock stock, ref int items) => items = stock.Items;

    // A singleton, resolved through the entity's world.
    [ForEntity]
    static void Charge(in Sales sales, in Fees fees, ref long due) => due = (long)sales.Count * fees.PerItem;

    // [Changed<T>]: the entry point takes the tick to look back from.
    [ForEntity, Changed<Stock>]
    static void Restocked(in Stock stock, ref int items) => items = stock.Items;

    // A generic method: the entry point takes the type argument.
    [ForEntity]
    static void Found<T>(in T component, ref bool found) where T : struct, IComponent => found = true;
}

// A system's [ForEntity] method: its entry point is a static member of the system, like any type's.
[EcsSystem]
public ref struct Restock
{
    [ForEach] static void Daily(ref Orders.Stock stock) => stock = stock with { Items = stock.Items + 1 };

    [ForEntity] static void Now(ref Orders.Stock stock, int items) => stock = stock with { Items = stock.Items + items };
}

public class ForEntityTests
{
    [Test]
    public void ASystemsMethodGetsAnEntryPointToo()
    {
        using var world = new World();
        world.AddSystem<Restock>();
        var shop = world.CreateEntity(new Orders.Stock(0));
        world.Update();
        Assert.That(Restock.Now(shop, 10), Is.True);
        Assert.That(shop.Read<Orders.Stock>().Items, Is.EqualTo(11));
    }

    [Test]
    public void TheEntryPointRunsTheMethodOnTheEntity()
    {
        using var world = new World();
        var shop = world.CreateEntity(new Orders.Stock(5), new Orders.Sales(0));
        Assert.That(Orders.Buy(shop, 2), Is.True);
        Assert.That((shop.Read<Orders.Stock>().Items, shop.Read<Orders.Sales>().Count), Is.EqualTo((3, 2)));
        int items = 0;
        Assert.That(Orders.Count(shop, ref items), Is.True);
        Assert.That(items, Is.EqualTo(3));
    }

    [Test]
    public void AStructuralChangeInTheMethodCarriesItsWritesOver()
    {
        using var world = new World();
        var shop = world.CreateEntity(new Orders.Stock(2), new Orders.Sales(1));
        Assert.That(Orders.Buy(shop, 2), Is.True);                            // sells out: closes, moves table
        Assert.That((shop.Has<Orders.Closed>(), shop.Read<Orders.Stock>().Items, shop.Read<Orders.Sales>().Count), Is.EqualTo((true, 0, 3)));
        Assert.That(Orders.Buy(shop, 1), Is.False);                           // closed: the filter refuses it
        Assert.That(shop.Read<Orders.Sales>().Count, Is.EqualTo(3));
    }

    [Test]
    public void FalseWhenTheEntityCannotRunIt()
    {
        using var world = new World();
        var noSales = world.CreateEntity(new Orders.Stock(5));
        var gone = world.CreateEntity(new Orders.Stock(5), new Orders.Sales(0));
        gone.Destroy();
        world.CreateEntity(new Orders.Stock(5), new Orders.Sales(0));           // its id, recycled
        Assert.That(Orders.Buy(noSales, 1), Is.False);
        Assert.That(Orders.Buy(gone, 1), Is.False);                           // stale handle
        Assert.That(Orders.Buy(default, 1), Is.False);
        long due = 0;
        Assert.That(Orders.Charge(default, ref due), Is.False);               // no world to find the singleton in
        Assert.That(noSales.Read<Orders.Stock>().Items, Is.EqualTo(5));
    }

    [Test]
    public void OnlyTheRefParametersAreStamped()
    {
        using var world = new World();
        var shop = world.CreateEntity(new Orders.Stock(5), new Orders.Sales(0));
        ulong mark = world.ChangeTick;
        world.Update();                                                       // the next tick
        int items = 0;
        Orders.Count(shop, ref items);
        var changed = QueryFilter.Create().Changed<Orders.Stock>(mark + 1);
        Assert.That(world.Count<Orders.Stock>(changed), Is.Zero);            // 'in': a read
        Orders.Buy(shop, 1);
        Assert.That(world.Count<Orders.Stock>(changed), Is.EqualTo(1));
        items = 0;
        Assert.That(Orders.Restocked(shop, mark + 1, ref items), Is.True);
        Assert.That(items, Is.EqualTo(4));
        items = 0;
        Assert.That(Orders.Restocked(shop, world.ChangeTick + 1, ref items), Is.False);
        Assert.That(items, Is.Zero);
    }

    [Test]
    public void SingletonsAndGenericMethods()
    {
        using var world = new World();
        world.AddSingleton<Orders.Fees>() = new Orders.Fees(3);
        var shop = world.CreateEntity(new Orders.Stock(5), new Orders.Sales(4));
        long due = 0;
        Assert.That(Orders.Charge(shop, ref due), Is.True);
        Assert.That(due, Is.EqualTo(12));
        bool found = false;
        Assert.That(Orders.Found<Orders.Stock>(shop, ref found) && found, Is.True);
        Assert.That(Orders.Found<Orders.Closed>(shop, ref found), Is.False);
    }

    [Test]
    public void RunningOnOneEntityDoesNotAllocate()
    {
        using var world = new World();
        world.AddSingleton<Orders.Fees>() = new Orders.Fees(1);
        var shop = world.CreateEntity(new Orders.Stock(1_000_000), new Orders.Sales(0));
        long due = 0;
        int items = 0;
        Orders.Buy(shop, 1);
        Orders.Charge(shop, ref due);
        Orders.Count(shop, ref items);
        long before = Allocations.Baseline();
        for (int i = 0; i < 1000; i++)
        {
            Orders.Buy(shop, 1);
            Orders.Charge(shop, ref due);
            Orders.Count(shop, ref items);
        }

        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
        Assert.That((items, due), Is.EqualTo((1_000_000 - 1001, 1001)));
    }
}
