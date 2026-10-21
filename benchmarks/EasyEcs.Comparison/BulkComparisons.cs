using System;
using BenchmarkDotNet.Attributes;
using EasyEcs;
using A = Arch.Core;
using D = DefaultEcs;
using F = Friflo.Engine.ECS;
using static Friflo.Engine.ECS.EntityStoreExtensions;

namespace EcsComparison.Features;

// Bulk changes, each library the way its docs recommend: a bulk call where the library has one, otherwise a loop over
// the entities. Each setup checks the library's result before anything is measured.

public record struct Vitals(int Value) : EasyEcs.IComponent;
public record struct Stunned(int Ticks) : EasyEcs.IComponent;
public record struct Shot(int X, int Y) : EasyEcs.IComponent;
public record struct Fuse(int Ticks) : EasyEcs.IComponent;

public struct FVitals : F.IComponent { public int Value; }
public struct FStunned : F.IComponent { public int Ticks; }
public struct FShot : F.IComponent { public int X, Y; }
public struct FFuse : F.IComponent { public int Ticks; }

public struct AVitals { public int Value; }
public struct AStunned { public int Ticks; }
public struct AShot { public int X, Y; }
public struct AFuse { public int Ticks; }

public record struct DVitals(int Value);
public record struct DStunned(int Ticks);
public record struct DShot(int X, int Y);
public record struct DFuse(int Ticks);

/// <summary>
/// A round reset: 100,000 fighters are all stunned, then the stun is lifted from everyone.
/// <list type="bullet">
/// <item>EasyEcs: <c>SetAll</c> / <c>RemoveAll</c> (after the first round the stun keeps its column: presence bits flip,
/// no row moves).</item>
/// <item>Friflo: an <c>EntityBatch</c> applied to a query's entities (<c>ApplyBatch</c>).</item>
/// <item>Arch: <c>world.Add(query, value)</c> / <c>world.Remove&lt;T&gt;(query)</c> (whole tables move).</item>
/// <item>DefaultEcs has no bulk change: <c>Set</c> / <c>Remove</c> per entity (sparse components, no move).</item>
/// </list>
/// </summary>
[MemoryDiagnoser]
public class BulkChangeComparison
{
    private const int Fighters = 100_000;

    private EasyEcs.World _world;
    private F.EntityStore _store;
    private F.ArchetypeQuery _fAll, _fStunned;
    private readonly F.EntityBatch _stun = new(), _lift = new();
    private A.World _arch;
    private D.World _default;
    private D.Entity[] _dFighters;

    private static readonly QueryFilter All = QueryFilter.Create().With<Vitals>();
    private static readonly A.QueryDescription ArchAll = new A.QueryDescription().WithAll<AVitals>();
    private static readonly A.QueryDescription ArchStunned = new A.QueryDescription().WithAll<AVitals, AStunned>();

    [GlobalSetup(Target = nameof(EasyEcs_SetAllRemoveAll))]
    public void SetupEasyEcs()
    {
        _world = new EasyEcs.World(new WorldOptions { InitialEntityCapacity = Fighters });
        _world.CreateEntities(new EasyEcs.Entity[Fighters], new Vitals(100));
        Check(() => EasyEcs_SetAllRemoveAll(), () => _world.Count<Stunned>(), () => _world.Count<Vitals>());
    }

    [GlobalSetup(Target = nameof(Friflo_ApplyBatch))]
    public void SetupFriflo()
    {
        _store = new F.EntityStore();
        for (int i = 0; i < Fighters; i++) _store.CreateEntity(new FVitals { Value = 100 });
        _fAll = _store.Query().AllComponents(F.ComponentTypes.Get<FVitals>());
        _fStunned = _store.Query().AllComponents(F.ComponentTypes.Get<FVitals, FStunned>());
        _stun.Add(new FStunned { Ticks = 3 });
        _lift.Remove<FStunned>();
        Check(() => Friflo_ApplyBatch(), () => _fStunned.Count, () => _fAll.Count);
    }

    [GlobalSetup(Target = nameof(Arch_AddRemoveByQuery))]
    public void SetupArch()
    {
        _arch = A.World.Create();
        for (int i = 0; i < Fighters; i++) _arch.Create(new AVitals { Value = 100 });
        Check(() => Arch_AddRemoveByQuery(), () => _arch.CountEntities(in ArchStunned), () => _arch.CountEntities(in ArchAll));
    }

    [GlobalSetup(Target = nameof(DefaultEcs_PerEntity))]
    public void SetupDefaultEcs()
    {
        _default = new D.World(Fighters);
        _dFighters = new D.Entity[Fighters];
        for (int i = 0; i < Fighters; i++)
        {
            _dFighters[i] = _default.CreateEntity();
            _dFighters[i].Set(new DVitals(100));
        }

        Check(() => DefaultEcs_PerEntity(), () => _default.GetEntities().With<DStunned>().AsSet().Count,
            () => _default.GetEntities().With<DVitals>().AsSet().Count);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _world?.Dispose();
        if (_arch != null) A.World.Destroy(_arch);
        _default?.Dispose();
        (_world, _store, _arch, _default) = (null, null, null, null);
    }

    [Benchmark(Baseline = true)]
    public int EasyEcs_SetAllRemoveAll() => _world.SetAll(new Stunned(3), in All) + _world.RemoveAll<Stunned>();

    [Benchmark]
    public int Friflo_ApplyBatch()
    {
        int stunned = _fAll.Count;
        _fAll.Entities.ApplyBatch(_stun);
        _fStunned.Entities.ApplyBatch(_lift);
        return stunned * 2;
    }

    [Benchmark]
    public int Arch_AddRemoveByQuery()
    {
        int stunned = _arch.CountEntities(in ArchAll);
        _arch.Add(in ArchAll, new AStunned { Ticks = 3 });
        _arch.Remove<AStunned>(in ArchStunned);
        return stunned * 2;
    }

    [Benchmark]
    public int DefaultEcs_PerEntity()
    {
        foreach (var fighter in _dFighters) fighter.Set(new DStunned(3));
        foreach (var fighter in _dFighters) fighter.Remove<DStunned>();
        return _dFighters.Length * 2;
    }

    /// <summary>One round changes every fighter twice and leaves none stunned, the fighters all alive.</summary>
    private static void Check(Func<int> round, Func<int> stunned, Func<int> fighters)
    {
        for (int i = 0; i < 3; i++)
            if (round() != Fighters * 2 || stunned() != 0 || fighters() != Fighters)
                throw new InvalidOperationException("the bulk change went wrong");
    }
}

/// <summary>
/// A volley: 10,000 shots are spawned beside 100,000 fighters, then all of them are cleared.
/// <list type="bullet">
/// <item>EasyEcs: <c>CreateEntities(span, values)</c> (one table, rows placed at once) and
/// <c>DestroyEntities&lt;Fuse&gt;()</c>.</item>
/// <item>Friflo: <c>CreateEntity(values)</c> per shot (no bulk create with values), then <c>DeleteEntity</c> per shot.</item>
/// <item>Arch: <c>Reserve</c> and <c>Create(values)</c> per shot (its <c>Create(span, signature, count)</c> made one
/// entity in 2.1.0), then <c>world.Destroy(query)</c>.</item>
/// <item>DefaultEcs: <c>CreateEntity</c> and <c>Set</c> per shot, then <c>Dispose</c> per shot.</item>
/// </list>
/// </summary>
[MemoryDiagnoser]
public class BulkSpawnComparison
{
    private const int Fighters = 100_000, Shots = 10_000;

    private EasyEcs.World _world;
    private readonly EasyEcs.Entity[] _shots = new EasyEcs.Entity[Shots];
    private F.EntityStore _store;
    private readonly F.Entity[] _fShots = new F.Entity[Shots];
    private F.ArchetypeQuery _fAll;
    private A.World _arch;
    private readonly A.Entity[] _aShots = new A.Entity[Shots];
    private D.World _default;
    private readonly D.Entity[] _dShots = new D.Entity[Shots];
    private int _tick;

    private static readonly A.Signature ArchShot = new(typeof(AShot), typeof(AFuse));
    private static readonly A.QueryDescription ArchShots = new A.QueryDescription().WithAll<AShot, AFuse>();

    [GlobalSetup(Target = nameof(EasyEcs_CreateAndDestroyAll))]
    public void SetupEasyEcs()
    {
        _world = new EasyEcs.World(new WorldOptions { InitialEntityCapacity = Fighters + Shots });
        _world.CreateEntities(new EasyEcs.Entity[Fighters], new Vitals(100));
        Check(() => EasyEcs_CreateAndDestroyAll(), () => _world.EntityCount);
    }

    [GlobalSetup(Target = nameof(Friflo_PerEntity))]
    public void SetupFriflo()
    {
        _store = new F.EntityStore();
        for (int i = 0; i < Fighters; i++) _store.CreateEntity(new FVitals { Value = 100 });
        _fAll = _store.Query();
        Check(() => Friflo_PerEntity(), () => _fAll.Count);
    }

    [GlobalSetup(Target = nameof(Arch_CreateAndDestroyByQuery))]
    public void SetupArch()
    {
        _arch = A.World.Create();
        for (int i = 0; i < Fighters; i++) _arch.Create(new AVitals { Value = 100 });
        Check(() => Arch_CreateAndDestroyByQuery(), () => _arch.Size + _arch.CountEntities(in ArchShots));
    }

    [GlobalSetup(Target = nameof(DefaultEcs_PerEntity))]
    public void SetupDefaultEcs()
    {
        _default = new D.World(Fighters + Shots);
        for (int i = 0; i < Fighters; i++) _default.CreateEntity().Set(new DVitals(100));
        Check(() => DefaultEcs_PerEntity(), () => _default.GetEntities().AsSet().Count);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _world?.Dispose();
        if (_arch != null) A.World.Destroy(_arch);
        _default?.Dispose();
        (_world, _store, _arch, _default) = (null, null, null, null);
    }

    [Benchmark(Baseline = true)]
    public int EasyEcs_CreateAndDestroyAll()
    {
        _world.CreateEntities(_shots, new Shot(_tick, ++_tick), new Fuse(20));
        return _world.DestroyAll<Fuse>();
    }

    [Benchmark]
    public int Friflo_PerEntity()
    {
        for (int i = 0; i < Shots; i++) _fShots[i] = _store.CreateEntity(new FShot { X = _tick, Y = _tick + 1 }, new FFuse { Ticks = 20 });
        _tick++;
        foreach (var shot in _fShots) shot.DeleteEntity();
        return Shots;
    }

    [Benchmark]
    public int Arch_CreateAndDestroyByQuery()
    {
        _arch.EnsureCapacity(in ArchShot, Shots);
        for (int i = 0; i < Shots; i++) _aShots[i] = _arch.Create(new AShot { X = _tick, Y = _tick + 1 }, new AFuse { Ticks = 20 });
        _tick++;
        _arch.Destroy(in ArchShots);
        return Shots;
    }

    [Benchmark]
    public int DefaultEcs_PerEntity()
    {
        for (int i = 0; i < Shots; i++)
        {
            var shot = _dShots[i] = _default.CreateEntity();
            shot.Set(new DShot(_tick, _tick + 1));
            shot.Set(new DFuse(20));
        }

        _tick++;
        foreach (var shot in _dShots) shot.Dispose();
        return Shots;
    }

    /// <summary>One volley spawns and clears every shot and leaves the fighters alone.</summary>
    private static void Check(Func<int> volley, Func<int> alive)
    {
        for (int i = 0; i < 3; i++)
            if (volley() != Shots || alive() != Fighters)
                throw new InvalidOperationException($"the volley went wrong: {alive()} alive");
    }
}

public record struct Cold1(int Value) : EasyEcs.IComponent;
public record struct Cold2(int Value) : EasyEcs.IComponent;
public record struct Cold3(int Value) : EasyEcs.IComponent;

public struct FCold1 : F.IComponent { public int Value; }
public struct FCold2 : F.IComponent { public int Value; }
public struct FCold3 : F.IComponent { public int Value; }

public struct ACold1 { public int Value; }
public struct ACold2 { public int Value; }
public struct ACold3 { public int Value; }

public record struct DCold1(int Value);
public record struct DCold2(int Value);
public record struct DCold3(int Value);

/// <summary>
/// A server's start: 100,000 entities with one, two or three components created in a world that has never held any,
/// the setup of Doraku/Ecs.CSharp.Benchmark (CreateEntityWith*Components): every invocation gets a fresh world from
/// <c>[IterationSetup]</c> and is measured once, so the first growth of every table and array, and any collection the
/// allocations set off, is part of the cost. Each library creates entities one by one without pre-sizing
/// (<c>PerEntity</c>), one by one after its pre-sizing call (<c>Reserved</c>) and in one bulk call where it has one
/// (<c>Bulk</c>).
/// <list type="bullet">
/// <item>EasyEcs: <c>CreateEntity(values)</c> per entity, after <c>EnsureCapacity&lt;T1..Tn&gt;(count)</c> when reserved;
/// bulk: <c>CreateEntities(new Entity[count], values)</c> (the handle array is part of the cost).</item>
/// <item>Friflo: <c>archetype.CreateEntity()</c> per entity (default values, as the external suite writes it), after
/// <c>store.Reserve</c> and <c>archetype.Reserve</c> when reserved; no bulk create.</item>
/// <item>Arch: <c>Create(values)</c> per entity, after <c>EnsureCapacity(signature, count)</c> when reserved (its bulk
/// <c>Create(span, signature, count)</c> made one entity in 2.1.0).</item>
/// <item>DefaultEcs: <c>CreateEntity</c> and <c>Set</c> per component; it has no pre-sizing (its world's capacity is a
/// maximum) and no bulk create.</item>
/// </list>
/// The cleanup checks each library's entity count before it disposes the world.
/// </summary>
[MemoryDiagnoser]
[WarmupCount(10)]
[IterationCount(40)]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory, BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByParams)]
public class ColdCreateComparison
{
    private const int Count = 100_000;
    private const string PerEntity = "PerEntity", Reserved = "Reserved", Bulk = "Bulk";

    [Params(1, 2, 3)]
    public int Components { get; set; }

    private EasyEcs.World _world;
    private F.EntityStore _store;
    private A.World _arch;
    private D.World _default;

    private static readonly A.Signature Arch1 = new(typeof(ACold1));
    private static readonly A.Signature Arch2 = new(typeof(ACold1), typeof(ACold2));
    private static readonly A.Signature Arch3 = new(typeof(ACold1), typeof(ACold2), typeof(ACold3));

    [IterationSetup(Targets = [nameof(EasyEcs_PerEntity), nameof(EasyEcs_PerEntity_Reserved), nameof(EasyEcs_Bulk)])]
    public void SetupEasyEcs() => _world = new EasyEcs.World();

    [IterationSetup(Targets = [nameof(Friflo_PerEntity), nameof(Friflo_PerEntity_Reserved)])]
    public void SetupFriflo() => _store = new F.EntityStore(F.PidType.UsePidAsId);

    [IterationSetup(Targets = [nameof(Arch_PerEntity), nameof(Arch_PerEntity_Reserved)])]
    public void SetupArch() => _arch = A.World.Create();

    [IterationSetup(Target = nameof(DefaultEcs_PerEntity))]
    public void SetupDefaultEcs() => _default = new D.World();

    [IterationCleanup]
    public void Cleanup()
    {
        int created = _world?.EntityCount ?? _store?.Count ?? _arch?.Size ?? _default?.GetEntities().AsSet().Count ?? -1;
        _world?.Dispose();
        if (_arch != null) A.World.Destroy(_arch);
        _default?.Dispose();
        (_world, _store, _arch, _default) = (null, null, null, null);
        if (created != Count) throw new InvalidOperationException($"created {created} entities, not {Count}");
    }

    /// <summary>Every case once on a fresh world, its entity count checked (the <c>--features</c> run).</summary>
    public void CheckAll()
    {
        (Action Setup, Action Create)[] cases =
        [
            (SetupEasyEcs, EasyEcs_PerEntity), (SetupEasyEcs, EasyEcs_PerEntity_Reserved), (SetupEasyEcs, EasyEcs_Bulk),
            (SetupFriflo, Friflo_PerEntity), (SetupFriflo, Friflo_PerEntity_Reserved),
            (SetupArch, Arch_PerEntity), (SetupArch, Arch_PerEntity_Reserved), (SetupDefaultEcs, DefaultEcs_PerEntity),
        ];
        for (int components = 1; components <= 3; components++)
        {
            Components = components;
            foreach (var (setup, create) in cases)
            {
                setup();
                create();
                Cleanup();
            }
        }
    }

    [Benchmark(Baseline = true), BenchmarkCategory(PerEntity)]
    public void EasyEcs_PerEntity() => EasyEcsLoop(_world);

    [Benchmark(Baseline = true), BenchmarkCategory(Reserved)]
    public void EasyEcs_PerEntity_Reserved()
    {
        var world = _world;
        switch (Components)
        {
            case 1: world.Reserve<Cold1>(Count); break;
            case 2: world.Reserve<Cold1, Cold2>(Count); break;
            default: world.Reserve<Cold1, Cold2, Cold3>(Count); break;
        }

        EasyEcsLoop(world);
    }

#pragma warning disable EECS031 // one CreateEntity per entity is what the per-entity cases measure
    private void EasyEcsLoop(EasyEcs.World world)
    {
        switch (Components)
        {
            case 1:
                for (int i = 0; i < Count; i++) world.CreateEntity(new Cold1(i));
                break;
            case 2:
                for (int i = 0; i < Count; i++) world.CreateEntity(new Cold1(i), new Cold2(i));
                break;
            default:
                for (int i = 0; i < Count; i++) world.CreateEntity(new Cold1(i), new Cold2(i), new Cold3(i));
                break;
        }
    }
#pragma warning restore EECS031

    [Benchmark(Baseline = true), BenchmarkCategory(Bulk)]
    public void EasyEcs_Bulk()
    {
        switch (Components)
        {
            case 1: _world.CreateEntities(new EasyEcs.Entity[Count], new Cold1(1)); break;
            case 2: _world.CreateEntities(new EasyEcs.Entity[Count], new Cold1(1), new Cold2(2)); break;
            default: _world.CreateEntities(new EasyEcs.Entity[Count], new Cold1(1), new Cold2(2), new Cold3(3)); break;
        }
    }

    [Benchmark, BenchmarkCategory(PerEntity)]
    public void Friflo_PerEntity() => FrifloLoop(FrifloArchetype());

    [Benchmark, BenchmarkCategory(Reserved)]
    public void Friflo_PerEntity_Reserved()
    {
        _store.EnsureCapacity(Count);
        var archetype = FrifloArchetype();
        archetype.EnsureCapacity(Count);
        FrifloLoop(archetype);
    }

    private F.Archetype FrifloArchetype() => _store.GetArchetype(Components switch
    {
        1 => F.ComponentTypes.Get<FCold1>(),
        2 => F.ComponentTypes.Get<FCold1, FCold2>(),
        _ => F.ComponentTypes.Get<FCold1, FCold2, FCold3>(),
    });

    private static void FrifloLoop(F.Archetype archetype)
    {
        for (int i = 0; i < Count; i++) archetype.CreateEntity();
    }

    [Benchmark, BenchmarkCategory(PerEntity)]
    public void Arch_PerEntity() => ArchLoop(_arch);

    [Benchmark, BenchmarkCategory(Reserved)]
    public void Arch_PerEntity_Reserved()
    {
        var world = _arch;
        switch (Components)
        {
            case 1: world.EnsureCapacity(in Arch1, Count); break;
            case 2: world.EnsureCapacity(in Arch2, Count); break;
            default: world.EnsureCapacity(in Arch3, Count); break;
        }

        ArchLoop(world);
    }

    private void ArchLoop(A.World world)
    {
        switch (Components)
        {
            case 1:
                for (int i = 0; i < Count; i++) world.Create(new ACold1 { Value = i });
                break;
            case 2:
                for (int i = 0; i < Count; i++) world.Create(new ACold1 { Value = i }, new ACold2 { Value = i });
                break;
            default:
                for (int i = 0; i < Count; i++) world.Create(new ACold1 { Value = i }, new ACold2 { Value = i }, new ACold3 { Value = i });
                break;
        }
    }

    [Benchmark, BenchmarkCategory(PerEntity)]
    public void DefaultEcs_PerEntity()
    {
        var world = _default;
        switch (Components)
        {
            case 1:
                for (int i = 0; i < Count; i++) world.CreateEntity().Set(new DCold1(i));
                break;
            case 2:
                for (int i = 0; i < Count; i++)
                {
                    var entity = world.CreateEntity();
                    entity.Set(new DCold1(i));
                    entity.Set(new DCold2(i));
                }

                break;
            default:
                for (int i = 0; i < Count; i++)
                {
                    var entity = world.CreateEntity();
                    entity.Set(new DCold1(i));
                    entity.Set(new DCold2(i));
                    entity.Set(new DCold3(i));
                }

                break;
        }
    }
}
