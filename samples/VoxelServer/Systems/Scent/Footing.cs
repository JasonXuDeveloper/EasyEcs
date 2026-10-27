using System.Runtime.InteropServices;
using EasyEcs;

namespace VoxelServer;

// The ground of each column as a path sees it, from the height map and the water: where a body stands (the top + 1)
// and whether water stands there. [ChangedAny]: only the chunks whose blocks or water changed, a dig, a load or a step
// of the water; and the footing is written only when a column changed, so a water step that only deepened a lake wakes
// no field. Each chunk writes its own footing and reads its own buffers: the chunks are split across threads.
[EcsSystem, UpdateInGroup<Simulation>]
public ref struct FootingSystem
{
    const int Columns = Terrain.Size * Terrain.Size;

    [ForEach(Parallel = true, ChunkSize = 64), ChangedAny<DynamicBuffer<Surface>, DynamicBuffer<Fluid>>]
    static void Update(in DynamicBuffer<Surface> surface, in DynamicBuffer<Fluid> water, in FluidCount count,
        Mut<DynamicBuffer<Footing>> footing)
    {
        var tops = surface.AsReadOnlySpan();
        if (tops.Length < Columns) return;                           // a chunk generated this tick: its height map is next
        Span<Footing> ground = stackalloc Footing[Columns];
        var levels = water.AsReadOnlySpan();
        for (int i = 0; i < Columns; i++)
        {
            int stand = tops[i].Top + 1;
            // Water only where the chunk has some, between its lowest and highest layer (FluidCount): a dry chunk reads none.
            bool wet = stand >= count.Low && stand <= count.High && levels[stand * Columns + i].Level > 0;
            ground[i] = new Footing((byte)(stand | (wet ? Footing.Wet : 0)));
        }
        var now = MemoryMarshal.AsBytes(footing.Value.AsReadOnlySpan());
        if (now.SequenceEqual(MemoryMarshal.AsBytes((ReadOnlySpan<Footing>)ground))) return;   // unchanged: the field sleeps on
        ground.CopyTo(footing.Write().SetCount(Columns));
    }
}
