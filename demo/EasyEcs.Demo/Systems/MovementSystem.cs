using EasyEcs;

namespace EasyEcs.Demo;

// Moves every spark by its velocity over the step. One call per spark.
[UpdateInGroup<PerEntity>]
public class MovementSystem : EcsSystem
{
    [ForEach]
    static void Move(ref Position p, in Velocity v, in Clock clock)
    {
        p.X += v.X * clock.Dt;
        p.Y += v.Y * clock.Dt;
    }
}
