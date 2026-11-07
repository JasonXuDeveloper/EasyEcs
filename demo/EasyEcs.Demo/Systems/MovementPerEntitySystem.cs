using EasyEcs;

namespace EasyEcs.Demo;

// MovementSystem's method with SimdMode.Off: the generator keeps it one call per spark, so the
// page can time what the vector loop saves.
[EcsSystem, UpdateInGroup<PerEntity>]
public ref struct MovementPerEntitySystem
{
    [ForEach(SimdMode = SimdMode.Off)]
    static void Move(ref Position p, in Velocity v, in Clock clock)
    {
        p.X += v.X * clock.Dt;
        p.Y += v.Y * clock.Dt;
    }
}
