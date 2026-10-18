using EasyEcs;

namespace EasyEcs.Demo;

// Moves the emitters along a slow Lissajous curve through the arena.
public class OrbitSystem : EcsSystem
{
    // Radians of the curve per second.
    private const float Speed = 0.11f;

    [ForEach]
    static void Orbit(ref Position p, ref Emitter emitter, in Arena arena, in Clock clock)
    {
        emitter.Phase += Speed * clock.Dt;
        float t = emitter.Phase;
        p.X = arena.Width * (0.5f + 0.36f * MathF.Sin(3 * t));
        p.Y = arena.Height * (0.5f + 0.34f * MathF.Sin(2 * t + MathF.PI / 2));
    }
}
