using EasyEcs;

namespace EasyEcs.Demo;

// Slows every spark down, as air would: a burst expands fast and settles. One call per spark.
[UpdateInGroup<PerEntity>]
public class DragSystem : EcsSystem
{
    [ForEach]
    static void Slow(ref Velocity v, in Clock clock)
    {
        float keep = Units.Keep(clock.Dt);
        v.X *= keep;
        v.Y *= keep;
    }
}
