using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using EasyEcs;

namespace VoxelServer;

// The scent spreads the way a pathfinder's distance field settles: a column's scent is the strongest of the columns a
// zombie can walk to from it (Footing.CanStep) less one step, or Rules.ScentReach where a player stands (Seed). Relaxing
// every column from its four neighbors again and again until nothing changes gives each column the strength of the
// nearest player along the ground, less the steps to it: around walls, out of pits and never through water.
//
// It runs as a shader over the chunks, like the water, with the differences that come from what a field is for. The
// water is watched as it flows, one step per tick; a distance field is only read once settled, so each step does as
// much as it can. [GaussSeidel] makes it so: the chunk's field (ref Tile<Scent, Ground>) is swept in place, each sweep
// reading the rows this sweep already wrote, until a sweep changes nothing; the sweeps turn direction (north to south,
// then back: tile.Sweep), and along each row the scent crosses the chunk east and west in four doublings (a scan: 1, 2,
// 4 then 8 columns, each as far as the ground lets a zombie walk), so a field settles in a sweep per turn of its paths.
// The chunks step in the two colors of a checkerboard by their position, so the second half steps from the first one's
// new edges: the scent crosses two chunks per update, not one. A chunk whose field came out the same sleeps, and the
// pass wakes it when its footing (or its neighbors') or its seeds change, or a neighbor's edge it reads.
//
// The chunk's own columns start from nothing (ColdStart), not from its last field: the scent only rises as it settles,
// so a player who left leaves no scent behind in the chunk (from its last field, a stale peak fades a step or two per
// sweep, as a distance-vector route counts to infinity: measured, 7.5 sweeps a step against 2.7, with a long tail to the
// cap). A stale edge of a neighbor fades the same way between chunks, two steps per update. Once settled, each column
// also gets the side a zombie on it walks toward (Trail), so a zombie reads one byte, not five columns and their ground.
[EcsSystem, UpdateInterval(Rules.ScentEvery)]
public ref struct ScentStepSystem
{
    const int Size = Terrain.Size;

    // Each sweep settles the paths with one more turn; a field settles in 2 or 3 and confirms in one more. A bound, for
    // ground that winds more than a chunk can.
    const int MaxSweeps = 2 * Size;

    // The slots of a row's ways: north, south, then the runs of 1, 2, 4 and 8 columns west, and east.
    const int North = 0, South = 1, West = 2, East = 6;

    [ForEach(Parallel = true, ChunkSize = 64), GaussSeidel(MaxSweeps = MaxSweeps)]
    static void Step(ref Tile<Scent, Ground> scent, Tile<Footing, Ground> footing, in DynamicBuffer<Seed> seeds,
        Scratch<ScentWays> scratch, Mut<DynamicBuffer<Trail>> trail)
    {
        scent.ExchangeHalo();
        scent.ColdStart();
        ref var ways = ref scratch.Value;
        int sweep = scent.Sweep;
        // Where each column may step and the seeds, once per step: the ground does not change while the field settles,
        // and a chunk's sweeps run one after another on one thread, whose Scratch keeps them. 16 columns at a time.
        if (sweep == 0)
        {
            footing.ExchangeHalo(0, 0, Footing.Unloaded, mask: false);
            var seeded = MemoryMarshal.Cast<Seed, byte>(seeds.AsReadOnlySpan());
            for (int z = 0; z < Size; z++)
            {
                var feet = footing.At(new TileRow<Ground>(0, z));
                var here = feet.Load128<byte>();
                var w = ways.Row(z);
                w[North] = Footing.CanStep(here, feet.Load128<byte>(0, 0, -1));
                w[South] = Footing.CanStep(here, feet.Load128<byte>(0, 0, 1));
                // A run of 2k columns is walkable when its two runs of k are.
                w[West] = Footing.CanStep(here, feet.Load128<byte>(-1));
                w[West + 1] = w[West] & FromWest(w[West], 1);
                w[West + 2] = w[West + 1] & FromWest(w[West + 1], 2);
                w[West + 3] = w[West + 2] & FromWest(w[West + 2], 4);
                w[East] = Footing.CanStep(here, feet.Load128<byte>(1));
                w[East + 1] = w[East] & FromEast(w[East], 1);
                w[East + 2] = w[East + 1] & FromEast(w[East + 1], 2);
                w[East + 3] = w[East + 2] & FromEast(w[East + 2], 4);
                ways.Seeds[z] = Vector128.Create(seeded.Slice(z * Size, Size));
            }
        }

        // A sweep: a row reads the row before it as this sweep left it (Gauss-Seidel), the ring as it was.
        bool changed = false;
        for (int k = 0; k < Size; k++)
        {
            int z = (sweep & 1) == 0 ? k : Size - 1 - k;
            var row = new TileRow<Ground>(0, z);
            var c = scent.At(row);
            var w = ways.Row(z);
            var here = c.Load128<byte>();
            var p = Vector128.Max(
                Vector128.Max(Fade(c.Load128<byte>(-1), 1) & w[West], Fade(c.Load128<byte>(1), 1) & w[East]),
                Vector128.Max(Fade(c.Load128<byte>(0, 0, -1), 1) & w[North], Fade(c.Load128<byte>(0, 0, 1), 1) & w[South]));
            p = Vector128.Max(p, ways.Seeds[z]);
            // Along the row, east to west, then west to east: each doubling takes the scent of the column 1, 2, 4 or 8
            // away, faded by as many steps, where the run between is walkable.
            p = Vector128.Max(p, Fade(FromEast(p, 1), 1) & w[East]);
            p = Vector128.Max(p, Fade(FromEast(p, 2), 2) & w[East + 1]);
            p = Vector128.Max(p, Fade(FromEast(p, 4), 4) & w[East + 2]);
            p = Vector128.Max(p, Fade(FromEast(p, 8), 8) & w[East + 3]);
            p = Vector128.Max(p, Fade(FromWest(p, 1), 1) & w[West]);
            p = Vector128.Max(p, Fade(FromWest(p, 2), 2) & w[West + 1]);
            p = Vector128.Max(p, Fade(FromWest(p, 4), 4) & w[West + 2]);
            p = Vector128.Max(p, Fade(FromWest(p, 8), 8) & w[West + 3]);
            if (p == here) continue;
            p.CopyTo(MemoryMarshal.Cast<Scent, byte>(scent.Write(row)));
            changed = true;
        }
        if (changed && sweep < MaxSweeps - 1) return;   // it sweeps again

        // Settled (or out of sweeps): the side each column's zombie walks toward, the neighbor of the strongest scent it
        // can step to, if stronger than its own; on a player's own column, Trail.Player.
        var toward = MemoryMarshal.Cast<Trail, byte>(trail.Write().AsSpan());
        for (int z = 0; z < Size; z++)
        {
            var c = scent.At(new TileRow<Ground>(0, z));
            var w = ways.Row(z);
            var here = c.Load128<byte>();
            var west = c.Load128<byte>(-1) & w[West];
            var east = c.Load128<byte>(1) & w[East];
            var north = c.Load128<byte>(0, 0, -1) & w[North];
            var south = c.Load128<byte>(0, 0, 1) & w[South];
            var best = Vector128.Max(Vector128.Max(west, east), Vector128.Max(north, south));
            var side = Vector128.ConditionalSelect(Vector128.Equals(best, west), Vector128.Create(Trail.West),
                Vector128.ConditionalSelect(Vector128.Equals(best, east), Vector128.Create(Trail.East),
                    Vector128.ConditionalSelect(Vector128.Equals(best, north), Vector128.Create(Trail.North), Vector128.Create(Trail.South))));
            side &= Vector128.GreaterThan(best, here);
            side = Vector128.ConditionalSelect(Vector128.Equals(here, Vector128.Create((byte)Rules.ScentReach)),
                Vector128.Create(Trail.Player), side);
            side.CopyTo(toward.Slice(z * Size, Size));
        }
    }

    // The scent `steps` further: that much less, 0 staying 0.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector128<byte> Fade(Vector128<byte> scent, int steps) => Vector128.SubtractSaturate(scent, Vector128.Create((byte)steps));

    // Each column takes the lane `n` columns east of it, the last n nothing (a shuffle: indices past 15 give 0). The
    // shifts are constants where called, so each is one shuffle of a constant (PSHUFB, NEON TBL).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector128<byte> FromEast(Vector128<byte> lanes, int n) => Vector128.Shuffle(lanes, Vector128<byte>.Indices + Vector128.Create((byte)n));

    // Each column takes the lane `n` columns west of it, the first n nothing (the index wraps past 255: 0).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector128<byte> FromWest(Vector128<byte> lanes, int n) => Vector128.Shuffle(lanes, Vector128<byte>.Indices - Vector128.Create((byte)n));
}

// What the sweeps of a chunk's step share, kept on the thread stepping it (Scratch): where each column may step (ten
// masks per row: north, south, the runs of 1 to 8 columns west and east) and the seeds, read in the first sweep.
public struct ScentWays
{
    public WayRows Ways;
    public SeedRows Seeds;

    // The ten masks of row z.
    [UnscopedRef]
    public Span<Vector128<byte>> Row(int z) => ((Span<Vector128<byte>>)Ways).Slice(z * 10, 10);
}

[InlineArray(10 * Terrain.Size)]
public struct WayRows
{
    Vector128<byte> _row;
}

[InlineArray(Terrain.Size)]
public struct SeedRows
{
    Vector128<byte> _row;
}
