using System;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// Lambda passes in one walk: two <c>world.ForEach</c> calls written one after the other on the same World, passing the
/// same values (the second may pass only names the first passes: the walk runs where the first is called), run as one walk (<c>Fused</c>), against the same calls with a statement between them, which keeps each
/// its own walk (<c>Apart</c>, the baseline). Per <see cref="Case"/>, over <see cref="Rows"/> units:
/// <list type="bullet">
/// <item><c>Movement</c>: integrate the velocity, then bounce off the rink's walls: two vector loops, one walk per block
/// of rows above the cache (below it each pass runs on its own, as a system's walk does).</item>
/// <item><c>MovementParallel</c>: the same lambdas as <c>ForEachParallel</c> calls: each chunk fused per block.</item>
/// </list>
/// The same computation either way (checked in setup). Allocates 0 bytes.
/// </summary>
[MemoryDiagnoser]
public class LambdaFusionBenchmark
{
    [Params(10_000, 1_000_000, 8_000_000)] public int Rows;

    [Params("Movement", "MovementParallel")] public string Case;

    private World _apart, _fused;
    private bool _parallel;

    public record struct Spot(float X, float Y) : IComponent;
    public record struct Push(float X, float Y) : IComponent;

    [GlobalSetup]
    public void Setup()
    {
        _parallel = Case == "MovementParallel";
        _apart = Populate();
        _fused = Populate();
        for (int i = 0; i < 3; i++)
        {
            Apart();
            Fused();
        }

        var fused = _fused.Read<Spot, Push>().GetEnumerator();
        foreach (var unit in _apart.Read<Spot, Push>())
            if (!fused.MoveNext() || fused.Current.Component1 != unit.Component1 || fused.Current.Component2 != unit.Component2)
                throw new InvalidOperationException($"{Case}: the fused walk differs from the passes apart");
    }

    private World Populate()
    {
        var world = new World(new WorldOptions { InitialEntityCapacity = Rows });
        world.Reserve<Spot, Push>(Rows);
        for (int i = 0; i < Rows; i++) world.CreateEntity(new Spot(i % 997, i % 991), new Push(i % 7 - 3, i % 5 - 2));
        return world;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _apart.Dispose();
        _fused.Dispose();
    }

    [Benchmark(Baseline = true)]
    public void Apart()
    {
        if (_parallel) MovementParallelApart(_apart, 1000);
        else MovementApart(_apart, 1000);
    }

    [Benchmark]
    public void Fused()
    {
        if (_parallel) MovementParallel(_fused, 1000);
        else Movement(_fused, 1000);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Between() { } // a statement between the calls: each runs its own walk

    private static void Movement(World world, float size)
    {
        world.ForEach(size, static (ref Spot p, in Push v, float size) => p = new Spot(p.X + v.X, p.Y + v.Y));
        world.ForEach(size, static (in Spot p, ref Push v, float size) =>
        {
            if (p.X < 0 || p.X > size) v = v with { X = -v.X };
            if (p.Y < 0 || p.Y > size) v = v with { Y = -v.Y };
        });
    }

    private static void MovementApart(World world, float size)
    {
        world.ForEach(size, static (ref Spot p, in Push v, float size) => p = new Spot(p.X + v.X, p.Y + v.Y));
        Between();
        world.ForEach(size, static (in Spot p, ref Push v, float size) =>
        {
            if (p.X < 0 || p.X > size) v = v with { X = -v.X };
            if (p.Y < 0 || p.Y > size) v = v with { Y = -v.Y };
        });
    }

    private static void MovementParallel(World world, float size)
    {
        world.ForEachParallel(size, static (ref Spot p, in Push v, float size) => p = new Spot(p.X + v.X, p.Y + v.Y));
        world.ForEachParallel(size, static (in Spot p, ref Push v, float size) =>
        {
            if (p.X < 0 || p.X > size) v = v with { X = -v.X };
            if (p.Y < 0 || p.Y > size) v = v with { Y = -v.Y };
        });
    }

    private static void MovementParallelApart(World world, float size)
    {
        world.ForEachParallel(size, static (ref Spot p, in Push v, float size) => p = new Spot(p.X + v.X, p.Y + v.Y));
        Between();
        world.ForEachParallel(size, static (in Spot p, ref Push v, float size) =>
        {
            if (p.X < 0 || p.X > size) v = v with { X = -v.X };
            if (p.Y < 0 || p.Y > size) v = v with { Y = -v.Y };
        });
    }
}
