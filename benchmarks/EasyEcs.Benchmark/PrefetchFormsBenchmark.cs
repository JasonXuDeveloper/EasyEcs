using System;
using BenchmarkDotNet.Attributes;
using EasyEcs.Benchmark.Locality;

namespace EasyEcs.Benchmark;

/// <summary>
/// The prefetching forms beyond the row form's entity chase (borrowed-ideas.md item 1): index probes
/// (<c>TryFind</c> / <c>Count</c> at a key a row holds, a hashed unique key and a key many entities share), chases per
/// element of a buffer of ids, a parallel probe, a lambda pass, and a walk two methods share (fusion). Each plain method
/// runs the generated code: past its form's cache (the last level for a probe on one thread, the L2 for a parallel
/// one) the pass picks the twin job that prefetches; Off and On force the gate (Off is today's job).
/// <see cref="PrefetchGate.Gates"/> narrows the gates to Auto for a base / head comparison (EASYECS_BENCH_GATES=auto).
/// </summary>
[MemoryDiagnoser]
public class ProbePrefetchBenchmark
{
    [ParamsSource(typeof(PrefetchGate), nameof(PrefetchGate.Sizes))] public int Entities;

    [ParamsSource(typeof(PrefetchGate), nameof(PrefetchGate.Gates))] public PrefetchGate.Gate Prefetch;

    public record struct Login(int Id) : IComponent, IUniqueIndexed<int>
    {
        public int Key => Id;
    }

    public record struct Clan(int Id) : IComponent, IIndexed<int>
    {
        public int Key => Id;
    }

    public record struct Seeker(int Login, int Clan) : IComponent;
    public record struct Found(int Value) : IComponent;

    private World _world;
    private PrefetchGate _gate;

    [GlobalSetup]
    public void Setup()
    {
        _gate = PrefetchGate.Set(Prefetch);
        _world = new World(new WorldOptions { InitialEntityCapacity = Entities * 3 });
        var random = new Random(42);
        var created = new Entity[Entities];
        _world.Reserve<Login>(Entities);
        for (int i = 0; i < Entities; i++) _world.CreateEntity(new Login(i * 7)); // unique (each key claimed): a hash probe each
        _world.CreateEntities(created, new Clan());
        for (int i = 0; i < created.Length; i++) created[i].Set(new Clan(i / 4)); // four entities per key
        _world.CreateEntities(created, new Seeker(), new Found());
        for (int i = 0; i < created.Length; i++) created[i].Get<Seeker>() = new Seeker(random.Next(Entities) * 7, random.Next(Entities / 4 + 1));
        for (int i = 0; i < 3; i++)
        {
            FindLogin();
            CountClan();
            FindLoginParallel();
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _world.Dispose();
        _gate.Dispose();
    }

    [ForEach]
    private static void Find(in Seeker seeker, ref Found found, Lookup<Login> logins) =>
        found.Value = logins.TryFind(seeker.Login, out var entity) ? entity.Id.Index : -1;

    [ForEach]
    private static void Count(in Seeker seeker, ref Found found, Lookup<Clan> clans) => found.Value += clans.Count(seeker.Clan);

    [ForEach(Parallel = true)]
    private static void FindParallel(in Seeker seeker, ref Found found, Lookup<Login> logins) =>
        found.Value = logins.TryFind(seeker.Login, out var entity) ? entity.Id.Index : -1;

    [Benchmark(Baseline = true)]
    public void FindLogin() => ProbePrefetchBenchmarkForEach.Find(_world);

    [Benchmark]
    public void CountClan() => ProbePrefetchBenchmarkForEach.Count(_world);

    [Benchmark]
    public void FindLoginParallel() => ProbePrefetchBenchmarkForEach.FindParallel(_world);

    [Benchmark]
    public void FindLoginLambda() =>
        _world.ForEach(static (in Seeker seeker, ref Found found, Lookup<Login> logins) =>
            found.Value = logins.TryFind(seeker.Login, out var entity) ? entity.Id.Index : -2);
}

/// <summary>
/// A chase per element of a buffer of ids (two allies per entity, picked at random) and a walk two methods share
/// (fusion, the first chasing): see <see cref="ProbePrefetchBenchmark"/>.
/// </summary>
[MemoryDiagnoser]
public class ChaseFormsPrefetchBenchmark
{
    [ParamsSource(typeof(PrefetchGate), nameof(PrefetchGate.Sizes))] public int Entities;

    [ParamsSource(typeof(PrefetchGate), nameof(PrefetchGate.Gates))] public PrefetchGate.Gate Prefetch;

    public record struct Ally(EntityId Target);

    private World _allies, _hunters;
    private PrefetchGate _gate;

    [GlobalSetup]
    public void Setup()
    {
        _gate = PrefetchGate.Set(Prefetch);
        _allies = new World(new WorldOptions { InitialEntityCapacity = Entities });
        var members = new Entity[Entities];
        _allies.CreateEntities(members, new Chase.Position(1, 2), new Chase.Heading(), new DynamicBuffer<Ally>());
        var random = new Random(42);
        for (int i = 0; i < members.Length; i++)
        {
            members[i].Get<Chase.Position>() = new Chase.Position(i * 0.5f, i * 0.25f);
            ref var allies = ref members[i].Get<DynamicBuffer<Ally>>();
            allies.Add(new Ally(members[random.Next(members.Length)].Id));
            allies.Add(new Ally(members[random.Next(members.Length)].Id));
        }

        _hunters = Chase.Create(Entities);
        _hunters.AddSystem<HuntWalkSystem>();
        for (int i = 0; i < 3; i++)
        {
            Party();
            Walk();
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _allies.Dispose();
        _hunters.Dispose();
        _gate.Dispose();
    }

    [ForEach]
    private static void Gather(in DynamicBuffer<Ally> allies, in Chase.Position position, ref Chase.Heading heading, Lookup<Chase.Position> positions)
    {
        float x = 0, y = 0;
        foreach (var ally in allies)
        {
            var at = positions[ally.Target];
            if (!at.HasValue) continue;
            x += at.Value.X - position.X;
            y += at.Value.Y - position.Y;
        }
        heading = new Chase.Heading(x, y);
    }

    [Benchmark(Baseline = true)]
    public void Party() => ChaseFormsPrefetchBenchmarkForEach.Gather(_allies);

    /// <summary>The fused walk of <see cref="HuntWalkSystem"/>: one update.</summary>
    [Benchmark]
    public void Walk() => _hunters.Update();
}

/// <summary>Two methods over the same rows, one walk (fusion): the first chases its prey, the second steers by it.</summary>
[EcsSystem]
public ref struct HuntWalkSystem
{
    [ForEach]
    private static void Aim(in Chase.Position position, in Chase.Prey prey, ref Chase.Heading heading, Lookup<Chase.Position> positions)
    {
        var at = positions[prey.Target];
        heading = at.HasValue ? new Chase.Heading(at.Value.X - position.X, at.Value.Y - position.Y) : default;
    }

    [ForEach]
    private static void Steer(in Chase.Position position, in Chase.Prey prey, ref Chase.Heading heading) =>
        heading = new Chase.Heading(heading.X * 0.5f + position.X, heading.Y * 0.5f + prey.Target.Index);
}

/// <summary>
/// The gates of a prefetching benchmark: Auto reads this machine's caches (each form's gate: the last level, or the L2
/// for the forms that pay past it), Off and On set the sizes EasyEcs.Core reads (internal, set as the tests do: 0 is
/// unknown, never past; 1 byte, always past) until disposed. The sizes measured are the class's own, or
/// EASYECS_BENCH_ENTITIES (a comma-separated list: the sizes between this machine's L2 and last-level cache).
/// </summary>
public sealed class PrefetchGate : IDisposable
{
    public enum Gate { Auto, Off, On }

    /// <summary>The gates a run measures: all three, or only Auto when EASYECS_BENCH_GATES=auto (a base / head comparison).</summary>
    public static Gate[] Gates =>
        string.Equals(Environment.GetEnvironmentVariable("EASYECS_BENCH_GATES"), "auto", StringComparison.OrdinalIgnoreCase)
            ? [Gate.Auto]
            : [Gate.Auto, Gate.Off, Gate.On];

    /// <summary>The entity counts of a run: EASYECS_BENCH_ENTITIES when set, else <paramref name="sizes"/>.</summary>
    public static int[] Entities(params int[] sizes) =>
        Environment.GetEnvironmentVariable("EASYECS_BENCH_ENTITIES") is { Length: > 0 } list
            ? Array.ConvertAll(list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), int.Parse)
            : sizes;

    /// <summary>The default sizes of the prefetch classes: in L1, past the L2, inside and past the last-level cache here.</summary>
    public static int[] Sizes => Entities(1_000, 262_144, 1_000_000, 16_000_000);

    private static readonly Type CacheSizes = typeof(World).Assembly.GetType("EasyEcs.CacheSizes")!;

    private static readonly System.Reflection.FieldInfo[] Caches =
    [
        CacheSizes.GetField("LastLevel", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!,
        CacheSizes.GetField("L2", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!,
    ];

    private readonly long[] _machine;

    private PrefetchGate(long[] machine) => _machine = machine;

    public static PrefetchGate Set(Gate gate)
    {
        Isa.Print();
        var set = new PrefetchGate(Array.ConvertAll(Caches, c => (long)c.GetValue(null)!));
        Console.WriteLine($"// last-level cache: {set._machine[0]} bytes, L2: {set._machine[1]} bytes");
        if (gate != Gate.Auto)
            foreach (var cache in Caches) cache.SetValue(null, gate == Gate.On ? 1L : 0L);
        return set;
    }

    public void Dispose()
    {
        for (int i = 0; i < Caches.Length; i++) Caches[i].SetValue(null, _machine[i]);
    }
}

/// <summary>
/// The core's loops over a list of ids (borrowed-ideas.md item 1, the bulk calls): DestroyEntities of a sixteenth of
/// the entities picked at random, then as many created again (their ids recycled), so every operation starts from the
/// same world. Past the cache the destroy prefetches the records of the ids ahead; Off and On force the gate.
/// </summary>
[MemoryDiagnoser]
public class IdListPrefetchBenchmark
{
    [ParamsSource(typeof(PrefetchGate), nameof(PrefetchGate.Sizes))] public int Entities;

    [ParamsSource(typeof(PrefetchGate), nameof(PrefetchGate.Gates))] public PrefetchGate.Gate Prefetch;

    private World _world;
    private PrefetchGate _gate;
    private Entity[] _all, _fresh;
    private int[] _picks;
    private EntityId[] _ids;

    [GlobalSetup]
    public void Setup()
    {
        _gate = PrefetchGate.Set(Prefetch);
        _world = new World(new WorldOptions { InitialEntityCapacity = Entities });
        _all = new Entity[Entities];
        _world.CreateEntities(_all, new Chase.Position(1, 2), new Chase.Heading());
        var order = new int[Entities];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        new Random(42).Shuffle(order);
        _picks = order.AsSpan(0, Entities / 16).ToArray();
        _ids = new EntityId[_picks.Length];
        _fresh = new Entity[_picks.Length];
        for (int i = 0; i < 3; i++) DestroyAndCreate();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _world.Dispose();
        _gate.Dispose();
    }

    [Benchmark]
    public int DestroyAndCreate()
    {
        for (int j = 0; j < _picks.Length; j++) _ids[j] = _all[_picks[j]].Id;
        int destroyed = _world.DestroyEntities(_ids);
        _world.CreateEntities(_fresh, new Chase.Position(1, 2), new Chase.Heading());
        for (int j = 0; j < _picks.Length; j++) _all[_picks[j]] = _fresh[j];
        return destroyed;
    }
}
