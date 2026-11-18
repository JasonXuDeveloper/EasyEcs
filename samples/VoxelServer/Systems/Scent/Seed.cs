using System.Runtime.InteropServices;
using EasyEcs;

namespace VoxelServer;

// The seeds of the field: Rules.ScentReach under each player of the chunk while zombies hunt, nothing by day. Each chunk
// finds its players in the index of footprints by chunk (a few reads) and writes its seeds only where they changed, so
// the field wakes where a player stepped onto another column, and everywhere at once when the hunt starts or ends (the
// step reads the seeds: a write of them wakes the chunk). Each chunk writes its own row: split across threads.
[EcsSystem, UpdateInterval(Rules.ScentEvery)]
public ref struct SeedSystem
{
    const int Columns = Terrain.Size * Terrain.Size;

    [ForEach(Parallel = true, ChunkSize = 64)]
    static void Seed(in Ground ground, in Hunt hunt, Lookup<Footprint> footprints, Mut<DynamicBuffer<Seed>> seeds)
    {
        Span<byte> now = stackalloc byte[Columns];
        now.Clear();
        if (hunt.On)
            foreach (var player in footprints.Find(ground.Pos)) now[footprints.Read(player.Id).Column] = Rules.ScentReach;
        if (MemoryMarshal.Cast<Seed, byte>(seeds.Value.AsReadOnlySpan()).SequenceEqual(now)) return;   // the field sleeps on
        now.CopyTo(MemoryMarshal.Cast<Seed, byte>(seeds.Write().SetCount(Columns)));
    }
}
