using EasyEcs;

namespace EasyEcs.Demo;

// What the page draws, written between updates into one float array the page reads in place:
// x, y, heat (0..1: the color) and light (0..2: the brightness) per spark.
public static class Frame
{
    public const int Stride = 4;

    // Seconds a spark takes to fade in at its birth and out before its end.
    private const float FadeTime = 0.6f;

    // A [ForEach] method outside a system: the generator writes
    // Frame.Write(world, frame, ref count) for the host.
    [ForEach]
    static void Write(in Position p, in Velocity v, in Lifetime life, ReadOnlyMaybe<Flash> flash,
        in Arena arena, float[] frame, ref int count)
    {
        // Heat is the speed against a fresh spark's: drag cools a spark from hot to cold
        float full = Units.BurstSpeed * MathF.Min(arena.Width, arena.Height);
        float heat = MathF.Min(1, MathF.Sqrt(v.X * v.X + v.Y * v.Y) / full);
        float fade = Math.Clamp(MathF.Min(life.Total - life.Left, life.Left) / FadeTime, 0, 1);

        // A wall hit flashes to the hot end and doubles the light, then fades with the Flash
        float hit = flash.HasValue ? flash.Value.Left / Units.FlashTime : 0;

        int i = count++ * Stride;
        frame[i] = p.X;
        frame[i + 1] = p.Y;
        frame[i + 2] = MathF.Max(heat, hit);
        frame[i + 3] = fade * (1 + hit);
    }
}
