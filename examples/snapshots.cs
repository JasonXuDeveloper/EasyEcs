#:package EasyEcs@4.0.1
// Snapshots: Capture copies a world in memory, Restore rolls it back; handles from before stay valid.
using EasyEcs;

using var world = new World();
world.AddSystem<PoisonSystem>();
var hero = world.CreateEntity(new Health(100), new Poisoned());

var save = world.Capture();                                        // before the turn
for (int i = 0; i < 5; i++) world.Update();
var minion = world.CreateEntity(new Health(5));
Console.WriteLine($"after the turn: hero {hero.Health.Value} hp, {world.EntityCount} entities");

world.Restore(save);                                               // undo it
Console.WriteLine($"restored: hero {hero.Health.Value} hp, {world.EntityCount} entities, minion alive {minion.IsAlive}");

public record struct Health(int Value) : IComponent;
public struct Poisoned : IComponent;

public class PoisonSystem : EcsSystem
{
    [ForEach, With<Poisoned>]
    static void Hurt(ref Health hp) => hp.Value -= 10;
}
