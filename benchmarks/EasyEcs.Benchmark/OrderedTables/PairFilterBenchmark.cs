using System;
using BenchmarkDotNet.Attributes;
using EasyEcs.Benchmark.Locality;

namespace EasyEcs.Benchmark;

/// <summary>
/// A pair method whose row filter passes few rows: hunters that lost their target look for the nearest quarry within
/// their own sight (a cutoff per row), the hunters still holding one skipped by <c>With&lt;Seeking&gt;</c> (a tag the
/// hunter table keeps a column of: losing and finding a target flip a presence bit). VoxelServer's zombie search as a
/// pair method (docs/plans/designs/voxel-rewrite.md section 11): about 11 of 4,000 zombies seek in a tick. Each tick
/// every body steps (forth, then back), <see cref="SeekPerMille"/> of the hunters seek (a new draw each tick: most
/// seekers find, others lose), then an update (the ordered tables' cadence) and the search. Each invocation runs 20
/// ticks (OperationsPerInvoke). Setup checks the search against every pair.
/// </summary>
[MemoryDiagnoser, IsaColumn]
public class PairFilterBenchmark
{
    private const int Ticks = 20;

    /// <summary>The hunters (as many quarries as half of them) and how many of each thousand seek in a tick.</summary>
    [ParamsSource(nameof(Cases))] public (int Hunters, int SeekPerMille) Case;

    /// <summary>VoxelServer's share (3 per mille), ten times it, every hunter; and the share at ten times the hunters.</summary>
    public static (int, int)[] Cases => [(4_000, 3), (4_000, 30), (4_000, 1000), (40_000, 3)];

    private int Hunters => Case.Hunters;
    private int SeekPerMille => Case.SeekPerMille;

    private World _world;
    private Entity[] _hunters;
    private Random _random;
    private int _tick;

    [GlobalSetup]
    public void Setup()
    {
        Isa.Print();
        _world = Seekers.Create(Hunters, out _hunters);
        _random = new Random(7);
        for (int t = 0; t < Ticks; t++) Tick();
        int at = Seekers.FirstMismatch(_world);
        if (at >= 0) throw new InvalidOperationException($"the search differs from every pair at hunter {at}");
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    /// <summary>Step the bodies, draw the seekers, update, search.</summary>
    private void Tick()
    {
        if (_tick++ % Ticks >= Ticks / 2) Seekers.StepBack(_world);
        else Seekers.StepForward(_world);
        Seekers.Lose(_world);
        int seek = Math.Max(1, (int)((long)Hunters * SeekPerMille / 1000));
        if (seek >= Hunters)
            foreach (var hunter in _hunters) hunter.Set(new Seekers.Seeking());
        else
            for (int i = 0; i < seek; i++) _hunters[_random.Next(Hunters)].Set(new Seekers.Seeking());
        _world.Update();
        Seekers.Acquire(_world);
    }

    [Benchmark(OperationsPerInvoke = Ticks)]
    public void Generated()
    {
        for (int t = 0; t < Ticks; t++) Tick();
    }
}

/// <summary>
/// The world of <see cref="PairFilterBenchmark"/>: hunters (a sight each, a target) and quarries on a square map, both
/// moving; the search of the hunters that seek as a pair method.
/// </summary>
public static class Seekers
{
    public record struct Spot(float X, float Y) : IComponent;
    public record struct Pace(float X, float Y) : IComponent;
    public record struct Sight(float Range) : IComponent;
    public record struct Target(EntityId Id, float Distance) : IComponent;
    public struct Hunter : IComponent;
    public struct Quarry : IComponent;

    /// <summary>A hunter without a target: what the search visits (a presence bit of the hunters' table).</summary>
    public struct Seeking : IComponent;

    /// <summary>How far a body moves per tick at full speed.</summary>
    public const float Step = 0.25f;

    /// <summary>A hunter's sight: 16 to 24 units.</summary>
    public const float Near = 16, Far = 24;

    /// <summary>
    /// <paramref name="hunters"/> hunters and half as many quarries spread uniformly over a map of about 25 quarries
    /// within a hunter's sight, velocities in [-1, 1]; every hunter holds a column of <see cref="Seeking"/>, absent.
    /// </summary>
    public static World Create(int hunters, out Entity[] all, int seed = 42)
    {
        int quarries = hunters / 2;
        float extent = MathF.Sqrt(quarries * MathF.PI * Near * Near / 25);
        var world = new World(new WorldOptions { InitialEntityCapacity = hunters + quarries + 16 });
        var random = new Random(seed);
        world.Reserve<Spot, Pace, Sight, Target, Hunter, Seeking>(hunters);
        world.Reserve<Spot, Pace, Quarry>(quarries);
        all = new Entity[hunters];
        for (int i = 0; i < hunters; i++)
        {
            all[i] = world.CreateEntity(Place(random, extent), Drift(random), new Sight(Near + random.NextSingle() * (Far - Near)),
                new Target(default, float.PositiveInfinity), new Hunter(), new Seeking());
            all[i].Remove<Seeking>();
        }

        for (int i = 0; i < quarries; i++) world.CreateEntity(Place(random, extent), Drift(random), new Quarry());
        return world;

        static Spot Place(Random random, float extent) => new(random.NextSingle() * extent, random.NextSingle() * extent);
        static Pace Drift(Random random) => new(random.NextSingle() * 2 - 1, random.NextSingle() * 2 - 1);
    }

    [ForEach]
    public static void StepForward(ref Spot p, in Pace v) => p = new Spot(p.X + v.X * Step, p.Y + v.Y * Step);

    [ForEach]
    public static void StepBack(ref Spot p, in Pace v) => p = new Spot(p.X - v.X * Step, p.Y - v.Y * Step);

    /// <summary>Last tick's seekers stop seeking (found or not), their targets dropped for the next draw.</summary>
    [ForEach, With<Seeking>]
    public static void Lose(Entity hunter, ref Target target)
    {
        target = new Target(default, float.PositiveInfinity);
        hunter.Remove<Seeking>();
    }

    /// <summary>The nearest quarry within the hunter's sight; ties to the lower entity index.</summary>
    [ForEach, With<Seeking>]
    public static void Acquire(in Spot p, in Sight sight, ref Target target, [Neighbor] in Spot q, [Neighbor] in Quarry _, [Neighbor] Entity other)
    {
        float dx = p.X - q.X, dy = p.Y - q.Y, d2 = dx * dx + dy * dy;
        if (d2 >= sight.Range * sight.Range || d2 > target.Distance || d2 == target.Distance && other.Id.Index >= target.Id.Index) return;
        target = new Target(other.Id, d2);
    }

    /// <summary>The first seeking hunter whose target differs from a walk over every quarry, or -1.</summary>
    public static int FirstMismatch(World world)
    {
        var quarries = new System.Collections.Generic.List<(EntityId Id, Spot At)>();
        foreach (var item in world.Query<Spot, Quarry>()) quarries.Add((item.Entity.Id, item.Component1));
        int at = 0;
        foreach (var item in world.Query<Spot, Sight, Target, Seeking>())
        {
            var p = item.Component1;
            float range2 = item.Component2.Range * item.Component2.Range, best = float.PositiveInfinity;
            EntityId found = default;
            foreach (var (id, q) in quarries)
            {
                float dx = p.X - q.X, dy = p.Y - q.Y, d2 = dx * dx + dy * dy;
                if (d2 >= range2 || d2 > best || d2 == best && id.Index >= found.Index) continue;
                best = d2;
                found = id;
            }

            if (item.Component3.Id != found) return at;
            at++;
        }

        return -1;
    }
}
