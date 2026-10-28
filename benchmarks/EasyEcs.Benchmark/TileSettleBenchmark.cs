using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

// A field read only once settled (a reach, a scent): each cell the strongest of its four neighbors less one, sources
// pinned at Reach.Source, over the torus of 32 x 32 plates of 16 x 16 cells. Sources move a cell per update, so the
// field changes around them and stays settled elsewhere. Settled by [GaussSeidel] (in place, two colors, the plates
// that settled sleep) against the same sweep written by hand: a step per color (a generic system: a color tag cannot be
// an attribute's argument, CS8968) that copies the plate's texture onto the stack, sweeps it until it settles and writes
// the plate's cells into a second buffer, and a commit per color that copies them back.

public record struct Reach(int V)
{
    public const int Source = 24;
}

public record struct NextReach(int V);

/// <summary>A plate of the reach field: faces only, so two colors.</summary>
public partial record struct ReachPlot([LookupKey] Plot Pos) : IComponent, ITile<Plot>
{
    static int ITileShape.Width => 16;
    static int ITileShape.Depth => 16;
    static Plot ITile<Plot>.Beside(Plot p, TileFaces face) => p.Wrap(face);
}

/// <summary>The plates whose key's X + Z is even: they step first.</summary>
public struct ReachRed : IComponent;

/// <summary>The plates whose key's X + Z is odd: they step after the red ones, from their new edges.</summary>
public struct ReachBlack : IComponent;

[MemoryDiagnoser]
public class TileSettleBenchmark
{
    [Params(1, 4)] public int Threads;

    /// <summary>How many sources move: a few (most plates settled, asleep) or many (most plates change every update).</summary>
    [Params(4, 256)] public int Sources;

    private World _gaussSeidel, _hand;
    private Dictionary<Plot, Entity> _gsPlates, _handPlates;
    private int _tick;

    [GlobalSetup]
    public void Setup()
    {
        (_gaussSeidel, _gsPlates) = Make(Threads, hand: false);
        _gaussSeidel.AddSystem<TileSettleSystems.SettleSystem>();
        (_hand, _handPlates) = Make(Threads, hand: true);
        _hand.AddSystem<TileSettleSystems.HandStepSystem<ReachRed>>();
        _hand.AddSystem<TileSettleSystems.HandCommitSystem<ReachRed>>();
        _hand.AddSystem<TileSettleSystems.HandStepSystem<ReachBlack>>();
        _hand.AddSystem<TileSettleSystems.HandCommitSystem<ReachBlack>>();
        for (int i = 0; i < 64; i++)
        {
            GaussSeidel();
            _tick--;
            Hand();
        }
        var left = _gaussSeidel.Query<ReachPlot, DynamicBuffer<Reach>>().GetEnumerator();
        while (left.MoveNext())
            if (!left.Current.Component2.AsReadOnlySpan().SequenceEqual(_handPlates[left.Current.Component1.Pos].Read<DynamicBuffer<Reach>>().AsReadOnlySpan()))
                throw new InvalidOperationException($"the fields differ at {left.Current.Component1.Pos}");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _gaussSeidel.Dispose();
        _hand.Dispose();
    }

    [Benchmark(Baseline = true)]
    public void Hand()
    {
        Move(_handPlates, _tick++);
        _hand.Update();
    }

    [Benchmark]
    public void GaussSeidel()
    {
        Move(_gsPlates, _tick++);
        _gaussSeidel.Update();
    }

    private static (World, Dictionary<Plot, Entity>) Make(int threads, bool hand)
    {
        var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        var plates = new Dictionary<Plot, Entity>();
        for (int z = 0; z < Plot.Side; z++)
        for (int x = 0; x < Plot.Side; x++)
        {
            var plate = world.CreateEntity(new ReachPlot(new Plot(x, z)));
            plate.GetOrAdd<DynamicBuffer<Reach>>().SetCount(256);
            if (hand)
            {
                plate.GetOrAdd<DynamicBuffer<NextReach>>().SetCount(256);
                if (((x + z) & 1) == 0) plate.Set(new ReachRed());
                else plate.Set(new ReachBlack());
            }
            plates.Add(new Plot(x, z), plate);
        }
        return (world, plates);
    }

    /// <summary>Each source a cell further along its diagonal: the cell it left cleared, the one it reached pinned.</summary>
    private void Move(Dictionary<Plot, Entity> plates, int tick)
    {
        const int cells = Plot.Side * 16;
        int step = cells * cells / Sources;
        for (int s = 0; s < Sources; s++)
        {
            int at = s * step;
            Set(plates, (at % cells + tick) % cells, (at / cells + tick) % cells, 0);
            Set(plates, (at % cells + tick + 1) % cells, (at / cells + tick + 1) % cells, Reach.Source);
        }

        static void Set(Dictionary<Plot, Entity> plates, int x, int z, int v) =>
            plates[new Plot(x >> 4, z >> 4)].GetOrAdd<DynamicBuffer<Reach>>().AsSpan()[(z & 15) * 16 + (x & 15)] = new Reach(v);
    }
}

internal static class TileSettleSystems
{
    /// <summary>The rule: a source keeps its strength, any other cell the strongest of its neighbors less one.</summary>
    public static int Rule(int here, int north, int south, int west, int east) =>
        here == Reach.Source ? here : Math.Max(0, Math.Max(Math.Max(north, south), Math.Max(west, east)) - 1);

    [EcsSystem]
    public ref struct SettleSystem
    {
        [ForEach(Parallel = true, ChunkSize = 16), GaussSeidel]
        static void Settle(ref Tile<Reach, ReachPlot> reach)
        {
            reach.ExchangeHalo();
            foreach (var row in reach.Rows(TileStencil.Layer))
            {
                var c = reach.At(row);
                var into = reach.Write(row);
                for (int x = 0; x < 16; x++)
                    into[x] = new Reach(Rule(c[x, 0, 0].V, c[x, 0, -1].V, c[x, 0, 1].V, c[x - 1, 0, 0].V, c[x + 1, 0, 0].V));
            }
        }
    }

    [EcsSystem]
    public ref struct HandStepSystem<TColor> where TColor : struct, IComponent
    {
        const int Row = 18;

        [ForEach(Parallel = true, ChunkSize = 16)]
        static void Step(in TColor color, Tile<Reach, ReachPlot> reach, Mut<DynamicBuffer<NextReach>> next)
        {
            reach.ExchangeHalo(0, 0, mask: false);
            Span<int> field = stackalloc int[Row * Row];
            for (int z = -1; z <= 16; z++)
            for (int x = -1; x <= 16; x++)
                field[(z + 1) * Row + x + 1] = reach[x, 0, z].V;
            for (int sweep = 0; sweep < 256; sweep++)
            {
                bool changed = false;
                for (int z = 1; z <= 16; z++)
                for (int x = 1; x <= 16; x++)
                {
                    int at = z * Row + x, v = Rule(field[at], field[at - Row], field[at + Row], field[at - 1], field[at + 1]);
                    if (v == field[at]) continue;
                    field[at] = v;
                    changed = true;
                }
                if (!changed) break;
            }
            var into = next.Write().AsSpan();
            for (int z = 0; z < 16; z++)
            for (int x = 0; x < 16; x++)
                into[z * 16 + x] = new NextReach(field[(z + 1) * Row + x + 1]);
        }
    }

    [EcsSystem]
    public ref struct HandCommitSystem<TColor> where TColor : struct, IComponent
    {
        [ForEach(Parallel = true, ChunkSize = 64)]
        static void Commit(in TColor color, in DynamicBuffer<NextReach> next, Mut<DynamicBuffer<Reach>> cells)
        {
            var from = next.AsReadOnlySpan();
            var into = cells.Write().AsSpan();
            for (int i = 0; i < from.Length; i++) into[i] = new Reach(from[i].V);
        }
    }
}
