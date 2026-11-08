using EasyEcs;

namespace EasyEcs.Demo;

// Slows every spark down, as air would: a burst expands fast and settles. Written for one spark,
// run as a vector loop.
[EcsSystem, UpdateInGroup<Vectorized>]
public ref struct DragSystem
{
    [ForEach]
    static void Slow(ref Velocity v, in Clock clock)
    {
        float keep = Units.Keep(clock.Dt);
        v.X *= keep;
        v.Y *= keep;
    }
}
