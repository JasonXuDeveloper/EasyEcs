using System;
using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// The full copies a snapshot still makes (borrowed-ideas.md item 5): a world of untracked components (80 bytes a row,
/// no change ticks), so a capture into a reused snapshot and a restore from it copy every row of every column whole
/// (Archetype.CopyWritten), the copies that remain after incremental Capture / Restore (backlog item 12d); <c>Grow</c>
/// resizes the table's columns twice (growth, then TrimExcess), each a copy of every row. 100,000
/// entities (8 MB) sit in the last-level cache, 4M (320 MiB) are past it (260 MiB on the reference VM). Checked in setup:
/// a restore brings back the captured values.
/// </summary>
[MemoryDiagnoser]
public class FullCopyBenchmark
{
    [Params(100_000, 4_000_000)] public int Entities;

    private World _world;
    private WorldSnapshot _snapshot;

    public record struct Body(float X, float Y, float Z, float W, float A, float B, float C, float D,
        float E, float F, float G, float H, float I, float J, float K, float L) : IComponent;

    public record struct Motion(float X, float Y, float Z, float W) : IComponent;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Entities });
        _world.Reserve<Body, Motion>(Entities);
        Entity probe = default;
        for (int i = 0; i < Entities; i++)
        {
            var e = _world.CreateEntity(new Body(i, i, i, i, i, i, i, i, i, i, i, i, i, i, i, i), new Motion(i, -i, i, -i));
            if (i == Entities / 2) probe = e;
        }

        _snapshot = _world.Capture();
        var kept = probe.Get<Body>();
        probe.Get<Body>().X = -1;
        _world.Restore(_snapshot);
        if (probe.Get<Body>() != kept) throw new InvalidOperationException("The restore lost a value");
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark]
    public void Capture() => _world.Capture(_snapshot);

    [Benchmark]
    public void Restore() => _world.Restore(_snapshot);

    /// <summary>Room for as many rows again (the columns grown: every row copied), then trimmed back (copied again).</summary>
    [Benchmark]
    public void Grow()
    {
        _world.Reserve<Body, Motion>(Entities);
        _world.TrimExcess();
    }
}
