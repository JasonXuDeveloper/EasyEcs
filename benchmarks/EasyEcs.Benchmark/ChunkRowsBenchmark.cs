using System;
using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// The chunk size the runtime picks for a parallel [ForEach] that does not set ChunkRows: one parallel system over 1,000
/// to 1,000,000 entities with a light body (a vector loop: a position moved by its velocity), a heavy one (steering
/// towards the nearest of 16 waypoints, scalar) and an uneven one (an eighth of the rows, all in one place, cost 64
/// times the others), and an expensive one (a thousandth of the entities, 1 to 1,000 rows of tens of microseconds
/// each: a chunk of terrain each, a path search each). Small passes show what chunks cost, large and uneven ones how they balance. Steady state
/// allocates 0 bytes.
/// </summary>
[MemoryDiagnoser]
public class ChunkRowsBenchmark
{
    public enum Body { Light, Heavy, Uneven, Expensive }

    [Params(1_000, 4_000, 8_000, 10_000, 100_000, 1_000_000)] public int Entities;
    [Params(Body.Light, Body.Heavy, Body.Uneven, Body.Expensive)] public Body Kind;

    private World _world;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Entities });
        switch (Kind)
        {
            case Body.Light:
                _world.CreateEntities(new Entity[Entities], new Position(0, 0), new Velocity(1, 0.5f));
                _world.AddSystem<Move>();
                break;
            case Body.Heavy:
                var waypoints = new ParallelForEachBenchmark.Waypoints();
                for (int i = 0; i < ParallelForEachBenchmark.Waypoints.Count; i++) waypoints[i] = (MathF.Cos(i) * 5_000, MathF.Sin(i) * 5_000);
                _world.AddSingleton<ParallelForEachBenchmark.Waypoints>() = waypoints;
                _world.CreateEntities(new Entity[Entities], new ParallelForEachBenchmark.Agent(0, 0, 1, 0));
                int n = 0;
                foreach (var item in _world.Query<ParallelForEachBenchmark.Agent>())
                    item.Component1 = new ParallelForEachBenchmark.Agent(n % 10_000, n++ / 10, 1, 0);
                _world.AddSystem<Steer>();
                break;
            case Body.Uneven:
                _world.CreateEntities(new Entity[Entities], new Hash(0, 1));
                int row = 0;
                foreach (var item in _world.Query<Hash>()) item.Component1 = new Hash((uint)row, row++ < Entities / 8 ? 64 : 1);
                _world.AddSystem<Mix>();
                break;
            default:
                _world.CreateEntities(new Entity[Entities / 1_000], new Hash(7, 1_000));
                _world.AddSystem<Mix>();
                break;
        }

        for (int i = 0; i < 20; i++) _world.Update();
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark]
    public void Tick() => _world.Update();

    public record struct Position(float X, float Y) : IComponent;
    public record struct Velocity(float X, float Y) : IComponent;

    /// <summary>A value mixed <c>Rounds</c> times: the cost of a row is its rounds.</summary>
    public record struct Hash(uint Value, int Rounds) : IComponent;

    [EcsSystem]
    public ref struct Move
    {
        [ForEach(Parallel = true)]
        static void Run(ref Position p, in Velocity v)
        {
            p.X += v.X;
            p.Y += v.Y;
        }
    }

    [EcsSystem]
    public ref struct Steer
    {
        [ForEach(Parallel = true)]
        static void Run(ref ParallelForEachBenchmark.Agent agent, in ParallelForEachBenchmark.Waypoints waypoints) =>
            ParallelForEachBenchmark.SteerAgent(ref agent, in waypoints);
    }

    [EcsSystem]
    public ref struct Mix
    {
        [ForEach(Parallel = true)]
        static void Run(ref Hash hash)
        {
            uint v = hash.Value;
            for (int i = 0; i < hash.Rounds * 16; i++) v = (v ^ (v >> 15)) * 0x2C1B3C6Du;
            hash.Value = v;
        }
    }
}
