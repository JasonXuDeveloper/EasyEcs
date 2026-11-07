using System;
using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

// A seven-point stencil over tall tiles whose data sits in a few layers, as a voxel chunk's water does: a torus of 16 x 16
// columns of 16 x 16 cells, 32 layers high (8 MiB a buffer), warmth only in layers 14 to 17. Whole: the cell method
// over every layer (the rows outside the band are skipped by the walk, but every layer is exchanged); Layers: the same
// method taking the band from a component of the column (ITileLayers: layers 13 to 18), so the pass exchanges and walks
// those alone.

/// <summary>
/// A column of the torus: 16 x 16 cells, 32 layers, the faces beside it (no tile above or below). [Tile] declares the
/// shape; Beside is written here because it wraps (the last column's east is the first).
/// </summary>
[Tile(Width = 16, Depth = 16, Height = 32)]
public partial record struct Tower([LookupKey] Plot Pos) : IComponent
{
    public const int Side = 16;

    static Plot ITile<Plot>.Beside(Plot p, TileFaces face) => face switch
    {
        TileFaces.North => new Plot(p.X, (p.Z + Side - 1) % Side),
        TileFaces.South => new Plot(p.X, (p.Z + 1) % Side),
        TileFaces.West => new Plot((p.X + Side - 1) % Side, p.Z),
        TileFaces.East => new Plot((p.X + 1) % Side, p.Z),
        _ => p,
    };
}

/// <summary>The layers of a tower that hold warmth, with the layers next to them: the pass of <see cref="TileLayersSystems.BandSystem"/>.</summary>
public record struct Band(int From, int To) : IComponent, ITileLayers;

[MemoryDiagnoser]
public class TileLayersBenchmark
{
    [Params(1, 4)] public int Threads;

    private World _whole, _layers;

    [GlobalSetup]
    public void Setup()
    {
        _whole = Towers(Threads, out var whole);
        _whole.AddSystem<TileLayersSystems.WholeSystem>();
        _layers = Towers(Threads, out var layers);
        _layers.AddSystem<TileLayersSystems.BandSystem>();
        _whole.Update();
        _layers.Update();
        // The same cells from both passes: the band holds every row whose stencil reads warmth.
        for (int i = 0; i < whole.Count; i++)
            if (!whole[i].Read<DynamicBuffer<NextWarmth>>().AsReadOnlySpan().SequenceEqual(layers[i].Read<DynamicBuffer<NextWarmth>>().AsReadOnlySpan()))
                throw new InvalidOperationException("the bounded pass differs from the whole one");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _whole.Dispose();
        _layers.Dispose();
    }

    [Benchmark(Baseline = true)] public void Whole() => _whole.Update();
    [Benchmark] public void Layers() => _layers.Update();

    /// <summary>A torus of towers, warmth (a fixed seed) in layers 14 to 17, the step buffer sized, the band set.</summary>
    private static World Towers(int threads, out System.Collections.Generic.List<Entity> towers)
    {
        towers = new System.Collections.Generic.List<Entity>();
        const int layer = 16 * 16, cells = layer * 32;
        var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        var random = new Random(7);
        for (int x = 0; x < Tower.Side; x++)
        for (int z = 0; z < Tower.Side; z++)
        {
            var tower = world.CreateEntity(new Tower(new Plot(x, z)), new Band(13, 18));
            var warmth = tower.GetOrAdd<DynamicBuffer<Warmth>>().SetCount(cells);
            for (int i = 14 * layer; i < 18 * layer; i++) warmth[i] = new Warmth(random.Next(1, 1000));
            tower.GetOrAdd<DynamicBuffer<NextWarmth>>().SetCount(cells);
            towers.Add(tower);
        }
        return world;
    }
}

internal static class TileLayersSystems
{
    /// <summary>The seven-point stencil over every layer.</summary>
    [EcsSystem]
    public ref struct WholeSystem
    {
        [ForEach(Parallel = true, ChunkSize = 16)]
        static void Step(Cell<Warmth, Tower> w, out NextWarmth next) =>
            next = new(w.Value.T * 0.4f + (w[-1, 0, 0].T + w[1, 0, 0].T + w[0, -1, 0].T + w[0, 1, 0].T + w[0, 0, -1].T + w[0, 0, 1].T) * 0.1f);
    }

    /// <summary>The same stencil over the band of each tower (ITileLayers).</summary>
    [EcsSystem]
    public ref struct BandSystem
    {
        [ForEach(Parallel = true, ChunkSize = 16)]
        static void Step(Cell<Warmth, Tower> w, in Band band, out NextWarmth next) =>
            next = new(w.Value.T * 0.4f + (w[-1, 0, 0].T + w[1, 0, 0].T + w[0, -1, 0].T + w[0, 1, 0].T + w[0, 0, -1].T + w[0, 0, 1].T) * 0.1f);
    }
}
