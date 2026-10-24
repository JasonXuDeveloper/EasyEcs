using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

namespace EasyEcs.Benchmark;

/// <summary>
/// What a [ForEach] method's declarations cost, one world update over 100,000 entities per case: the pairs the
/// analyzer's performance rules choose between (EECS025-EECS028; EECS052: 1,000 entities created and destroyed). Each
/// pair does the same work; the first of a category is the declaration the analyzer reports, the baseline the one it
/// suggests. 0 bytes.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class AccessBenchmark
{
    public struct Speed : IComponent { public int V; }
    public struct Armor : IComponent { public int V; }
    public struct Gold : IComponent, ITracked { public int V; }
    public struct Total : ISingletonComponent { public long V; }
    public struct Step : ISingletonComponent { public int V; }
    public struct Boss : IComponent;
    public struct Heat : IComponent { public long A, B; }
    public struct Cold : IComponent { public long A, B; }
    public struct T1 : IComponent;
    public struct T2 : IComponent;
    public struct T3 : IComponent;
    public struct T4 : IComponent;

    [Params(100_000)] public int Entities;

    private World _trackedRef, _trackedIn, _untrackedRef, _untrackedIn, _unused, _with, _tag, _withTag,
        _entityRead, _parameter, _entityHas, _maybe, _worldSingleton, _singleton, _mixed;

    private Entity[] _handles, _wide;
    private World _wideWorld, _spawn;
    private const int Spawns = 1_000;
    private readonly EntityId[] _spawned = new EntityId[Spawns];
    private static readonly QueryFilter Bosses = QueryFilter.Create().With<Boss>();

#pragma warning disable EECS025, EECS026, EECS027 // the reported side of each pair
    [EcsSystem]
    public ref struct TrackedRef
    {
        [ForEach] static void Sum(ref Gold gold, ref Total total) => total.V += gold.V;
    }

    [EcsSystem]
    public ref struct UntrackedRef
    {
        [ForEach] static void Sum(ref Speed speed, ref Total total) => total.V += speed.V;
    }

    [EcsSystem]
    public ref struct UnusedParameter
    {
        [ForEach] static void Run(ref Speed speed, in Armor armor) => speed.V++;
    }

    [EcsSystem]
    public ref struct TagParameter
    {
        [ForEach] static void Run(ref Speed speed, in Boss boss) => speed.V++;
    }

    [EcsSystem]
    public ref struct EntityRead
    {
        [ForEach] static void Run(Entity e, ref Speed speed) => speed.V += e.Read<Armor>().V;
    }

    [EcsSystem]
    public ref struct EntityHas
    {
        [ForEach] static void Run(Entity e, ref Speed speed) { if (e.Has<Boss>()) speed.V++; }
    }

    [EcsSystem]
    public ref struct WorldSingleton
    {
        [ForEach] static void Run(ref Speed speed, World world) => speed.V += world.GetSingleton<Step>().V;
    }
#pragma warning restore EECS025, EECS026, EECS027

    [EcsSystem]
    public ref struct TrackedIn
    {
        [ForEach] static void Sum(in Gold gold, ref Total total) => total.V += gold.V;
    }

    [EcsSystem]
    public ref struct UntrackedIn
    {
        [ForEach] static void Sum(in Speed speed, ref Total total) => total.V += speed.V;
    }

    [EcsSystem]
    public ref struct WithFilter
    {
        [ForEach, With<Armor>] static void Run(ref Speed speed) => speed.V++;
    }

    [EcsSystem]
    public ref struct WithTag
    {
        [ForEach, With<Boss>] static void Run(ref Speed speed) => speed.V++;
    }

    [EcsSystem]
    public ref struct Parameter
    {
        [ForEach] static void Run(ref Speed speed, in Armor armor) => speed.V += armor.V;
    }

    [EcsSystem]
    public ref struct MaybeParameter
    {
        [ForEach] static void Run(ref Speed speed, ReadOnlyMaybe<Boss> boss) { if (boss.HasValue) speed.V++; }
    }

    [EcsSystem]
    public ref struct SingletonParameter
    {
        [ForEach] static void Run(ref Speed speed, in Step step) => speed.V += step.V;
    }

    [EcsSystem]
    public ref struct StaticMethod
    {
        [ForEach] static void Run(ref Speed speed) => speed.V++;
    }

    [GlobalSetup]
    public void Setup()
    {
        _trackedRef = Build<TrackedRef>();
        _trackedIn = Build<TrackedIn>();
        _untrackedRef = Build<UntrackedRef>();
        _untrackedIn = Build<UntrackedIn>();
        _unused = Build<UnusedParameter>();
        _with = Build<WithFilter>();
        _tag = Build<TagParameter>();
        _withTag = Build<WithTag>();
        _entityRead = Build<EntityRead>();
        _parameter = Build<Parameter>();
        _entityHas = Build<EntityHas>();
        _maybe = Build<MaybeParameter>();
        _worldSingleton = Build<WorldSingleton>();
        _singleton = Build<SingletonParameter>();

        // 16 tables (every combination of four tags), half of the rows bosses; the handles of 10,000 of them. The rows
        // spread over 32 tables, so the entity storage is what grows large: reserve it.
        _mixed = new World();
        _mixed.EnsureEntityCapacity(Entities);
        _handles = new Entity[10_000];
        for (int i = 0; i < Entities; i++)
        {
            var e = _mixed.CreateEntity(new Speed { V = i });
            if ((i & 1) != 0) e.Set(new T1());
            if ((i & 2) != 0) e.Set(new T2());
            if ((i & 4) != 0) e.Set(new T3());
            if ((i & 8) != 0) e.Set(new T4());
            if ((i & 16) != 0) e.Set(new Boss());
            if (i < _handles.Length) _handles[i] = e;
        }

        _wideWorld = Build<StaticMethod>();
        _wide = new Entity[10_000];
        int n = 0;
        foreach (var item in _wideWorld.Query<Speed>())
            if (n < _wide.Length) _wide[n++] = item.Entity;
        _spawn = new World();
        CreateThenSet();
        CreateWithComponent();
        SetOneByOne();
        SetTogether();
        WideOneByOne();
        WideTogether();
    }

    /// <summary>One table of every component (a boss tag on every row), the singletons, and the one system.</summary>
    private World Build<TSystem>() where TSystem : struct, allows ref struct
    {
        var world = new World(new WorldOptions { InitialEntityCapacity = Entities });
        world.Reserve<Speed, Armor, Gold, Boss>(Entities);
        for (int i = 0; i < Entities; i++)
            world.CreateEntity(new Speed { V = i }, new Armor { V = 1 }, new Gold { V = i }, new Boss());
        world.AddSingleton<Total>();
        world.AddSingleton<Step>().V = 1;
        world.AddSystem<TSystem>();
        return world;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (var world in new[] { _trackedRef, _trackedIn, _untrackedRef, _untrackedIn, _unused, _with, _tag, _withTag,
                     _entityRead, _parameter, _entityHas, _maybe, _worldSingleton, _singleton, _mixed, _wideWorld, _spawn })
            world.Dispose();
    }

    [Benchmark, BenchmarkCategory("TrackedReadOnly")] public void TrackedByRef() => _trackedRef.Update();
    [Benchmark(Baseline = true), BenchmarkCategory("TrackedReadOnly")] public void TrackedByIn() => _trackedIn.Update();

    [Benchmark, BenchmarkCategory("UntrackedReadOnly")] public void UntrackedByRef() => _untrackedRef.Update();
    [Benchmark(Baseline = true), BenchmarkCategory("UntrackedReadOnly")] public void UntrackedByIn() => _untrackedIn.Update();

    [Benchmark, BenchmarkCategory("UnusedComponent")] public void UnusedParameterRow() => _unused.Update();
    [Benchmark(Baseline = true), BenchmarkCategory("UnusedComponent")] public void WithAttribute() => _with.Update();

    [Benchmark, BenchmarkCategory("Tag")] public void TagParameterRow() => _tag.Update();
    [Benchmark(Baseline = true), BenchmarkCategory("Tag")] public void WithTagAttribute() => _withTag.Update();

    [Benchmark, BenchmarkCategory("OwnComponent")] public void EntityReadPerRow() => _entityRead.Update();
    [Benchmark(Baseline = true), BenchmarkCategory("OwnComponent")] public void ComponentParameter() => _parameter.Update();

    [Benchmark, BenchmarkCategory("OptionalComponent")] public void EntityHasPerRow() => _entityHas.Update();
    [Benchmark(Baseline = true), BenchmarkCategory("OptionalComponent")] public void MaybeParameterRow() => _maybe.Update();

    [Benchmark, BenchmarkCategory("Singleton")] public void WorldSingletonPerRow() => _worldSingleton.Update();
    [Benchmark(Baseline = true), BenchmarkCategory("Singleton")] public void SingletonParameterRow() => _singleton.Update();

    [Benchmark, BenchmarkCategory("SeveralComponents")]
    public int SetOneByOne()
    {
        foreach (var e in _handles)
        {
            e.Set(new Armor { V = 1 });
            e.Set(new Gold { V = 1 });
            e.Remove<Armor>();
            e.Remove<Gold>();
        }

        return _handles.Length;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SeveralComponents")]
    public int SetTogether()
    {
        foreach (var e in _handles)
        {
            e.Set(new Armor { V = 1 }, new Gold { V = 1 });
            e.Remove<Armor, Gold>();
        }

        return _handles.Length;
    }

#pragma warning disable EECS032
    [Benchmark, BenchmarkCategory("CountOrAny")] public bool CountAboveZero() => _mixed.Count<Speed>() > 0;
#pragma warning restore EECS032
    [Benchmark(Baseline = true), BenchmarkCategory("CountOrAny")] public bool AnyCall() => _mixed.Any<Speed>();

    [Benchmark, BenchmarkCategory("FilterInLoop")]
    public long HasInLoop()
    {
        long sum = 0;
#pragma warning disable EECS032
        foreach (var item in _mixed.Query<Speed>())
        {
            if (!item.Entity.Has<Boss>()) continue;
            sum += item.Component1.V;
        }
#pragma warning restore EECS032

        return sum;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("FilterInLoop")]
    public long WithFilterQuery()
    {
        long sum = 0;
        foreach (var item in _mixed.Query<Speed>(Bosses)) sum += item.Component1.V;
        return sum;
    }

    [Benchmark, BenchmarkCategory("CreateThenSet")]
    public int CreateThenSet()
    {
        for (int i = 0; i < Spawns; i++)
        {
#pragma warning disable EECS052 // the reported side
            var e = _spawn.CreateEntity(new Speed { V = i });
#pragma warning restore EECS052
            e.Set(new Armor { V = 1 });
            _spawned[i] = e;
        }

        return _spawn.DestroyEntities(_spawned);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("CreateThenSet")]
    public int CreateWithComponent()
    {
        for (int i = 0; i < Spawns; i++) _spawned[i] = _spawn.CreateEntity(new Speed { V = i }, new Armor { V = 1 });
        return _spawn.DestroyEntities(_spawned);
    }

    [Benchmark, BenchmarkCategory("SeveralComponentsWide")]
    public int WideOneByOne()
    {
        foreach (var e in _wide)
        {
            e.Set(new Heat { A = 1 });
            e.Set(new Cold { A = 1 });
            e.Remove<Heat>();
            e.Remove<Cold>();
        }

        return _wide.Length;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SeveralComponentsWide")]
    public int WideTogether()
    {
        foreach (var e in _wide)
        {
            e.Set(new Heat { A = 1 }, new Cold { A = 1 });
            e.Remove<Heat, Cold>();
        }

        return _wide.Length;
    }
}
