using EasyEcs;

namespace EasyEcs.Demo;

// Turns sparks back at the walls and makes them flash: the hit is written as data (Flash),
// FadeSystem takes it from there.
[EcsSystem]
public ref struct BounceSystem
{
    [ForEach(SimdMode = SimdMode.Off)]
    static void Bounce(Entity spark, ref Position p, ref Velocity v, in Arena arena)
    {
        bool hit = false;
        if (p.X < 0 || p.X > arena.Width)
        {
            p.X = Math.Clamp(p.X, 0, arena.Width);
            v.X = -v.X;
            hit = true;
        }

        if (p.Y < 0 || p.Y > arena.Height)
        {
            p.Y = Math.Clamp(p.Y, 0, arena.Height);
            v.Y = -v.Y;
            hit = true;
        }

        if (hit)
        {
            spark.SetFlash(Units.FlashTime);
        }
    }
}
