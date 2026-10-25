using EasyEcs;

namespace EasyEcs.Demo;

// Dims the flash of the sparks that hit a wall and removes it when it is gone.
[EcsSystem]
public ref struct FadeSystem
{
    [ForEach(SimdMode = SimdMode.Off)]
    static void Fade(Entity spark, ref Flash flash, in Clock clock)
    {
        flash.Left -= clock.Dt;
        if (flash.Left <= 0)
        {
            // Flips a presence bit: the spark keeps its row
            spark.RemoveFlash();
        }
    }
}
