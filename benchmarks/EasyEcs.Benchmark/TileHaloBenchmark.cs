using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// Backlog item 12a, decided by this prototype: where a tile's halo lives. Exchange is what Tile.ExchangeHalo does
/// before every step (the plate and the facing edges of its eight neighbors copied into an 18 x 18 texture, then the
/// stencil over the texture); Padded stores every plate with its ring (18 x 18 cells), the stencil reading its own
/// storage, and after the step copies only the ring from the neighbors' edges (68 cells instead of 324). Both a
/// 9-point blur over the torus of 32 x 32 plates of TileStencilBenchmark, ping-pong buffers, the same sums bit for bit
/// (checked in setup), as plain loops on the world's parallel runner: the layout alone.
/// </summary>
[MemoryDiagnoser]
public class TileHaloBenchmark
{
    [Params(1, 4)] public int Threads;

    private const int Side = Plot.Side, Plates = Side * Side, W = 16, P = W + 2;

    private World _world;
    private float[][] _cells, _next;     // Exchange: 16 x 16 per plate
    private float[][] _front, _back;     // Padded: 18 x 18 per plate, the ring valid in _front

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { MaxDegreeOfParallelism = Threads });
        var random = new Random(7);
        _cells = new float[Plates][];
        _next = new float[Plates][];
        _front = new float[Plates][];
        _back = new float[Plates][];
        for (int i = 0; i < Plates; i++)
        {
            _cells[i] = new float[W * W];
            _next[i] = new float[W * W];
            _front[i] = new float[P * P];
            _back[i] = new float[P * P];
            for (int c = 0; c < W * W; c++) _cells[i][c] = _front[i][(c / W + 1) * P + c % W + 1] = random.Next(1000);
        }
        var ring = new RingJob { Cells = _front };
        _world.ForParallel(Plates, ref ring);
        for (int step = 0; step < 3; step++)
        {
            Exchange();
            Padded();
        }
        for (int i = 0; i < Plates; i++)
        for (int c = 0; c < W * W; c++)
            if (_cells[i][c] != _front[i][(c / W + 1) * P + c % W + 1])
                throw new InvalidOperationException($"plate {i}, cell {c}: {_cells[i][c]} and {_front[i][(c / W + 1) * P + c % W + 1]}");
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark(Baseline = true)]
    public void Exchange()
    {
        var job = new ExchangeJob { Cells = _cells, Next = _next };
        _world.ForParallel(Plates, ref job, 16);
        (_cells, _next) = (_next, _cells);
    }

    [Benchmark]
    public void Padded()
    {
        var step = new PaddedJob { Front = _front, Back = _back };
        _world.ForParallel(Plates, ref step, 16);
        var ring = new RingJob { Cells = _back };
        _world.ForParallel(Plates, ref ring, 16);
        (_front, _back) = (_back, _front);
    }

    private static int At(int plate, int dx, int dz) => (plate / Side + dz + Side) % Side * Side + (plate % Side + dx + Side) % Side;

    /// <summary>The 9-point blur of the 16 cells of a row from 18 x 18 cells (row <paramref name="z"/> + 1 the row's).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Blur(ref float texture, int z, Span<float> into)
    {
        for (int x = 0; x < W; x += 4)
        {
            nuint up = (nuint)(z * P + x), here = up + P, down = here + P;
            ((Vector128.LoadUnsafe(ref texture, up) + Vector128.LoadUnsafe(ref texture, up + 1) + Vector128.LoadUnsafe(ref texture, up + 2)
              + Vector128.LoadUnsafe(ref texture, here) + Vector128.LoadUnsafe(ref texture, here + 1) + Vector128.LoadUnsafe(ref texture, here + 2)
              + Vector128.LoadUnsafe(ref texture, down) + Vector128.LoadUnsafe(ref texture, down + 1) + Vector128.LoadUnsafe(ref texture, down + 2)) * (1f / 9))
                .CopyTo(into.Slice(x));
        }
    }

    /// <summary>The texture of ExchangeHalo: the plate, then the facing edges and corners of its eight neighbors.</summary>
    private struct ExchangeJob : IForRange
    {
        public float[][] Cells, Next;

        [SkipLocalsInit]
        public void Execute(int start, int end)
        {
            Span<float> texture = stackalloc float[P * P];
            for (int plate = start; plate < end; plate++)
            {
                var own = Cells[plate];
                for (int z = 0; z < W; z++) own.AsSpan(z * W, W).CopyTo(texture.Slice((z + 1) * P + 1));
                var north = Cells[At(plate, 0, -1)];
                var south = Cells[At(plate, 0, 1)];
                north.AsSpan((W - 1) * W, W).CopyTo(texture.Slice(1));
                south.AsSpan(0, W).CopyTo(texture.Slice((W + 1) * P + 1));
                var west = Cells[At(plate, -1, 0)];
                var east = Cells[At(plate, 1, 0)];
                for (int z = 0; z < W; z++)
                {
                    texture[(z + 1) * P] = west[z * W + W - 1];
                    texture[(z + 1) * P + W + 1] = east[z * W];
                }
                texture[0] = Cells[At(plate, -1, -1)][W * W - 1];
                texture[W + 1] = Cells[At(plate, 1, -1)][(W - 1) * W];
                texture[(W + 1) * P] = Cells[At(plate, -1, 1)][W - 1];
                texture[(W + 1) * P + W + 1] = Cells[At(plate, 1, 1)][0];
                ref float t = ref MemoryMarshal.GetReference(texture);
                var into = Next[plate].AsSpan();
                for (int z = 0; z < W; z++) Blur(ref t, z, into.Slice(z * W, W));
            }
        }
    }

    /// <summary>The stencil over the plate's own padded storage, into the back buffer's interior.</summary>
    private struct PaddedJob : IForRange
    {
        public float[][] Front, Back;

        public void Execute(int start, int end)
        {
            for (int plate = start; plate < end; plate++)
            {
                ref float t = ref MemoryMarshal.GetArrayDataReference(Front[plate]);
                var into = Back[plate].AsSpan();
                for (int z = 0; z < W; z++) Blur(ref t, z, into.Slice((z + 1) * P + 1, W));
            }
        }
    }

    /// <summary>The ring of each plate pulled from its neighbors' edges (each plate writes only its own ring).</summary>
    private struct RingJob : IForRange
    {
        public float[][] Cells;

        public void Execute(int start, int end)
        {
            for (int plate = start; plate < end; plate++)
            {
                var own = Cells[plate].AsSpan();
                Cells[At(plate, 0, -1)].AsSpan(W * P + 1, W).CopyTo(own.Slice(1));
                Cells[At(plate, 0, 1)].AsSpan(P + 1, W).CopyTo(own.Slice((W + 1) * P + 1));
                var west = Cells[At(plate, -1, 0)];
                var east = Cells[At(plate, 1, 0)];
                for (int z = 1; z <= W; z++)
                {
                    own[z * P] = west[z * P + W];
                    own[z * P + W + 1] = east[z * P + 1];
                }
                own[0] = Cells[At(plate, -1, -1)][W * P + W];
                own[W + 1] = Cells[At(plate, 1, -1)][W * P + 1];
                own[(W + 1) * P] = Cells[At(plate, -1, 1)][P + W];
                own[(W + 1) * P + W + 1] = Cells[At(plate, 1, 1)][P + 1];
            }
        }
    }
}
