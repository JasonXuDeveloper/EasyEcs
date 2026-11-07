using EasyEcs;

namespace EasyEcs.Demo;

// Moves every spark by its velocity over the step. The method is written for one spark; the
// generator turns it into a vector loop over whole runs of sparks.
[EcsSystem, UpdateInGroup<Vectorized>]
public ref struct MovementSystem
{
    [ForEach]
    static void Move(ref Position p, in Velocity v, in Clock clock)
    {
        p.X += v.X * clock.Dt;
        p.Y += v.Y * clock.Dt;
    }
}
