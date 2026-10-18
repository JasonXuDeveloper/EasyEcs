using EasyEcs;

namespace EasyEcs.Demo;

// Dims the glow of the sparks that bounced and removes it when it is gone.
public class FadeSystem : EcsSystem
{
    [ForEach]
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
