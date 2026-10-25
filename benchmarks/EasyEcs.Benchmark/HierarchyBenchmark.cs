using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// Destroying linked entities in a warm world: 1,000 squads (a leader and 10 members) are created, then the leaders
/// destroyed. <c>Cascade</c>: members are <see cref="ChildOf"/> their leader and die with it. <c>RemoveLinks</c>: members
/// follow their leader under <see cref="TargetPolicy.Remove"/> and lose the link (then go with the next round).
/// </summary>
[MemoryDiagnoser]
public partial class HierarchyBenchmark
{
    [Params(1_000)] public int Squads;

    private World _world;
    private Entity[] _leaders, _members;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World();
        _world.Index<Leader, EntityId>().WhenTargetDestroyed(TargetPolicy.Remove);
        _leaders = new Entity[Squads];
        _members = new Entity[10];
        Cascade();
        RemoveLinks();
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark(Baseline = true)]
    public int Cascade()
    {
        _world.CreateEntities(_leaders, new Health { Value = 100 });
        foreach (var leader in _leaders) _world.CreateEntities(_members, new ChildOf(leader), new Energy { Value = 1 });
        return _world.DestroyAll<Health>();
    }

    [Benchmark]
    public int RemoveLinks()
    {
        _world.CreateEntities(_leaders, new Health { Value = 100 });
        foreach (var leader in _leaders) _world.CreateEntities(_members, new Leader(leader), new Energy { Value = 1 });
        int destroyed = _world.DestroyAll<Health>();
        return destroyed + _world.DestroyAll<Energy>();
    }

    public partial record struct Leader([LookupKey] EntityId Target) : IComponent;
}
