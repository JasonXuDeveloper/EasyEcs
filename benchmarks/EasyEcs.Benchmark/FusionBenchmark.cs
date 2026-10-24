using System;
using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// Pass fusion: two passes over the same rows as one walk, each row loaded into cache once. Per <see cref="Case"/>, the
/// same two methods run apart (<c>Apart</c>, the baseline: the second method has <c>FusionMode.Off</c>) and fused
/// (<c>Fused</c>), over <see cref="Rows"/> units:
/// <list type="bullet">
/// <item><c>Movement</c>: integrate the velocity, then bounce off the rink's walls: two vector loops, fused per block of
/// rows.</item>
/// <item><c>MovementParallel</c>: the same methods with <c>Parallel = true</c>: each chunk fused per block.</item>
/// <item><c>Vitals</c>: poison decays health, then health regenerates up to its maximum: two scalar methods, fused per
/// row.</item>
/// <item><c>Systems</c>: integrate and bounce as two systems, declared one after the other: the world runs them as one
/// walk.</item>
/// </list>
/// Above L2 (100,000 rows and more) the fused walk loads each row once instead of twice. Allocates 0 bytes.
/// </summary>
[MemoryDiagnoser]
public class FusionBenchmark
{
    [Params(10_000, 100_000, 1_000_000, 8_000_000)] public int Rows;

    [Params("Movement", "MovementParallel", "Vitals", "Systems")] public string Case;

    private World _apart, _fused;

    [GlobalSetup]
    public void Setup()
    {
        _apart = Populate();
        _fused = Populate();
        switch (Case)
        {
            case "Movement":
                _apart.AddSystem<MovementApart>();
                _fused.AddSystem<Movement>();
                break;
            case "MovementParallel":
                _apart.AddSystem<MovementParallelApart>();
                _fused.AddSystem<MovementParallel>();
                break;
            case "Vitals":
                _apart.AddSystem<VitalsApart>();
                _fused.AddSystem<Vitals>();
                break;
            default:
                _apart.AddSystem<IntegrateApart>();
                _apart.AddSystem<BounceApart>();
                _fused.AddSystem<Integrate>();
                _fused.AddSystem<Bounce>();
                break;
        }

        // Both runs are the same computation: check it once, then warm up.
        for (int i = 0; i < 3; i++)
        {
            Apart();
            Fused();
        }

        int at = 0;
        var fused = _fused.Read<Coords, Thrust, Wellness>().GetEnumerator();
        foreach (var unit in _apart.Read<Coords, Thrust, Wellness>())
        {
            if (!fused.MoveNext() || fused.Current.Component1 != unit.Component1 || fused.Current.Component2 != unit.Component2 || fused.Current.Component3 != unit.Component3)
                throw new InvalidOperationException($"{Case}: row {at} differs between the fused walk and the passes apart");
            at++;
        }

        if (!_fused.DescribeSchedule().ToString().Contains("one walk with") && Case != "Systems")
            throw new InvalidOperationException($"{Case}: the methods do not share one walk");
    }

    private World Populate()
    {
        var world = new World(new WorldOptions { InitialEntityCapacity = Rows });
        world.AddSingleton<Rink>() = new Rink(1000);
        for (int i = 0; i < Rows; i++)
            world.CreateEntity(new Coords(i % 997, i % 991), new Thrust(i % 7 - 3, i % 5 - 2), new Wellness(i % 100, 100), new Toxin(i % 3));
        return world;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _apart.Dispose();
        _fused.Dispose();
    }

    [Benchmark(Baseline = true)]
    public void Apart() => _apart.Update();

    [Benchmark]
    public void Fused() => _fused.Update();

    public record struct Coords(float X, float Y) : IComponent;
    public record struct Thrust(float X, float Y) : IComponent;
    public record struct Wellness(int Value, int Max) : IComponent;
    public record struct Toxin(int Dose) : IComponent;
    public record struct Rink(float Size) : ISingletonComponent;

    [EcsSystem]
    public ref struct Movement
    {
        [ForEach]
        static void Integrate(ref Coords p, in Thrust v) => p = new Coords(p.X + v.X, p.Y + v.Y);

        [ForEach]
        static void Bounce(in Coords p, ref Thrust v, in Rink rink)
        {
            if (p.X < 0 || p.X > rink.Size) v = v with { X = -v.X };
            if (p.Y < 0 || p.Y > rink.Size) v = v with { Y = -v.Y };
        }
    }

    [EcsSystem]
    public ref struct MovementApart
    {
        [ForEach]
        static void Integrate(ref Coords p, in Thrust v) => p = new Coords(p.X + v.X, p.Y + v.Y);

        [ForEach(FusionMode = FusionMode.Off)]
        static void Bounce(in Coords p, ref Thrust v, in Rink rink)
        {
            if (p.X < 0 || p.X > rink.Size) v = v with { X = -v.X };
            if (p.Y < 0 || p.Y > rink.Size) v = v with { Y = -v.Y };
        }
    }

    [EcsSystem]
    public ref struct MovementParallel
    {
        [ForEach(Parallel = true)]
        static void Integrate(ref Coords p, in Thrust v) => p = new Coords(p.X + v.X, p.Y + v.Y);

        [ForEach(Parallel = true)]
        static void Bounce(in Coords p, ref Thrust v, in Rink rink)
        {
            if (p.X < 0 || p.X > rink.Size) v = v with { X = -v.X };
            if (p.Y < 0 || p.Y > rink.Size) v = v with { Y = -v.Y };
        }
    }

    [EcsSystem]
    public ref struct MovementParallelApart
    {
        [ForEach(Parallel = true)]
        static void Integrate(ref Coords p, in Thrust v) => p = new Coords(p.X + v.X, p.Y + v.Y);

        [ForEach(Parallel = true, FusionMode = FusionMode.Off)]
        static void Bounce(in Coords p, ref Thrust v, in Rink rink)
        {
            if (p.X < 0 || p.X > rink.Size) v = v with { X = -v.X };
            if (p.Y < 0 || p.Y > rink.Size) v = v with { Y = -v.Y };
        }
    }

    [EcsSystem]
    public ref struct Vitals
    {
        [ForEach]
        static void Decay(ref Wellness hp, in Toxin poison)
        {
            if (poison.Dose > 0) hp = hp with { Value = hp.Value - poison.Dose * 2 };
        }

        [ForEach, With<Toxin>]
        static void Regenerate(ref Wellness hp) => hp = hp with { Value = Math.Min(hp.Max, hp.Value + 3) };
    }

    [EcsSystem]
    public ref struct VitalsApart
    {
        [ForEach]
        static void Decay(ref Wellness hp, in Toxin poison)
        {
            if (poison.Dose > 0) hp = hp with { Value = hp.Value - poison.Dose * 2 };
        }

        [ForEach(FusionMode = FusionMode.Off), With<Toxin>]
        static void Regenerate(ref Wellness hp) => hp = hp with { Value = Math.Min(hp.Max, hp.Value + 3) };
    }

    // Two systems, the second declared right after the first: one walk when they run next to each other.
    [EcsSystem]
    public ref struct Integrate
    {
        [ForEach]
        static void Run(ref Coords p, in Thrust v) => p = new Coords(p.X + v.X, p.Y + v.Y);
    }

    [EcsSystem]
    public ref struct Bounce
    {
        [ForEach]
        static void Run(in Coords p, ref Thrust v, in Rink rink)
        {
            if (p.X < 0 || p.X > rink.Size) v = v with { X = -v.X };
            if (p.Y < 0 || p.Y > rink.Size) v = v with { Y = -v.Y };
        }
    }

    [EcsSystem]
    public ref struct IntegrateApart
    {
        [ForEach]
        static void Run(ref Coords p, in Thrust v) => p = new Coords(p.X + v.X, p.Y + v.Y);
    }

    [EcsSystem]
    public ref struct BounceApart
    {
        [ForEach(FusionMode = FusionMode.Off)]
        static void Run(in Coords p, ref Thrust v, in Rink rink)
        {
            if (p.X < 0 || p.X > rink.Size) v = v with { X = -v.X };
            if (p.Y < 0 || p.Y > rink.Size) v = v with { Y = -v.Y };
        }
    }
}
