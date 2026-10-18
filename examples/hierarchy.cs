#:package EasyEcs@4.0.1
// Hierarchy: ChildOf links a child to its parent; destroying a parent destroys its descendants.
using EasyEcs;

using var world = new World();
var ship = world.CreateEntity(new Name("ship"));
var turret = world.CreateEntity(new Name("turret"), new ChildOf(ship));
var gun = world.CreateEntity(new Name("gun"), new ChildOf(turret));
var dock = world.CreateEntity(new Name("dock"));

foreach (var child in world.Children(ship)) Console.WriteLine($"ship > {child.Read<Name>().Value}");

gun.Set(new ChildOf(ship));                                       // re-parent: set a new value
turret.Remove<ChildOf>();                                         // detach: the turret is a root now
Console.WriteLine($"ship children: {Count(world, ship)}, turret children: {Count(world, turret)}");

ship.Destroy();                                                    // the gun goes with it
Console.WriteLine($"left: {world.EntityCount} entities (turret alive {turret.IsAlive}, gun alive {gun.IsAlive}, dock alive {dock.IsAlive})");

static int Count(World world, Entity parent)
{
    int count = 0;
    foreach (var _ in world.Children(parent)) count++;
    return count;
}

public record struct Name(string Value) : IComponent;
