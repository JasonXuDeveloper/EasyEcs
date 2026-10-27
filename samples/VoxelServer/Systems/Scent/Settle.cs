using System.Runtime.InteropServices;
using EasyEcs;

namespace VoxelServer;

// Ends a half of the checkerboard: the chunks of TColor that stepped. A step that changed nothing lets the chunk sleep
// until something wakes it; one that changed makes the step the field (the ping-pong of the water's commit) and wakes
// the chunks across the edges that changed. Those are of the other color, never this one (no chunk touches its own
// color across a face), so the black half wakes and steps in the same update as the red one that woke it. On one thread:
// waking another chunk is a presence flip of a row this pass does not own; a copy of 256 bytes per chunk is nothing to
// split.
[EcsSystem, UpdateInGroup<Simulation>, UpdateInterval(Rules.ScentEvery)]
public ref struct ScentSettleSystem<TColor> where TColor : struct, IComponent
{
    // Facing reads the neighbors of the ground tile: through the footing, since the scent is the buffer written here.
    [ForEach, With<Spreading>]
    static void Settle(Entity chunk, in TColor color, in ScentStep step, in DynamicBuffer<NextScent> next,
        Mut<DynamicBuffer<Scent>> scent, Tile<Footing, Ground> ground, World world)
    {
        if (!step.Changed)
        {
            chunk.RemoveSpreading();
            return;
        }
        next.AsReadOnlySpan().CopyTo(MemoryMarshal.Cast<Scent, NextScent>(scent.Write().AsSpan()));
        foreach (var id in ground.Facing(step.Edges)) world.GetEntity(id).SetSpreading();
    }
}
