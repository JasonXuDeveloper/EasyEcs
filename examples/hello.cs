#:package EasyEcs@4.0.1
// Hello: components, a tag, a singleton, two systems and the update loop.
using EasyEcs;

using var world = new World();
world.AddSingleton<Clock>();
world.AddSystem<MoveSystem>();
world.AddSystem<ClockSystem>();

var hero = world.CreateEntity(new Position(0, 0), new Velocity(1, 2), new Hero());
world.CreateEntity(new Position(5, 5), new Velocity(-1, 0));

for (int i = 0; i < 3; i++) world.Update();
Console.WriteLine($"after {world.Clock.Tick} ticks the hero is at {hero.Position}");   // (3, 6)

public record struct Position(float X, float Y) : IComponent;
public record struct Velocity(float X, float Y) : IComponent;
public struct Hero : IComponent;                                    // a tag
public record struct Clock(long Tick) : ISingletonComponent;        // one per world

public class MoveSystem : EcsSystem
{
    [ForEach]                                                       // once per entity with both components
    static void Move(ref Position p, in Velocity v) => p = new Position(p.X + v.X, p.Y + v.Y);
}

public class ClockSystem : EcsSystem
{
    protected override void OnUpdate() => World.Clock.Tick++;
}
