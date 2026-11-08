using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

// A cell pass with a result per tile, the voxel water's shape: byte cells (a level 0 to 8) on the tall towers of
// TileLayersBenchmark (rows of 16 bytes: one Vector128 each), damp in layers 8 to 23, each cell taking one level less
// than its wettest side, and a tally per tower (wet and changed cells summed, the first and last wet layer). Cell: the
// method on one cell with ref Reduce<DampTally>; Hand: the same pass written over the tile with Load128 and the row's
// counts by popcount, as samples/VoxelServer's Flow.cs was.

/// <summary>A level of damp, 0 to 8.</summary>
public record struct Damp(byte Level);

/// <summary>The step's level of <see cref="Damp"/>.</summary>
public record struct NextDamp(byte Level);

/// <summary>What a tower's step adds up to: its wet and changed cells, the lowest and highest wet layer.</summary>
public partial struct DampTally : IComponent
{
    [Sum] public ushort Wet;
    [Sum] public ushort Moved;
    [Min] public short First;
    [Max] public short Last;
}

[MemoryDiagnoser]
public class TileTallyBenchmark
{
    [Params(1, 4)] public int Threads;

    private World _cell, _hand;

    [GlobalSetup]
    public void Setup()
    {
        _cell = Towers(Threads, out var cell);
        _cell.AddSystem<TileTallySystems.CellSystem>();
        _hand = Towers(Threads, out var hand);
        _hand.AddSystem<TileTallySystems.HandSystem>();
        _cell.Update();
        _hand.Update();
        // The same step and tally from both (rows the hand pass skips stay zero from SetCount, as the cell pass fills them).
        for (int i = 0; i < cell.Count; i++)
            if (!cell[i].Read<DynamicBuffer<NextDamp>>().AsReadOnlySpan().SequenceEqual(hand[i].Read<DynamicBuffer<NextDamp>>().AsReadOnlySpan())
                || !cell[i].Read<DampTally>().Equals(hand[i].Read<DampTally>()))
                throw new InvalidOperationException("the cell pass differs from the hand-written one");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _cell.Dispose();
        _hand.Dispose();
    }

    [Benchmark(Baseline = true)] public void Hand() => _hand.Update();
    [Benchmark] public void Cell() => _cell.Update();

    /// <summary>A torus of towers, damp (a fixed seed, a third of the cells) in layers 8 to 23, the step buffer sized.</summary>
    private static World Towers(int threads, out System.Collections.Generic.List<Entity> towers)
    {
        towers = new System.Collections.Generic.List<Entity>();
        const int layer = 16 * 16, cells = layer * 32;
        var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        var random = new Random(11);
        for (int x = 0; x < Tower.Side; x++)
        for (int z = 0; z < Tower.Side; z++)
        {
            var tower = world.CreateEntity(new Tower(new Plot(x, z)), DampTally.Identity);
            var damp = tower.GetOrAdd<DynamicBuffer<Damp>>().SetCount(cells);
            for (int i = 8 * layer; i < 24 * layer; i++)
                if (random.Next(3) == 0) damp[i] = new Damp((byte)random.Next(1, 9));
            tower.GetOrAdd<DynamicBuffer<NextDamp>>().SetCount(cells);
            towers.Add(tower);
        }
        return world;
    }
}

internal static class TileTallySystems
{
    /// <summary>One cell: its next level and what it adds to the tower's tally.</summary>
    [EcsSystem]
    public ref struct CellSystem
    {
        [ForEach(Parallel = true, ChunkSize = 16)]
        static void Spread(Cell<Damp, Tower> d, out NextDamp next, ref Reduce<DampTally> tally)
        {
            int here = d.Value.Level;
            int side = Math.Max(Math.Max(d[-1, 0, 0].Level, d[1, 0, 0].Level), Math.Max(d[0, 0, -1].Level, d[0, 0, 1].Level));
            int level = Math.Max(here, Math.Max(side, 1) - 1);
            next = new NextDamp((byte)level);
            tally.Value.Wet += level != 0 ? (ushort)1 : (ushort)0;
            tally.Value.Moved += level != here ? (ushort)1 : (ushort)0;
            tally.Value.First = Math.Min(tally.Value.First, level != 0 ? (short)d.Y : short.MaxValue);
            tally.Value.Last = Math.Max(tally.Value.Last, level != 0 ? (short)d.Y : short.MinValue);
        }
    }

    /// <summary>The same step over the tile by hand: 16 cells a row, the row's counts by popcount of its masks.</summary>
    [EcsSystem]
    public ref struct HandSystem
    {
        [ForEach(Parallel = true, ChunkSize = 16)]
        static void Spread(Tile<Damp, Tower> damp, Mut<DynamicBuffer<NextDamp>> step, ref DampTally tally)
        {
            damp.ExchangeHalo();
            var next = MemoryMarshal.Cast<NextDamp, byte>(step.Write().AsSpan());
            int wet = 0, moved = 0, first = short.MaxValue, last = short.MinValue;
            foreach (var row in damp.Rows(TileStencil.Self | TileStencil.NorthSouth))
            {
                var c = damp.At(row);
                var here = c.Load128<byte>();
                var side = Vector128.Max(Vector128.Max(c.Load128<byte>(-1), c.Load128<byte>(1)),
                    Vector128.Max(c.Load128<byte>(0, 0, -1), c.Load128<byte>(0, 0, 1)));
                var level = Vector128.Max(here, Vector128.Max(side, Vector128<byte>.One) - Vector128<byte>.One);
                level.CopyTo(next.Slice(row.Index, 16));
                uint lanes = (~Vector128.Equals(level, Vector128<byte>.Zero)).ExtractMostSignificantBits();
                wet += BitOperations.PopCount(lanes);
                if (lanes != 0) (first, last) = (Math.Min(first, row.Y), Math.Max(last, row.Y));
                moved += BitOperations.PopCount((~Vector128.Equals(level, here)).ExtractMostSignificantBits());
            }
            tally = new DampTally { Wet = (ushort)wet, Moved = (ushort)moved, First = (short)first, Last = (short)last };
        }
    }
}
