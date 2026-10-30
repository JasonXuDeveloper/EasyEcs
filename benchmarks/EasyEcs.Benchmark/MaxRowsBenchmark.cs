using System;
using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// A budget of requests served per tick, as a server loads chunks: <see cref="Backlog"/> requests wait, and each
/// operation serves <see cref="Budget"/> of them, each creating an entity (what a load does), consumes the requests
/// served and adds as many new ones, so the backlog stays. The generated form, <c>[ForEach(MaxRows = N), Consume]</c>
/// (the rows collected first, the method on each outside the walk, each request destroyed after it), against the
/// OnUpdate written by hand that it replaces (VoxelServer's ChunkReadSystem before: CollectIds into a span, the keys
/// copied, one DestroyEntities, then a loop). Steady state allocates 0 bytes.
/// </summary>
[MemoryDiagnoser]
public class MaxRowsBenchmark
{
    [Params(64, 256)] public int Budget;

    [Params(256, 10_000)] public int Backlog;

    private World _world;
    private int _next;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Backlog * 2 + Budget * 4 });
        for (; _next < Backlog; _next++) _world.CreateEntity(new Request(_next));
        for (int i = 0; i < 20; i++)
        {
            HandWritten();
            Generated();
        }
    }

    [GlobalCleanup]
    public void Dispose() => _world.Dispose();

    [Benchmark(Baseline = true)]
    public int HandWritten()
    {
        int served = Budget == 64 ? Requests.ServeByHand64(_world) : Requests.ServeByHand256(_world);
        return Refill(served);
    }

    [Benchmark]
    public int Generated()
    {
        if (Budget == 64) Requests.Serve64(_world);
        else Requests.Serve256(_world);
        return Refill(Budget);
    }

    /// <summary>What was loaded goes, and as many requests come in as were served: the world stays the same size.</summary>
    private int Refill(int served)
    {
        int loaded = _world.DestroyAll<Loaded>();
        _world.Reserve<Request>(served);
        for (int i = 0; i < served; i++) _world.CreateEntity(new Request(_next++));
        return loaded;
    }

    public record struct Request(int Key) : IComponent;
    public record struct Loaded(int Key) : IComponent;
}

public static class Requests
{
    [ForEach(MaxRows = 64), Consume]
    internal static void Serve64(in MaxRowsBenchmark.Request request, World world) => world.CreateEntity(new MaxRowsBenchmark.Loaded(request.Key));

    [ForEach(MaxRows = 256), Consume]
    internal static void Serve256(in MaxRowsBenchmark.Request request, World world) => world.CreateEntity(new MaxRowsBenchmark.Loaded(request.Key));

    internal static int ServeByHand64(World world)
    {
        Span<EntityId> served = stackalloc EntityId[64];
        Span<int> keys = stackalloc int[64];
        return ServeByHand(world, served, keys);
    }

    internal static int ServeByHand256(World world)
    {
        Span<EntityId> served = stackalloc EntityId[256];
        Span<int> keys = stackalloc int[256];
        return ServeByHand(world, served, keys);
    }

    // The form [ForEach(MaxRows = N)] replaces (EECS119 reports it; the copy and the bulk destroy keep it from the fix).
    private static int ServeByHand(World world, Span<EntityId> served, Span<int> keys)
    {
        int count = world.CollectIds<MaxRowsBenchmark.Request>(served);   // the rest waits
        for (int i = 0; i < count; i++) keys[i] = world.GetEntity(served[i]).Read<MaxRowsBenchmark.Request>().Key;
        world.DestroyEntities(served[..count]);
        foreach (int key in keys[..count]) world.CreateEntity(new MaxRowsBenchmark.Loaded(key));
        return count;
    }
}
