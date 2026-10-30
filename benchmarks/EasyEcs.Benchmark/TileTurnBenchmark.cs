using System;
using System.Collections.Generic;
using System.Reflection;
using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

// Backlog 8c's open question: should Rows() walk the odd sweeps of a [GaussSeidel] step from the last row back by
// itself? A glow written with Rows() (its springs are cells of the field, so the rows the mask skips hold nothing to
// do): each cell the strongest of its neighbors less one, a spring its strength, settled in place from its last values
// while springs move. Forwards walks every sweep from the first row; Turned walks the odd sweeps from the last row back
// (the prototype's switch, TileRows.TurnOddSweeps, set per process by the [Params]). The two settle to one field.

public record struct Glow(int V)
{
    public const int Strength = 24, Spring = 1000;
}

/// <summary>A plate of the glow: faces only, so two colors.</summary>
public partial record struct GlowPlot([LookupKey] Plot Pos) : IComponent, ITile<Plot>
{
    static int ITileShape.Width => 16;
    static int ITileShape.Depth => 16;
    static Plot ITile<Plot>.Beside(Plot p, TileFaces face) => p.Wrap(face);
}

[MemoryDiagnoser]
public class TileTurnBenchmark
{
    [Params(1, 4)] public int Threads;

    /// <summary>How many springs move: a few (most plates settled, asleep) or many.</summary>
    [Params(4, 256)] public int Springs;

    /// <summary>The cells a spring moves per update: a step or a hop of 8.</summary>
    [Params(1, 8)] public int Hop;

    /// <summary>Odd sweeps from the last row back (the prototype's switch, one per process).</summary>
    [Params(false, true)] public bool Turned;

    private World _world;
    private Dictionary<Plot, Entity> _plates;
    private int _tick;

    [GlobalSetup]
    public void Setup()
    {
        Turn(Turned);
        _world = new World(new WorldOptions { MaxDegreeOfParallelism = Threads });
        _plates = new Dictionary<Plot, Entity>();
        _world.Reserve<GlowPlot>(Plot.Side * Plot.Side);
        for (int n = 0; n < Plot.Side * Plot.Side; n++)
        {
            int x = n % Plot.Side, z = n / Plot.Side;
            var plate = _world.CreateEntity(new GlowPlot(new Plot(x, z)));
            plate.GetOrAdd<DynamicBuffer<Glow>>().SetCount(256);
            _plates.Add(new Plot(x, z), plate);
        }
        _world.AddSystem<TileTurnSystems.GlowSystem>();
        for (int i = 0; i < 64; i++) Step();
        // The other walk settles to the same field: checked against a world walked the other way.
        Turn(!Turned);
        var other = new World(new WorldOptions { MaxDegreeOfParallelism = Threads });
        var plates = new Dictionary<Plot, Entity>();
        foreach (var (pos, plate) in _plates)
        {
            var copy = other.CreateEntity(new GlowPlot(pos));
            copy.GetOrAdd<DynamicBuffer<Glow>>().SetCount(256);
            plate.Read<DynamicBuffer<Glow>>().AsReadOnlySpan().CopyTo(copy.GetOrAdd<DynamicBuffer<Glow>>().AsSpan());
            plates.Add(pos, copy);
        }
        other.AddSystem<TileTurnSystems.GlowSystem>();
        Move(plates, _tick);
        other.Update();
        Turn(Turned);
        Step();
        foreach (var (pos, plate) in _plates)
            if (!plate.Read<DynamicBuffer<Glow>>().AsReadOnlySpan().SequenceEqual(plates[pos].Read<DynamicBuffer<Glow>>().AsReadOnlySpan()))
                throw new InvalidOperationException($"the walks settle to different fields at {pos}");
        other.Dispose();
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark]
    public void Step()
    {
        Move(_plates, _tick++);
        _world.Update();
    }

    private static void Turn(bool on) =>
        typeof(World).Assembly.GetType("EasyEcs.TileTurn")!.GetField("OddSweeps", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.SetValue(null, on);

    /// <summary>Each spring <see cref="Hop"/> cells further along its diagonal: the cell it left dark, the one it reached a spring.</summary>
    private void Move(Dictionary<Plot, Entity> plates, int tick)
    {
        int cells = Plot.Side * 16;
        int step = cells * cells / Springs;
        for (int s = 0; s < Springs; s++)
        {
            int at = s * step, from = tick * Hop, to = from + Hop;
            Set(plates, (at % cells + from) % cells, (at / cells + from) % cells, 0);
            Set(plates, (at % cells + to) % cells, (at / cells + to) % cells, Glow.Spring);
        }

        static void Set(Dictionary<Plot, Entity> plates, int x, int z, int v) =>
            plates[new Plot(x >> 4, z >> 4)].GetOrAdd<DynamicBuffer<Glow>>().AsSpan()[(z & 15) * 16 + (x & 15)] = new Glow(v);
    }
}

internal static class TileTurnSystems
{
    /// <summary>A spring stays; any other cell is the strongest of its neighbors less one (a spring counts its strength).</summary>
    public static int Rule(int self, int north, int south, int west, int east)
    {
        if (self >= Glow.Spring) return self;
        int most = Math.Max(Math.Max(Math.Min(north, Glow.Strength), Math.Min(south, Glow.Strength)),
            Math.Max(Math.Min(west, Glow.Strength), Math.Min(east, Glow.Strength)));
        return Math.Max(0, most - 1);
    }

    [EcsSystem]
    public ref struct GlowSystem
    {
        [ForEach(Parallel = true, ChunkSize = 16), GaussSeidel]
        static void Settle(ref Tile<Glow, GlowPlot> glow)
        {
            glow.ExchangeHalo();
            foreach (var row in glow.Rows(TileStencil.Layer))
            {
                var c = glow.At(row);
                var into = glow.Write(row);
                for (int x = 0; x < 16; x++)
                    into[x] = new Glow(Rule(c[x, 0, 0].V, c[x, 0, -1].V, c[x, 0, 1].V, c[x - 1, 0, 0].V, c[x + 1, 0, 0].V));
            }
        }
    }
}
