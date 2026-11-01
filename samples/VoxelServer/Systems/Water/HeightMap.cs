using System.Runtime.InteropServices;
using EasyEcs;

namespace VoxelServer;

// A block update: a chunk whose blocks changed (dug, or just created or loaded) gets its height map computed again
// (here) and its water, and its neighbors', moving again (WaterWakeSystem): two systems, one job each, both
// [Changed<DynamicBuffer<Block>>], so they visit only those chunks and the dig itself knows nothing of water or of the sky.
//
// The height map is 256 columns: each layer from the bottom up writes its height into the columns whose block is
// solid, so after the 32 layers each column holds its top. The loop over a layer's columns is plain C#: the generator
// gives it a vector loop (16 to 64 columns per step, the write of each column under its block's test, no branch). The
// 256 bytes are on the stack (measured: an order of magnitude faster than in the buffer's array), and copied into the
// buffer once, through the span SetCount returns.
[EcsSystem, UpdateInGroup<Simulation>]
public ref struct HeightMapSystem
{
    const int Columns = Terrain.Size * Terrain.Size;

    [ForEach, Changed<DynamicBuffer<Block>>]
    static void Update(in DynamicBuffer<Block> blocks, Mut<DynamicBuffer<Surface>> surface)
    {
        var solid = blocks.AsReadOnlySpan();
        Span<byte> top = stackalloc byte[Columns];                     // zeroed: bedrock, at 0, is never dug
        for (int y = 1; y < Terrain.Height; y++)
        {
            var layer = solid.Slice(y * Columns, Columns);
            for (int i = 0; i < Columns; i++)
                if (layer[i] != Block.Air) top[i] = (byte)y;
        }
        top.CopyTo(MemoryMarshal.Cast<Surface, byte>(surface.Write().SetCount(Columns)));
    }
}
