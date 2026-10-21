using System.Numerics;

namespace BlockWorld;

// The numbers of the game, in ticks and blocks. A vanilla server runs 20 ticks a second (50 ms each).
public static class Rules
{
    public const int TicksPerSecond = 20;
    public const int DayLength = 600;          // 30 seconds a day, so a short run sees a night
    public const int ViewDistance = 2;         // chunks around a player in each direction: 5 x 5 loaded
    public const int UnloadAfter = 100;        // ticks a chunk stays loaded after the last player saw it
    public const float Gravity = 0.08f;
    public const float WalkSpeed = 0.2f;
    public const float ZombieSpeed = 0.12f;
    public const int ZombieHealth = 20;
    public const int ZombieDamage = 3;
    public const int PlayerHealth = 20;
    public const int SpawnEvery = 100;         // ticks between two zombies per player at night
    public const int ItemLifetime = 300;
    public const float PickupRange = 1.5f;

    public static bool IsNight(in Clock clock) => clock.Tick % clock.DayLength >= clock.DayLength / 2;

    // Players start on a line through the origin, three blocks apart, standing on the ground.
    public static Vector3 SpawnPoint(int client)
    {
        int x = client % 16 * 3, z = client / 16 * 3;
        return new Vector3(x + 0.5f, Terrain.SurfaceHeight(x, z) + 1, z + 0.5f);
    }

    // A deterministic number from 0 to 255 for any pair of integers: the same world and the same bots on every run.
    public static int Hash(int a, int b)
    {
        unchecked
        {
            uint h = (uint)(a * 73856093) ^ (uint)(b * 19349663);
            h ^= h >> 13;
            h *= 0x5bd1e995;
            h ^= h >> 15;
            return (int)(h & 255);
        }
    }
}
