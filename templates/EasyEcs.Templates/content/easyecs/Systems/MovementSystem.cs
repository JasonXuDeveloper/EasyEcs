using EasyEcs;

namespace EasyEcsApp;

// Moves every entity with a position and a velocity over one tick.
// A [ForEach] method runs once per entity that has all of its component parameters:
// `ref` for what it writes, `in` for what it reads. A singleton can be a parameter too.
[EcsSystem]
public ref struct MovementSystem
{
    [ForEach]
    static void Move(ref Position position, in Velocity velocity, in Clock clock)
    {
        float dt = clock.DeltaTime;
        position = new Position(position.X + velocity.X * dt, position.Y + velocity.Y * dt);
    }
}
