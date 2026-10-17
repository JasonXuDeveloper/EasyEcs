using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// Inventories as a relation: 100,000 items, each pointing at one of 1,000 owners (<c>ItemOf(Entity Owner)</c>,
/// indexed by its owner). Per operation, 100 items change hands (trades: <c>Set</c>, which re-indexes the item at once)
/// and the inventories of 100 owners are counted through the index. Steady state allocates 0 bytes.
/// </summary>
[MemoryDiagnoser]
public class IndexBenchmark
{
    [Params(100_000)] public int Items;
    [Params(1_000)] public int Owners;

    private World _world;
    private Entity[] _owners;
    private Entity[] _items;
    private ValueIndex<ItemOf, EntityId> _index;
    private int _next;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Items + Owners });
        _owners = new Entity[Owners];
        _world.CreateEntities(_owners, new Gold(0));
        _items = new Entity[Items];
        for (int i = 0; i < Items; i++) _items[i] = _world.CreateEntity(new ItemOf(_owners[i % Owners]));
        _index = _world.Index<ItemOf, EntityId>();
        for (int i = 0; i < 20; i++) TradeAndCount();
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark]
    public int TradeAndCount()
    {
        for (int i = 0; i < 100; i++)
        {
            _next = (_next + 7919) % _items.Length;
            _items[_next].Set(new ItemOf(_owners[(_next + i) % _owners.Length]));
        }

        int total = 0;
        for (int i = 0; i < 100; i++) total += _index.Count(_owners[(_next + i * 13) % _owners.Length]);
        return total;
    }

    public record struct ItemOf(EntityId Owner) : IComponent, IIndexed<EntityId>
    {
        public EntityId Key => Owner;
    }

    public record struct Gold(int Amount) : IComponent;
}
