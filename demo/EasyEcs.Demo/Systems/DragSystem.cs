using EasyEcs;

namespace EasyEcs.Demo;

// Slows every spark down, as air would: a burst expands fast and settles. One call per spark.
[EcsSystem, UpdateInGroup<PerEntity>]
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
