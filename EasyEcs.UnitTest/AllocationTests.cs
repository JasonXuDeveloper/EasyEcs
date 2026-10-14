using System;
using EasyEcs.Core;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>
/// Correctness guard for the zero-GC contract: steady-state hot paths must not allocate. (Performance numbers come
/// from EasyEcs.Benchmark; this only asserts the allocation budget.)
/// </summary>
public class AllocationTests
{
    private struct Integrate : IForEach<Position, Velocity>
    {
        public void Execute(int entityId, ref Position p, ref Velocity v) => p.X += v.X;
    }

    private static readonly QueryFilter NotFrozen = QueryFilter.Create().Without<Frozen>();

    [Test]
    public void SteadyStateHotPathsDoNotAllocate()
    {
        using var world = new World(new WorldOptions { InitialEntityCapacity = 4096 });
        world.AddSystem<MoveSystem>();
        world.AddSingleton<Clock>();
        for (int i = 0; i < 1000; i++) world.CreateEntity().Value.AddComponents<Position, Velocity>();

        void Run()
        {
            var job = new Integrate();
            world.ForEach<Position, Velocity, Integrate>(ref job, NotFrozen);
            foreach (var item in world.Query<Position, Velocity>()) item.Component1.Y += 1;
            _ = world.Count<Position>(NotFrozen);
            foreach (var item in world.Query<Position>())
            {
                item.Entity.AddComponent<Frozen>();
                item.Entity.RemoveComponent<Frozen>();
            }

            var churn = world.CreateEntity();
            churn.Value.AddComponent<Health>();
            world.DestroyEntity(churn);
            world.GetSingleton<Clock>().Tick++;
            world.Update();
        }

        for (int i = 0; i < 10; i++) Run(); // warm-up: caches, archetypes, edges, JIT
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) Run();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.That(allocated, Is.EqualTo(0), $"{allocated} bytes allocated in steady state");
    }
}
