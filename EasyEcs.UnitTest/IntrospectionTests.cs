using System;
using System.Linq;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public class IntrospectionTests
{
    [Test]
    public void ATableShowsItsComponentsRowsAndMemory()
    {
        using var world = new World();
        world.CreateEntity(new Health(1), new Armor(2));
        var unarmored = world.CreateEntity(new Health(3), new Armor(4));
        world.CreateEntity(new Position(0, 0));
        unarmored.Remove<Armor>();                                            // stays in its table, without the column

        var table = world.Describe().Single(t => t.Columns.Any(c => c.Type == typeof(Armor)));
        Assert.That(table.Columns.Select(c => c.Type), Is.EquivalentTo(new[] { typeof(Health), typeof(Armor) })); // no tick column
        Assert.That(table.Columns.Single(c => c.Type == typeof(Armor)).Without, Is.EqualTo(1));
        Assert.That((table.Entities, table.Rows), Is.EqualTo((2, 2)));
        Assert.That(table.Capacity, Is.GreaterThanOrEqualTo(2));
        Assert.That(table.Bytes, Is.GreaterThanOrEqualTo(table.Capacity * (8L + sizeof(int) * 2 + sizeof(ulong)))); // slots, values, ticks
    }

    [Test]
    public void AnEntityShowsItsValuesAndWhichAreDisabled()
    {
        using var world = new World();
        var entity = world.CreateEntity(new Health(1), new Armor(2));
        entity.Disable<Armor>();
        Assert.That(entity.Describe(), Is.EquivalentTo(new[]
        {
            new ComponentValue(new Health(1), true),
            new ComponentValue(new Armor(2), false),
        }));

        entity.Destroy();
        Assert.That(entity.Describe(), Is.Empty);
    }

    [Test]
    public void AGroupedTableShowsItsGroup()
    {
        using var world = new World();
        world.GroupBy<Zone>();
        world.CreateEntity(new Zone(7));
        var table = world.Describe().Single(t => t.Entities == 1);
        Assert.That(table.Columns.Single().Group, Is.EqualTo(7));
    }
}
