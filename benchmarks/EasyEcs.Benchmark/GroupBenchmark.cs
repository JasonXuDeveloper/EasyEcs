using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// Players by zone: 100,000 players in 16 zones; per operation, 100 players change zones and the health of every
/// player in 4 zones is summed. Three worlds, one way each: the zone as a group (<c>GroupBy</c>, a table per zone,
/// <c>InGroup</c> walks one table), as a value index (<c>Find</c> walks the zone's entities by handle) and as a plain
/// component a scan compares. Steady state allocates 0 bytes.
/// </summary>
[MemoryDiagnoser]
public partial class GroupBenchmark
{
    [Params(100_000)] public int Players;
    private const int Zones = 16;

    private World _grouped, _indexed, _scanned;
    private Entity[] _groupedPlayers, _indexedPlayers, _scannedPlayers;
    private QueryFilter[] _inZone;
    private ValueIndex<IndexedZone, int> _index;
    private int _next;

    [GlobalSetup]
    public void Setup()
    {
        _grouped = new World(new WorldOptions { InitialEntityCapacity = Players });
        _grouped.GroupBy<GroupedZone>();
        _indexed = new World(new WorldOptions { InitialEntityCapacity = Players });
        _scanned = new World(new WorldOptions { InitialEntityCapacity = Players });
        _groupedPlayers = new Entity[Players];
        _indexedPlayers = new Entity[Players];
        _scannedPlayers = new Entity[Players];
        for (int i = 0; i < Players; i++)
        {
            _groupedPlayers[i] = _grouped.CreateEntity(new Vitality(i % 100), new GroupedZone(i % Zones));
            _indexedPlayers[i] = _indexed.CreateEntity(new Vitality(i % 100), new IndexedZone(i % Zones));
            _scannedPlayers[i] = _scanned.CreateEntity(new Vitality(i % 100), new PlainZone(i % Zones));
        }

        _inZone = new QueryFilter[Zones];
        for (int z = 0; z < Zones; z++) _inZone[z] = QueryFilter.Create().InGroup<GroupedZone>(z);
        _index = _indexed.Index<IndexedZone, int>();
        for (int i = 0; i < 10; i++)
        {
            Group();
            Index();
            Scan();
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _grouped.Dispose();
        _indexed.Dispose();
        _scanned.Dispose();
    }

    [Benchmark(Baseline = true)]
    public long Group()
    {
        _next++;
        for (int i = 0; i < 100; i++) _groupedPlayers[Pick(i)].Set(new GroupedZone(Destination(i)));
        long total = 0;
        for (int z = 0; z < 4; z++)
            foreach (var item in _grouped.Query<Vitality>(_inZone[(_next + z) % Zones]))
                total += item.Component1.Value;
        return total;
    }

    [Benchmark]
    public long Index()
    {
        _next++;
        for (int i = 0; i < 100; i++) _indexedPlayers[Pick(i)].Set(new IndexedZone(Destination(i)));
        long total = 0;
        for (int z = 0; z < 4; z++)
            foreach (var player in _index.Find((_next + z) % Zones))
                total += player.Read<Vitality>().Value;
        return total;
    }

    [Benchmark]
    public long Scan()
    {
        _next++;
        for (int i = 0; i < 100; i++) _scannedPlayers[Pick(i)].Get<PlainZone>().Id = Destination(i);
        long total = 0;
        for (int z = 0; z < 4; z++)
        {
            int zone = (_next + z) % Zones;
            foreach (var item in _scanned.Query<Vitality, PlainZone>())
                if (item.Component2.Id == zone) total += item.Component1.Value;
        }

        return total;
    }

    private int Pick(int i) => (int)((uint)(_next * 7919 + i * 104729) % (uint)Players);

    private int Destination(int i) => (_next + i) % Zones;

    public record struct Vitality(int Value) : IComponent;
    public partial record struct GroupedZone([Group] int Id) : IComponent;
    public partial record struct IndexedZone([IndexKey] int Id) : IComponent;
    public record struct PlainZone(int Id) : IComponent;
}
