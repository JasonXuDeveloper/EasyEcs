using System;
using System.Buffers;
using BenchmarkDotNet.Attributes;
using EasyEcs.Benchmark.Locality;

namespace EasyEcs.Benchmark;

/// <summary>
/// Where the page faults of a large world are, and what they cost: the ops that allocate a world's large arrays (a
/// world created at its size, one grown to it without a reservation, a reservation alone, a load, a capture into a new
/// snapshot, a restore that rewrites the tables, one Instantiate per entity into a new world), at 1M and 16M entities
/// of LookupChase's three components (24 bytes a row, 16 more for the record). <see cref="PageFaultDiagnoser"/> reads the
/// faults, the kernel time and the huge-page counters per op; <see cref="HugePages"/> sets the internal switch
/// (EasyEcs.HugePages.Enabled) in setup, so the 4 KiB and 2 MiB forms run in one binary.
/// </summary>
[MemoryDiagnoser, PageFaultDiagnoser]
public class PageFaultBenchmark
{
    /// <summary>1M and 16M; EASYECS_BENCH_ENTITIES (comma-separated) picks others.</summary>
    [ParamsSource(nameof(Sizes))] public int Entities;

    /// <summary>4 KiB and 2 MiB pages; EASYECS_BENCH_HUGEPAGES (off / on) picks one.</summary>
    [ParamsSource(nameof(Pages))] public bool HugePages;

    public static int[] Sizes => Environment.GetEnvironmentVariable("EASYECS_BENCH_ENTITIES") is { Length: > 0 } list
        ? Array.ConvertAll(list.Split(','), int.Parse)
        : [1_000_000, 16_000_000];

    public static bool[] Pages => Choice("EASYECS_BENCH_HUGEPAGES");

    /// <summary>The sized arrays faulted in when allocated (EasyEcs.HugePages.Prefault) or on first touch; EASYECS_BENCH_PREFAULT (off / on) picks one.</summary>
    [ParamsSource(nameof(Prefaults))] public bool Prefault;

    public static bool[] Prefaults => Choice("EASYECS_BENCH_PREFAULT");

    private static bool[] Choice(string variable) => Environment.GetEnvironmentVariable(variable) switch
    {
        "off" => [false],
        "on" => [true],
        _ => [false, true],
    };

    private World _world;
    private WorldSnapshot _snapshot;
    private ArrayBufferWriter<byte> _save;
    private Entity[] _handles;
    private Prefab _prefab;
    private World _reserved;

    [GlobalSetup]
    public void Setup()
    {
        var pages = typeof(World).Assembly.GetType("EasyEcs.HugePages")!;
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        pages.GetField("Enabled", flags)!.SetValue(null, HugePages);
        pages.GetField("Prefault", flags)!.SetValue(null, Prefault);
        _handles = new Entity[Entities];
        _world = Chase.Create(Entities);
        _snapshot = _world.Capture();
        _save = new ArrayBufferWriter<byte>(Entities * 40);
        _world.Save(_save);
        _prefab = new Prefab().Set(new Chase.Position(1, 2)).Set(new Chase.Prey()).Set(new Chase.Heading());
        using var loaded = new World();
        loaded.Load(_save.WrittenSpan);
        if (loaded.EntityCount != Entities) throw new InvalidOperationException("The load lost entities");
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    /// <summary>A world reserved for its size, then filled in one call.</summary>
    [Benchmark(Baseline = true)]
    public int Create()
    {
        using var world = new World(new WorldOptions { InitialEntityCapacity = Entities });
        world.Reserve<Chase.Position, Chase.Prey, Chase.Heading>(Entities);
        world.CreateEntities(_handles, new Chase.Position(1, 2), new Chase.Prey(), new Chase.Heading());
        return world.EntityCount;
    }

    /// <summary>A world grown to the size without a reservation, 4,096 entities a call (every doubling step).</summary>
    [Benchmark]
    public int Grow()
    {
        using var world = new World();
        var batch = _handles.AsSpan(0, 4096);
        for (int made = 0; made < Entities; made += batch.Length)
            world.CreateEntities(batch, new Chase.Position(1, 2), new Chase.Prey(), new Chase.Heading());
        return world.EntityCount;
    }

    /// <summary>The reservation alone: what it costs before any row is written.</summary>
    [Benchmark]
    public int Reserve()
    {
        using var world = new World(new WorldOptions { InitialEntityCapacity = Entities });
        world.Reserve<Chase.Position, Chase.Prey, Chase.Heading>(Entities);
        return world.EntityCount;
    }

    /// <summary>A save of the world loaded into a new world.</summary>
    [Benchmark]
    public int Load()
    {
        using var world = new World();
        world.Load(_save.WrittenSpan);
        return world.EntityCount;
    }

    /// <summary>A capture into a new snapshot: a copy of every table and of the records.</summary>
    [Benchmark]
    public int Capture() => _world.Capture().EntityCount;

    /// <summary>A restore after a structural change: every table and the records copied back.</summary>
    [Benchmark]
    public int Restore()
    {
        _world.CreateEntity();
        _world.Restore(_snapshot);
        return _world.EntityCount;
    }

    /// <summary>
    /// A wave of spawns into a world reserved for it before: the reservation (not measured) is made on memory the
    /// process handed back 4 s earlier (a full collection, then a pause: a VM reporting free pages to its host has
    /// discarded it by then), as a server reserves at start-up and fills during a tick later.
    /// </summary>
    [Benchmark]
    public int Wave()
    {
        _reserved.CreateEntities(_handles, new Chase.Position(1, 2), new Chase.Prey(), new Chase.Heading());
        return _reserved.EntityCount;
    }

    [IterationSetup(Target = nameof(Wave))]
    public void ReserveForWave()
    {
        _reserved?.Dispose();
        _reserved = null;
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        System.Threading.Thread.Sleep(4000);
        _reserved = new World(new WorldOptions { InitialEntityCapacity = Entities });
        _reserved.Reserve<Chase.Position, Chase.Prey, Chase.Heading>(Entities);
    }

    /// <summary>One Instantiate per entity into a new world (no reservation).</summary>
    [Benchmark]
    public int InstantiateEach()
    {
        using var world = new World();
#pragma warning disable EECS031 // one Instantiate per entity is what this case measures
        for (int i = 0; i < Entities; i++) world.Instantiate(_prefab);
#pragma warning restore EECS031
        return world.EntityCount;
    }
}
