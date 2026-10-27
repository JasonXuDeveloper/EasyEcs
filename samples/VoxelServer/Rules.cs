using System.Numerics;

namespace VoxelServer;

// The numbers of the game, in ticks and blocks. A vanilla server runs 20 ticks a second (50 ms each).
public static class Rules
{
    public const int TicksPerSecond = 20;
    public const int DayLength = 600;          // 30 seconds a day, so a short run sees a night
    public const float PlayerVision = 32;      // blocks every player sees: two chunks each way, 5 x 5 chunks loaded
    public const float ZombieVision = 35;      // blocks a zombie sees a player from: its follow range in Minecraft
    public const int LookEvery = TicksPerSecond / 2;   // ticks between two looks of a zombie for a player to hunt
    public const int ScentReach = (int)ZombieVision;   // the scent where a player stands: one less per step, 0 past Vision
    public const int ScentEvery = 4;           // ticks between two steps of the scent: a zombie crosses a block in 8
    public const int MaxDrop = 3;              // blocks a mob's path drops at most, as Minecraft's pathfinding
    public const int UnloadAfter = 100;        // ticks a chunk stays loaded after the last player saw it
    public const float Gravity = 0.08f;
    public const float WalkSpeed = 0.2f;
    public const float ZombieSpeed = 0.12f;
    public const int ZombieHealth = 20;
    public const int ZombieDamage = 3;
    public const int PlayerHealth = 20;
    public const int SpawnEvery = 100;         // ticks between two zombies per player at night
    public const int SpawnMin = 24, SpawnMax = 32;   // blocks from the player a zombie rises (24 at least, as in Minecraft)
    public const int BurnEvery = 5;            // ticks between two points of fire damage (Minecraft: 20; days are short here)
    public const int BurnFor = 40;             // ticks a zombie burns on after it found shade
    public const int ItemLifetime = 300;       // ticks an item lies on the ground (Minecraft: 6000; the run is a minute)
    public const int PickupDelay = 10;         // ticks before a dropped item can be picked up, as in Minecraft
    public const float PickupRange = 1.5f;
    public const int StackSize = 64, InventorySlots = 36;
    public const int FlowDelay = 5;            // ticks before water spreads one block further
    public const float FloatSpeed = 0.1f;      // the upward speed water gives a humanoid; gravity takes 0.08 back
    public const float CurrentSpeed = 0.15f;   // the push of the water next to a spring, in blocks per tick
    public const int PositionBytes = 16;       // an entity update: the entity's id and its position
    public const int HealthBytes = 2;          // the health bar an update carries for a player or a zombie
    public const int MaxRequests = 256;        // chunks read or saved in one tick at most; the others wait a tick
    public const int MaxSaved = 512;           // entities a chunk takes to the region files at most, itself included

    // What a server for `players` holds at its peak, reserved before the first tick (Program.cs) so that no tick grows a
    // table, an index or a buffer pool: the peaks of this sample's runs with headroom. Loaded chunks grow with how far
    // the players spread, not with how many they are (a crowd shares its chunks); zombies with the nights.
    public static Capacity CapacityFor(int players) =>
        new(Players: players, Chunks: 192 + players / 3, Zombies: 32 + 3 * players, Items: 128 + players);

    // The chunks a view reaches in each direction.
    public static int ChunksIn(float range) => (int)MathF.Ceiling(range / Terrain.Size);

    public static bool IsNight(in Clock clock) => clock.Tick % clock.DayLength >= clock.DayLength / 2;

    // One chunk in four has a spring in its middle.
    public static bool HasSpring(ChunkPos chunk) => Hash(chunk.X, chunk.Z + 1000) % 4 == 0;

    // Players start on a grid 16 wide from the origin, three blocks apart, standing on the ground.
    public static Vector3 SpawnPoint(int client)
    {
        int x = client % 16 * 3, z = client / 16 * 3;
        return new Vector3(x + 0.5f, Terrain.SurfaceHeight(x, z) + 1, z + 0.5f);
    }

    // The seed of the world's EntityRandom: what the bots and the zombies draw is the same on every run.
    public const ulong Seed = 0x5EED;

    // A deterministic number from 0 to 255 for any pair of integers: the same terrain on every run.
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

// The peak a server is sized for: see Rules.CapacityFor. Only some chunks flow at once, half of them at most.
public readonly record struct Capacity(int Players, int Chunks, int Zombies, int Items)
{
    public int Flowing => Chunks / 2;
    public int Requests => Chunks / 2;
}
