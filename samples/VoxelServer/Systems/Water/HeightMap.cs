using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using EasyEcs;

namespace VoxelServer;

// A block update: a chunk whose blocks changed (dug, or just created or loaded) gets its height map computed again
// (here) and its water, and its neighbours', moving again (WaterWakeSystem): two systems, one job each, both
// [Changed<DynamicBuffer<Block>>], so they visit only those chunks and the dig itself knows nothing of water or of the sky.
//
// The height map is 16 vectors of 16 columns: each layer from the bottom up overwrites the lanes whose block is solid
// with its height, so after the 32 layers each lane holds the top of its column, with no branch. The 256 bytes are read
// and written once per layer, on the stack (measured: an order of magnitude faster than in the buffer's array), and
// copied into the buffer once, through the span SetCount returns.
[UpdateInGroup<Simulation>]
public class HeightMapSystem : EcsSystem
{
    const int Columns = Terrain.Size * Terrain.Size;

    [ForEach, Changed<DynamicBuffer<Block>>]
    static void Update(in DynamicBuffer<Block> blocks, Mut<DynamicBuffer<Surface>> surface)
    {
        var solid = MemoryMarshal.Cast<Block, byte>(blocks.AsReadOnlySpan());
        Span<byte> top = stackalloc byte[Columns];                     // zeroed: bedrock, at 0, is never dug
        for (int y = 1; y < Terrain.Height; y++)
        {
            var layer = solid.Slice(y * Columns, Columns);
            var height = Vector128.Create((byte)y);
            for (int i = 0; i < Columns; i += Vector128<byte>.Count)
            {
                var air = Vector128.Equals(Vector128.Create(layer.Slice(i, Vector128<byte>.Count)), Vector128<byte>.Zero);
                var below = Vector128.Create((ReadOnlySpan<byte>)top.Slice(i, Vector128<byte>.Count));
                Vector128.ConditionalSelect(air, below, height).CopyTo(top.Slice(i, Vector128<byte>.Count));
            }
        }
        top.CopyTo(MemoryMarshal.Cast<Surface, byte>(surface.Write().SetCount(Columns)));
    }
}
