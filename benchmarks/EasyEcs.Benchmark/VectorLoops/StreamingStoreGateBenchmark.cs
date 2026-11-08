using System;
using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// The generated loops' streaming stores (StreamingStores, microarch.md candidate 8) in a world, the gate as the machine
/// decides it (Auto), forced off and forced on (PrefetchGate: the last-level cache taken as unknown or as 1 byte).
/// <c>Predict</c>: a pass writing a column it never reads (<c>next = position + velocity * dt</c>, three floats per row,
/// the flat loop), then the next pass reading that column (<c>Settle</c>) in the same operation, since a streamed column
/// is out of the cache when the next pass reads it. <c>Mix</c>: one float written from four (the loop over whole
/// vectors), and its reader. At 1M rows the passes' columns are in this machine's last-level cache (36 MB and 20 MB of
/// 260 MB); at 16M rows past it (576 MB and 320 MB). Base / head runs: EASYECS_BENCH_GATES=auto.
/// </summary>
[MemoryDiagnoser]
public class StreamingStoreGateBenchmark
{
    public record struct Pos3(float X, float Y, float Z) : IComponent;
    public record struct Vel3(float X, float Y, float Z) : IComponent;
    public record struct Next3(float X, float Y, float Z) : IComponent;
    public record struct Gain(float Value) : IComponent;
    public record struct Bias(float Value) : IComponent;
    public record struct Rate(float Value) : IComponent;
    public record struct Load(float Value) : IComponent;
    public record struct Out(float Value) : IComponent;

    [Params(1 << 20, 1 << 24)] public int Rows;

    [ParamsSource(typeof(PrefetchGate), nameof(PrefetchGate.Gates))] public PrefetchGate.Gate Stream;

    private World _moving, _mixing;
    private PrefetchGate _gate;

    [GlobalSetup]
    public void Setup()
    {
        _gate = PrefetchGate.Set(Stream);
        _moving = new World(new WorldOptions { InitialEntityCapacity = Rows });
        var entities = new Entity[Rows];
        _moving.CreateEntities(entities, new Pos3(1, 2, 3), new Vel3(0.5f, -0.25f, 1), new Next3());
        _mixing = new World(new WorldOptions { InitialEntityCapacity = Rows });
        _mixing.CreateEntities(entities, new Gain(2), new Bias(1), new Rate(0.5f), new Load(3), new Out());
        Predict();
        Mix();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _moving.Dispose();
        _mixing.Dispose();
        _gate.Dispose();
    }

    /// <summary>The write-only pass and the pass reading its column.</summary>
    [Benchmark]
    public void Predict()
    {
        StreamPasses.Predict(_moving);
        StreamPasses.Settle(_moving);
    }

    /// <summary>The write-only pass alone.</summary>
    [Benchmark]
    public void PredictOnly() => StreamPasses.Predict(_moving);

    /// <summary>One float written from four, and the pass reading it.</summary>
    [Benchmark]
    public void Mix()
    {
        StreamPasses.Blend(_mixing);
        StreamPasses.Drain(_mixing);
    }

    /// <summary>The write-only pass alone.</summary>
    [Benchmark]
    public void BlendOnly() => StreamPasses.Blend(_mixing);
}

/// <summary>The passes of <see cref="StreamingStoreGateBenchmark"/>.</summary>
public static class StreamPasses
{
    private const float Dt = 1f / 30;

    [ForEach]
    internal static void Predict(in StreamingStoreGateBenchmark.Pos3 p, in StreamingStoreGateBenchmark.Vel3 v, ref StreamingStoreGateBenchmark.Next3 next) =>
        next = new StreamingStoreGateBenchmark.Next3(p.X + v.X * Dt, p.Y + v.Y * Dt, p.Z + v.Z * Dt);

    // Reads the predicted column: velocities bounce off a box.
    [ForEach]
    internal static void Settle(in StreamingStoreGateBenchmark.Next3 next, ref StreamingStoreGateBenchmark.Vel3 v) =>
        v = new StreamingStoreGateBenchmark.Vel3(next.X > 100f ? -v.X : v.X, next.Y > 100f ? -v.Y : v.Y, next.Z > 100f ? -v.Z : v.Z);

    [ForEach]
    internal static void Blend(in StreamingStoreGateBenchmark.Gain g, in StreamingStoreGateBenchmark.Bias b, in StreamingStoreGateBenchmark.Rate r,
        in StreamingStoreGateBenchmark.Load l, ref StreamingStoreGateBenchmark.Out o) => o.Value = g.Value * l.Value + b.Value * r.Value;

    // Reads the blended column into a load it accumulates.
    [ForEach]
    internal static void Drain(in StreamingStoreGateBenchmark.Out o, ref StreamingStoreGateBenchmark.Load l) =>
        l.Value = MathF.Min(l.Value + o.Value * 0.001f, 1000f);
}
