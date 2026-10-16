using System;
using BenchmarkDotNet.Attributes;
using EasyEcs;
using A = Arch.Core;
using D = DefaultEcs;
using F = Friflo.Engine.ECS;
using static Friflo.Engine.ECS.EntityStoreExtensions;
using static Friflo.Engine.ECS.IndexExtensions;

namespace EcsComparison.Features;

// The two jobs change tracking and value indexes exist for, written in each library the way its docs recommend:
// a built-in feature where the library has one, otherwise the usual workaround (scan and compare, or a dirty tag).
// Each library's setup checks that its result is correct before anything is measured.

/// <summary>EasyEcs change tracking: health is tracked, the replicated copy is not.</summary>
public record struct Hp(int Value) : EasyEcs.IComponent, ITrackChanges;

public record struct Replica(int Value) : EasyEcs.IComponent;

public struct ChangedOnly : ISystemGroup;
public struct ScanAll : ISystemGroup;

[UpdateInGroup<ChangedOnly>]
public partial class ReplicateChanged : EcsSystem
{
    [ForEach, Changed<Hp>]
    private static void Copy(in Hp hp, ref Replica replica) => replica = new Replica(hp.Value);
}

[UpdateInGroup<ScanAll>]
public partial class ReplicateScan : EcsSystem
{
    [ForEach]
    private static void Copy(in Hp hp, ref Replica replica)
    {
        if (replica.Value != hp.Value) replica = new Replica(hp.Value);
    }
}

/// <summary>EasyEcs relation: an item points at its owner, indexed by the owner.</summary>
public record struct ItemOf(EasyEcs.Entity Owner) : EasyEcs.IComponent, IIndexed<EasyEcs.Entity>
{
    public EasyEcs.Entity Key => Owner;
}

public struct FHp : F.IComponent { public int Value; }
public struct FReplica : F.IComponent { public int Value; }
public struct FDirty : F.ITag;

public struct FItemOf : F.ILinkComponent
{
    public F.Entity Owner;
    public F.Entity GetIndexedValue() => Owner;
}

public struct AHp { public int Value; }
public struct AReplica { public int Value; }
public struct ADirty;

public struct DHp { public int Value; }
public struct DReplica { public int Value; }
public record struct DItemOf(D.Entity Owner);

/// <summary>
/// Replicating changed state: 100,000 entities, <see cref="Written"/> of which get a new health per tick (through
/// handles; 10 is a quiet tick, 1,000 a busy one), then the changed healths are copied into the replicated copy.
/// <list type="bullet">
/// <item>EasyEcs: <c>[Changed&lt;Hp&gt;]</c> (per-row change ticks), and a full scan for reference.</item>
/// <item>DefaultEcs: an entity set built with <c>WhenChanged&lt;T&gt;()</c>, fed by <c>NotifyChanged&lt;T&gt;()</c>.</item>
/// <item>Friflo and Arch have no change filter: a full scan comparing values, or a <c>Dirty</c> tag added on write
/// and removed after the copy (Friflo through its command buffer, Arch with its bulk remove by query).</item>
/// </list>
/// </summary>
[MemoryDiagnoser]
public class ChangeTrackingComparison
{
    private const int Entities = 100_000;

    [Params(10, 1_000)] public int Written;

    private EasyEcs.World _world;
    private EasyEcs.Entity[] _entities;
    private F.EntityStore _store;
    private F.CommandBuffer _buffer;
    private F.Entity[] _fEntities;
    private F.ArchetypeQuery<FHp, FReplica> _fAll, _fDirty;
    private A.World _arch;
    private A.Entity[] _aEntities;
    private D.World _default;
    private D.Entity[] _dEntities;
    private D.EntitySet _dChanged;
    private int _next;

    private static readonly A.QueryDescription ArchAll = new A.QueryDescription().WithAll<AHp, AReplica>();
    private static readonly A.QueryDescription ArchDirty = new A.QueryDescription().WithAll<AHp, AReplica, ADirty>();

    [GlobalSetup(Targets = [nameof(EasyEcs_Changed), nameof(EasyEcs_Scan)])]
    public void SetupEasyEcs()
    {
        _world = new EasyEcs.World(new WorldOptions { InitialEntityCapacity = Entities });
        _entities = new EasyEcs.Entity[Entities];
        _world.CreateEntities(_entities, new Hp(100), new Replica(100));
        _world.AddSystem<ReplicateChanged>();
        _world.AddSystem<ReplicateScan>();
        WarmUp(EasyEcs_Changed, () => { foreach (var e in _entities) Check(e.Get<Replica>().Value == e.Get<Hp>().Value); });
        WarmUp(EasyEcs_Scan, () => { foreach (var e in _entities) Check(e.Get<Replica>().Value == e.Get<Hp>().Value); });
    }

    [GlobalSetup(Targets = [nameof(Friflo_Scan), nameof(Friflo_DirtyTag)])]
    public void SetupFriflo()
    {
        _store = new F.EntityStore();
        _buffer = _store.GetCommandBuffer();
        _buffer.ReuseBuffer = true;
        _fEntities = new F.Entity[Entities];
        for (int i = 0; i < Entities; i++)
            _fEntities[i] = _store.CreateEntity(new FHp { Value = 100 }, new FReplica { Value = 100 });
        _fAll = _store.Query<FHp, FReplica>();
        _fDirty = _store.Query<FHp, FReplica>().AllTags(F.Tags.Get<FDirty>());
        Action check = () => { foreach (var e in _fEntities) Check(e.GetComponent<FReplica>().Value == e.GetComponent<FHp>().Value); };
        WarmUp(Friflo_DirtyTag, check);
        WarmUp(Friflo_Scan, check);
    }

    [GlobalSetup(Targets = [nameof(Arch_Scan), nameof(Arch_DirtyTag)])]
    public void SetupArch()
    {
        _arch = A.World.Create();
        _aEntities = new A.Entity[Entities];
        for (int i = 0; i < Entities; i++) _aEntities[i] = _arch.Create(new AHp { Value = 100 }, new AReplica { Value = 100 });
        Action check = () => { foreach (var e in _aEntities) Check(_arch.Get<AReplica>(e).Value == _arch.Get<AHp>(e).Value); };
        WarmUp(Arch_DirtyTag, check);
        WarmUp(Arch_Scan, check);
    }

    [GlobalSetup(Target = nameof(DefaultEcs_WhenChanged))]
    public void SetupDefaultEcs()
    {
        _default = new D.World(Entities);
        _dEntities = new D.Entity[Entities];
        for (int i = 0; i < Entities; i++)
        {
            var e = _dEntities[i] = _default.CreateEntity();
            e.Set(new DHp { Value = 100 });
            e.Set(new DReplica { Value = 100 });
        }

        _dChanged = _default.GetEntities().With<DReplica>().WhenChanged<DHp>().AsSet();
        _dChanged.Complete();
        WarmUp(DefaultEcs_WhenChanged, () => { foreach (var e in _dEntities) Check(e.Get<DReplica>().Value == e.Get<DHp>().Value); });
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _world?.Dispose();
        if (_arch != null) A.World.Destroy(_arch);
        _default?.Dispose();
        (_world, _arch, _default) = (null, null, null);
    }

    /// <summary>The next entity that takes damage (a different slice every tick, the same order in every library).</summary>
    private int Next() => _next = (_next + 97) % Entities;

    [Benchmark(Baseline = true)]
    public void EasyEcs_Changed()
    {
        for (int i = 0; i < Written; i++) _entities[Next()].Get<Hp>().Value--;
        _world.Update<ChangedOnly>();
    }

    [Benchmark]
    public void EasyEcs_Scan()
    {
        for (int i = 0; i < Written; i++) _entities[Next()].Get<Hp>().Value--;
        _world.Update<ScanAll>();
    }

    [Benchmark]
    public void DefaultEcs_WhenChanged()
    {
        for (int i = 0; i < Written; i++)
        {
            var e = _dEntities[Next()];
            e.Get<DHp>().Value--;
            e.NotifyChanged<DHp>();
        }

        var hps = _default.GetComponents<DHp>();
        var replicas = _default.GetComponents<DReplica>();
        foreach (ref readonly var e in _dChanged.GetEntities()) replicas[e].Value = hps[e].Value;
        _dChanged.Complete();
    }

    [Benchmark]
    public void Friflo_Scan()
    {
        for (int i = 0; i < Written; i++) _fEntities[Next()].GetComponent<FHp>().Value--;
        foreach (var (hps, replicas, _) in _fAll.Chunks)
        {
            var h = hps.Span;
            var r = replicas.Span;
            for (int i = 0; i < h.Length; i++)
                if (r[i].Value != h[i].Value) r[i].Value = h[i].Value;
        }
    }

    [Benchmark]
    public void Friflo_DirtyTag()
    {
        for (int i = 0; i < Written; i++)
        {
            var e = _fEntities[Next()];
            e.GetComponent<FHp>().Value--;
            e.AddTag<FDirty>();
        }

        foreach (var (hps, replicas, entities) in _fDirty.Chunks)
        {
            var h = hps.Span;
            var r = replicas.Span;
            for (int i = 0; i < h.Length; i++)
            {
                r[i].Value = h[i].Value;
                _buffer.RemoveTag<FDirty>(entities.Ids[i]);
            }
        }

        _buffer.Playback();
    }

    [Benchmark]
    public void Arch_Scan()
    {
        for (int i = 0; i < Written; i++) _arch.Get<AHp>(_aEntities[Next()]).Value--;
        var copy = new CopyIfChanged();
        _arch.InlineQuery<CopyIfChanged, AHp, AReplica>(in ArchAll, ref copy);
    }

    [Benchmark]
    public void Arch_DirtyTag()
    {
        for (int i = 0; i < Written; i++)
        {
            var e = _aEntities[Next()];
            _arch.Get<AHp>(e).Value--;
            if (!_arch.Has<ADirty>(e)) _arch.Add<ADirty>(e);
        }

        var copy = new Copy();
        _arch.InlineQuery<Copy, AHp, AReplica>(in ArchDirty, ref copy);
        _arch.Remove<ADirty>(in ArchDirty);
    }

    private struct CopyIfChanged : A.IForEach<AHp, AReplica>
    {
        public void Update(ref AHp hp, ref AReplica replica)
        {
            if (replica.Value != hp.Value) replica.Value = hp.Value;
        }
    }

    private struct Copy : A.IForEach<AHp, AReplica>
    {
        public void Update(ref AHp hp, ref AReplica replica) => replica.Value = hp.Value;
    }

    /// <summary>Warm up, then check the replicated copy (setups run both variants of a library on one world).</summary>
    private static void WarmUp(Action tick, Action check)
    {
        for (int i = 0; i < 200; i++) tick();
        check();
    }

    private static void Check(bool ok)
    {
        if (!ok) throw new InvalidOperationException("the replicated copy does not match");
    }
}

/// <summary>
/// Inventories as a relation: 100,000 items, each pointing at one of 1,000 owners. Per operation <see cref="Trades"/>
/// items change hands (1: lookups dominate, as on most ticks; 100: a burst of trades) and the inventories of 100 owners
/// are counted, each library through its built-in index:
/// <list type="bullet">
/// <item>EasyEcs: <c>IIndexed&lt;Entity&gt;</c>, <c>world.Index&lt;ItemOf, Entity&gt;().Count(owner)</c>.</item>
/// <item>Friflo: <c>ILinkComponent</c>, <c>owner.GetIncomingLinks&lt;T&gt;().Count</c>; the index follows
/// <c>AddComponent</c>.</item>
/// <item>DefaultEcs: <c>EntityMultiMap&lt;T&gt;</c> keyed by the component value; it follows <c>Set</c>.</item>
/// </list>
/// Arch has no value index (relations live in a separate package), so it is not part of this comparison.
/// </summary>
[MemoryDiagnoser]
public class IndexComparison
{
    private const int Items = 100_000, Owners = 1_000, Counts = 100;

    [Params(1, 100)] public int Trades;

    private EasyEcs.World _world;
    private EasyEcs.Entity[] _owners, _items;
    private ValueIndex<ItemOf, EasyEcs.Entity> _index;
    private F.EntityStore _store;
    private F.Entity[] _fOwners, _fItems;
    private D.World _default;
    private D.Entity[] _dOwners, _dItems;
    private D.EntityMultiMap<DItemOf> _dIndex;
    private int _next;

    [GlobalSetup(Target = nameof(EasyEcs_Index))]
    public void SetupEasyEcs()
    {
        _world = new EasyEcs.World(new WorldOptions { InitialEntityCapacity = Items + Owners });
        _owners = new EasyEcs.Entity[Owners];
        for (int i = 0; i < Owners; i++) _owners[i] = _world.CreateEntity();
        _items = new EasyEcs.Entity[Items];
        for (int i = 0; i < Items; i++) _items[i] = _world.CreateEntity(new ItemOf(_owners[i % Owners]));
        _index = _world.Index<ItemOf, EasyEcs.Entity>();
        Check(EasyEcs_Index, o => _index.Count(_owners[o]), i => Array.IndexOf(_owners, _items[i].Get<ItemOf>().Owner));
    }

    [GlobalSetup(Target = nameof(Friflo_Links))]
    public void SetupFriflo()
    {
        _store = new F.EntityStore();
        _fOwners = new F.Entity[Owners];
        for (int i = 0; i < Owners; i++) _fOwners[i] = _store.CreateEntity();
        _fItems = new F.Entity[Items];
        for (int i = 0; i < Items; i++) _fItems[i] = _store.CreateEntity(new FItemOf { Owner = _fOwners[i % Owners] });
        Check(Friflo_Links, o => _fOwners[o].GetIncomingLinks<FItemOf>().Count,
            i => Array.IndexOf(_fOwners, _fItems[i].GetComponent<FItemOf>().Owner));
    }

    [GlobalSetup(Target = nameof(DefaultEcs_MultiMap))]
    public void SetupDefaultEcs()
    {
        _default = new D.World(Items + Owners);
        _dOwners = new D.Entity[Owners];
        for (int i = 0; i < Owners; i++) _dOwners[i] = _default.CreateEntity();
        _dItems = new D.Entity[Items];
        for (int i = 0; i < Items; i++)
        {
            _dItems[i] = _default.CreateEntity();
            _dItems[i].Set(new DItemOf(_dOwners[i % Owners]));
        }

        _dIndex = _default.GetEntities().AsMultiMap<DItemOf>();
        Check(DefaultEcs_MultiMap, o => _dIndex.Count(new DItemOf(_dOwners[o])),
            i => Array.IndexOf(_dOwners, _dItems[i].Get<DItemOf>().Owner));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _world?.Dispose();
        _dIndex?.Dispose();
        _default?.Dispose();
        (_world, _dIndex, _default) = (null, null, null);
    }

    [Benchmark(Baseline = true)]
    public int EasyEcs_Index()
    {
        for (int i = 0; i < Trades; i++)
        {
            int item = NextItem();
            _items[item].Get<ItemOf>() = new ItemOf(_owners[(item + i) % Owners]);
        }

        int total = 0;
        for (int i = 0; i < Counts; i++) total += _index.Count(_owners[Owner(i)]);
        return total;
    }

    [Benchmark]
    public int Friflo_Links()
    {
        for (int i = 0; i < Trades; i++)
        {
            int item = NextItem();
            _fItems[item].AddComponent(new FItemOf { Owner = _fOwners[(item + i) % Owners] });
        }

        int total = 0;
        for (int i = 0; i < Counts; i++) total += _fOwners[Owner(i)].GetIncomingLinks<FItemOf>().Count;
        return total;
    }

    [Benchmark]
    public int DefaultEcs_MultiMap()
    {
        for (int i = 0; i < Trades; i++)
        {
            int item = NextItem();
            _dItems[item].Set(new DItemOf(_dOwners[(item + i) % Owners]));
        }

        int total = 0;
        for (int i = 0; i < Counts; i++) total += _dIndex.Count(new DItemOf(_dOwners[Owner(i)]));
        return total;
    }

    private int NextItem() => _next = (_next + 7919) % Items;

    private int Owner(int i) => (_next + i * 13) % Owners;

    /// <summary>Warm up, then check every owner's inventory against the items' own components.</summary>
    private static void Check(Func<int> operation, Func<int, int> inventory, Func<int, int> ownerOf)
    {
        for (int i = 0; i < 200; i++) operation();
        var expected = new int[Owners];
        for (int i = 0; i < Items; i++) expected[ownerOf(i)]++;
        for (int o = 0; o < Owners; o++)
            if (inventory(o) != expected[o])
                throw new InvalidOperationException($"owner {o} holds {inventory(o)} items, expected {expected[o]}");
    }
}
