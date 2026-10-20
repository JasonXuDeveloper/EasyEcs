using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// Spawning from a prefab (4 components, one tracked) and clearing the spawned entities, in a warm world: one
/// Instantiate per entity against the bulk Instantiate, which copies the prefab's row into consecutive rows at once.
/// The *Child cases spawn children of one parent with their own position: Instantiate then Set (a row copy, then a
/// move to the table with ChildOf) against Instantiate with the components given (one row copy into that table).
/// </summary>
[MemoryDiagnoser]
public class PrefabBenchmark
{
    [Params(1_000)] public int N;

    private World _world;
    private Prefab _prefab;
    private Entity[] _spawned;
    private Entity _parent;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World();
        _prefab = new Prefab().Set(new Position { X = 1, Y = 2 }).Set(new Velocity { X = 3 }).Set(new Health { Value = 100 })
            .Set(new Energy { Value = 5 });
        _spawned = new Entity[N];
        _parent = _world.CreateEntity();
        InstantiateEach();
        InstantiateBatch();
        InstantiateThenSetChild();
        InstantiateWithChild();
        InstantiateBatchWithChild();
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark(Baseline = true)]
    public int InstantiateEach()
    {
#pragma warning disable EECS031 // the baseline: one Instantiate per entity is what this case measures
        for (int i = 0; i < _spawned.Length; i++) _spawned[i] = _world.Instantiate(_prefab);
#pragma warning restore EECS031
        return _world.DestroyEntities<Health>();
    }

    [Benchmark]
    public int InstantiateBatch()
    {
        _world.Instantiate(_prefab, _spawned);
        return _world.DestroyEntities<Health>();
    }

    [Benchmark]
    public int InstantiateThenSetChild()
    {
#pragma warning disable EECS031 // each entity its own position: one call per entity is what this case measures
        for (int i = 0; i < _spawned.Length; i++)
            _spawned[i] = _world.Instantiate(_prefab).Set(new ChildOf(_parent), new Position { X = i, Y = 2 });
#pragma warning restore EECS031
        return _world.DestroyEntities<Health>();
    }

    [Benchmark]
    public int InstantiateWithChild()
    {
        for (int i = 0; i < _spawned.Length; i++)
            _spawned[i] = _world.Instantiate(_prefab, new ChildOf(_parent), new Position { X = i, Y = 2 });
        return _world.DestroyEntities<Health>();
    }

    [Benchmark]
    public int InstantiateBatchWithChild()
    {
        _world.Instantiate(_prefab, _spawned, new ChildOf(_parent), new Position { X = 1, Y = 2 });
        return _world.DestroyEntities<Health>();
    }
}
