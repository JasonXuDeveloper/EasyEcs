using EasyEcs;

namespace EasyEcs.Demo;

// Moves every spark by its velocity over the step. One call per spark.
[EcsSystem, UpdateInGroup<PerEntity>]
public ref struct MovementSystem
{
    [ForEach]
    static void Move(ref Position p, in Velocity v, in Clock clock)
    {
        p.X += v.X * clock.Dt;
        p.Y += v.Y * clock.Dt;
    }
}
