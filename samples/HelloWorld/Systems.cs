using EasyEcs;

namespace HelloWorld;

// Each system has one job and keeps no state of its own: every value lives in a
// component or a singleton.
//
// A [ForEach] method runs once per entity that has all of its component parameters.
// `ref` marks what the method writes, `in` what it only reads.

// Moves every entity that has a position and a velocity.
public class MoveSystem : EcsSystem
{
    [ForEach]
    static void Move(ref Position position, in Velocity velocity)
    {
        position = new Position(position.X + velocity.X, position.Y + velocity.Y);
    }
}

// Prints where the players are. [With<Player>] limits the method to entities
// that carry the Player tag. A singleton can be a parameter too.
public class ReportSystem : EcsSystem
{
    [ForEach, With<Player>]
    static void Report(in Position position, in Clock clock)
    {
        Console.WriteLine($"tick {clock.Tick}: player at ({position.X}, {position.Y})");
    }
}

// Advances the clock once per update. OnUpdate runs once per update, not per entity.
public class ClockSystem : EcsSystem
{
    protected override void OnUpdate()
    {
        World.Clock.Tick++;
    }
}
