#:package EasyEcs@4.0.1
// Prefabs: a recipe of component values outside any world, instantiated one at a time or as a wave.
using EasyEcs;

using var world = new World();

// Generated writers return the prefab, so a recipe is one chain. A prefab can start from another.
var goblin = new Prefab().SetPosition(0, 0).SetHealth(30).SetEnemy();
var chief = new Prefab(goblin).SetHealth(80);                     // a goblin with more health

var first = world.Instantiate(goblin);                            // a copy of every value
first.Position = new Position(10, 20);                            // then only what differs
var wave = new Entity[100];
world.Instantiate(goblin, wave);                                  // a wave: one pass through the structural gate
var boss = world.Instantiate(chief);
var twin = boss.Clone();                                          // a copy of a living entity, as it is now

Console.WriteLine($"{world.Count<Health>(QueryFilter.Create().With<Enemy>())} enemies");                  // 103
Console.WriteLine($"first at {first.Position}, boss {boss.Health.Value} hp, twin {twin.Health.Value} hp");
Console.WriteLine($"queries never see the prefab itself: {world.EntityCount} entities");

public record struct Position(float X, float Y) : IComponent;
public record struct Health(int Value) : IComponent;
public struct Enemy : IComponent;
