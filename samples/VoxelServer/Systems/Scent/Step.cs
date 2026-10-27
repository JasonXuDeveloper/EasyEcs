using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using EasyEcs;

namespace VoxelServer;

// The scent spreads the way a pathfinder's distance field settles: a column's scent is the strongest of the columns a
// zombie can walk to from it (Footing.CanStep) less one step, or Rules.ScentReach where a player stands. Relaxing every
// column from its four neighbors again and again until nothing changes gives each column the strength of the nearest
// player along the ground, less the steps to it: around walls, out of pits and never through water.
//
// It runs as a shader over the chunks whose field may still change (Spreading), in passes like the water's, with two
// differences that come from what a field is for. The water is watched as it flows, one step per tick; a distance
// field is only read once settled, so each step does as much as it can:
// - A chunk settles its own field before it stops. Its texture is its own (Pad copied it with the edges of its
//   neighbors' around), so it sweeps it in place, north to south then back, until a sweep changes nothing. A sweep
//   carries the scent across the whole chunk north-south, row after row, and along each row east and west in four
//   doublings (a scan: 1, 2, 4 then 8 columns, each as far as the ground lets a zombie walk), so a field settles in a
//   sweep per turn of its paths. Only the chunk's ring, its neighbors' edges, stays as it was before the pass.
// - The chunks step in two halves of a checkerboard (Red, Black: ScentStepSystem<Red>, its settle, then <Black>), so
//   the black chunks step from the red ones' new edges: the scent crosses two chunks per update, not one.
//
// Where the players are, the seeds of the field, comes from the index of their Footprints by chunk: a few reads per
// chunk. The chunk's own columns start from nothing, not from its last field: the scent only rises as it settles, so a
// player who left leaves no scent behind in the chunk (from its last field, a stale peak fades a step or two per sweep,
// as a distance-vector route counts to infinity: measured, 7.5 sweeps a step against 2.7, with a long tail to the cap).
// A stale edge of a neighbor fades the same way between chunks, two steps per update. Once settled, each column also
// gets the side a zombie on it walks toward (Trail), so a zombie reads one byte, not five columns and their ground.
[EcsSystem, UpdateInGroup<Simulation>, UpdateInterval(Rules.ScentEvery)]
public ref struct ScentStepSystem<TColor> where TColor : struct, IComponent
{
    const int Size = Terrain.Size, Row = Size + 2;   // the texture: the chunk's 16 x 16 columns in a ring of its neighbors'

    // Each sweep settles the paths with one more turn; a field settles in 2 or 3 and confirms in one more. A bound, for
    // ground that winds more than a chunk can.
    const int MaxSweeps = 2 * Size;

    // The slots of a row's ways: north, south, then the runs of 1, 2, 4 and 8 columns west, and east.
    const int North = 0, South = 1, West = 2, East = 6, Ways = 10;

    // The color is a parameter, not [With<TColor>]: an attribute cannot name a type parameter (C# CS8968), and a tag
    // parameter filters the same. By day no zombie hunts (Hunt): the step changes nothing and the chunks sleep.
    [ForEach(Parallel = true, ChunkRows = 64), With<Spreading>]
    [SkipLocalsInit]
    static void Step(in TColor color, in Hunt hunt, in Ground ground, Tile<Scent, Ground> scent, Tile<Footing, Ground> footing,
        Lookup<Footprint> footprints, Mut<DynamicBuffer<NextScent>> next, Mut<DynamicBuffer<Trail>> trail, ref ScentStep step)
    {
        if (!hunt.On)
        {
            step = default;
            return;
        }
        var last = scent.Pad();
        var floor = footing.Pad(0, 0, Footing.Unloaded, mask: false);

        // The field to sweep: the neighbors' edges around (the corners are never read), the chunk's own columns from
        // nothing. Two loads of 16 columns cover a row of 18, the ring included; the middle 16 are then cleared.
        Span<byte> field = stackalloc byte[Row * Row];
        ref byte at = ref MemoryMarshal.GetReference(field);
        last.At(new TileRow<Ground>(0, 0)).Load128<byte>(-1, 0, -1).StoreUnsafe(ref at);
        last.At(new TileRow<Ground>(0, 0)).Load128<byte>(1, 0, -1).StoreUnsafe(ref at, 2);
        last.At(new TileRow<Ground>(0, Size - 1)).Load128<byte>(-1, 0, 1).StoreUnsafe(ref at, (Size + 1) * Row);
        last.At(new TileRow<Ground>(0, Size - 1)).Load128<byte>(1, 0, 1).StoreUnsafe(ref at, (Size + 1) * Row + 2);

        // Where each column may step: north, south, and along the row the runs a scan crosses in 1, 2, 4 and 8 columns,
        // west and east. The ground does not change while the field settles: the rule runs once per step, 16 columns at
        // a time.
        Span<Vector128<byte>> ways = stackalloc Vector128<byte>[Ways * Size];
        for (int z = 0; z < Size; z++)
        {
            var cells = last.At(new TileRow<Ground>(0, z));
            cells.Load128<byte>(-1).StoreUnsafe(ref at, (nuint)((z + 1) * Row));
            cells.Load128<byte>(1).StoreUnsafe(ref at, (nuint)((z + 1) * Row + 2));
            Vector128<byte>.Zero.StoreUnsafe(ref at, (nuint)((z + 1) * Row + 1));   // the chunk's own: from nothing
            var feet = floor.At(new TileRow<Ground>(0, z));
            var here = feet.Load128<byte>();
            var w = ways.Slice(z * Ways, Ways);
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
        }

        // The seeds: Rules.ScentReach under each player of the chunk.
        Span<byte> seeds = stackalloc byte[Size * Size];
        seeds.Clear();
        foreach (var player in footprints.Find(ground.Pos)) seeds[footprints.Read(player.Id).Column] = Rules.ScentReach;

        // The sweeps, in place: a row reads the row before it as this sweep left it (Gauss-Seidel), the ring as it was.
        for (int sweep = 0; sweep < MaxSweeps; sweep++)
        {
            bool changed = false;
            for (int k = 0; k < Size; k++)
            {
                int z = (sweep & 1) == 0 ? k : Size - 1 - k;
                nuint i = (nuint)((z + 1) * Row + 1);
                var w = ways.Slice(z * Ways, Ways);
                var here = Vector128.LoadUnsafe(ref at, i);
                var p = Vector128.Max(
                    Vector128.Max(Fade(Vector128.LoadUnsafe(ref at, i - 1), 1) & w[West], Fade(Vector128.LoadUnsafe(ref at, i + 1), 1) & w[East]),
                    Vector128.Max(Fade(Vector128.LoadUnsafe(ref at, i - Row), 1) & w[North], Fade(Vector128.LoadUnsafe(ref at, i + Row), 1) & w[South]));
                p = Vector128.Max(p, Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(seeds), (nuint)(z * Size)));
                // Along the row, east to west, then west to east: each doubling takes the scent of the column 1, 2, 4
                // or 8 away, faded by as many steps, where the run between is walkable.
                p = Vector128.Max(p, Fade(FromEast(p, 1), 1) & w[East]);
                p = Vector128.Max(p, Fade(FromEast(p, 2), 2) & w[East + 1]);
                p = Vector128.Max(p, Fade(FromEast(p, 4), 4) & w[East + 2]);
                p = Vector128.Max(p, Fade(FromEast(p, 8), 8) & w[East + 3]);
                p = Vector128.Max(p, Fade(FromWest(p, 1), 1) & w[West]);
                p = Vector128.Max(p, Fade(FromWest(p, 2), 2) & w[West + 1]);
                p = Vector128.Max(p, Fade(FromWest(p, 4), 4) & w[West + 2]);
                p = Vector128.Max(p, Fade(FromWest(p, 8), 8) & w[West + 3]);
                if (p == here) continue;
                p.StoreUnsafe(ref at, i);
                changed = true;
            }
            if (!changed) break;
        }

        // The settled field into the step, and the side each column's zombie walks toward: the neighbor of the
        // strongest scent it can step to, if stronger than its own; on a player's own column, Trail.Player.
        var into = MemoryMarshal.Cast<NextScent, byte>(next.Write().AsSpan());
        var toward = MemoryMarshal.Cast<Trail, byte>(trail.Write().AsSpan());
        for (int z = 0; z < Size; z++)
        {
            nuint i = (nuint)((z + 1) * Row + 1);
            var w = ways.Slice(z * Ways, Ways);
            var here = Vector128.LoadUnsafe(ref at, i);
            var west = Vector128.LoadUnsafe(ref at, i - 1) & w[West];
            var east = Vector128.LoadUnsafe(ref at, i + 1) & w[East];
            var north = Vector128.LoadUnsafe(ref at, i - Row) & w[North];
            var south = Vector128.LoadUnsafe(ref at, i + Row) & w[South];
            var best = Vector128.Max(Vector128.Max(west, east), Vector128.Max(north, south));
            var side = Vector128.ConditionalSelect(Vector128.Equals(best, west), Vector128.Create(Trail.West),
                Vector128.ConditionalSelect(Vector128.Equals(best, east), Vector128.Create(Trail.East),
                    Vector128.ConditionalSelect(Vector128.Equals(best, north), Vector128.Create(Trail.North), Vector128.Create(Trail.South))));
            side &= Vector128.GreaterThan(best, here);
            side = Vector128.ConditionalSelect(Vector128.Equals(here, Vector128.Create((byte)Rules.ScentReach)),
                Vector128.Create(Trail.Player), side);
            here.CopyTo(into.Slice(z * Size, Size));
            side.CopyTo(toward.Slice(z * Size, Size));
        }
        var edges = scent.ChangedEdges(MemoryMarshal.Cast<byte, Scent>(into), out bool moved);
        step = new ScentStep(edges, moved);
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
