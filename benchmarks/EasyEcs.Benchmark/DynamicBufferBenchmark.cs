using System.Collections.Generic;
using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// A list per entity, replicated when it changes: 100,000 bags of 8 items, of which <see cref="Written"/> trade an item
/// per tick (input applied through handles), then one [Changed] system sends the changed bags. <c>DynamicBuffer</c>
/// holds the bag as a <see cref="DynamicBuffer{T}"/>; <c>List</c> as a <see cref="List{T}"/> in a tracked component,
/// the way to hold a collection without buffers. Steady state allocates 0 bytes either way.
/// </summary>
[MemoryDiagnoser]
public class DynamicBufferBenchmark
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
        else _world.CreateEntities(_entities, new DynamicBuffer<int>(), new Sent());
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
                ref var bag = ref entity.Get<DynamicBuffer<int>>();
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
                var items = _entities[_next].Get<DynamicBuffer<int>>().AsSpan();
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
    public class SendBuffers : EcsSystem
    {
        [ForEach, Changed<DynamicBuffer<int>>]
        private static void Send(in DynamicBuffer<int> bag, ref Sent sent)
        {
            int sum = 0;
            foreach (int item in bag) sum += item;
            sent = new Sent(sum);
        }
    }

    [UpdateInGroup<Replication>]
    public class SendLists : EcsSystem
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

/// <summary>
/// Actors that load and unload: each operation creates <see cref="Actors"/> entities with a mailbox (a
/// <see cref="DynamicBuffer{T}"/>), posts <see cref="Mail"/> messages to each, and destroys them, the way a server
/// keeps players coming and going. A destroyed entity's list goes back to a pool and serves the next mailbox.
/// </summary>
[MemoryDiagnoser]
public class BufferChurnBenchmark
{
    public const int Mail = 4;

    [Params(1_000)] public int Actors;

    private World _world;
    private Entity[] _actors;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Actors });
        _actors = new Entity[Actors];
        for (int i = 0; i < 20; i++) LoadAndUnload();
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark]
    public void LoadAndUnload()
    {
        _world.CreateEntities(_actors, new DynamicBuffer<long>(), new Actor());
        foreach (var actor in _actors)
        {
            ref var mailbox = ref actor.Get<DynamicBuffer<long>>();
            for (int m = 0; m < Mail; m++) mailbox.Add(m);
        }

        foreach (var actor in _actors) actor.Destroy(); // one by one, as actors unload
    }

    public record struct Actor(int Shard) : IComponent;
}
