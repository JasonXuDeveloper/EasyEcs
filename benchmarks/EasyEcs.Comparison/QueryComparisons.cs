using System;
using System.IO;
using BenchmarkDotNet.Attributes;
using EasyEcs;
using Nino.Core;
using A = Arch.Core;
using D = DefaultEcs;
using F = Friflo.Engine.ECS;
using static Friflo.Engine.ECS.EntityStoreExtensions;
using static Friflo.Engine.ECS.IndexExtensions;

namespace EcsComparison.Features;

// Queries by value, parallel queries and saves, each library the way its docs recommend. Each setup checks the
// library's result before anything is measured.

/// <summary>EasyEcs: a player's rating (an ordered index) and zone (a group, or a value index).</summary>
public partial record struct Rating([IndexKey] int Value) : EasyEcs.IComponent;

public partial record struct Region([Group] int Value) : EasyEcs.IComponent;

public partial record struct RegionKey([IndexKey] int Value) : EasyEcs.IComponent;

public record struct Gold(int Value) : EasyEcs.IComponent;

public struct FRating : F.IIndexedComponent<int>
{
    public int Value;
    public int GetIndexedValue() => Value;
}

public struct FRegion : F.IIndexedComponent<int>
{
    public int Value;
    public int GetIndexedValue() => Value;
}

public struct FGold : F.IComponent { public int Value; }

/// <summary>
/// Queries by value: 100,000 players with a rating (0-4,999) and a zone (16 zones). Per operation 1,000 ratings and
/// 100 zones change, then the players rated 2,000-2,010 are counted and the gold of the players in 4 zones summed.
/// <list type="bullet">
/// <item>EasyEcs: an ordered value index (<c>Range</c>, keys kept sorted); the zone as a group (<c>GroupBy</c>, a
/// table per zone, <c>InGroup</c>) and as a value index (<c>Find</c>).</item>
/// <item>Friflo: <c>IIndexedComponent&lt;int&gt;</c>, <c>ValueInRange</c> and <c>ComponentIndex&lt;T, int&gt;()[zone]</c>.</item>
/// </list>
/// Arch and DefaultEcs have no range query (DefaultEcs' <c>EntityMultiMap</c> finds one key, see the index
/// comparison), so they are not part of this comparison.
/// </summary>
[MemoryDiagnoser]
public class ValueQueryComparison
{
    private const int Players = 100_000, Zones = 16;

    private EasyEcs.World _world;
    private EasyEcs.Entity[] _players;
    private ValueIndex<Rating, int> _ratings;
    private ValueIndex<RegionKey, int> _regions;
    private QueryFilter[] _inZone;
    private F.EntityStore _store;
    private F.Entity[] _fPlayers;
    private F.ArchetypeQuery<FRating> _fRange;
    private int _next;

    [GlobalSetup(Targets = [nameof(EasyEcs_Group), nameof(EasyEcs_Index)])]
    public void SetupEasyEcs()
    {
        _world = new EasyEcs.World();
        _world.GroupBy<Region>();
        _world.EnsureEntityCapacity(Players); // the rows spread over a table per zone: the entity storage is what grows large
        _players = new EasyEcs.Entity[Players];
        for (int i = 0; i < Players; i++)
            _players[i] = _world.CreateEntity(new Rating(i % 5_000), new Region(i % Zones), new RegionKey(i % Zones), new Gold(i % 100));
        _ratings = _world.Index<Rating, int>();
        _regions = _world.Index<RegionKey, int>();
        _inZone = new QueryFilter[Zones];
        for (int z = 0; z < Zones; z++) _inZone[z] = QueryFilter.Create().InGroup<Region>(z);
        Check(EasyEcs_Group, i => _players[i].Read<Rating>().Value, i => _players[i].Read<Region>().Value, i => _players[i].Read<Gold>().Value);
        Check(EasyEcs_Index, i => _players[i].Read<Rating>().Value, i => _players[i].Read<RegionKey>().Value, i => _players[i].Read<Gold>().Value);
    }

    [GlobalSetup(Target = nameof(Friflo_IndexedComponent))]
    public void SetupFriflo()
    {
        _store = new F.EntityStore();
        _fPlayers = new F.Entity[Players];
        for (int i = 0; i < Players; i++)
            _fPlayers[i] = _store.CreateEntity(new FRating { Value = i % 5_000 }, new FRegion { Value = i % Zones }, new FGold { Value = i % 100 });
        _fRange = _store.Query<FRating>().ValueInRange<FRating, int>(2_000, 2_010);
        Check(Friflo_IndexedComponent, i => _fPlayers[i].GetComponent<FRating>().Value, i => _fPlayers[i].GetComponent<FRegion>().Value,
            i => _fPlayers[i].GetComponent<FGold>().Value);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _world?.Dispose();
        (_world, _store) = (null, null);
    }

    [Benchmark(Baseline = true)]
    public long EasyEcs_Group()
    {
        Change(out int zone);
        for (int i = 0; i < 1_000; i++) _players[Pick(i)].Set(new Rating(NewRating(i)));
        for (int i = 0; i < 100; i++) _players[Pick(i * 7)].Set(new Region(NewZone(i)));
        long total = 0;
        foreach (var _ in _ratings.Range(2_000, 2_010)) total += 1_000_000;
        for (int z = 0; z < 4; z++)
            foreach (var item in _world.Query<Gold>(_inZone[(zone + z) % Zones]))
                total += item.Component1.Value;
        return total;
    }

    [Benchmark]
    public long EasyEcs_Index()
    {
        Change(out int zone);
        for (int i = 0; i < 1_000; i++) _players[Pick(i)].Set(new Rating(NewRating(i)));
        for (int i = 0; i < 100; i++) _players[Pick(i * 7)].Set(new RegionKey(NewZone(i)));
        long total = 0;
        foreach (var _ in _ratings.Range(2_000, 2_010)) total += 1_000_000;
        for (int z = 0; z < 4; z++)
            foreach (var player in _regions.Find((zone + z) % Zones))
                total += player.Read<Gold>().Value;
        return total;
    }

    [Benchmark]
    public long Friflo_IndexedComponent()
    {
        Change(out int zone);
        for (int i = 0; i < 1_000; i++) _fPlayers[Pick(i)].AddComponent(new FRating { Value = NewRating(i) });
        for (int i = 0; i < 100; i++) _fPlayers[Pick(i * 7)].AddComponent(new FRegion { Value = NewZone(i) });
        long total = 0;
        foreach (var _ in _fRange.Entities) total += 1_000_000;
        for (int z = 0; z < 4; z++)
            foreach (var player in _store.ComponentIndex<FRegion, int>()[(zone + z) % Zones])
                total += player.GetComponent<FGold>().Value;
        return total;
    }

    private void Change(out int zone) => zone = ++_next % Zones;
    private int Pick(int i) => (_next * 7_919 + i * 104_729) % Players;
    private int NewRating(int i) => (_next * 31 + i * 17) % 5_000;
    private int NewZone(int i) => (_next + i) % Zones;

    /// <summary>Warm up, then check one operation's result against the players' own components.</summary>
    private void Check(Func<long> operation, Func<int, int> rating, Func<int, int> zone, Func<int, int> gold)
    {
        for (int i = 0; i < 10; i++) operation();
        long total = operation(), expected = 0;
        int first = _next % Zones;
        for (int i = 0; i < Players; i++)
        {
            if (rating(i) is >= 2_000 and <= 2_010) expected += 1_000_000;
            if ((zone(i) - first + Zones) % Zones < 4) expected += gold(i);
        }

        if (total != expected) throw new InvalidOperationException($"computed {total}, expected {expected}");
    }
}

/// <summary>EasyEcs: an agent steering towards the nearest of 16 waypoints, on all cores.</summary>
public struct Agent : EasyEcs.IComponent
{
    public float X, Y, Vx, Vy;
}

public static class Steering
{
    [ForEach(Parallel = true)]
    static void Steer(ref Agent agent) => Waypoints.Steer(ref agent.X, ref agent.Y, ref agent.Vx, ref agent.Vy);
}

/// <summary>The per-entity work of the parallel comparison, shared by every library: heavy enough to split.</summary>
public static class Waypoints
{
    private static readonly float[] Xs = new float[16], Ys = new float[16];

    static Waypoints()
    {
        for (int i = 0; i < 16; i++) (Xs[i], Ys[i]) = (MathF.Cos(i) * 5_000, MathF.Sin(i) * 5_000);
    }

    public static void Steer(ref float x, ref float y, ref float vx, ref float vy)
    {
        float best = float.MaxValue, tx = 0, ty = 0;
        for (int i = 0; i < 16; i++)
        {
            float dx = Xs[i] - x, dy = Ys[i] - y, d = dx * dx + dy * dy;
            if (d < best) (best, tx, ty) = (d, Xs[i], Ys[i]);
        }

        float length = MathF.Sqrt(best) + 1e-3f;
        vx = (tx - x) / length;
        vy = (ty - y) / length;
        x += vx;
        y += vy;
    }
}

public struct FAgent : F.IComponent { public float X, Y, Vx, Vy; }
public struct AAgent { public float X, Y, Vx, Vy; }
public struct DAgent { public float X, Y, Vx, Vy; }

/// <summary>
/// A parallel query: 100,000 agents steer (the nearest of 16 waypoints, then a step) on all cores.
/// <list type="bullet">
/// <item>EasyEcs: <c>[ForEach(Parallel = true)]</c> (a runner: chunks of rows on the thread pool).</item>
/// <item>Friflo: a <c>QueryJob</c> with a <c>ParallelJobRunner</c>.</item>
/// <item>Arch: <c>InlineParallelQuery</c> on its job scheduler.</item>
/// <item>DefaultEcs: an <c>AEntitySetSystem</c> with a <c>DefaultParallelRunner</c>.</item>
/// </list>
/// </summary>
[MemoryDiagnoser]
public class ParallelQueryComparison
{
    private const int Agents = 100_000;

    private EasyEcs.World _world;
    private F.EntityStore _store;
    private F.QueryJob<FAgent> _fJob;
    private A.World _arch;
    private Schedulers.JobScheduler _scheduler;
    private D.World _default;
    private D.Threading.DefaultParallelRunner _runner;
    private DefaultSteer _dSteer;

    private static readonly A.QueryDescription ArchAgents = new A.QueryDescription().WithAll<AAgent>();

    [GlobalSetup(Target = nameof(EasyEcs_Parallel))]
    public void SetupEasyEcs()
    {
        _world = new EasyEcs.World(new WorldOptions { InitialEntityCapacity = Agents });
        _world.Reserve<Agent>(Agents);
        var agents = new EasyEcs.Entity[Agents];
        for (int i = 0; i < Agents; i++) agents[i] = _world.CreateEntity(new Agent { X = i % 10_000, Y = i / 10, Vx = 1 });
        Check(EasyEcs_Parallel, i => (agents[i].Read<Agent>().X, agents[i].Read<Agent>().Y));
    }

    [GlobalSetup(Target = nameof(Friflo_QueryJob))]
    public void SetupFriflo()
    {
        _store = new F.EntityStore { JobRunner = new F.ParallelJobRunner(Environment.ProcessorCount) };
        var agents = new F.Entity[Agents];
        for (int i = 0; i < Agents; i++) agents[i] = _store.CreateEntity(new FAgent { X = i % 10_000, Y = i / 10, Vx = 1 });
        _fJob = _store.Query<FAgent>().ForEach(static (chunk, _) =>
        {
            var a = chunk.Span;
            for (int i = 0; i < a.Length; i++) Waypoints.Steer(ref a[i].X, ref a[i].Y, ref a[i].Vx, ref a[i].Vy);
        });
        Check(Friflo_QueryJob, i => (agents[i].GetComponent<FAgent>().X, agents[i].GetComponent<FAgent>().Y));
    }

    [GlobalSetup(Target = nameof(Arch_InlineParallelQuery))]
    public void SetupArch()
    {
        _scheduler = new Schedulers.JobScheduler(new Schedulers.JobScheduler.Config
        {
            ThreadPrefixName = "Arch", ThreadCount = 0, MaxExpectedConcurrentJobs = 64, StrictAllocationMode = false,
        });
        A.World.SharedJobScheduler = _scheduler;
        _arch = A.World.Create();
        var agents = new A.Entity[Agents];
        for (int i = 0; i < Agents; i++) agents[i] = _arch.Create(new AAgent { X = i % 10_000, Y = i / 10, Vx = 1 });
        Check(Arch_InlineParallelQuery, i => (_arch.Get<AAgent>(agents[i]).X, _arch.Get<AAgent>(agents[i]).Y));
    }

    [GlobalSetup(Target = nameof(DefaultEcs_ParallelRunner))]
    public void SetupDefaultEcs()
    {
        _default = new D.World(Agents);
        _runner = new D.Threading.DefaultParallelRunner(Environment.ProcessorCount);
        var agents = new D.Entity[Agents];
        for (int i = 0; i < Agents; i++)
        {
            agents[i] = _default.CreateEntity();
            agents[i].Set(new DAgent { X = i % 10_000, Y = i / 10, Vx = 1 });
        }

        _dSteer = new DefaultSteer(_default, _runner);
        Check(DefaultEcs_ParallelRunner, i => (agents[i].Get<DAgent>().X, agents[i].Get<DAgent>().Y));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _world?.Dispose();
        if (_arch != null) A.World.Destroy(_arch);
        _scheduler?.Dispose();
        _dSteer?.Dispose();
        _runner?.Dispose();
        _default?.Dispose();
        (_world, _store, _arch, _scheduler, _dSteer, _runner, _default) = (null, null, null, null, null, null, null);
    }

    [Benchmark(Baseline = true)]
    public void EasyEcs_Parallel() => Steering.Steer(_world);

    [Benchmark]
    public void Friflo_QueryJob() => _fJob.RunParallel();

    [Benchmark]
    public void Arch_InlineParallelQuery()
    {
        var steer = new ArchSteer();
        _arch.InlineParallelQuery<ArchSteer, AAgent>(in ArchAgents, ref steer);
    }

    [Benchmark]
    public void DefaultEcs_ParallelRunner() => _dSteer.Update(0);

    private struct ArchSteer : A.IForEach<AAgent>
    {
        public void Update(ref AAgent a) => Waypoints.Steer(ref a.X, ref a.Y, ref a.Vx, ref a.Vy);
    }

    private sealed class DefaultSteer(D.World world, D.Threading.IParallelRunner runner)
        : D.System.AEntitySetSystem<int>(world.GetEntities().With<DAgent>().AsSet(), runner, 1_024)
    {
        protected override void Update(int state, ReadOnlySpan<D.Entity> entities)
        {
            foreach (ref readonly var e in entities)
            {
                ref var a = ref e.Get<DAgent>();
                Waypoints.Steer(ref a.X, ref a.Y, ref a.Vx, ref a.Vy);
            }
        }
    }

    /// <summary>Run 20 steps, then check every agent against the same 20 steps computed on one thread.</summary>
    private static void Check(Action step, Func<int, (float X, float Y)> position)
    {
        for (int s = 0; s < 20; s++) step();
        for (int i = 0; i < Agents; i += 997)
        {
            float x = i % 10_000, y = i / 10, vx = 1, vy = 0;
            for (int s = 0; s < 20; s++) Waypoints.Steer(ref x, ref y, ref vx, ref vy);
            if (position(i) != (x, y)) throw new InvalidOperationException($"agent {i} at {position(i)}, expected ({x}, {y})");
        }
    }
}

/// <summary>EasyEcs: a unit saved with its world.</summary>
public record struct Spot(float X, float Y) : EasyEcs.IComponent;

public record struct Stock(int Gold, int Wood) : EasyEcs.IComponent;

public struct FSpot : F.IComponent { public float X, Y; }
public struct FStock : F.IComponent { public int Gold, Wood; }
public record struct DSpot(float X, float Y);
public record struct DStock(int Gold, int Wood);

/// <summary>
/// Saving and loading a world: 10,000 units (a position and a stock each) are written and read back into a world.
/// Formats differ, so the table labels them:
/// <list type="bullet">
/// <item>EasyEcs: <c>Save</c> / <c>Load</c>, binary (Nino; unmanaged rows copied with a field hash).</item>
/// <item>Friflo: <c>EntitySerializer</c>, JSON (<c>WriteStore</c> / <c>ReadIntoStore</c>).</item>
/// <item>DefaultEcs: <c>BinarySerializer</c>, binary (<c>Serialize</c> / <c>Deserialize</c>, a new world each time;
/// the previous one is disposed).</item>
/// </list>
/// Arch saves through a separate package (Arch.Persistence), so it is not part of this comparison.
/// </summary>
[MemoryDiagnoser]
public class SaveComparison
{
    private const int Units = 10_000;

    private EasyEcs.World _world, _loaded;
    private readonly NinoArrayBufferWriter _bytes = new();
    private F.EntityStore _store;
    private F.Serialize.EntitySerializer _serializer;
    private D.World _default, _dLoaded;
    private D.Serialization.BinarySerializer _binary;
    private readonly MemoryStream _stream = new();

    [GlobalSetup(Target = nameof(EasyEcs_SaveLoad))]
    public void SetupEasyEcs()
    {
        _world = new EasyEcs.World(new WorldOptions { InitialEntityCapacity = Units });
        _loaded = new EasyEcs.World(new WorldOptions { InitialEntityCapacity = Units });
        _world.Reserve<Spot, Stock>(Units);
        for (int i = 0; i < Units; i++) _world.CreateEntity(new Spot(i, -i), new Stock(i % 100, i % 7));
        Check(() => EasyEcs_SaveLoad(), () =>
        {
            long sum = 0;
            foreach (var item in _loaded.Query<Spot, Stock>()) sum += (long)item.Component1.X + item.Component2.Gold + item.Component2.Wood;
            return sum;
        });
    }

    [GlobalSetup(Target = nameof(Friflo_Json))]
    public void SetupFriflo()
    {
        _store = new F.EntityStore();
        _serializer = new F.Serialize.EntitySerializer();
        for (int i = 0; i < Units; i++) _store.CreateEntity(new FSpot { X = i, Y = -i }, new FStock { Gold = i % 100, Wood = i % 7 });
        F.EntityStore loaded = null;
        Check(() => loaded = Friflo_Json(), () =>
        {
            long sum = 0;
            foreach (var (spots, stocks, _) in loaded.Query<FSpot, FStock>().Chunks)
                for (int i = 0; i < spots.Length; i++) sum += (long)spots.Span[i].X + stocks.Span[i].Gold + stocks.Span[i].Wood;
            return sum;
        });
    }

    [GlobalSetup(Target = nameof(DefaultEcs_Binary))]
    public void SetupDefaultEcs()
    {
        _default = new D.World(Units);
        _binary = new D.Serialization.BinarySerializer();
        for (int i = 0; i < Units; i++)
        {
            var e = _default.CreateEntity();
            e.Set(new DSpot(i, -i));
            e.Set(new DStock(i % 100, i % 7));
        }

        Check(() => DefaultEcs_Binary(), () =>
        {
            long sum = 0;
            foreach (var e in _dLoaded) sum += (long)e.Get<DSpot>().X + e.Get<DStock>().Gold + e.Get<DStock>().Wood;
            return sum;
        });
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _world?.Dispose();
        _loaded?.Dispose();
        _default?.Dispose();
        _dLoaded?.Dispose();
        (_world, _loaded, _store, _default, _dLoaded) = (null, null, null, null, null);
    }

    [Benchmark(Baseline = true)]
    public int EasyEcs_SaveLoad()
    {
        _bytes.ResetWrittenCount();
        _world.Save(_bytes);
        _loaded.Load(_bytes.WrittenSpan);
        return _bytes.WrittenCount;
    }

    [Benchmark]
    public F.EntityStore Friflo_Json()
    {
        _stream.SetLength(0);
        _serializer.WriteStore(_store, _stream);
        _stream.Position = 0;
        var loaded = new F.EntityStore();
        _serializer.ReadIntoStore(loaded, _stream);
        return loaded;
    }

    [Benchmark]
    public D.World DefaultEcs_Binary()
    {
        _stream.SetLength(0);
        _binary.Serialize(_stream, _default);
        _stream.Position = 0;
        _dLoaded?.Dispose();                     // the world of the previous load
        return _dLoaded = _binary.Deserialize(_stream);
    }

    /// <summary>Warm up, then check the loaded world against the saved one (a checksum over every unit).</summary>
    private static void Check(Action roundTrip, Func<long> loaded)
    {
        for (int i = 0; i < 5; i++) roundTrip();
        long expected = 0;
        for (int i = 0; i < Units; i++) expected += i + i % 100 + i % 7;
        if (loaded() != expected) throw new InvalidOperationException($"loaded checksum {loaded()}, expected {expected}");
    }
}
