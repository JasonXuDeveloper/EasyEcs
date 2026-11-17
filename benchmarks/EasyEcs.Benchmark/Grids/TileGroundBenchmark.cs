using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

// The ground of tall tiles (Ground<TTile>): a field of one layer on the grid of 16 x 16 x 8 chunks, as a voxel world's
// scent lies on its chunks, stepped in place over a torus of 32 x 32 chunks (262,144 ground cells, 1 MiB) or 512 x 512.
// A cross (the five-point diffusion: the halo read in place) and a box (the nine-point blur: the halo copied), each as
// the cell method (Cell<Warmth, Ground<Chunk>>, out Warmth), the same method as written (SimdMode.Off), and the walk a
// stencil writes by hand: the ground by ref, each row's columns walked with Load128 and Store (Columns(4)), or with
// Load512 by rows of 16 where Vector512 is accelerated (Columns(16)).

[MemoryDiagnoser]
public class TileGroundBenchmark
{
    [Params(1, 4)] public int Threads;

    [Params(Plot.Default, 512)] public int Side;

    private World _crossCell, _crossOff, _crossHand, _crossHand512, _boxCell, _boxOff, _boxHand, _boxHand512;

    [GlobalSetup]
    public void Setup()
    {
        Plot.Side = Side;
        _crossCell = Make<TileGroundSystems.CrossCellSystem>();
        _crossOff = Make<TileGroundSystems.CrossOffSystem>();
        _crossHand = Make<TileGroundSystems.CrossHandSystem>();
        _crossHand512 = Make<TileGroundSystems.CrossHand512System>();
        _boxCell = Make<TileGroundSystems.BoxCellSystem>();
        _boxOff = Make<TileGroundSystems.BoxOffSystem>();
        _boxHand = Make<TileGroundSystems.BoxHandSystem>();
        _boxHand512 = Make<TileGroundSystems.BoxHand512System>();
        for (int i = 0; i < 3; i++)
        {
            Cross_Cell();
            Cross_Off();
            Cross_Hand();
            Cross_Hand512();
            Box_Cell();
            Box_Off();
            Box_Hand();
            Box_Hand512();
        }
        // Every form steps the same ground, bit for bit.
        TileWorlds.Same(_crossCell, _crossHand);
        TileWorlds.Same(_crossOff, _crossHand);
        TileWorlds.Same(_crossHand512, _crossHand);
        TileWorlds.Same(_boxCell, _boxHand);
        TileWorlds.Same(_boxOff, _boxHand);
        TileWorlds.Same(_boxHand512, _boxHand);
    }

    private World Make<TSystem>() where TSystem : struct, allows ref struct
    {
        var world = TileWorlds.Warm(Threads, p => new TileGroundSystems.Chunk(p), next: false);
        world.AddSystem<TSystem>();
        return world;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (var world in new[] { _crossCell, _crossOff, _crossHand, _crossHand512, _boxCell, _boxOff, _boxHand, _boxHand512 }) world.Dispose();
    }

    [Benchmark(Baseline = true)] public void Cross_Hand() => _crossHand.Update();
    [Benchmark] public void Cross_Hand512() => _crossHand512.Update();
    [Benchmark] public void Cross_Cell() => _crossCell.Update();
    [Benchmark] public void Cross_Off() => _crossOff.Update();
    [Benchmark] public void Box_Hand() => _boxHand.Update();
    [Benchmark] public void Box_Hand512() => _boxHand512.Update();
    [Benchmark] public void Box_Cell() => _boxCell.Update();
    [Benchmark] public void Box_Off() => _boxOff.Update();
}

// A layer of the chunks under their ground: the cross, slower into a cell over stone in the chunk's top layer (7), over
// the torus of 32 x 32 chunks (each with 2,048 stones: 8 MiB, which 512 x 512 chunks would make 2 GiB). Slower, not
// zero (GroundHeatSystem's rule): stones that zero their cells drain the ground to zero within the run, and quiet tiles
// sleep. The ground cell reading that layer (Layer<Stone, Chunk, Top>: that layer's halo alone exchanged), the same
// method as written (SimdMode.Off), and the walk by hand: the chunk's tile by value (ExchangeHalo(7, 7) copies layers 6
// to 8), each row's columns walked with Load128, the stones' row at the layer's cursor.

[MemoryDiagnoser]
public class TileGroundLayerBenchmark
{
    [Params(1, 4)] public int Threads;

    private World _cell, _off, _hand;

    [GlobalSetup]
    public void Setup()
    {
        Plot.Side = Plot.Default;
        _cell = Make<TileGroundSystems.LayerCellSystem>();
        _off = Make<TileGroundSystems.LayerOffSystem>();
        _hand = Make<TileGroundSystems.LayerHandSystem>();
        for (int i = 0; i < 3; i++)
        {
            Layer_Cell();
            Layer_Off();
            Layer_Hand();
        }
        // Every form steps the same ground, bit for bit.
        TileWorlds.Same(_cell, _hand);
        TileWorlds.Same(_off, _hand);
    }

    /// <summary>A world of chunks with warm ground, each with 2,048 stones: one top-layer cell in eight solid, the layers under it full of them (never read).</summary>
    private World Make<TSystem>() where TSystem : struct, allows ref struct
    {
        var world = TileWorlds.Warm(Threads, p => new TileGroundSystems.Chunk(p), next: false);
        var random = new System.Random(11);
        var chunks = new System.Collections.Generic.List<Entity>();
        foreach (var item in world.Query<TileGroundSystems.Chunk>()) chunks.Add(item.Entity);
        foreach (var chunk in chunks)
        {
            var stones = chunk.GetOrAdd<DynamicBuffer<TileGroundSystems.Stone>>().SetCount(16 * 16 * 8);
            for (int i = 0; i < 7 * 256; i++) stones[i] = new TileGroundSystems.Stone(1);
            for (int i = 0; i < 32; i++) stones[7 * 256 + random.Next(256)] = new TileGroundSystems.Stone(1);
        }
        world.AddSystem<TSystem>();
        return world;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _cell.Dispose();
        _off.Dispose();
        _hand.Dispose();
    }

    [Benchmark(Baseline = true)] public void Layer_Hand() => _hand.Update();
    [Benchmark] public void Layer_Cell() => _cell.Update();
    [Benchmark] public void Layer_Off() => _off.Update();
}

internal static partial class TileGroundSystems
{
    /// <summary>A chunk of the torus: 16 x 16 cells, 8 layers, every neighbor around it; its ground is Ground&lt;Chunk&gt;.</summary>
    public partial record struct Chunk([LookupKey] Plot Pos) : IComponent, ITile<Plot>
    {
        static int ITileShape.Width => 16;
        static int ITileShape.Depth => 16;
        static int ITileShape.Height => 8;
        static TileNeighborhood ITileShape.Neighborhood => TileNeighborhood.All;
        static Plot ITile<Plot>.Beside(Plot p, TileFaces face) => p.Wrap(face);
    }

    private const float Rate = 0.2f;

    [EcsSystem]
    public ref struct CrossCellSystem
    {
        [ForEach(Parallel = true, ChunkSize = 16)]
        static void Step(Cell<Warmth, Ground<Chunk>> w, out Warmth next) =>
            next = new(w.Value.T + (w[-1, 0, 0].T + w[1, 0, 0].T + w[0, 0, -1].T + w[0, 0, 1].T - 4f * w.Value.T) * Rate);
    }

    [EcsSystem]
    public ref struct CrossOffSystem
    {
        [ForEach(Parallel = true, ChunkSize = 16, SimdMode = SimdMode.Off)]
        static void Step(Cell<Warmth, Ground<Chunk>> w, out Warmth next) =>
            next = new(w.Value.T + (w[-1, 0, 0].T + w[1, 0, 0].T + w[0, 0, -1].T + w[0, 0, 1].T - 4f * w.Value.T) * Rate);
    }

    [EcsSystem]
    public ref struct CrossHandSystem
    {
        [ForEach(Parallel = true, ChunkSize = 16)]
        static void Step(ref Tile<Warmth, Ground<Chunk>> warmth)
        {
            warmth.ExchangeHalo();
            foreach (var row in warmth.Rows(TileStencil.Layer))
            {
                var into = warmth.Write(row);
                foreach (var col in warmth.At(row).Columns(4))
                {
                    var here = col.Load128<float>();
                    col.Store(here + (col.Load128<float>(-1) + col.Load128<float>(1) + col.Load128<float>(0, 0, -1) + col.Load128<float>(0, 0, 1) - 4f * here) * Rate, into);
                }
            }
        }
    }

    /// <summary>The cross by rows of 16 floats (Columns(16), Load512) where Vector512 is accelerated, else <see cref="CrossHandSystem"/>'s walk.</summary>
    [EcsSystem]
    public ref struct CrossHand512System
    {
        [ForEach(Parallel = true, ChunkSize = 16)]
        static void Step(ref Tile<Warmth, Ground<Chunk>> warmth)
        {
            warmth.ExchangeHalo();
            foreach (var row in warmth.Rows(TileStencil.Layer))
            {
                var into = warmth.Write(row);
                if (Vector512.IsHardwareAccelerated)
                    foreach (var col in warmth.At(row).Columns(16))
                    {
                        var here = col.Load512<float>();
                        col.Store(here + (col.Load512<float>(-1) + col.Load512<float>(1) + col.Load512<float>(0, 0, -1) + col.Load512<float>(0, 0, 1) - 4f * here) * Rate, into);
                    }
                else
                    foreach (var col in warmth.At(row).Columns(4))
                    {
                        var here = col.Load128<float>();
                        col.Store(here + (col.Load128<float>(-1) + col.Load128<float>(1) + col.Load128<float>(0, 0, -1) + col.Load128<float>(0, 0, 1) - 4f * here) * Rate, into);
                    }
            }
        }
    }

    [EcsSystem]
    public ref struct BoxCellSystem
    {
        [ForEach(Parallel = true, ChunkSize = 16)]
        static void Step(Cell<Warmth, Ground<Chunk>> w, out Warmth next) =>
            next = new((w[-1, 0, -1].T + w[0, 0, -1].T + w[1, 0, -1].T
                + w[-1, 0, 0].T + w.Value.T + w[1, 0, 0].T
                + w[-1, 0, 1].T + w[0, 0, 1].T + w[1, 0, 1].T) * (1f / 9));
    }

    [EcsSystem]
    public ref struct BoxOffSystem
    {
        [ForEach(Parallel = true, ChunkSize = 16, SimdMode = SimdMode.Off)]
        static void Step(Cell<Warmth, Ground<Chunk>> w, out Warmth next) =>
            next = new((w[-1, 0, -1].T + w[0, 0, -1].T + w[1, 0, -1].T
                + w[-1, 0, 0].T + w.Value.T + w[1, 0, 0].T
                + w[-1, 0, 1].T + w[0, 0, 1].T + w[1, 0, 1].T) * (1f / 9));
    }

    [EcsSystem]
    public ref struct BoxHandSystem
    {
        [ForEach(Parallel = true, ChunkSize = 16)]
        static void Step(ref Tile<Warmth, Ground<Chunk>> warmth)
        {
            warmth.ExchangeHalo();
            foreach (var row in warmth.Rows(TileStencil.Layer))
            {
                var into = warmth.Write(row);
                foreach (var col in warmth.At(row).Columns(4))
                    col.Store((col.Load128<float>(-1, 0, -1) + col.Load128<float>(0, 0, -1) + col.Load128<float>(1, 0, -1)
                        + col.Load128<float>(-1) + col.Load128<float>() + col.Load128<float>(1)
                        + col.Load128<float>(-1, 0, 1) + col.Load128<float>(0, 0, 1) + col.Load128<float>(1, 0, 1)) * (1f / 9), into);
            }
        }
    }

    /// <summary>The box by rows of 16 floats (Columns(16), Load512) where Vector512 is accelerated, else <see cref="BoxHandSystem"/>'s walk.</summary>
    [EcsSystem]
    public ref struct BoxHand512System
    {
        [ForEach(Parallel = true, ChunkSize = 16)]
        static void Step(ref Tile<Warmth, Ground<Chunk>> warmth)
        {
            warmth.ExchangeHalo();
            foreach (var row in warmth.Rows(TileStencil.Layer))
            {
                var into = warmth.Write(row);
                if (Vector512.IsHardwareAccelerated)
                    foreach (var col in warmth.At(row).Columns(16))
                        col.Store((col.Load512<float>(-1, 0, -1) + col.Load512<float>(0, 0, -1) + col.Load512<float>(1, 0, -1)
                            + col.Load512<float>(-1) + col.Load512<float>() + col.Load512<float>(1)
                            + col.Load512<float>(-1, 0, 1) + col.Load512<float>(0, 0, 1) + col.Load512<float>(1, 0, 1)) * (1f / 9), into);
                else
                    foreach (var col in warmth.At(row).Columns(4))
                        col.Store((col.Load128<float>(-1, 0, -1) + col.Load128<float>(0, 0, -1) + col.Load128<float>(1, 0, -1)
                            + col.Load128<float>(-1) + col.Load128<float>() + col.Load128<float>(1)
                            + col.Load128<float>(-1, 0, 1) + col.Load128<float>(0, 0, 1) + col.Load128<float>(1, 0, 1)) * (1f / 9), into);
            }
        }
    }

    /// <summary>How fast warmth spreads into a cell over stone: slower than over open ground (<see cref="Rate"/>).</summary>
    private const float StoneRate = 0.05f;

    /// <summary>A chunk's stone: 0 open, else solid (4 bytes, the lanes of the warmth).</summary>
    public record struct Stone(int Solid);

    /// <summary>The top layer of a <see cref="Chunk"/>.</summary>
    public struct Top : ILayer
    {
        public static int Index => 7;
    }

    [EcsSystem]
    public ref struct LayerCellSystem
    {
        [ForEach(Parallel = true, ChunkSize = 16)]
        static void Step(Cell<Warmth, Ground<Chunk>> w, Layer<Stone, Chunk, Top> stone, out Warmth next) =>
            next = new(w.Value.T + (w[-1, 0, 0].T + w[1, 0, 0].T + w[0, 0, -1].T + w[0, 0, 1].T - 4f * w.Value.T) * (stone.Value.Solid != 0 ? StoneRate : Rate));
    }

    [EcsSystem]
    public ref struct LayerOffSystem
    {
        [ForEach(Parallel = true, ChunkSize = 16, SimdMode = SimdMode.Off)]
        static void Step(Cell<Warmth, Ground<Chunk>> w, Layer<Stone, Chunk, Top> stone, out Warmth next) =>
            next = new(w.Value.T + (w[-1, 0, 0].T + w[1, 0, 0].T + w[0, 0, -1].T + w[0, 0, 1].T - 4f * w.Value.T) * (stone.Value.Solid != 0 ? StoneRate : Rate));
    }

    [EcsSystem]
    public ref struct LayerHandSystem
    {
        [ForEach(Parallel = true, ChunkSize = 16)]
        static void Step(ref Tile<Warmth, Ground<Chunk>> warmth, Tile<Stone, Chunk> stones)
        {
            warmth.ExchangeHalo();
            stones.ExchangeHalo(Top.Index, Top.Index, mask: false);
            foreach (var row in warmth.Rows(TileStencil.Layer))
            {
                var c = warmth.At(row);
                var s = stones.At(new TileRow<Chunk>(Top.Index, row.Z));
                var into = MemoryMarshal.Cast<Warmth, float>(warmth.Write(row));
                for (int x = 0; x < 16; x += 4)
                {
                    var here = c.Load128<float>(x);
                    var rate = Vector128.ConditionalSelect(Vector128.Equals(s.Load128<int>(x), Vector128<int>.Zero).AsSingle(), Vector128.Create(Rate), Vector128.Create(StoneRate));
                    (here + (c.Load128<float>(x - 1) + c.Load128<float>(x + 1) + c.Load128<float>(x, 0, -1) + c.Load128<float>(x, 0, 1) - 4f * here) * rate).CopyTo(into.Slice(x));
                }
            }
        }
    }
}
