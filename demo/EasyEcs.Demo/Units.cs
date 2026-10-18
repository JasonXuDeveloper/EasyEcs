using EasyEcs;

namespace EasyEcs.Demo;

// The recipe of a spark: a row outside the world that Instantiate copies. SpawnSystem fills in the values.
public static class Units
{
    public static readonly Prefab Spark = new Prefab().Set(new Position(), new Velocity(), new Lifetime(1, 1));

    // Seconds a spark glows after a bounce.
    public const float FlashTime = 0.5f;

    // A deterministic random number per (tick, n): the same run every time, no generator object to keep.
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
