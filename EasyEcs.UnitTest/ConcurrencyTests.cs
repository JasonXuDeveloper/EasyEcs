using System;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>
/// Structural changes belong to the world's owner thread: one made while the world runs work on several threads throws
/// instead of tearing the tables. The cold paths those threads reach on their own (a query's first match, a missing
/// singleton) stay safe under contention.
/// </summary>
public partial class ConcurrencyTests
{
    public record struct Spawner(Entity Owner) : IComponent;   // holds a handle: a way to reach the world

    /// <summary>Changes structure through a handle a component holds: wrong in a system that shares a batch.</summary>
    public partial class SpawnFromBatch : EcsSystem
    {
#pragma warning disable EECS013 // the misuse under test: the runtime must catch what the analyzer reports
        [ForEach]
        static void Spawn(in Spawner spawner) => spawner.Owner.Add<Marked>();
#pragma warning restore EECS013
    }

    public partial class Heal : EcsSystem
    {
        [ForEach]
        static void Run(ref Health health) => health.Value++;
    }

    private struct SpawnFromChunk(World world) : IForEach<Health>, INoStructuralChanges
    {
        public void Execute(ref Health health) => world.CreateEntity(); // breaks the promise INoStructuralChanges makes
    }

    [Test]
    public void StructuralChangeInAParallelBatchThrowsAndLeavesTheWorldUsable()
    {
        using var world = new World(new WorldOptions { ParallelSystems = true });
        var owner = world.CreateEntity(new Health(0));
        world.CreateEntity(new Spawner(owner));
        world.AddSystem<SpawnFromBatch>();
        world.AddSystem<Heal>();

        var error = Assert.Catch(() => world.Update());
        Assert.That(Flatten(error), Is.InstanceOf<InvalidOperationException>());
        Assert.That(owner.Has<Marked>(), Is.False);

        world.RemoveSystem<SpawnFromBatch>();
        world.Update();
        owner.Add<Marked>(); // the gate is open again
        Assert.That(owner.Get<Health>().Value, Is.EqualTo(2)); // Heal ran in both updates
    }

    [Test]
    public void StructuralChangeInAParallelForEachThrowsAndLeavesTheWorldUsable()
    {
        using var world = new World();
        var entities = new Entity[World.ChunkRows * 2 + 1];
        world.CreateEntities(entities, new Health(0));
        int count = world.EntityCount;

        var job = new SpawnFromChunk(world);
        var error = Assert.Catch(() => world.ForEachParallel<Health, SpawnFromChunk>(ref job));
        Assert.That(Flatten(error), Is.InstanceOf<InvalidOperationException>());
        Assert.That(world.EntityCount, Is.EqualTo(count));

        world.CreateEntity();
        Assert.That(world.EntityCount, Is.EqualTo(count + 1));
    }

    [Test]
    public void FirstQueriesAndSingletonsFromManyThreadsStayConsistent()
    {
        using var world = new World();
        world.CreateEntity(new C1(1), new C2(2), new C3(3));
        world.CreateEntity(new C1(1), new C2(2));
        world.CreateEntity(new C1(1));

        // Every thread races to make the first match of the same queries and to add the same singleton.
        Parallel.For(0, 8, _ =>
        {
            Assert.That(world.Count<C1>(), Is.EqualTo(3));
            Assert.That(world.Count<C1, C2>(), Is.EqualTo(2));
            Assert.That(world.Count<C1, C2, C3>(), Is.EqualTo(1));
            world.GetOrAddSingleton<Clock>();
        });

        Assert.That(world.HasSingleton<Clock>(), Is.True);
    }

    private static Exception Flatten(Exception error) =>
        error is AggregateException aggregate ? aggregate.Flatten().InnerExceptions[0] : error;
}
