using System.Numerics;

namespace VoxelServer;

// The numbers of the game, in ticks and blocks. A vanilla server runs 20 ticks a second (50 ms each).
public static class Rules
{
    public const int TicksPerSecond = 20;
    public const int DayLength = 600;          // 30 seconds a day, so a short run sees a night
    public const float PlayerVision = 32;      // blocks a player sees: two chunks each way, 5 x 5 chunks loaded
    public const float ZombieVision = 35;      // blocks a zombie sees a player from: its follow range in Minecraft
    public const int LoadEvery = TicksPerSecond / 2;   // ticks between two passes of ChunkLoadSystem
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
    public const float Current = 0.15f;        // the push of the water next to a spring, in blocks per tick
    public const int PositionBytes = 16;       // an entity update: the entity's id and its position
    public const int HealthBytes = 2;          // the health bar an update carries for a player or a zombie
    public const int MaxRequests = 256;        // chunks read or saved in one tick at most; the others wait a tick
    public const int MaxSeen = 256;            // entities a client shows at most; the others wait for room
    public const int MaxSaved = 512;           // entities a chunk takes to the region files at most, itself included
    public const int ViewChunks = 81;          // chunks a view holds at most: 9 x 9, a vision of up to 64 blocks

    // The chunks a view reaches in each direction.
    public static int ChunksIn(float range) => (int)MathF.Ceiling(range / Terrain.Size);

    // The players a chunk's changes go to: its viewers at the last load pass, none if nobody had it in view then.
    public static int Watching(in Viewers viewers, in Clock clock) =>
        clock.Tick - viewers.Tick < LoadEvery ? viewers.Count : 0;

    public static bool IsNight(in Clock clock) => clock.Tick % clock.DayLength >= clock.DayLength / 2;

    // One chunk in four has a spring in its middle.
    public static bool HasSpring(ChunkPos chunk) => Hash(chunk.X, chunk.Z + 1000) % 4 == 0;

    // Players start on a grid 16 wide from the origin, three blocks apart, standing on the ground.
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
