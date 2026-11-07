using EasyEcs;

namespace EasyEcs.Demo;

// DragSystem's method with SimdMode.Off: one call per spark.
[EcsSystem, UpdateInGroup<PerEntity>]
public ref struct DragPerEntitySystem
{
    [ForEach(SimdMode = SimdMode.Off)]
    static void Slow(ref Velocity v, in Clock clock)
    {
        float keep = Units.Keep(clock.Dt);
        v.X *= keep;
        v.Y *= keep;
    }
}
