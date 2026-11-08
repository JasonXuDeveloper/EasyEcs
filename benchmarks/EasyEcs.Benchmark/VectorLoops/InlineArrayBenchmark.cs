using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// Loops over inline arrays inside a [ForEach] method for one row (inner-loops.md WP1): the loop unrolled into the
/// method's vector loop, each element a lane of the row. As written (Scalar: SimdMode.Off), as the generator's vector
/// loop of the same method (Generated) and, for <c>Spectrum</c>, as the Vector&lt;float&gt; loop written by hand over the
/// columns cast to floats (HandWritten). <c>Spectrum</c>: eight gains of a row faded and fed from another component's
/// eight (each element alike: a flat loop over rows * 8 floats). <c>Mix</c>: four gains folded into a running sum, in
/// order, two of them rewritten (elements used apart: one vector per element). <c>Nearest</c>: the nearest of eight
/// waypoints of a value of the pass past a minimum distance, ties to the first (BattleServer's planning: an argmin
/// carried from step to step, each waypoint broadcast once per run).
/// </summary>
[MemoryDiagnoser]
public class InlineArrayBenchmark
{
    [InlineArray(8)] public struct Eight { private float _g; }
    [InlineArray(4)] public struct Four { private float _g; }
    public struct Gains : IComponent { public Eight G; }
    public struct Feeds : IComponent { public Eight G; }
    public struct Quad : IComponent { public Four G; }
    public record struct Level(float Value) : IComponent;
    public record struct Spot(float X, float Y) : IComponent;
    public record struct Aim(int Index) : IComponent;
    public struct Waypoint { public float X, Y; }
    [InlineArray(8)] public struct Waypoints { private Waypoint _w; }
    public struct Route { public Waypoints Points; }

    [Params(1_000, 100_000)] public int Entities;

    private World _world;
    private Route _route;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Entities });
        _world.Reserve<Gains, Feeds, Quad, Level, Spot, Aim>(Entities);
        var random = new Random(3);
        for (int n = 0; n < Entities; n++)
        {
            Gains g = default;
            Feeds f = default;
            Quad q = default;
            for (int k = 0; k < 8; k++) (g.G[k], f.G[k]) = (random.Next(-100, 100) * 0.25f, random.Next(-100, 100) * 0.125f);
            for (int k = 0; k < 4; k++) q.G[k] = random.Next(-100, 100) * 0.25f;
            _world.CreateEntity(g, f, q, new Level(random.Next(-8, 8)), new Spot(random.Next(-500, 500), random.Next(-500, 500)), new Aim(-1));
        }

        for (int k = 0; k < 8; k++) _route.Points[k] = new Waypoint { X = random.Next(-500, 500), Y = random.Next(-500, 500) };
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark(Baseline = true)] public void SpectrumScalar() => ArrayPasses.SpectrumScalar(_world, 0.5f);
    [Benchmark] public void SpectrumGenerated() => ArrayPasses.SpectrumGenerated(_world, 0.5f);
    [Benchmark] public void SpectrumHandWritten() => ArrayPasses.SpectrumHandWritten(_world, 0.5f);
    [Benchmark] public void MixScalar() => ArrayPasses.MixScalar(_world);
    [Benchmark] public void MixGenerated() => ArrayPasses.MixGenerated(_world);
    [Benchmark] public void NearestScalar() => ArrayPasses.NearestScalar(_world, _route);
    [Benchmark] public void NearestGenerated() => ArrayPasses.NearestGenerated(_world, _route);
}

/// <summary>The passes of <see cref="InlineArrayBenchmark"/>: each method twice, row by row and as the vector loop.</summary>
public static class ArrayPasses
{
    [ForEach(SimdMode = SimdMode.Off)]
    internal static void SpectrumScalar(ref InlineArrayBenchmark.Gains s, in InlineArrayBenchmark.Feeds by, float fade)
    {
        for (int b = 0; b < 8; b++) s.G[b] = s.G[b] * fade + by.G[b];
    }

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void SpectrumGenerated(ref InlineArrayBenchmark.Gains s, in InlineArrayBenchmark.Feeds by, float fade)
    {
        for (int b = 0; b < 8; b++) s.G[b] = s.G[b] * fade + by.G[b];
    }

#pragma warning disable EECS103 // the hand-written loop is what this measures against
    [ForEach]
    internal static void SpectrumHandWritten(Span<InlineArrayBenchmark.Gains> s, ReadOnlySpan<InlineArrayBenchmark.Feeds> by, float fade)
    {
        var to = MemoryMarshal.Cast<InlineArrayBenchmark.Gains, float>(s);
        var from = MemoryMarshal.Cast<InlineArrayBenchmark.Feeds, float>(by);
        var scale = new Vector<float>(fade);
        int i = 0;
        for (; i <= to.Length - Vector<float>.Count; i += Vector<float>.Count)
            (new Vector<float>(to[i..]) * scale + new Vector<float>(from[i..])).CopyTo(to[i..]);
        for (; i < to.Length; i++) to[i] = to[i] * fade + from[i];
    }
#pragma warning restore EECS103

    [ForEach(SimdMode = SimdMode.Off)]
    internal static void MixScalar(ref InlineArrayBenchmark.Quad q, in InlineArrayBenchmark.Level l)
    {
        float sum = l.Value;
        for (int k = 0; k < 4; k++) sum = sum * 0.5f + q.G[k];
        q.G[0] = sum;
        q.G[3] = q.G[1] - q.G[2] * l.Value;
    }

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void MixGenerated(ref InlineArrayBenchmark.Quad q, in InlineArrayBenchmark.Level l)
    {
        float sum = l.Value;
        for (int k = 0; k < 4; k++) sum = sum * 0.5f + q.G[k];
        q.G[0] = sum;
        q.G[3] = q.G[1] - q.G[2] * l.Value;
    }

    [ForEach(SimdMode = SimdMode.Off)]
    internal static void NearestScalar(in InlineArrayBenchmark.Spot p, ref InlineArrayBenchmark.Aim aim, in InlineArrayBenchmark.Route route)
    {
        int next = -1;
        float nearest = float.MaxValue;
        for (int i = 0; i < 8; i++)
        {
            float dx = route.Points[i].X - p.X, dy = route.Points[i].Y - p.Y, d = dx * dx + dy * dy;
            if (d > 64 * 64 && d < nearest) (nearest, next) = (d, i);
        }
        aim = new InlineArrayBenchmark.Aim(next);
    }

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void NearestGenerated(in InlineArrayBenchmark.Spot p, ref InlineArrayBenchmark.Aim aim, in InlineArrayBenchmark.Route route)
    {
        int next = -1;
        float nearest = float.MaxValue;
        for (int i = 0; i < 8; i++)
        {
            float dx = route.Points[i].X - p.X, dy = route.Points[i].Y - p.Y, d = dx * dx + dy * dy;
            if (d > 64 * 64 && d < nearest) (nearest, next) = (d, i);
        }
        aim = new InlineArrayBenchmark.Aim(next);
    }
}
