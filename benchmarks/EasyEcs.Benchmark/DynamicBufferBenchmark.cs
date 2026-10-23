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

    [NotSaved]
    public record struct ListBag(List<int> Items) : IComponent, ITracked;

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

/// <summary>
/// Chunks that load and unload around players: each operation creates <see cref="Chunks"/> entities with 8,192 blocks
/// (a <see cref="DynamicBuffer{T}"/> of a byte-sized enum, 8 KB) and destroys them. The lists are longer than the pool
/// keeps by default (4 KB); <see cref="Pooled"/> raises <see cref="DynamicBuffer{T}.MaxPooledLength"/> to a chunk, so
/// each chunk reuses the list of one unloaded before.
/// </summary>
[MemoryDiagnoser]
public class ChunkChurnBenchmark
{
    public const int Blocks = 8192;

    [Params(64)] public int Chunks;
    [Params(false, true)] public bool Pooled;

    private World _world;
    private Entity[] _chunks;
    private Block[] _blocks;

    [GlobalSetup]
    public void Setup()
    {
        DynamicBuffer<Block>.MaxPooledLength = Pooled ? Blocks : 4096;
        _world = new World(new WorldOptions { InitialEntityCapacity = Chunks });
        _chunks = new Entity[Chunks];
        _blocks = new Block[Blocks];
        for (int i = 0; i < 20; i++) LoadAndUnload();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _world.Dispose();
        DynamicBuffer<Block>.MaxPooledLength = 4096;
    }

    [Benchmark]
    public void LoadAndUnload()
    {
        _world.CreateEntities(_chunks, new DynamicBuffer<Block>(), new Chunk());
        foreach (var chunk in _chunks) chunk.Get<DynamicBuffer<Block>>().AddRange(_blocks);
        foreach (var chunk in _chunks) chunk.Destroy(); // one by one, as chunks unload
    }

    public enum Block : byte { Air, Stone }

    public record struct Chunk(int X, int Z) : IComponent;
}

/// <summary>
/// Actors spawned from a prefab whose mailbox starts empty (<c>SetBuffer&lt;long&gt;()</c>), posted
/// <see cref="Mail"/> messages and destroyed, <see cref="Actors"/> at a time: an empty buffer of a prefab gives its
/// instances no list of their own, so their first message takes a list a destroyed actor gave back.
/// </summary>
[MemoryDiagnoser]
public class PrefabBufferChurnBenchmark
{
    public const int Mail = 4;

    [Params(1_000)] public int Actors;

    private World _world;
    private Prefab _prefab;
    private Entity[] _actors;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Actors });
        _prefab = new Prefab().Set(new Actor()).SetBuffer<long>();
        _actors = new Entity[Actors];
        for (int i = 0; i < 20; i++) SpawnAndUnload();
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark]
    public int SpawnAndUnload()
    {
        _world.Instantiate(_prefab, _actors);
        foreach (var actor in _actors)
        {
            ref var mailbox = ref actor.Get<DynamicBuffer<long>>();
            for (int m = 0; m < Mail; m++) mailbox.Add(m);
        }

        return _world.DestroyAll<Actor>();
    }

    public record struct Actor(int Shard) : IComponent;
}

/// <summary>
/// Mailboxes filled by a parallel <c>[ForEach]</c>: <see cref="Actors"/> actors are created with empty mailboxes, a
/// parallel system posts <see cref="Mail"/> messages to each (the first write of each mailbox, on whichever thread
/// runs its rows), and the actors are destroyed on the owner thread, which takes their lists back.
/// </summary>
[MemoryDiagnoser]
public class ParallelBufferChurnBenchmark
{
    public const int Mail = 4;

    [Params(100_000)] public int Actors;

    private World _world;
    private Entity[] _actors;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Actors });
        _world.AddSystem<PostSystem>();
        _actors = new Entity[Actors];
        for (int i = 0; i < 20; i++) LoadPostUnload();
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark]
    public int LoadPostUnload()
    {
        _world.CreateEntities(_actors, new DynamicBuffer<long>(), new Actor());
        _world.Update();
        return _world.DestroyAll<Actor>();
    }

    public record struct Actor(int Shard) : IComponent;

    public class PostSystem : EcsSystem
    {
        [ForEach(Parallel = true), With<Actor>]
        static void Post(Mut<DynamicBuffer<long>> mailbox)
        {
            ref var posted = ref mailbox.Write();
            for (int m = 0; m < Mail; m++) posted.Add(m);
        }
    }
}

/// <summary>
/// Chunks unloaded to bytes and loaded back, as a voxel server does around its players: <see cref="Chunks"/>
/// entities with 8,192 blocks each (a <see cref="DynamicBuffer{T}"/> of a byte-sized enum) are saved with
/// <c>SaveEntities</c>, destroyed, and created again by <c>LoadEntities</c>. A loaded chunk takes the list of an unloaded one.
/// </summary>
[MemoryDiagnoser]
public class ChunkReloadBenchmark
{
    public const int Blocks = 8192;

    [Params(64)] public int Chunks;

    private World _world;
    private EntityId[] _ids;
    private Entity[] _loaded;
    private readonly Nino.Core.NinoArrayBufferWriter _bytes = new();

    [GlobalSetup]
    public void Setup()
    {
        DynamicBuffer<Block>.MaxPooledLength = Blocks;
        _world = new World(new WorldOptions { InitialEntityCapacity = Chunks });
        _ids = new EntityId[Chunks];
        _loaded = new Entity[Chunks];
        var blocks = new Block[Blocks];
        _world.CreateEntities(_loaded, new Chunk(), new DynamicBuffer<Block>());
        for (int i = 0; i < Chunks; i++)
        {
            _loaded[i].Get<DynamicBuffer<Block>>().AddRange(blocks);
            _ids[i] = _loaded[i].Id;
        }

        for (int i = 0; i < 20; i++) UnloadAndLoad();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _world.Dispose();
        DynamicBuffer<Block>.MaxPooledLength = 4096;
    }

    [Benchmark]
    public int UnloadAndLoad()
    {
        _bytes.ResetWrittenCount();
        _world.SaveEntities(_bytes, _ids);
        _world.DestroyEntities(_ids);
        int loaded = _world.LoadEntities(_bytes.WrittenSpan, _loaded);
        for (int i = 0; i < loaded; i++) _ids[i] = _loaded[i].Id;
        return loaded;
    }

    public enum Block : byte { Air, Stone }

    public record struct Chunk(int X, int Z) : IComponent;
}
