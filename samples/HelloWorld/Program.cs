using EasyEcs;

using var world = new World();
world.AddSingleton<Clock>();
world.AddSystem<MoveSystem>();
world.AddSystem<ReportSystem>();
world.AddSystem<ClockSystem>();

world.CreateEntity(new Position(0, 0), new Velocity(1, 2), new Player());
world.CreateEntity(new Position(5, 5), new Velocity(-1, 0));      // moves too, but is not reported

for (int i = 0; i < 3; i++) world.Update();
Console.WriteLine($"{world.EntityCount} entities after {world.Clock.Tick} ticks");

// Components: plain data on entities.
public record struct Position(float X, float Y) : IComponent;
public record struct Velocity(float X, float Y) : IComponent;
public struct Player : IComponent;                                   // a tag: no fields, a filter

// A singleton: one value per world.
public record struct Clock(long Tick) : ISingletonComponent;

// Systems: one job each, stateless. A [ForEach] method runs once per entity that has its parameters.
public class MoveSystem : EcsSystem
{
    [ForEach]
    static void Move(ref Position p, in Velocity v) => p = new Position(p.X + v.X, p.Y + v.Y);
}

public class ReportSystem : EcsSystem
{
    [ForEach, With<Player>]
    static void Report(in Position p, in Clock clock) => Console.WriteLine($"tick {clock.Tick}: player at ({p.X}, {p.Y})");
}

public class ClockSystem : EcsSystem
{
    protected override void OnUpdate() => World.Clock.Tick++;
}
