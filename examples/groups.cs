#:package EasyEcs@4.0.1
// Groups: GroupBy splits tables by the value of an IGroup component, so a query of one group walks only its rows.
// System groups (systems run at their own cadence, [UpdateInGroup<T>]) are another thing: see samples/GameLoop.
using EasyEcs;

using var world = new World();
world.GroupBy<Zone>();                                             // at setup: existing entities move now
for (int i = 0; i < 9; i++) world.CreateEntity(new Zone(i % 3), new Health(10 * i));

Console.WriteLine($"zone 1: {world.Count<Health>(QueryFilter.Create().InGroup<Zone>(1))} units, {Sum(world, 1)} hp");
foreach (var item in world.Query<Health>(QueryFilter.Create().InGroup<Zone>(2)))
    item.Entity.SetZone(1);                                        // a new value moves the entity to its group's tables
Console.WriteLine($"zone 1: {world.Count<Health>(QueryFilter.Create().InGroup<Zone>(1))} units, {Sum(world, 1)} hp");

static int Sum(World world, int zone)
{
    int sum = 0;
    foreach (var item in world.Query<Health>(QueryFilter.Create().InGroup<Zone>(zone))) sum += item.Health.Value;
    return sum;
}

public record struct Zone(int Id) : IComponent, IGroup { public int Group => Id; }
public record struct Health(int Value) : IComponent;
