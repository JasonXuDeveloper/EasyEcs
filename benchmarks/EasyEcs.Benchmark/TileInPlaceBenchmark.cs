using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// Whether a stencil should read its neighbors' edges in place instead of exchanging the halo into a padded copy: the
/// hand-written fourth-order Laplacian (TileStencil's Hand: four face lookups, a 20 x 20 texture on the stack) against
/// the same step reading the rows north and south of each row straight from the neighbors' buffers, only the two cells
/// west and east of a row put beside it in a window of 20 (InPlace) or shifted in registers (InPlaceShift), and the four lookups alone (Lookups: what reading
/// in place still pays per plate). Hand and InPlace commit the same way, so their difference is the copies.
/// </summary>
[MemoryDiagnoser]
public class TileInPlaceBenchmark
{
    [Params(1, 4)] public int Threads;

    [Params(Plot.Default, 512)] public int Side;

    private World _hand, _inPlace, _shift, _lookups;

    [GlobalSetup]
    public void Setup()
    {
        Plot.Side = Side;
        _hand = TileWorlds.Warm<HandPlot>(Threads, p => new HandPlot(p));
        _hand.AddSystem<TileSystems.HandWaveSystem>();
        _hand.AddSystem<TileSystems.CommitWarmthSystem>();
        _inPlace = TileWorlds.Warm<HandPlot>(Threads, p => new HandPlot(p));
        _inPlace.AddSystem<InPlaceSystems.InPlaceWaveSystem>();
        _inPlace.AddSystem<TileSystems.CommitWarmthSystem>();
        _shift = TileWorlds.Warm<HandPlot>(Threads, p => new HandPlot(p));
        _shift.AddSystem<InPlaceSystems.InPlaceShiftSystem>();
        _shift.AddSystem<TileSystems.CommitWarmthSystem>();
        _lookups = TileWorlds.Warm<HandPlot>(Threads, p => new HandPlot(p));
        _lookups.AddSystem<InPlaceSystems.LookupsSystem>();
        for (int i = 0; i < 3; i++)
        {
            Hand();
            InPlace();
            InPlaceShift();
            Lookups();
        }
        TileWorlds.Same(_inPlace, _hand);
        TileWorlds.Same(_shift, _hand);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _hand.Dispose();
        _inPlace.Dispose();
        _shift.Dispose();
        _lookups.Dispose();
    }

    [Benchmark(Baseline = true)] public void Hand() => _hand.Update();
    [Benchmark] public void InPlace() => _inPlace.Update();
    [Benchmark] public void InPlaceShift() => _shift.Update();
    [Benchmark] public void Lookups() => _lookups.Update();
}

internal static class InPlaceSystems
{
    private static readonly float[] Zeros = new float[16];

    /// <summary>Row <paramref name="z"/> (−2 to 17) of the plate, or of the plate north or south of it.</summary>
    private static ReadOnlySpan<float> RowAt(int z, ReadOnlySpan<float> self, ReadOnlySpan<float> north, ReadOnlySpan<float> south) =>
        z < 0 ? (north.IsEmpty ? Zeros : north.Slice((16 + z) * 16, 16))
        : z > 15 ? (south.IsEmpty ? Zeros : south.Slice((z - 16) * 16, 16))
        : self.Slice(z * 16, 16);

    /// <summary>The Laplacian of HandWaveSystem, its rows along z read in place, its row along x in a window of 20.</summary>
    [EcsSystem, SkipLocalsInit]
    public ref struct InPlaceWaveSystem
    {
        [ForEach(Parallel = true, ChunkSize = 16)]
        static void Step(in HandPlot plot, in DynamicBuffer<Warmth> warmth, Lookup<HandPlot> plots,
            Lookup<DynamicBuffer<Warmth>> warmths, Mut<DynamicBuffer<NextWarmth>> next)
        {
            var self = MemoryMarshal.Cast<Warmth, float>(warmth.AsReadOnlySpan());
            var north = TileSystems.Cells(plots.TryFind(plot.Pos.Offset(0, -1), out Entity northId), northId, warmths);
            var south = TileSystems.Cells(plots.TryFind(plot.Pos.Offset(0, 1), out Entity southId), southId, warmths);
            var west = TileSystems.Cells(plots.TryFind(plot.Pos.Offset(-1, 0), out Entity westId), westId, warmths);
            var east = TileSystems.Cells(plots.TryFind(plot.Pos.Offset(1, 0), out Entity eastId), eastId, warmths);
            Span<float> window = stackalloc float[20];
            var into = MemoryMarshal.Cast<NextWarmth, float>(next.Write().AsSpan());
            for (int z = 0; z < 16; z++)
            {
                int at = z * 16;
                window[0] = west.IsEmpty ? 0 : west[at + 14];
                window[1] = west.IsEmpty ? 0 : west[at + 15];
                self.Slice(at, 16).CopyTo(window.Slice(2, 16));
                window[18] = east.IsEmpty ? 0 : east[at];
                window[19] = east.IsEmpty ? 0 : east[at + 1];
                ref float w = ref MemoryMarshal.GetReference(window);
                ref float m2 = ref MemoryMarshal.GetReference(RowAt(z - 2, self, north, south));
                ref float m1 = ref MemoryMarshal.GetReference(RowAt(z - 1, self, north, south));
                ref float p1 = ref MemoryMarshal.GetReference(RowAt(z + 1, self, north, south));
                ref float p2 = ref MemoryMarshal.GetReference(RowAt(z + 2, self, north, south));
                for (int x = 0; x < 16; x += 4)
                {
                    // The invariant: x + 4 + 3 < 20 in the window, x + 3 < 16 in each row of 16.
                    nuint p = (nuint)x;
                    var here = Vector128.LoadUnsafe(ref w, p + 2);
                    var alongX = -Vector128.LoadUnsafe(ref w, p) + 16f * Vector128.LoadUnsafe(ref w, p + 1) - 30f * here
                        + 16f * Vector128.LoadUnsafe(ref w, p + 3) - Vector128.LoadUnsafe(ref w, p + 4);
                    var alongZ = -Vector128.LoadUnsafe(ref m2, p) + 16f * Vector128.LoadUnsafe(ref m1, p) - 30f * here
                        + 16f * Vector128.LoadUnsafe(ref p1, p) - Vector128.LoadUnsafe(ref p2, p);
                    (here + (alongX + alongZ) * (1f / 12) * TileSystems.Rate).CopyTo(into.Slice(at + x));
                }
            }
        }
    }

    /// <summary>
    /// InPlace with the row along x shifted in registers (each vector of the row against the one before and after it, the
    /// neighbors' edge vectors at the ends) instead of written to a window on the stack and read back unaligned: a load
    /// that spans two earlier stores cannot be forwarded from them and waits for them to reach the cache.
    /// </summary>
    [EcsSystem]
    public ref struct InPlaceShiftSystem
    {
        [ForEach(Parallel = true, ChunkSize = 16)]
        static void Step(in HandPlot plot, in DynamicBuffer<Warmth> warmth, Lookup<HandPlot> plots,
            Lookup<DynamicBuffer<Warmth>> warmths, Mut<DynamicBuffer<NextWarmth>> next)
        {
            var self = MemoryMarshal.Cast<Warmth, float>(warmth.AsReadOnlySpan());
            var north = TileSystems.Cells(plots.TryFind(plot.Pos.Offset(0, -1), out Entity northId), northId, warmths);
            var south = TileSystems.Cells(plots.TryFind(plot.Pos.Offset(0, 1), out Entity southId), southId, warmths);
            var west = TileSystems.Cells(plots.TryFind(plot.Pos.Offset(-1, 0), out Entity westId), westId, warmths);
            var east = TileSystems.Cells(plots.TryFind(plot.Pos.Offset(1, 0), out Entity eastId), eastId, warmths);
            var into = MemoryMarshal.Cast<NextWarmth, float>(next.Write().AsSpan());
            for (int z = 0; z < 16; z++)
            {
                int at = z * 16;
                var row = self.Slice(at, 16);
                var v0 = Vector128.Create(row.Slice(0, 4));
                var v1 = Vector128.Create(row.Slice(4, 4));
                var v2 = Vector128.Create(row.Slice(8, 4));
                var v3 = Vector128.Create(row.Slice(12, 4));
                var before = west.IsEmpty ? Vector128<float>.Zero : Vector128.Create(west.Slice(at + 12, 4));
                var after = east.IsEmpty ? Vector128<float>.Zero : Vector128.Create(east.Slice(at, 4));
                ref float m2 = ref MemoryMarshal.GetReference(RowAt(z - 2, self, north, south));
                ref float m1 = ref MemoryMarshal.GetReference(RowAt(z - 1, self, north, south));
                ref float p1 = ref MemoryMarshal.GetReference(RowAt(z + 1, self, north, south));
                ref float p2 = ref MemoryMarshal.GetReference(RowAt(z + 2, self, north, south));
                Cells(before, v0, v1, ref m2, ref m1, ref p1, ref p2, 0, into.Slice(at));
                Cells(v0, v1, v2, ref m2, ref m1, ref p1, ref p2, 4, into.Slice(at + 4));
                Cells(v1, v2, v3, ref m2, ref m1, ref p1, ref p2, 8, into.Slice(at + 8));
                Cells(v2, v3, after, ref m2, ref m1, ref p1, ref p2, 12, into.Slice(at + 12));
            }
        }

        /// <summary>Four cells of a row: x − 2 and x − 1 from <paramref name="prev"/> and <paramref name="cur"/>, x + 1 and x + 2 from <paramref name="cur"/> and <paramref name="next"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Cells(Vector128<float> prev, Vector128<float> cur, Vector128<float> next, ref float m2, ref float m1, ref float p1, ref float p2,
            nuint p, Span<float> into)
        {
            Vector128<float> xm2, xm1, xp1, xp2;
            if (Ssse3.IsSupported)
            {
                xm2 = Ssse3.AlignRight(cur.AsByte(), prev.AsByte(), 8).AsSingle();
                xm1 = Ssse3.AlignRight(cur.AsByte(), prev.AsByte(), 12).AsSingle();
                xp1 = Ssse3.AlignRight(next.AsByte(), cur.AsByte(), 4).AsSingle();
                xp2 = Ssse3.AlignRight(next.AsByte(), cur.AsByte(), 8).AsSingle();
            }
            else
            {
                xm2 = Vector128.Create(prev[2], prev[3], cur[0], cur[1]);
                xm1 = Vector128.Create(prev[3], cur[0], cur[1], cur[2]);
                xp1 = Vector128.Create(cur[1], cur[2], cur[3], next[0]);
                xp2 = Vector128.Create(cur[2], cur[3], next[0], next[1]);
            }
            var alongX = -xm2 + 16f * xm1 - 30f * cur + 16f * xp1 - xp2;
            var alongZ = -Vector128.LoadUnsafe(ref m2, p) + 16f * Vector128.LoadUnsafe(ref m1, p) - 30f * cur
                + 16f * Vector128.LoadUnsafe(ref p1, p) - Vector128.LoadUnsafe(ref p2, p);
            (cur + (alongX + alongZ) * (1f / 12) * TileSystems.Rate).CopyTo(into);
        }
    }

    /// <summary>The four face lookups of a plate and nothing else: one cell of each neighbor read, so none is dropped.</summary>
    [EcsSystem]
    public ref struct LookupsSystem
    {
        [ForEach(Parallel = true, ChunkSize = 16)]
        static void Step(in HandPlot plot, Lookup<HandPlot> plots, Lookup<DynamicBuffer<Warmth>> warmths, Mut<DynamicBuffer<NextWarmth>> next)
        {
            var north = TileSystems.Cells(plots.TryFind(plot.Pos.Offset(0, -1), out Entity northId), northId, warmths);
            var south = TileSystems.Cells(plots.TryFind(plot.Pos.Offset(0, 1), out Entity southId), southId, warmths);
            var west = TileSystems.Cells(plots.TryFind(plot.Pos.Offset(-1, 0), out Entity westId), westId, warmths);
            var east = TileSystems.Cells(plots.TryFind(plot.Pos.Offset(1, 0), out Entity eastId), eastId, warmths);
            float sum = (north.IsEmpty ? 0 : north[0]) + (south.IsEmpty ? 0 : south[0]) + (west.IsEmpty ? 0 : west[0]) + (east.IsEmpty ? 0 : east[0]);
            next.Write().AsSpan()[0] = new NextWarmth(sum);
        }
    }
}
