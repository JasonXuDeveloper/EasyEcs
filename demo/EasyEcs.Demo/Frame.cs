using EasyEcs;

namespace EasyEcs.Demo;

// What the page draws, written between updates into one float array the page reads in place:
// x, y, glow (0..1) and opacity (0..1) per spark.
public static class Frame
{
    public const int Stride = 4;

    // Seconds a spark takes to fade in at its birth and out before its end.
    private const float FadeTime = 0.5f;

    // A [ForEach] method outside a system: the generator writes Frame.Write(world, frame, ref count) for the host.
    [ForEach]
    static void Write(in Position p, in Lifetime life, Maybe<Flash> flash, float[] frame, ref int count)
    {
        int i = count++ * Stride;
        frame[i] = p.X;
        frame[i + 1] = p.Y;
        frame[i + 2] = flash.HasValue ? flash.Value.Left / Units.FlashTime : 0;
        frame[i + 3] = Math.Clamp(Math.Min(life.Total - life.Left, life.Left) / FadeTime, 0, 1);
    }
}
