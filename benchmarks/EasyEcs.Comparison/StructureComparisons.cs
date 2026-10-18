using System;
using BenchmarkDotNet.Attributes;
using EasyEcs;
using A = Arch.Core;
using D = DefaultEcs;
using F = Friflo.Engine.ECS;
using static Friflo.Engine.ECS.EntityStoreExtensions;
using static Friflo.Engine.ECS.RelationExtensions;

namespace EcsComparison.Features;

// Structure: hierarchies, relations, disabling entities and structural changes while iterating, each library the
// way its docs recommend. Each setup checks the library's result before anything is measured.

public record struct Squad(int Strength) : EasyEcs.IComponent;
public record struct Member(int Rank) : EasyEcs.IComponent;
public struct FSquad : F.IComponent { public int Strength; }
public struct FMember : F.IComponent { public int Rank; }

/// <summary>
/// A hierarchy with cascade: 1,000 squads (a leader and 10 members) are created, then the leaders destroyed with
/// their members, in a warm world.
/// <list type="bullet">
/// <item>EasyEcs: members are <c>ChildOf(leader)</c>; destroying the leaders destroys the members.</item>
/// <item>Friflo: <c>AddChild</c>; <c>DeleteEntity</c> on a leader detaches its children, so they are deleted first.</item>
/// </list>
/// Arch and DefaultEcs (0.17) have no hierarchy, so they are not part of this comparison.
/// </summary>
[MemoryDiagnoser]
public class HierarchyComparison
{
    private const int Squads = 1_000, Members = 10;

    private EasyEcs.World _world;
    private EasyEcs.Entity[] _leaders, _members;
    private F.EntityStore _store;
    private F.Entity[] _fLeaders;

    [GlobalSetup(Target = nameof(EasyEcs_ChildOf))]
    public void SetupEasyEcs()
    {
        _world = new EasyEcs.World();
        _leaders = new EasyEcs.Entity[Squads];
        _members = new EasyEcs.Entity[Members];
        Check(EasyEcs_ChildOf, () => _world.EntityCount);
    }

    [GlobalSetup(Target = nameof(Friflo_AddChild))]
    public void SetupFriflo()
    {
        _store = new F.EntityStore();
        _fLeaders = new F.Entity[Squads];
        Check(Friflo_AddChild, () => _store.Count);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _world?.Dispose();
        (_world, _store) = (null, null);
    }

    [Benchmark(Baseline = true)]
    public int EasyEcs_ChildOf()
    {
        _world.CreateEntities(_leaders, new Squad(100));
        foreach (var leader in _leaders) _world.CreateEntities(_members, new ChildOf(leader), new Member(1));
        return _world.DestroyEntities<Squad>();
    }

    [Benchmark]
    public int Friflo_AddChild()
    {
        for (int i = 0; i < Squads; i++)
        {
            var leader = _fLeaders[i] = _store.CreateEntity(new FSquad { Strength = 100 });
            for (int m = 0; m < Members; m++) leader.AddChild(_store.CreateEntity(new FMember { Rank = 1 }));
        }

        foreach (var leader in _fLeaders)
        {
            var members = leader.ChildEntities;
            for (int m = members.Count - 1; m >= 0; m--) members[m].DeleteEntity();
            leader.DeleteEntity();
        }

        return Squads;
    }

    /// <summary>Warm up, then check that a round leaves no entity behind.</summary>
    private static void Check(Func<int> round, Func<int> alive)
    {
        for (int i = 0; i < 20; i++) round();
        if (alive() != 0) throw new InvalidOperationException($"{alive()} entities left after a round");
    }
}

/// <summary>EasyEcs multi-target relation: a player's links, indexed by their target.</summary>
public record struct Follows(EasyEcs.EntityId Target) : IRelation;

public record struct Fame(int Value) : EasyEcs.IComponent;

public struct FFollows : F.ILinkRelation
{
    public F.Entity Target;
    public F.Entity GetRelationKey() => Target;
}

public struct FFame : F.IComponent { public int Value; }

/// <summary>
/// Friend lists as a multi-target relation: 10,000 players following 8 others each. Per operation 100 players
/// unfollow one player and follow another, then the followers of 100 players are counted and walked.
/// <list type="bullet">
/// <item>EasyEcs: <c>IRelation</c> (a <c>DynamicBuffer</c> of links per entity), <c>Relations&lt;T&gt;()</c> counts
/// and finds the followers.</item>
/// <item>Friflo: <c>ILinkRelation</c>, <c>GetIncomingLinks&lt;T&gt;()</c>.</item>
/// </list>
/// Arch (relations in a separate package) and DefaultEcs (one component of a type per entity) have no multi-target
/// relation, so they are not part of this comparison.
/// </summary>
[MemoryDiagnoser]
public class RelationComparison
{
    private const int Players = 10_000, Links = 8;

    private EasyEcs.World _world;
    private EasyEcs.Entity[] _players;
    private RelationIndex<Follows> _followers;
    private F.EntityStore _store;
    private F.Entity[] _fPlayers;
    private int _next;

    [GlobalSetup(Target = nameof(EasyEcs_Relations))]
    public void SetupEasyEcs()
    {
        _world = new EasyEcs.World(new WorldOptions { InitialEntityCapacity = Players });
        _players = new EasyEcs.Entity[Players];
        _world.CreateEntities(_players, new Fame(0), new DynamicBuffer<Follows>());
        for (int i = 0; i < Players; i++)
            for (int k = 1; k <= Links; k++)
                _players[i].AddRelation(new Follows(_players[Target(i, k)]));
        _followers = _world.Relations<Follows>();
        Check(EasyEcs_Relations, p => _followers.Count(_players[p]), p =>
        {
            ref readonly var links = ref _players[p].Read<DynamicBuffer<Follows>>();
            var targets = new int[links.Count];
            for (int i = 0; i < targets.Length; i++) targets[i] = links[i].Target.Index - _players[0].Id.Index;
            return targets;
        });
    }

    [GlobalSetup(Target = nameof(Friflo_LinkRelations))]
    public void SetupFriflo()
    {
        _store = new F.EntityStore();
        _fPlayers = new F.Entity[Players];
        for (int i = 0; i < Players; i++) _fPlayers[i] = _store.CreateEntity(new FFame());
        for (int i = 0; i < Players; i++)
            for (int k = 1; k <= Links; k++)
                _fPlayers[i].AddRelation(new FFollows { Target = _fPlayers[Target(i, k)] });
        Check(Friflo_LinkRelations, p => _fPlayers[p].GetIncomingLinks<FFollows>().Count, p =>
        {
            var links = _fPlayers[p].GetRelations<FFollows>();
            var targets = new int[links.Length];
            for (int i = 0; i < targets.Length; i++) targets[i] = links[i].Target.Id - _fPlayers[0].Id;
            return targets;
        });
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _world?.Dispose();
        (_world, _store) = (null, null);
    }

    private static int Target(int player, int k) => (player + k * 97) % Players;

    [Benchmark(Baseline = true)]
    public int EasyEcs_Relations()
    {
        for (int i = 0; i < 100; i++)
        {
            var player = _players[NextPlayer()];
            ref readonly var links = ref player.Read<DynamicBuffer<Follows>>();
            var dropped = links[(_next + i) % links.Count].Target;
            player.RemoveRelation<Follows>(dropped);
            player.AddRelation(new Follows(_players[NewTarget(i)]));
        }

        int total = 0;
        for (int i = 0; i < 100; i++)
        {
            var star = _players[Star(i)];
            total += _followers.Count(star);
            foreach (var follower in _followers.Find(star)) total += follower.Id.Index & 1;
        }

        return total;
    }

    [Benchmark]
    public int Friflo_LinkRelations()
    {
        for (int i = 0; i < 100; i++)
        {
            var player = _fPlayers[NextPlayer()];
            var links = player.GetRelations<FFollows>();
            var dropped = links[(_next + i) % links.Length].Target;
            player.RemoveRelation<FFollows>(dropped);
            player.AddRelation(new FFollows { Target = _fPlayers[NewTarget(i)] });
        }

        int total = 0;
        for (int i = 0; i < 100; i++)
        {
            var followers = _fPlayers[Star(i)].GetIncomingLinks<FFollows>();
            total += followers.Count;
            foreach (var follower in followers) total += follower.Entity.Id & 1;
        }

        return total;
    }

    private int NextPlayer() => _next = (_next + 7919) % Players;
    private int NewTarget(int i) => (_next + i * 31 + 1) % Players;
    private int Star(int i) => (_next + i * 13) % Players;

    /// <summary>Warm up, then check every player's follower count against the links the players hold.</summary>
    private static void Check(Func<int> operation, Func<int, int> followers, Func<int, int[]> targets)
    {
        for (int i = 0; i < 20; i++) operation();
        var expected = new int[Players];
        for (int p = 0; p < Players; p++)
            foreach (int t in targets(p)) expected[t]++;
        for (int p = 0; p < Players; p++)
            if (followers(p) != expected[p])
                throw new InvalidOperationException($"player {p} has {followers(p)} followers, expected {expected[p]}");
    }
}

/// <summary>EasyEcs: the vitality summed over awake units; asleep units carry a tag (or have it disabled).</summary>
public record struct Vigor(int Value) : EasyEcs.IComponent;

public struct Asleep : EasyEcs.IComponent;

public static class Vigors
{
    [ForEach, Without<Asleep>]
    static void SumAwake(in Vigor vigor, ref long total) => total += vigor.Value;

    [ForEach]
    static void Sum(in Vigor vigor, ref long total) => total += vigor.Value;
}

public struct FVigor : F.IComponent { public int Value; }
public struct AVigor { public int Value; }
public struct AAsleep;
public struct DVigor { public int Value; }

/// <summary>
/// Disabling whole entities: 100,000 units; per operation <see cref="Switched"/> units fall asleep and the ones that
/// fell asleep the operation before wake up (100: a few switch, most stay as they are; 10,000: frequent switching),
/// then the vitality of the awake units is summed.
/// <list type="bullet">
/// <item>EasyEcs: an <c>Asleep</c> tag and <c>[Without&lt;Asleep&gt;]</c>; and <c>Disable&lt;Vigor&gt;()</c>, which
/// keeps the value while queries skip it.</item>
/// <item>Friflo: <c>entity.Enabled</c> (a <c>Disabled</c> tag queries leave out).</item>
/// <item>DefaultEcs: <c>entity.Disable()</c> / <c>Enable()</c> (entity sets leave disabled entities out).</item>
/// <item>Arch: a tag and <c>WithNone</c>.</item>
/// </list>
/// </summary>
[MemoryDiagnoser]
public class DisableComparison
{
    private const int Units = 100_000;

    [Params(100, 10_000)] public int Switched;

    private EasyEcs.World _world;
    private EasyEcs.Entity[] _units;
    private F.EntityStore _store;
    private F.Entity[] _fUnits;
    private F.ArchetypeQuery<FVigor> _fAwake;
    private A.World _arch;
    private A.Entity[] _aUnits;
    private D.World _default;
    private D.Entity[] _dUnits;
    private D.EntitySet _dAwake;
    private int[] _previous, _current;
    private bool[] _asleep;
    private int _next, _archRows;

    private static readonly A.QueryDescription ArchAwake = new A.QueryDescription().WithAll<AVigor>().WithNone<AAsleep>();

    [GlobalSetup(Target = nameof(EasyEcs_Tag))]
    public void SetupEasyEcsTag()
    {
        CreateEasyEcs();
        Check(EasyEcs_Tag);
    }

    [GlobalSetup(Target = nameof(EasyEcs_DisableComponent))]
    public void SetupEasyEcsDisable()
    {
        CreateEasyEcs();
        Check(EasyEcs_DisableComponent);
    }

    private void CreateEasyEcs()
    {
        _world = new EasyEcs.World(new WorldOptions { InitialEntityCapacity = Units });
        _units = new EasyEcs.Entity[Units];
        for (int i = 0; i < Units; i++) _units[i] = _world.CreateEntity(new Vigor(i % 100));
    }

    [GlobalSetup(Target = nameof(Friflo_Enabled))]
    public void SetupFriflo()
    {
        _store = new F.EntityStore();
        _fUnits = new F.Entity[Units];
        for (int i = 0; i < Units; i++) _fUnits[i] = _store.CreateEntity(new FVigor { Value = i % 100 });
        _fAwake = _store.Query<FVigor>();
        Check(Friflo_Enabled);
    }

    [GlobalSetup(Target = nameof(Arch_Tag))]
    public void SetupArch()
    {
        _arch = A.World.Create();
        _aUnits = new A.Entity[Units];
        for (int i = 0; i < Units; i++) _aUnits[i] = _arch.Create(new AVigor { Value = i % 100 });
        Check(Arch_Tag, rowsOnly: true);
    }

    [GlobalSetup(Target = nameof(DefaultEcs_Disable))]
    public void SetupDefaultEcs()
    {
        _default = new D.World(Units);
        _dUnits = new D.Entity[Units];
        for (int i = 0; i < Units; i++)
        {
            _dUnits[i] = _default.CreateEntity();
            _dUnits[i].Set(new DVigor { Value = i % 100 });
        }

        _dAwake = _default.GetEntities().With<DVigor>().AsSet();
        Check(DefaultEcs_Disable);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _world?.Dispose();
        if (_arch != null) A.World.Destroy(_arch);
        _dAwake?.Dispose();
        _default?.Dispose();
        (_world, _store, _arch, _dAwake, _default) = (null, null, null, null, null);
    }

    private void Slices()
    {
        _next = 0;
        _previous = new int[Switched];
        _current = new int[Switched];
        _asleep = new bool[Units];
        for (int i = 0; i < Switched; i++) _previous[i] = -1;
    }

    /// <summary>The units that fall asleep this operation (a different slice each time, the same in every library).</summary>
    private void NextSlice()
    {
        (_previous, _current) = (_current, _previous);
        foreach (int i in _previous) if (i >= 0) _asleep[i] = false;
        for (int i = 0; i < Switched; i++) _asleep[_current[i] = _next = (_next + 97) % Units] = true;
    }

    [Benchmark(Baseline = true)]
    public long EasyEcs_Tag()
    {
        NextSlice();
        foreach (int i in _previous) if (i >= 0) _units[i].Remove<Asleep>();
        foreach (int i in _current) _units[i].Set(new Asleep());
        long total = 0;
        Vigors.SumAwake(_world, ref total);
        return total;
    }

    [Benchmark]
    public long EasyEcs_DisableComponent()
    {
        NextSlice();
        foreach (int i in _previous) if (i >= 0) _units[i].Enable<Vigor>();
        foreach (int i in _current) _units[i].Disable<Vigor>();
        long total = 0;
        Vigors.Sum(_world, ref total);
        return total;
    }

    [Benchmark]
    public long Friflo_Enabled()
    {
        NextSlice();
        foreach (int i in _previous) if (i >= 0) _fUnits[i].Enabled = true;
        foreach (int i in _current) _fUnits[i].Enabled = false;
        long total = 0;
        foreach (var (vigors, _) in _fAwake.Chunks)
        {
            var v = vigors.Span;
            for (int i = 0; i < v.Length; i++) total += v[i].Value;
        }

        return total;
    }

    [Benchmark]
    public long Arch_Tag()
    {
        NextSlice();
        foreach (int i in _previous) if (i >= 0) _arch.Remove<AAsleep>(_aUnits[i]);
        foreach (int i in _current) _arch.Add<AAsleep>(_aUnits[i]);
        var sum = new SumVigor();
        _arch.InlineQuery<SumVigor, AVigor>(in ArchAwake, ref sum);
        _archRows = sum.Rows;
        return sum.Total;
    }

    [Benchmark]
    public long DefaultEcs_Disable()
    {
        NextSlice();
        foreach (int i in _previous) if (i >= 0) _dUnits[i].Enable();
        foreach (int i in _current) _dUnits[i].Disable();
        long total = 0;
        var vigors = _default.GetComponents<DVigor>();
        foreach (ref readonly var e in _dAwake.GetEntities()) total += vigors[e].Value;
        return total;
    }

    private struct SumVigor : A.IForEach<AVigor>
    {
        public long Total;
        public int Rows;

        public void Update(ref AVigor vigor)
        {
            Total += vigor.Value;
            Rows++;
        }
    }

    /// <summary>
    /// Runs a few operations of <paramref name="operation"/> and checks each sum against the awake flags. Arch 2.1.0
    /// visits a stale row in some rounds (the right number of rows, one of them with another unit's value, while
    /// <c>Get</c> returns the right one), so its check counts the rows it visits (<paramref name="rowsOnly"/>).
    /// </summary>
    private void Check(Func<long> operation, bool rowsOnly = false)
    {
        Slices();
        for (int round = 0; round < 20; round++)
        {
            long total = operation(), expected = 0;
            int awake = 0;
            for (int i = 0; i < Units; i++)
                if (!_asleep[i])
                {
                    expected += i % 100;
                    awake++;
                }

            if (rowsOnly ? _archRows != awake : total != expected)
                throw new InvalidOperationException(rowsOnly ? $"visited {_archRows} rows, expected {awake}" : $"summed {total}, expected {expected}");
        }
    }
}

/// <summary>EasyEcs: units whose health runs out die during the pass that finds them, and come back in the next.</summary>
public record struct Life(int Value) : EasyEcs.IComponent;

public record struct Deaths(int Count) : EasyEcs.IComponent;

public struct Fallen : EasyEcs.IComponent;

public struct KillPass : ISystemGroup;

[UpdateInGroup<KillPass>]
public class Reap : EcsSystem
{
    [ForEach, Without<Fallen>]
    private static void Kill(EasyEcs.Entity unit, in Life life, ref Deaths deaths)
    {
        if (life.Value > 0) return;
        deaths.Count++;
        unit.Set(new Fallen());
    }
}

[UpdateInGroup<KillPass>, UpdateAfter<Reap>]
public class Raise : EcsSystem
{
    [ForEach, With<Fallen>]
    private static void Revive(EasyEcs.Entity unit, ref Life life)
    {
        life.Value = 100;
        unit.Remove<Fallen>();
    }
}

public struct FLife : F.IComponent { public int Value; }
public struct FDeaths : F.IComponent { public int Count; }
public struct FFallen : F.ITag;
public struct ALife { public int Value; }
public struct ADeaths { public int Count; }
public struct AFallen;
public struct DLife { public int Value; }
public struct DDeaths { public int Count; }
public struct DFallen;

/// <summary>
/// Structural changes found while iterating: 100,000 units; per operation <see cref="Killed"/> units lose their
/// health (through handles), then one pass over the living marks those at zero as fallen (adds a tag) and a second
/// pass over the fallen revives them (removes it).
/// <list type="bullet">
/// <item>EasyEcs: the passes change structure directly (<c>unit.Set</c> / <c>Remove</c> inside <c>[ForEach]</c>).</item>
/// <item>Friflo, Arch, DefaultEcs: their command buffers (<c>CommandBuffer</c>, <c>CommandBuffer</c>,
/// <c>EntityCommandRecorder</c>), played back after each pass.</item>
/// </list>
/// </summary>
[MemoryDiagnoser]
public class StructuralChangeComparison
{
    private const int Units = 100_000;

    [Params(10, 1_000)] public int Killed;

    private EasyEcs.World _world;
    private EasyEcs.Entity[] _units;
    private F.EntityStore _store;
    private F.CommandBuffer _buffer;
    private F.Entity[] _fUnits;
    private F.ArchetypeQuery<FLife, FDeaths> _fLiving;
    private F.ArchetypeQuery<FLife> _fFallen;
    private A.World _arch;
    private Arch.Buffer.CommandBuffer _aBuffer;
    private A.Entity[] _aUnits;
    private D.World _default;
    private D.Command.EntityCommandRecorder _recorder;
    private D.Entity[] _dUnits;
    private D.EntitySet _dLiving, _dFallen;
    private int _next;
    private long _killed;

    private static readonly A.QueryDescription ArchLiving = new A.QueryDescription().WithAll<ALife, ADeaths>().WithNone<AFallen>();
    private static readonly A.QueryDescription ArchFallen = new A.QueryDescription().WithAll<ALife, AFallen>();

    [GlobalSetup(Target = nameof(EasyEcs_Direct))]
    public void SetupEasyEcs()
    {
        _world = new EasyEcs.World(new WorldOptions { InitialEntityCapacity = Units });
        _units = new EasyEcs.Entity[Units];
        _world.CreateEntities(_units, new Life(100), new Deaths());
        _world.AddSystem<Reap>();
        _world.AddSystem<Raise>();
        Check(EasyEcs_Direct, () =>
        {
            long deaths = 0;
            foreach (var u in _units) deaths += u.Read<Deaths>().Count + (u.Read<Life>().Value == 100 && !u.Has<Fallen>() ? 0 : 1_000_000);
            return deaths;
        });
    }

    [GlobalSetup(Target = nameof(Friflo_CommandBuffer))]
    public void SetupFriflo()
    {
        _store = new F.EntityStore();
        _buffer = _store.GetCommandBuffer();
        _buffer.ReuseBuffer = true;
        _fUnits = new F.Entity[Units];
        for (int i = 0; i < Units; i++) _fUnits[i] = _store.CreateEntity(new FLife { Value = 100 }, new FDeaths());
        _fLiving = _store.Query<FLife, FDeaths>().WithoutAnyTags(F.Tags.Get<FFallen>());
        _fFallen = _store.Query<FLife>().AllTags(F.Tags.Get<FFallen>());
        Check(Friflo_CommandBuffer, () =>
        {
            long deaths = 0;
            foreach (var u in _fUnits)
                deaths += u.GetComponent<FDeaths>().Count + (u.GetComponent<FLife>().Value == 100 && !u.Tags.Has<FFallen>() ? 0 : 1_000_000);
            return deaths;
        });
    }

    [GlobalSetup(Target = nameof(Arch_CommandBuffer))]
    public void SetupArch()
    {
        _arch = A.World.Create();
        _aBuffer = new Arch.Buffer.CommandBuffer();
        _aUnits = new A.Entity[Units];
        for (int i = 0; i < Units; i++) _aUnits[i] = _arch.Create(new ALife { Value = 100 }, new ADeaths());
        Check(Arch_CommandBuffer, () =>
        {
            long deaths = 0;
            foreach (var u in _aUnits)
                deaths += _arch.Get<ADeaths>(u).Count + (_arch.Get<ALife>(u).Value == 100 && !_arch.Has<AFallen>(u) ? 0 : 1_000_000);
            return deaths;
        });
    }

    [GlobalSetup(Target = nameof(DefaultEcs_CommandRecorder))]
    public void SetupDefaultEcs()
    {
        _default = new D.World(Units);
        _recorder = new D.Command.EntityCommandRecorder();
        _dUnits = new D.Entity[Units];
        for (int i = 0; i < Units; i++)
        {
            _dUnits[i] = _default.CreateEntity();
            _dUnits[i].Set(new DLife { Value = 100 });
            _dUnits[i].Set(new DDeaths());
        }

        _dLiving = _default.GetEntities().With<DLife>().With<DDeaths>().Without<DFallen>().AsSet();
        _dFallen = _default.GetEntities().With<DLife>().With<DFallen>().AsSet();
        Check(DefaultEcs_CommandRecorder, () =>
        {
            long deaths = 0;
            foreach (var u in _dUnits) deaths += u.Get<DDeaths>().Count + (u.Get<DLife>().Value == 100 && !u.Has<DFallen>() ? 0 : 1_000_000);
            return deaths;
        });
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _world?.Dispose();
        if (_arch != null) A.World.Destroy(_arch);
        _aBuffer?.Dispose();
        _recorder?.Dispose();
        _dLiving?.Dispose();
        _dFallen?.Dispose();
        _default?.Dispose();
        (_world, _store, _arch, _aBuffer, _recorder, _dLiving, _dFallen, _default) = (null, null, null, null, null, null, null, null);
    }

    /// <summary>The next unit hit (a different slice each operation, the same in every library).</summary>
    private int Next() => _next = (_next + 97) % Units;

    [Benchmark(Baseline = true)]
    public void EasyEcs_Direct()
    {
        for (int i = 0; i < Killed; i++) _units[Next()].Get<Life>().Value = 0;
        _world.Update<KillPass>();
        _killed += Killed;
    }

    [Benchmark]
    public void Friflo_CommandBuffer()
    {
        for (int i = 0; i < Killed; i++) _fUnits[Next()].GetComponent<FLife>().Value = 0;
        foreach (var (lives, deaths, entities) in _fLiving.Chunks)
        {
            var l = lives.Span;
            var d = deaths.Span;
            for (int i = 0; i < l.Length; i++)
            {
                if (l[i].Value > 0) continue;
                d[i].Count++;
                _buffer.AddTag<FFallen>(entities.Ids[i]);
            }
        }

        _buffer.Playback();
        foreach (var (lives, entities) in _fFallen.Chunks)
        {
            var l = lives.Span;
            for (int i = 0; i < l.Length; i++)
            {
                l[i].Value = 100;
                _buffer.RemoveTag<FFallen>(entities.Ids[i]);
            }
        }

        _buffer.Playback();
        _killed += Killed;
    }

    [Benchmark]
    public void Arch_CommandBuffer()
    {
        for (int i = 0; i < Killed; i++) _arch.Get<ALife>(_aUnits[Next()]).Value = 0;
        var kill = new ArchKill { Buffer = _aBuffer };
        _arch.InlineEntityQuery<ArchKill, ALife, ADeaths>(in ArchLiving, ref kill);
        _aBuffer.Playback(_arch);
        var revive = new ArchRevive { Buffer = _aBuffer };
        _arch.InlineEntityQuery<ArchRevive, ALife>(in ArchFallen, ref revive);
        _aBuffer.Playback(_arch);
        _killed += Killed;
    }

    [Benchmark]
    public void DefaultEcs_CommandRecorder()
    {
        for (int i = 0; i < Killed; i++) _dUnits[Next()].Get<DLife>().Value = 0;
        var lives = _default.GetComponents<DLife>();
        var deaths = _default.GetComponents<DDeaths>();
        foreach (ref readonly var e in _dLiving.GetEntities())
        {
            if (lives[e].Value > 0) continue;
            deaths[e].Count++;
            _recorder.Record(e).Set<DFallen>();
        }

        _recorder.Execute();
        foreach (ref readonly var e in _dFallen.GetEntities())
        {
            lives[e].Value = 100;
            _recorder.Record(e).Remove<DFallen>();
        }

        _recorder.Execute();
        _killed += Killed;
    }

    private struct ArchKill : A.IForEachWithEntity<ALife, ADeaths>
    {
        public Arch.Buffer.CommandBuffer Buffer;

        public void Update(A.Entity entity, ref ALife life, ref ADeaths deaths)
        {
            if (life.Value > 0) return;
            deaths.Count++;
            Buffer.Add<AFallen>(entity);
        }
    }

    private struct ArchRevive : A.IForEachWithEntity<ALife>
    {
        public Arch.Buffer.CommandBuffer Buffer;

        public void Update(A.Entity entity, ref ALife life)
        {
            life.Value = 100;
            Buffer.Remove<AFallen>(entity);
        }
    }

    /// <summary>Warm up, then check that every unit hit died once and is alive again (deaths summed over all units).</summary>
    private void Check(Action operation, Func<long> deaths)
    {
        _killed = 0;
        for (int i = 0; i < 200; i++) operation();
        if (deaths() != _killed) throw new InvalidOperationException($"{deaths()} deaths counted, {_killed} made");
    }
}
