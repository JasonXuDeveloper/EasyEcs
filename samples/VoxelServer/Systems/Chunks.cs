using System.Runtime.CompilerServices;
using EasyEcs;

namespace VoxelServer;

// Loads the chunks around every player and marks them as seen. A missing chunk is generated on the spot: an entity
// with its Chunk key, its LastSeen tick and a buffer of 8,192 blocks, and, in one chunk in four, a spring of water
// that starts to flow. Twice a second is enough: a player walks four blocks a second and the view reaches two chunks
// ahead.
public class ChunkLoadSystem : EcsSystem
{
    public override int UpdateInterval => Rules.TicksPerSecond / 2;

    [ForEach, With<Player>]
    void Load(in Position position, in Clock clock, ref Stats stats)
    {
        var center = Terrain.ChunkOf((int)MathF.Floor(position.Value.X), (int)MathF.Floor(position.Value.Z));
        for (int dz = -Rules.ViewDistance; dz <= Rules.ViewDistance; dz++)
        for (int dx = -Rules.ViewDistance; dx <= Rules.ViewDistance; dx++)
        {
            var pos = new ChunkPos(center.X + dx, center.Z + dz);
            if (World.ChunkIndex.TryGet(pos, out var chunk)) chunk.LastSeen = new LastSeen(clock.Tick);
            else
            {
                Create(World, pos, clock.Tick, ref stats);
                stats.ChunksLoaded++;
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Create(World world, ChunkPos pos, long tick, ref Stats stats)
    {
        Span<Block> blocks = stackalloc Block[Terrain.BlocksPerChunk];
        Terrain.Generate(pos, blocks);
        var chunk = world.CreateEntity(new Chunk(pos), new LastSeen(tick), new DynamicBuffer<Block>());
        chunk.WriteBlock().AddRange(blocks);
        if (!Rules.HasSpring(pos)) return;

        int x = pos.X * Terrain.Size + Terrain.Size / 2, z = pos.Z * Terrain.Size + Terrain.Size / 2;
        var spring = new BlockPos(x, Terrain.SurfaceHeight(x, z) + 1, z);
        Terrain.PourWater(world, chunk, spring, spring, level: 0, tick, ref stats);
    }
}

// Frees the chunks no player has seen for a while; their water goes with them (each cell is a child of its chunk).
// Slices = 20 spreads the check over a second: each tick visits a twentieth of the chunks, so the server never pays
// for all of them in one tick. Destroying an entity in the middle of the loop is fine: structural changes apply at
// once.
public class ChunkUnloadSystem : EcsSystem
{
    [ForEach(Slices = Rules.TicksPerSecond), With<Chunk>]
    static void Unload(Entity chunk, in LastSeen seen, in Clock clock, ref Stats stats)
    {
        if (clock.Tick - seen.Tick <= Rules.UnloadAfter) return;
        chunk.Destroy();
        stats.ChunksUnloaded++;
    }
}
