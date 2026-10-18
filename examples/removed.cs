#:package EasyEcs@4.0.1
// Removal detection: [Removed<T>] visits entities that lost a tracked component since the system last ran.
using EasyEcs;

using var world = new World();
world.AddSystem<BurnSystem>();
world.AddSystem<FireOutSystem>();
var a = world.CreateEntity(new Burning(1), new Health(10));
var b = world.CreateEntity(new Burning(3), new Health(10));

for (int tick = 1; tick <= 4; tick++)
{
    world.Update();
    Console.WriteLine($"tick {tick}: a {a.Health.Value} hp, burning {a.HasBurning}; b {b.Health.Value} hp, burning {b.HasBurning}");
}

public record struct Burning(int Ticks) : IComponent, ITrackChanges;   // tracked: removals are stamped too
public record struct Health(int Value) : IComponent;

public class BurnSystem : EcsSystem
{
    [ForEach]
    static void Burn(Entity unit, ref Burning burning, ref Health hp)
    {
        hp.Value -= 2;
        if (--burning.Ticks == 0) unit.RemoveBurning();               // the fire goes out
    }
}

[UpdateAfter<BurnSystem>]
public class FireOutSystem : EcsSystem
{
    [ForEach, Removed<Burning>]                                     // lost Burning since the last run, still without it
    static void FireOut(Entity unit, in Health hp) => Console.WriteLine($"  {unit}: the fire went out at {hp.Value} hp");
}
