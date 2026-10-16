using System.Collections.Generic;
using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// A list per entity, replicated when it changes: 100,000 bags of 8 items, of which <see cref="Written"/> trade an item
/// per tick (input applied through handles), then one [Changed] system sends the changed bags. <c>Buffer</c> holds the
/// bag as a <see cref="Buffer{T}"/>; <c>List</c> as a <see cref="List{T}"/> in a tracked component, the way to hold a
/// collection without buffers. Steady state allocates 0 bytes either way.
/// </summary>
[MemoryDiagnoser]
public partial class BufferBenchmark
{
    public const int Items = 8;

    [Params(100_000)] public int Entities;
    [Params(1_000)] public int Written;
    [Params(false, true)] public bool List;

    private World _world;
    private Entity[] _entities;
    private int _next;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Entities });
        _entities = new Entity[Entities];
        if (List) _world.CreateEntities(_entities, new ListBag(), new Sent());
        else _world.CreateEntities(_entities, new Buffer<int>(), new Sent());
        foreach (var entity in _entities)
        {
            if (List)
            {
                ref var bag = ref entity.Get<ListBag>();
                bag.Items = new List<int>(Items);
                for (int i = 0; i < Items; i++) bag.Items.Add(i);
            }
            else
            {
                ref var bag = ref entity.Get<Buffer<int>>();
                bag.EnsureCapacity(Items);
                for (int i = 0; i < Items; i++) bag.Add(i);
            }
        }

        if (List) _world.AddSystem<SendLists>();
        else _world.AddSystem<SendBuffers>();
        for (int i = 0; i < 20; i++) Tick();
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark]
    public void Tick()
    {
        // Input: a different slice of entities trades its first item each tick.
        for (int i = 0; i < Written; i++)
        {
            _next = (_next + 97) % _entities.Length;
            if (List)
            {
                var items = _entities[_next].Get<ListBag>().Items;
                items[0] = items[^1];
                items[^1] = _next;
            }
            else
            {
                var items = _entities[_next].Get<Buffer<int>>().AsSpan();
                items[0] = items[^1];
                items[^1] = _next;
            }
        }

        _world.Update<Replication>();
    }

    public struct Replication : ISystemGroup;

    public record struct ListBag(List<int> Items) : IComponent, ITrackChanges;

    /// <summary>A checksum of what clients last received.</summary>
    public record struct Sent(int Sum) : IComponent;

    [UpdateInGroup<Replication>]
    public partial class SendBuffers : EcsSystem
    {
        [ForEach, Changed<Buffer<int>>]
        private static void Send(in Buffer<int> bag, ref Sent sent)
        {
            int sum = 0;
            foreach (int item in bag) sum += item;
            sent = new Sent(sum);
        }
    }

    [UpdateInGroup<Replication>]
    public partial class SendLists : EcsSystem
    {
        [ForEach, Changed<ListBag>]
        private static void Send(in ListBag bag, ref Sent sent)
        {
            int sum = 0;
            foreach (int item in bag.Items) sum += item;
            sent = new Sent(sum);
        }
    }
}
