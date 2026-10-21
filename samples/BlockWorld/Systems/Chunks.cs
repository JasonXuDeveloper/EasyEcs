using System.Runtime.CompilerServices;
using EasyEcs;

namespace BlockWorld;

// Loads the chunks around every player and marks them as seen. A missing chunk is generated on the spot: an entity
// with its Chunk key, its LastSeen tick and a buffer of 8,192 blocks. Twice a second is enough: a player walks four
// blocks a second and the view reaches two chunks ahead.
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
                Create(World, pos, clock.Tick);
                stats.ChunksLoaded++;
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Create(World world, ChunkPos pos, long tick)
    {
        Span<Block> blocks = stackalloc Block[Terrain.BlocksPerChunk];
        Terrain.Generate(pos, blocks);
        var chunk = world.CreateEntity(new Chunk(pos), new LastSeen(tick), new DynamicBuffer<Block>());
        chunk.Get<DynamicBuffer<Block>>().AddRange(blocks);
    }
}

// Frees the chunks no player has seen for a while. Destroying an entity in the middle of the loop is fine: structural
// changes apply at once. (A real server would save a dug chunk first; see the SaveLoad sample.)
public class ChunkUnloadSystem : EcsSystem
{
    public override int UpdateInterval => Rules.TicksPerSecond;

    [ForEach, With<Chunk>]
    static void Unload(Entity chunk, in LastSeen seen, in Clock clock, ref Stats stats)
    {
        if (clock.Tick - seen.Tick <= Rules.UnloadAfter) return;
        chunk.Destroy();
        stats.ChunksUnloaded++;
    }
}
