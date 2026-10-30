using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

// A scent read only once settled (VoxelServer's zombies follow one): Odor.Strength on a spring, one less per cell away,
// 0 past its reach, over the torus of 32 x 32 plates of 16 x 16 cells. The springs are a second buffer beside the field
// and move a cell or a hop per update, so the scent they leave behind must go. Settled by [GaussSeidel] from nothing in
// every step (Restart), the same with its odd sweeps from the last row back (Turn: tile.Sweep), by [GaussSeidel] from
// the field's last values (Linger: what a spring left only fades a step per sweep), and by
// hand as the sample does it: a step per color (a generic system: a color tag cannot be an attribute's argument, CS8968)
// that copies the plate's halo onto the stack, its own cells cleared, sweeps until it settles and writes the plate's
// cells into a second buffer, and a commit per color that copies them back.

public record struct Odor(int V)
{
    public const int Strength = 24;
}

public record struct NextOdor(int V);

public record struct OdorSpring(byte On);

/// <summary>A plate of the scent: faces only, so two colors.</summary>
public partial record struct OdorPlot([LookupKey] Plot Pos) : IComponent, ITile<Plot>
{
    static int ITileShape.Width => 16;
    static int ITileShape.Depth => 16;
    static Plot ITile<Plot>.Beside(Plot p, TileFaces face) => p.Wrap(face);
}

/// <summary>The plates whose key's X + Z is even: they step first.</summary>
public struct OdorRed : IComponent;

/// <summary>The plates whose key's X + Z is odd: they step after the red ones, from their new edges.</summary>
public struct OdorBlack : IComponent;

[MemoryDiagnoser]
public class TileRestartBenchmark
{
    [Params(1, 4)] public int Threads;

    /// <summary>How many springs move: a few (most plates settled, asleep) or many (most plates change every update).</summary>
    [Params(4, 256)] public int Springs;

    /// <summary>The cells a spring moves per update: a step (its old scent still nearly right) or a hop of 8 (gone).</summary>
    [Params(1, 8)] public int Hop;

    private World _restart, _turn, _linger, _hand;
    private Dictionary<Plot, Entity> _restartPlates, _turnPlates, _lingerPlates, _handPlates;
    private int _tick;

    [GlobalSetup]
    public void Setup()
    {
        (_restart, _restartPlates) = Make(Threads, hand: false);
        _restart.AddSystem<TileRestartSystems.RestartSystem>();
        (_turn, _turnPlates) = Make(Threads, hand: false);
        _turn.AddSystem<TileRestartSystems.TurnSystem>();
        (_linger, _lingerPlates) = Make(Threads, hand: false);
        _linger.AddSystem<TileRestartSystems.LingerSystem>();
        (_hand, _handPlates) = Make(Threads, hand: true);
        _hand.AddSystem<TileRestartSystems.HandStepSystem<OdorRed>>();
        _hand.AddSystem<TileRestartSystems.HandCommitSystem<OdorRed>>();
        _hand.AddSystem<TileRestartSystems.HandStepSystem<OdorBlack>>();
        _hand.AddSystem<TileRestartSystems.HandCommitSystem<OdorBlack>>();
        for (int i = 0; i < 64; i++)
        {
            Restart();
            _tick--;
            Turn();
            _tick--;
            Linger();
            _tick--;
            Hand();
        }
        // Settled from the same springs, the three are one field (a settled scent is the strength less the steps).
        for (int i = 0; i < 2 * Plot.Side; i++)
        {
            _restart.Update();
            _turn.Update();
            _linger.Update();
            _hand.Update();
        }
        var left = _restart.Query<OdorPlot, DynamicBuffer<Odor>>().GetEnumerator();
        while (left.MoveNext())
        {
            var cells = left.Current.Component2.AsReadOnlySpan();
            var pos = left.Current.Component1.Pos;
            if (!cells.SequenceEqual(_handPlates[pos].Read<DynamicBuffer<Odor>>().AsReadOnlySpan())
                || !cells.SequenceEqual(_turnPlates[pos].Read<DynamicBuffer<Odor>>().AsReadOnlySpan())
                || !cells.SequenceEqual(_lingerPlates[pos].Read<DynamicBuffer<Odor>>().AsReadOnlySpan()))
                throw new InvalidOperationException($"the fields differ at {pos}");
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _restart.Dispose();
        _turn.Dispose();
        _linger.Dispose();
        _hand.Dispose();
    }

    [Benchmark(Baseline = true)]
    public void Hand()
    {
        Move(_handPlates, _tick++);
        _hand.Update();
    }

    [Benchmark]
    public void Restart()
    {
        Move(_restartPlates, _tick++);
        _restart.Update();
    }

    [Benchmark]
    public void Turn()
    {
        Move(_turnPlates, _tick++);
        _turn.Update();
    }

    [Benchmark]
    public void Linger()
    {
        Move(_lingerPlates, _tick++);
        _linger.Update();
    }

    private static (World, Dictionary<Plot, Entity>) Make(int threads, bool hand)
    {
        var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        var plates = new Dictionary<Plot, Entity>();
        world.Reserve<OdorPlot>(Plot.Side * Plot.Side);
        for (int n = 0; n < Plot.Side * Plot.Side; n++)
        {
            int x = n % Plot.Side, z = n / Plot.Side;
            var plate = world.CreateEntity(new OdorPlot(new Plot(x, z)));
            plate.GetOrAdd<DynamicBuffer<Odor>>().SetCount(256);
            plate.GetOrAdd<DynamicBuffer<OdorSpring>>().SetCount(256);
            if (hand)
            {
                plate.GetOrAdd<DynamicBuffer<NextOdor>>().SetCount(256);
                if (((x + z) & 1) == 0) plate.Set(new OdorRed());
                else plate.Set(new OdorBlack());
            }
            plates.Add(new Plot(x, z), plate);
        }
        return (world, plates);
    }

    /// <summary>Each spring <see cref="Hop"/> cells further along its diagonal: the cell it left dry, the one it reached a spring.</summary>
    private void Move(Dictionary<Plot, Entity> plates, int tick)
    {
        int cells = Plot.Side * 16;
        int step = cells * cells / Springs;
        for (int s = 0; s < Springs; s++)
        {
            int at = s * step, from = tick * Hop, to = from + Hop;
            Set(plates, (at % cells + from) % cells, (at / cells + from) % cells, 0);
            Set(plates, (at % cells + to) % cells, (at / cells + to) % cells, 1);
        }

        static void Set(Dictionary<Plot, Entity> plates, int x, int z, byte on) =>
            plates[new Plot(x >> 4, z >> 4)].GetOrAdd<DynamicBuffer<OdorSpring>>().AsSpan()[(z & 15) * 16 + (x & 15)] = new OdorSpring(on);
    }
}

internal static class TileRestartSystems
{
    /// <summary>The rule: a spring's strength, any other cell the strongest of its neighbors less one.</summary>
    public static int Rule(bool spring, int north, int south, int west, int east) =>
        spring ? Odor.Strength : Math.Max(0, Math.Max(Math.Max(north, south), Math.Max(west, east)) - 1);

    [EcsSystem]
    public ref struct RestartSystem
    {
        [ForEach(Parallel = true, ChunkSize = 16), GaussSeidel]
        static void Settle(ref Tile<Odor, OdorPlot> odor, Tile<OdorSpring, OdorPlot> springs)
        {
            springs.ExchangeHalo(0, 0, mask: false);
            odor.ExchangeHalo();
            odor.Restart();
            for (int z = 0; z < 16; z++) // a source term: every row
            {
                var row = new TileRow<OdorPlot>(0, z);
                var c = odor.At(row);
                var s = springs.At(row);
                var into = odor.Write(row);
                for (int x = 0; x < 16; x++)
                    into[x] = new Odor(Rule(s[x, 0, 0].On != 0, c[x, 0, -1].V, c[x, 0, 1].V, c[x - 1, 0, 0].V, c[x + 1, 0, 0].V));
            }
        }
    }

    [EcsSystem]
    public ref struct TurnSystem
    {
        [ForEach(Parallel = true, ChunkSize = 16), GaussSeidel]
        static void Settle(ref Tile<Odor, OdorPlot> odor, Tile<OdorSpring, OdorPlot> springs)
        {
            springs.ExchangeHalo(0, 0, mask: false);
            odor.ExchangeHalo();
            odor.Restart();
            for (int k = 0; k < 16; k++) // odd sweeps from the last row back: the scent crosses the plate both ways
            {
                var row = new TileRow<OdorPlot>(0, (odor.Sweep & 1) == 0 ? k : 15 - k);
                var c = odor.At(row);
                var s = springs.At(row);
                var into = odor.Write(row);
                for (int x = 0; x < 16; x++)
                    into[x] = new Odor(Rule(s[x, 0, 0].On != 0, c[x, 0, -1].V, c[x, 0, 1].V, c[x - 1, 0, 0].V, c[x + 1, 0, 0].V));
            }
        }
    }

    [EcsSystem]
    public ref struct LingerSystem
    {
        [ForEach(Parallel = true, ChunkSize = 16), GaussSeidel]
        static void Settle(ref Tile<Odor, OdorPlot> odor, Tile<OdorSpring, OdorPlot> springs)
        {
            springs.ExchangeHalo(0, 0, mask: false);
            odor.ExchangeHalo();
            for (int z = 0; z < 16; z++)
            {
                var row = new TileRow<OdorPlot>(0, z);
                var c = odor.At(row);
                var s = springs.At(row);
                var into = odor.Write(row);
                for (int x = 0; x < 16; x++)
                    into[x] = new Odor(Rule(s[x, 0, 0].On != 0, c[x, 0, -1].V, c[x, 0, 1].V, c[x - 1, 0, 0].V, c[x + 1, 0, 0].V));
            }
        }
    }

    [EcsSystem]
    public ref struct HandStepSystem<TColor> where TColor : struct, IComponent
    {
        const int Row = 18;

        [ForEach(Parallel = true, ChunkSize = 16)]
        static void Step(in TColor color, Tile<Odor, OdorPlot> odor, in DynamicBuffer<OdorSpring> springs, Mut<DynamicBuffer<NextOdor>> next)
        {
            odor.ExchangeHalo(0, 0, mask: false);
            Span<int> field = stackalloc int[Row * Row];
            for (int z = -1; z <= 16; z++)
            for (int x = -1; x <= 16; x++)
                field[(z + 1) * Row + x + 1] = x is >= 0 and < 16 && z is >= 0 and < 16 ? 0 : odor[x, 0, z].V; // its own from nothing
            var on = springs.AsReadOnlySpan();
            for (int sweep = 0; sweep < 256; sweep++)
            {
                bool changed = false;
                for (int z = 1; z <= 16; z++)
                for (int x = 1; x <= 16; x++)
                {
                    int at = z * Row + x;
                    int v = Rule(on[(z - 1) * 16 + x - 1].On != 0, field[at - Row], field[at + Row], field[at - 1], field[at + 1]);
                    if (v == field[at]) continue;
                    field[at] = v;
                    changed = true;
                }
                if (!changed) break;
            }
            var into = next.Write().AsSpan();
            for (int z = 0; z < 16; z++)
            for (int x = 0; x < 16; x++)
                into[z * 16 + x] = new NextOdor(field[(z + 1) * Row + x + 1]);
        }
    }

    [EcsSystem]
    public ref struct HandCommitSystem<TColor> where TColor : struct, IComponent
    {
        [ForEach(Parallel = true, ChunkSize = 64)]
        static void Commit(in TColor color, in DynamicBuffer<NextOdor> next, Mut<DynamicBuffer<Odor>> cells)
        {
            var from = next.AsReadOnlySpan();
            var into = cells.Write().AsSpan();
            for (int i = 0; i < from.Length; i++) into[i] = new Odor(from[i].V);
        }
    }
}
