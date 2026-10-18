using EasyEcs;
using HelloWorld;

using var world = new World();

// The clock starts at tick 0.
world.AddSingleton<Clock>();

// Systems run in the order they are added, once per world.Update().
world.AddSystem<MoveSystem>();
world.AddSystem<ReportSystem>();
world.AddSystem<ClockSystem>();

// A player, and another entity that moves too but is not a player, so it is not reported.
world.CreateEntity(new Position(0, 0), new Velocity(1, 2), new Player());
world.CreateEntity(new Position(5, 5), new Velocity(-1, 0));

for (int i = 0; i < 3; i++)
{
    world.Update();
}

Console.WriteLine($"{world.EntityCount} entities after {world.Clock.Tick} ticks");
