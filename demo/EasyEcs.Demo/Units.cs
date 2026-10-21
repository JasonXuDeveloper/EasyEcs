using EasyEcs;

namespace EasyEcs.Demo;

// The recipes and tuning of the demo, shared by the systems.
public static class Units
{
    // The recipe of a spark: a row outside the world that Instantiate copies. A burst passes each
    // spark's own values to Instantiate, which writes them in the same row copy.
    public static readonly Prefab Spark =
        new Prefab().Set(new Position(), new Velocity(), new Lifetime(1, 1));

    // Seconds a spark glows after a wall hit.
    public const float FlashTime = 0.35f;

    // The speed of a fresh spark, as a fraction of the arena's shorter side per second. Drag slows
    // it down, so the color of a spark (by speed) tells how old it is.
    public const float BurstSpeed = 0.6f;

    // The share of its speed a spark loses per second, to drag.
    public const float Drag = 0.8f;

    // What a spark keeps of its speed over a step of `dt` seconds.
    public static float Keep(float dt) => MathF.Max(0, 1 - Drag * dt);

    // The emitters, spread evenly along their curve.
    public const int Emitters = 5;

    // A deterministic random number per (tick, n): the same run every time, no generator object to
    // keep.
    public static uint Roll(long tick, int n)
    {
        ulong x = (ulong)tick * 0x9E3779B97F4A7C15UL ^ (uint)n * 0xBF58476D1CE4E5B9UL;
        x ^= x >> 31;
        x *= 0x94D049BB133111EBUL;
        return (uint)(x ^ (x >> 29));
    }

    // A roll as a float in [0, 1).
    public static float Unit(long tick, int n) => (Roll(tick, n) >> 8) * (1f / (1 << 24));
}
