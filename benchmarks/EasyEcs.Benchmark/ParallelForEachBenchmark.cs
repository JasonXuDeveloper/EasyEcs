using System;
using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// One heavy per-entity system over 100,000 entities (steering: the nearest of 16 waypoints, then a step towards it),
/// as a plain [ForEach] and as [ForEach(Parallel = true)], whose rows run in chunks on several threads; and the same
/// work as an ad-hoc job that receives each entity's handle (<c>ForEachEntity</c> / <c>ForEachEntityParallel</c>).
/// Steady state must allocate 0 bytes per tick either way.
/// </summary>
[MemoryDiagnoser]
public class ParallelForEachBenchmark
{
    [Params(100_000)] public int Entities;
    [Params(false, true)] public bool Parallel;

    private World _world;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Entities });
        var waypoints = new Waypoints();
        for (int i = 0; i < Waypoints.Count; i++) waypoints[i] = (MathF.Cos(i) * 5_000, MathF.Sin(i) * 5_000);
        _world.AddSingleton<Waypoints>() = waypoints;
        _world.CreateEntities(new Entity[Entities], new Agent(0, 0, 1, 0));
        var entity = 0;
        foreach (var item in _world.Query<Agent>()) item.Component1 = new Agent(entity % 10_000, entity++ / 10, 1, 0);
        if (Parallel) _world.AddSystem<SteerInParallel>();
        else _world.AddSystem<Steer>();
        for (int i = 0; i < 20; i++) _world.Update();
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark]
    public void Tick() => _world.Update();

    /// <summary>The steering as a struct job with entity handles, outside systems (tools, request handlers).</summary>
    [Benchmark]
    public void EntityJob()
    {
        var job = new SteerEntities { Waypoints = _world.GetSingleton<Waypoints>() };
        if (Parallel) _world.ForEachEntityParallel<Agent, SteerEntities>(ref job);
        else _world.ForEachEntity<Agent, SteerEntities>(ref job);
    }

    public record struct Agent(float X, float Y, float Vx, float Vy) : IComponent;

    public struct Waypoints : ISingletonComponent
    {
        public const int Count = 16;
        private WaypointArray _points;

        public (float X, float Y) this[int i]
        {
            readonly get => _points[i];
            set => _points[i] = value;
        }
    }

    [System.Runtime.CompilerServices.InlineArray(Waypoints.Count)]
    public struct WaypointArray
    {
        private (float X, float Y) _first;
    }

    internal static void SteerAgent(ref Agent agent, in Waypoints waypoints)
    {
        float best = float.MaxValue, tx = 0, ty = 0;
        for (int i = 0; i < Waypoints.Count; i++)
        {
            var (x, y) = waypoints[i];
            float d = (x - agent.X) * (x - agent.X) + (y - agent.Y) * (y - agent.Y);
            if (d < best) (best, tx, ty) = (d, x, y);
        }

        float length = MathF.Sqrt(best) + 1e-3f;
        agent.Vx = (tx - agent.X) / length;
        agent.Vy = (ty - agent.Y) / length;
        agent.X += agent.Vx;
        agent.Y += agent.Vy;
    }

    private struct SteerEntities : IForEachEntity<Agent>, INoStructuralChanges
    {
        public Waypoints Waypoints;

        public void Execute(Entity entity, ref Agent agent)
        {
            SteerAgent(ref agent, in Waypoints);
            if (float.IsNaN(agent.X)) Diverged(entity); // the handle is built for every row, used on a rare branch
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void Diverged(Entity entity) => throw new InvalidOperationException($"{entity} diverged");

    public class Steer : EcsSystem
    {
        [ForEach]
        static void Run(ref Agent agent, in Waypoints waypoints) => SteerAgent(ref agent, in waypoints);
    }

    public class SteerInParallel : EcsSystem
    {
        [ForEach(Parallel = true)]
        static void Run(ref Agent agent, in Waypoints waypoints) => SteerAgent(ref agent, in waypoints);
    }
}
