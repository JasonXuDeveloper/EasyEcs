using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

// Stencils over a torus of 32 x 32 plates of 16 x 16 cells (262,144 cells, 1 MiB a buffer: in cache), or of 512 x 512
// (67 million cells, 256 MiB a buffer: past the last-level cache): what Tile<TCell, TTile> costs against the same
// stencil written by hand (a Lookup per neighbor, a padded texture on the stack), what sleeping tiles save, and what
// finding the neighbors costs in a pass.

public record struct Plot(int X, int Z)
{
    /// <summary>The plots along each side of the torus: <see cref="Default"/>, or a benchmark's [Params] set before its worlds are made.</summary>
    public static int Side = Default;

    public const int Default = 32;

    /// <summary>The plot beside this one across a face, around the torus.</summary>
    public Plot Wrap(TileFaces face) => face switch
    {
        TileFaces.North => new Plot(X, (Z + Side - 1) % Side),
        TileFaces.South => new Plot(X, (Z + 1) % Side),
        TileFaces.West => new Plot((X + Side - 1) % Side, Z),
        TileFaces.East => new Plot((X + 1) % Side, Z),
        _ => this,
    };

    /// <summary>The plot <paramref name="dx"/>, <paramref name="dz"/> away, around the torus.</summary>
    public Plot Offset(int dx, int dz) => new((X + dx + Side) % Side, (Z + dz + Side) % Side);
}

public record struct Warmth(float T);
public record struct NextWarmth(float T);
public record struct Seed(byte Alive);
public record struct NextSeed(byte Alive);

/// <summary>A plate of the 9-point blur: every neighbor, edges and corners.</summary>
public partial record struct BlurPlot([LookupKey] Plot Pos) : IComponent, ITile<Plot>
{
    static int ITileShape.Width => 16;
    static int ITileShape.Depth => 16;
    static TileNeighborhood ITileShape.Neighborhood => TileNeighborhood.All;
    static Plot ITile<Plot>.Beside(Plot p, TileFaces face) => p.Wrap(face);
}

/// <summary>A plate of the blur whose key maps onto a slot of an array (IDirectMapped): a neighbor is one array read.</summary>
public partial record struct DirectPlot([UniqueKey] Plot Pos) : IComponent, ITile<Plot>, IDirectMapped<Plot>
{
    static int ITileShape.Width => 16;
    static int ITileShape.Depth => 16;
    static TileNeighborhood ITileShape.Neighborhood => TileNeighborhood.All;
    static Plot ITile<Plot>.Beside(Plot p, TileFaces face) => p.Wrap(face);
    static int IDirectMapped<Plot>.Slots => Plot.Side * Plot.Side;
    static int IDirectMapped<Plot>.SlotOf(Plot p) => p.Z * Plot.Side + p.X;
}

/// <summary>A plate of the fourth-order Laplacian: five points along each axis, a halo of 2, faces only.</summary>
public partial record struct WavePlot([LookupKey] Plot Pos) : IComponent, ITile<Plot>
{
    static int ITileShape.Width => 16;
    static int ITileShape.Depth => 16;
    static int ITileShape.Halo => 2;
    static Plot ITile<Plot>.Beside(Plot p, TileFaces face) => p.Wrap(face);
}

/// <summary>A plate of the Game of Life.</summary>
public partial record struct LifePlot([LookupKey] Plot Pos) : IComponent, ITile<Plot>
{
    static int ITileShape.Width => 16;
    static int ITileShape.Depth => 16;
    static TileNeighborhood ITileShape.Neighborhood => TileNeighborhood.All;
    static Plot ITile<Plot>.Beside(Plot p, TileFaces face) => p.Wrap(face);
}

/// <summary>A plate of the hand-written stencils: only its key, the neighbors found by hand.</summary>
public record struct HandPlot(Plot Pos) : IComponent, IIndexed<Plot>
{
    readonly Plot IIndexed<Plot>.Key => Pos;
}

/// <summary>Tags a plate whose step may change it.</summary>
public struct Awake : IComponent;

/// <summary>What a plate's last step changed: whether anything, and the faces whose edge.</summary>
public record struct SeedChange(TileFaces Edges, bool Changed) : IComponent;

/// <summary>Where the neighbors were found: an id that keeps the search from being dropped.</summary>
public record struct Probe(EntityId North, EntityId SouthEast) : IComponent;

/// <summary>The eight neighbors of a plate, found once and kept (the alternative to a search per pass).</summary>
public record struct Around(Around.Ids Of) : IComponent
{
    [InlineArray(8)]
    public struct Ids
    {
        private EntityId _first;
    }
}

[MemoryDiagnoser]
public class TileStencilBenchmark
{
    [Params(1, 4)] public int Threads;

    [Params(Plot.Default, 512)] public int Side;

    private World _blurTile, _blurDirect, _blurHand, _blurPingPong, _waveTile, _waveHand, _wavePingPong;

    [GlobalSetup]
    public void Setup()
    {
        Plot.Side = Side;
        _blurTile = TileWorlds.Warm<BlurPlot>(Threads, p => new BlurPlot(p));
        _blurTile.AddSystem<TileSystems.BlurSystem>();
        _blurTile.AddSystem<TileSystems.CommitWarmthSystem>();
        _blurDirect = TileWorlds.Warm<DirectPlot>(Threads, p => new DirectPlot(p));
        _blurDirect.AddSystem<TileSystems.BlurDirectSystem>();
        _blurDirect.AddSystem<TileSystems.CommitWarmthSystem>();
        _blurHand = TileWorlds.Warm<HandPlot>(Threads, p => new HandPlot(p));
        _blurHand.AddSystem<TileSystems.HandBlurSystem>();
        _blurHand.AddSystem<TileSystems.CommitWarmthSystem>();
        _waveTile = TileWorlds.Warm<WavePlot>(Threads, p => new WavePlot(p));
        _waveTile.AddSystem<TileSystems.WaveSystem>();
        _waveTile.AddSystem<TileSystems.CommitWarmthSystem>();
        _waveHand = TileWorlds.Warm<HandPlot>(Threads, p => new HandPlot(p));
        _waveHand.AddSystem<TileSystems.HandWaveSystem>();
        _waveHand.AddSystem<TileSystems.CommitWarmthSystem>();
        _blurPingPong = TileWorlds.Warm<BlurPlot>(Threads, p => new BlurPlot(p), next: false);
        _blurPingPong.AddSystem<TileSystems.PingBlurSystem>();
        _wavePingPong = TileWorlds.Warm<WavePlot>(Threads, p => new WavePlot(p), next: false);
        _wavePingPong.AddSystem<TileSystems.PingWaveSystem>();
        for (int i = 0; i < 3; i++)
        {
            Blur9_Tile();
            Blur9_TileDirect();
            Blur9_Hand();
            Laplacian_Tile();
            Laplacian_Hand();
            Blur9_PingPong();
            Laplacian_PingPong();
        }
        TileWorlds.Same(_blurTile, _blurHand);
        TileWorlds.Same(_blurPingPong, _blurHand);
        TileWorlds.Same(_wavePingPong, _waveHand);
        TileWorlds.Same(_blurDirect, _blurHand);
        TileWorlds.Same(_waveTile, _waveHand);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _blurTile.Dispose();
        _blurDirect.Dispose();
        _blurHand.Dispose();
        _waveTile.Dispose();
        _waveHand.Dispose();
        _blurPingPong.Dispose();
        _wavePingPong.Dispose();
    }

    [Benchmark] public void Blur9_Tile() => _blurTile.Update();
    [Benchmark] public void Blur9_TileDirect() => _blurDirect.Update();
    [Benchmark] public void Blur9_Hand() => _blurHand.Update();
    [Benchmark] public void Laplacian_Tile() => _waveTile.Update();
    [Benchmark] public void Laplacian_Hand() => _waveHand.Update();
    [Benchmark] public void Blur9_PingPong() => _blurPingPong.Update();
    [Benchmark] public void Laplacian_PingPong() => _wavePingPong.Update();
}

/// <summary>
/// A quiet Game of Life: a blinker on every 64th plate (16 of 1,024, or 4,096 of 262,144). Always on steps every plate (each skipping its empty rows);
/// Sparse steps the plates that are awake (the 16, which never change their edges) and lets the others sleep, by hand
/// (an Awake tag, a commit, a sleep and a wake system); PingPong steps each plate through ref Tile, the pass keeping its
/// second buffer and letting the plates that cannot change sleep.
/// </summary>
[MemoryDiagnoser]
public class TileActiveBenchmark
{
    [Params(1, 4)] public int Threads;

    [Params(Plot.Default, 512)] public int Side;

    private World _always, _sparse, _pingPong;

    [GlobalSetup]
    public void Setup()
    {
        Plot.Side = Side;
        _always = TileWorlds.Blinkers(Threads, awake: false);
        _always.AddSystem<TileSystems.LifeSystem>();
        _always.AddSystem<TileSystems.CommitSeedSystem>();
        _sparse = TileWorlds.Blinkers(Threads, awake: true);
        _sparse.AddSystem<TileSystems.SparseLifeSystem>();
        _sparse.AddSystem<TileSystems.CommitAwakeSystem>();
        _sparse.AddSystem<TileSystems.SleepSystem>();
        _sparse.AddSystem<TileSystems.WakeSystem>();
        _pingPong = TileWorlds.Blinkers(Threads, awake: false, next: false);
        _pingPong.AddSystem<TileSystems.PingLifeSystem>();
        for (int i = 0; i < 4; i++)
        {
            AlwaysOn();
            Sparse();
            PingPong();
        }
        if (_sparse.Count<Awake>(QueryFilter.Create()) != Side / 8 * (Side / 8)) throw new InvalidOperationException("the plates with a blinker stay awake");
        TileWorlds.SameSeeds(_always, _sparse);
        TileWorlds.SameSeeds(_always, _pingPong);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _always.Dispose();
        _sparse.Dispose();
        _pingPong.Dispose();
    }

    [Benchmark(Baseline = true)] public void AlwaysOn() => _always.Update();
    [Benchmark] public void Sparse() => _sparse.Update();
    [Benchmark] public void PingPong() => _pingPong.Update();
}

/// <summary>
/// What finding a plate's eight neighbors costs in a pass: Search is a pass whose method takes the tile (the
/// generated code searches the key index for the eight keys) and does nothing else; SearchDirect the same with a
/// direct-mapped key (an array read per neighbor); Cached reads the eight ids from a component kept beside the plate
/// instead.
/// </summary>
[MemoryDiagnoser]
public class TileNeighborBenchmark
{
    [Params(1, 4)] public int Threads;

    private World _search, _direct, _cached;

    [GlobalSetup]
    public void Setup()
    {
        _search = TileWorlds.Warm<BlurPlot>(Threads, p => new BlurPlot(p), probe: true);
        _search.AddSystem<TileSystems.SearchSystem>();
        _direct = TileWorlds.Warm<DirectPlot>(Threads, p => new DirectPlot(p), probe: true);
        _direct.AddSystem<TileSystems.SearchDirectSystem>();
        _cached = TileWorlds.Warm<BlurPlot>(Threads, p => new BlurPlot(p), probe: true);
        _cached.AddSystem<TileSystems.CacheSystem>();
        _cached.Update();
        _cached.RemoveSystem<TileSystems.CacheSystem>();
        _cached.AddSystem<TileSystems.CachedSystem>();
        Search();
        SearchDirect();
        Cached();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _search.Dispose();
        _direct.Dispose();
        _cached.Dispose();
    }

    [Benchmark(Baseline = true)] public void Search() => _search.Update();
    [Benchmark] public void SearchDirect() => _direct.Update();
    [Benchmark] public void Cached() => _cached.Update();
}

internal static class TileWorlds
{
    /// <summary>A torus of plates of random warmth (a fixed seed), the step buffer sized.</summary>
    public static World Warm<TPlot>(int threads, Func<Plot, TPlot> make, bool probe = false, bool next = true) where TPlot : struct, IComponent
    {
        var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        var random = new Random(7);
        world.Reserve<TPlot>(Plot.Side * Plot.Side);
        for (int n = 0; n < Plot.Side * Plot.Side; n++)
        {
            int x = n % Plot.Side, z = n / Plot.Side;
            var plate = world.CreateEntity(make(new Plot(x, z)));
            var warmth = plate.GetOrAdd<DynamicBuffer<Warmth>>().SetCount(256);
            for (int i = 0; i < 256; i++) warmth[i] = new Warmth(random.Next(1000));
            if (next) plate.GetOrAdd<DynamicBuffer<NextWarmth>>().SetCount(256);
            if (probe) plate.Set(new Probe());
        }
        return world;
    }

    /// <summary>A torus of empty Life plates with a blinker in the middle of every 64th (8 by 8), or of <paramref name="percent"/> percent of them, spread evenly in creation order.</summary>
    public static World Blinkers(int threads, bool awake, bool next = true, int percent = 0)
    {
        var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        world.Reserve<LifePlot>(Plot.Side * Plot.Side);
        for (int n = 0; n < Plot.Side * Plot.Side; n++)
        {
            int x = n % Plot.Side, z = n / Plot.Side;
            var plate = world.CreateEntity(new LifePlot(new Plot(x, z)));
            var seeds = plate.GetOrAdd<DynamicBuffer<Seed>>().SetCount(256);
            if (next) plate.GetOrAdd<DynamicBuffer<NextSeed>>().SetCount(256);
            if (percent > 0 ? n * percent / 100 != (n + 1) * percent / 100 : x % 8 == 3 && z % 8 == 5)
                for (int i = 7; i <= 9; i++) seeds[8 * 16 + i] = new Seed(1);
            if (awake)
            {
                plate.Set(new SeedChange());
                plate.SetAwake();
            }
        }
        return world;
    }

    /// <summary>Throws unless both worlds hold the same warmth, cell for cell, in creation order.</summary>
    public static void Same(World a, World b)
    {
        var left = a.Query<DynamicBuffer<Warmth>>().GetEnumerator();
        var right = b.Query<DynamicBuffer<Warmth>>().GetEnumerator();
        while (left.MoveNext())
        {
            right.MoveNext();
            var l = left.Current.Component1.AsReadOnlySpan();
            var r = right.Current.Component1.AsReadOnlySpan();
            for (int i = 0; i < l.Length; i++)
                if (l[i].T != r[i].T) throw new InvalidOperationException($"warmth differs at {i}: {l[i].T} and {r[i].T}");
        }
    }

    /// <summary>Throws unless both worlds hold the same cells.</summary>
    public static void SameSeeds(World a, World b)
    {
        var left = a.Query<DynamicBuffer<Seed>>().GetEnumerator();
        var right = b.Query<DynamicBuffer<Seed>>().GetEnumerator();
        while (left.MoveNext())
        {
            right.MoveNext();
            if (!left.Current.Component1.AsReadOnlySpan().SequenceEqual(right.Current.Component1.AsReadOnlySpan()))
                throw new InvalidOperationException("the cells differ");
        }
    }
}

internal static class TileSystems
{
    /// <summary>
    /// The 9-point blur of one plate, a row of 16 in four vectors. Inlined by force: the systems' one-line methods are
    /// the JIT's root, whose inline budget scales with its own IL, so inlined by size it would leave the cursor's loads
    /// as calls (2.9x slower).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Blur<TTile>(Tile<Warmth, TTile> warmth, Mut<DynamicBuffer<NextWarmth>> next)
        where TTile : struct, IComponent, ITileShape
    {
        warmth.ExchangeHalo();
        var into = MemoryMarshal.Cast<NextWarmth, float>(next.Write().AsSpan());
        foreach (var row in warmth.Rows(TileStencil.Layer))
        {
            var c = warmth.At(row);
            for (int x = 0; x < TTile.Width; x += 4)
                ((c.Load128<float>(x - 1, 0, -1) + c.Load128<float>(x, 0, -1) + c.Load128<float>(x + 1, 0, -1)
                  + c.Load128<float>(x - 1) + c.Load128<float>(x) + c.Load128<float>(x + 1)
                  + c.Load128<float>(x - 1, 0, 1) + c.Load128<float>(x, 0, 1) + c.Load128<float>(x + 1, 0, 1)) * (1f / 9))
                    .CopyTo(into.Slice(row.Index + x));
        }
    }

    [EcsSystem]
    public ref struct BlurSystem
    {
        [ForEach(Parallel = true, ChunkSize = 16)]
        static void Step(Tile<Warmth, BlurPlot> warmth, Mut<DynamicBuffer<NextWarmth>> next) => Blur(warmth, next);
    }

    [EcsSystem]
    public ref struct BlurDirectSystem
    {
        [ForEach(Parallel = true, ChunkSize = 16)]
        static void Step(Tile<Warmth, DirectPlot> warmth, Mut<DynamicBuffer<NextWarmth>> next) => Blur(warmth, next);
    }

    public const float Rate = 0.05f;

    [EcsSystem]
    public ref struct WaveSystem
    {
        [ForEach(Parallel = true, ChunkSize = 16)]
        static void Step(Tile<Warmth, WavePlot> warmth, Mut<DynamicBuffer<NextWarmth>> next)
        {
            warmth.ExchangeHalo();
            var into = MemoryMarshal.Cast<NextWarmth, float>(next.Write().AsSpan());
            foreach (var row in warmth.Rows(TileStencil.Layer))
            {
                var c = warmth.At(row);
                for (int x = 0; x < 16; x += 4)
                {
                    var here = c.Load128<float>(x);
                    var alongX = -c.Load128<float>(x - 2) + 16f * c.Load128<float>(x - 1) - 30f * here + 16f * c.Load128<float>(x + 1) - c.Load128<float>(x + 2);
                    var alongZ = -c.Load128<float>(x, 0, -2) + 16f * c.Load128<float>(x, 0, -1) - 30f * here + 16f * c.Load128<float>(x, 0, 1) - c.Load128<float>(x, 0, 2);
                    (here + (alongX + alongZ) * (1f / 12) * Rate).CopyTo(into.Slice(row.Index + x));
                }
            }
        }
    }

    /// <summary>The blur stepped into the plate's second buffer (ref Tile): no NextWarmth, no commit, the swap O(1) per plate.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PingBlur<TTile>(ref Tile<Warmth, TTile> warmth) where TTile : struct, IComponent, ITileShape
    {
        warmth.ExchangeHalo();
        foreach (var row in warmth.Rows(TileStencil.Layer))
        {
            var c = warmth.At(row);
            var into = MemoryMarshal.Cast<Warmth, float>(warmth.Write(row));
            for (int x = 0; x < TTile.Width; x += 4)
                ((c.Load128<float>(x - 1, 0, -1) + c.Load128<float>(x, 0, -1) + c.Load128<float>(x + 1, 0, -1)
                  + c.Load128<float>(x - 1) + c.Load128<float>(x) + c.Load128<float>(x + 1)
                  + c.Load128<float>(x - 1, 0, 1) + c.Load128<float>(x, 0, 1) + c.Load128<float>(x + 1, 0, 1)) * (1f / 9))
                    .CopyTo(into.Slice(x));
        }
    }

    [EcsSystem]
    public ref struct PingBlurSystem
    {
        [ForEach(Parallel = true, ChunkSize = 16)]
        static void Step(ref Tile<Warmth, BlurPlot> warmth) => PingBlur(ref warmth);
    }

    [EcsSystem]
    public ref struct PingWaveSystem
    {
        [ForEach(Parallel = true, ChunkSize = 16)]
        static void Step(ref Tile<Warmth, WavePlot> warmth)
        {
            warmth.ExchangeHalo();
            foreach (var row in warmth.Rows(TileStencil.Layer))
            {
                var c = warmth.At(row);
                var into = MemoryMarshal.Cast<Warmth, float>(warmth.Write(row));
                for (int x = 0; x < 16; x += 4)
                {
                    var here = c.Load128<float>(x);
                    var alongX = -c.Load128<float>(x - 2) + 16f * c.Load128<float>(x - 1) - 30f * here + 16f * c.Load128<float>(x + 1) - c.Load128<float>(x + 2);
                    var alongZ = -c.Load128<float>(x, 0, -2) + 16f * c.Load128<float>(x, 0, -1) - 30f * here + 16f * c.Load128<float>(x, 0, 1) - c.Load128<float>(x, 0, 2);
                    (here + (alongX + alongZ) * (1f / 12) * Rate).CopyTo(into.Slice(x));
                }
            }
        }
    }

    [EcsSystem]
    public ref struct CommitWarmthSystem
    {
        [ForEach(Parallel = true, ChunkSize = 64)]
        static void Commit(in DynamicBuffer<NextWarmth> next, Mut<DynamicBuffer<Warmth>> warmth) =>
            next.AsReadOnlySpan().CopyTo(MemoryMarshal.Cast<Warmth, NextWarmth>(warmth.Write().AsSpan()));
    }

    // The hand-written blur: the eight neighbors found through the key index, their cells read through a lookup, the
    // plate and its ring of one cell copied into an 18 x 18 texture on the stack, then the same row loop.
    [EcsSystem, SkipLocalsInit]
    public ref struct HandBlurSystem
    {
        const int Row = 18;

        [ForEach(Parallel = true, ChunkSize = 16)]
        static void Step(in HandPlot plot, in DynamicBuffer<Warmth> warmth, Lookup<HandPlot> plots,
            Lookup<DynamicBuffer<Warmth>> warmths, Mut<DynamicBuffer<NextWarmth>> next)
        {
            Span<float> pad = stackalloc float[Row * Row];
            var self = MemoryMarshal.Cast<Warmth, float>(warmth.AsReadOnlySpan());
            var north = Cells(plots.TryFind(plot.Pos.Offset(0, -1), out Entity northId), northId, warmths);
            var south = Cells(plots.TryFind(plot.Pos.Offset(0, 1), out Entity southId), southId, warmths);
            var west = Cells(plots.TryFind(plot.Pos.Offset(-1, 0), out Entity westId), westId, warmths);
            var east = Cells(plots.TryFind(plot.Pos.Offset(1, 0), out Entity eastId), eastId, warmths);
            // The corners: the cell of each diagonal neighbor nearest this plate.
            pad[0] = Corner(plots.TryFind(plot.Pos.Offset(-1, -1), out Entity nw), nw, 255, warmths);
            pad[Row - 1] = Corner(plots.TryFind(plot.Pos.Offset(1, -1), out Entity ne), ne, 240, warmths);
            pad[(Row - 1) * Row] = Corner(plots.TryFind(plot.Pos.Offset(-1, 1), out Entity sw), sw, 15, warmths);
            pad[Row * Row - 1] = Corner(plots.TryFind(plot.Pos.Offset(1, 1), out Entity se), se, 0, warmths);
            Band(north, 15 * 16, pad.Slice(1, 16));
            Band(south, 0, pad.Slice((Row - 1) * Row + 1, 16));
            for (int z = 0; z < 16; z++)
            {
                int p = (z + 1) * Row;
                pad[p] = west.IsEmpty ? 0 : west[z * 16 + 15];
                self.Slice(z * 16, 16).CopyTo(pad.Slice(p + 1, 16));
                pad[p + 17] = east.IsEmpty ? 0 : east[z * 16];
            }

            var into = MemoryMarshal.Cast<NextWarmth, float>(next.Write().AsSpan());
            ref float t = ref MemoryMarshal.GetReference(pad);
            for (int z = 0; z < 16; z++)
            for (int x = 0; x < 16; x += 4)
            {
                // The invariant: the texel (x + 1, z + 1) and its ring lie inside the 18 x 18 texture.
                nuint p = (nuint)((z + 1) * Row + x + 1);
                var sum = Vector128.LoadUnsafe(ref t, p - Row - 1) + Vector128.LoadUnsafe(ref t, p - Row) + Vector128.LoadUnsafe(ref t, p - Row + 1)
                    + Vector128.LoadUnsafe(ref t, p - 1) + Vector128.LoadUnsafe(ref t, p) + Vector128.LoadUnsafe(ref t, p + 1)
                    + Vector128.LoadUnsafe(ref t, p + Row - 1) + Vector128.LoadUnsafe(ref t, p + Row) + Vector128.LoadUnsafe(ref t, p + Row + 1);
                (sum * (1f / 9)).CopyTo(into.Slice(z * 16 + x));
            }
        }

        static float Corner(bool found, EntityId id, int cell, Lookup<DynamicBuffer<Warmth>> warmths) =>
            found ? warmths.Read(id)[cell].T : 0;
    }

    // The hand-written fourth-order Laplacian: the four face neighbors, a 20 x 20 texture (a ring of two cells).
    [EcsSystem, SkipLocalsInit]
    public ref struct HandWaveSystem
    {
        const int Row = 20;

        [ForEach(Parallel = true, ChunkSize = 16)]
        static void Step(in HandPlot plot, in DynamicBuffer<Warmth> warmth, Lookup<HandPlot> plots,
            Lookup<DynamicBuffer<Warmth>> warmths, Mut<DynamicBuffer<NextWarmth>> next)
        {
            Span<float> pad = stackalloc float[Row * Row];
            var self = MemoryMarshal.Cast<Warmth, float>(warmth.AsReadOnlySpan());
            var north = Cells(plots.TryFind(plot.Pos.Offset(0, -1), out Entity northId), northId, warmths);
            var south = Cells(plots.TryFind(plot.Pos.Offset(0, 1), out Entity southId), southId, warmths);
            var west = Cells(plots.TryFind(plot.Pos.Offset(-1, 0), out Entity westId), westId, warmths);
            var east = Cells(plots.TryFind(plot.Pos.Offset(1, 0), out Entity eastId), eastId, warmths);
            for (int k = 0; k < 2; k++)
            {
                Band(north, (14 + k) * 16, pad.Slice(k * Row + 2, 16));
                Band(south, k * 16, pad.Slice((18 + k) * Row + 2, 16));
            }
            for (int z = 0; z < 16; z++)
            {
                int p = (z + 2) * Row, at = z * 16;
                pad[p] = west.IsEmpty ? 0 : west[at + 14];
                pad[p + 1] = west.IsEmpty ? 0 : west[at + 15];
                self.Slice(at, 16).CopyTo(pad.Slice(p + 2, 16));
                pad[p + 18] = east.IsEmpty ? 0 : east[at];
                pad[p + 19] = east.IsEmpty ? 0 : east[at + 1];
            }

            var into = MemoryMarshal.Cast<NextWarmth, float>(next.Write().AsSpan());
            ref float t = ref MemoryMarshal.GetReference(pad);
            for (int z = 0; z < 16; z++)
            for (int x = 0; x < 16; x += 4)
            {
                // The invariant: the texel (x + 2, z + 2) and two cells each way along both axes lie inside the texture.
                nuint p = (nuint)((z + 2) * Row + x + 2);
                var here = Vector128.LoadUnsafe(ref t, p);
                var alongX = -Vector128.LoadUnsafe(ref t, p - 2) + 16f * Vector128.LoadUnsafe(ref t, p - 1) - 30f * here
                    + 16f * Vector128.LoadUnsafe(ref t, p + 1) - Vector128.LoadUnsafe(ref t, p + 2);
                var alongZ = -Vector128.LoadUnsafe(ref t, p - 2 * Row) + 16f * Vector128.LoadUnsafe(ref t, p - Row) - 30f * here
                    + 16f * Vector128.LoadUnsafe(ref t, p + Row) - Vector128.LoadUnsafe(ref t, p + 2 * Row);
                (here + (alongX + alongZ) * (1f / 12) * Rate).CopyTo(into.Slice(z * 16 + x));
            }
        }
    }

    /// <summary>A neighbor's cells, empty when no plate was found (the search stays in the method: a Lookup gets its
    /// index when the method calls TryFind on the parameter itself).</summary>
    private static ReadOnlySpan<float> Cells(bool found, EntityId id, Lookup<DynamicBuffer<Warmth>> warmths) =>
        found ? MemoryMarshal.Cast<Warmth, float>(warmths.Read(id).AsReadOnlySpan()) : default;

    /// <summary>A neighbor's facing row of 16 into the ring, or zeros.</summary>
    private static void Band(ReadOnlySpan<float> cells, int at, Span<float> into)
    {
        if (cells.IsEmpty) into.Clear();
        else cells.Slice(at, 16).CopyTo(into);
    }

    /// <summary>Life on bytes: born with three live neighbors, lives on with two or three; a row of 16 in one vector.</summary>
    private static void Life(Tile<Seed, LifePlot> life, Span<byte> into)
    {
        life.ExchangeHalo();
        foreach (var row in life.Rows(TileStencil.Layer))
        {
            var c = life.At(row);
            var count = c.Load128<byte>(-1, 0, -1) + c.Load128<byte>(0, 0, -1) + c.Load128<byte>(1, 0, -1)
                + c.Load128<byte>(-1) + c.Load128<byte>(1)
                + c.Load128<byte>(-1, 0, 1) + c.Load128<byte>(0, 0, 1) + c.Load128<byte>(1, 0, 1);
            var born = Vector128.Equals(count, Vector128.Create((byte)3));
            var stays = Vector128.Equals(count, Vector128.Create((byte)2)) & c.Load128<byte>();
            ((born | stays) & Vector128<byte>.One).CopyTo(into.Slice(row.Index));
        }
    }

    [EcsSystem]
    public ref struct LifeSystem
    {
        [ForEach(Parallel = true, ChunkSize = 16)]
        static void Step(Tile<Seed, LifePlot> life, Mut<DynamicBuffer<NextSeed>> next)
        {
            var into = MemoryMarshal.Cast<NextSeed, byte>(next.Write().AsSpan());
            into.Clear(); // the rows the mask skips are empty
            Life(life, into);
        }
    }

    /// <summary>Life stepped into the plate's second buffer: the rows it skips are zeroed for it, and the plates that cannot change sleep.</summary>
    [EcsSystem]
    public ref struct PingLifeSystem
    {
        [ForEach(Parallel = true, ChunkSize = 16)]
        static void Step(ref Tile<Seed, LifePlot> life)
        {
            life.ExchangeHalo();
            foreach (var row in life.Rows(TileStencil.Layer))
            {
                var c = life.At(row);
                var count = c.Load128<byte>(-1, 0, -1) + c.Load128<byte>(0, 0, -1) + c.Load128<byte>(1, 0, -1)
                    + c.Load128<byte>(-1) + c.Load128<byte>(1)
                    + c.Load128<byte>(-1, 0, 1) + c.Load128<byte>(0, 0, 1) + c.Load128<byte>(1, 0, 1);
                var born = Vector128.Equals(count, Vector128.Create((byte)3));
                var stays = Vector128.Equals(count, Vector128.Create((byte)2)) & c.Load128<byte>();
                ((born | stays) & Vector128<byte>.One).CopyTo(MemoryMarshal.Cast<Seed, byte>(life.Write(row)));
            }
        }
    }

    [EcsSystem]
    public ref struct CommitSeedSystem
    {
        [ForEach(Parallel = true, ChunkSize = 64)]
        static void Commit(in DynamicBuffer<NextSeed> next, Mut<DynamicBuffer<Seed>> seeds) =>
            next.AsReadOnlySpan().CopyTo(MemoryMarshal.Cast<Seed, NextSeed>(seeds.Write().AsSpan()));
    }

    [EcsSystem]
    public ref struct SparseLifeSystem
    {
        [ForEach(Parallel = true, ChunkSize = 16), With<Awake>]
        static void Step(Tile<Seed, LifePlot> life, Mut<DynamicBuffer<NextSeed>> next, ref SeedChange change)
        {
            var step = next.Write().AsSpan();
            var into = MemoryMarshal.Cast<NextSeed, byte>(step);
            into.Clear();
            Life(life, into);
            var edges = life.ChangedEdges(MemoryMarshal.Cast<NextSeed, Seed>(step), out bool changed);
            change = new SeedChange(edges, changed);
        }
    }

    [EcsSystem]
    public ref struct CommitAwakeSystem
    {
        [ForEach, With<Awake>]
        static void Commit(in DynamicBuffer<NextSeed> next, Mut<DynamicBuffer<Seed>> seeds) =>
            next.AsReadOnlySpan().CopyTo(MemoryMarshal.Cast<Seed, NextSeed>(seeds.Write().AsSpan()));
    }

    [EcsSystem]
    public ref struct SleepSystem
    {
        [ForEach, With<Awake>]
        static void Sleep(Entity plate, in SeedChange change)
        {
            if (!change.Changed) plate.RemoveAwake();
        }
    }

    [EcsSystem]
    public ref struct WakeSystem
    {
        [ForEach, With<Awake>]
        static void Wake(in SeedChange change, Tile<Seed, LifePlot> life, World world)
        {
            foreach (var id in life.Facing(change.Edges)) world.GetEntity(id).SetAwake();
        }
    }

    [EcsSystem]
    public ref struct SearchSystem
    {
        [ForEach(Parallel = true, ChunkSize = 16)]
        static void Find(Tile<Warmth, BlurPlot> warmth, ref Probe probe) =>
            probe = new Probe(warmth.Beside(TileFaces.North), warmth.Beside(1, 0, 1));
    }

    [EcsSystem]
    public ref struct SearchDirectSystem
    {
        [ForEach(Parallel = true, ChunkSize = 16)]
        static void Find(Tile<Warmth, DirectPlot> warmth, ref Probe probe) =>
            probe = new Probe(warmth.Beside(TileFaces.North), warmth.Beside(1, 0, 1));
    }

    [EcsSystem]
    public ref struct CacheSystem
    {
        [ForEach, With<Probe>]
        static void Keep(Entity plate, Tile<Warmth, BlurPlot> warmth)
        {
            var around = new Around.Ids();
            int i = 0;
            for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
                if ((dx | dz) != 0) around[i++] = warmth.Beside(dx, 0, dz);
            plate.Set(new Around(around));
        }
    }

    [EcsSystem]
    public ref struct CachedSystem
    {
        [ForEach(Parallel = true, ChunkSize = 16)]
        static void Find(in Around around, ref Probe probe) => probe = new Probe(around.Of[1], around.Of[7]);
    }
}
