using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// Friend lists as a relation: 10,000 players with 8 <c>Follows</c> links each. Per operation, 100 players unfollow
/// one player and follow another (<c>RemoveRelation</c> / <c>AddRelation</c>, which keep the index current), then the
/// followers of 100 players are counted (<c>Count</c>, O(1)) and walked (<c>Find</c>). Steady state allocates 0 bytes.
/// </summary>
[MemoryDiagnoser]
public class RelationBenchmark
{
    [Params(10_000)] public int Players;
    private const int Links = 8;

    private World _world;
    private Entity[] _players;
    private RelationIndex<Follows> _followers;
    private int _next;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Players });
        _players = new Entity[Players];
        _world.CreateEntities(_players, new Fame(0), new DynamicBuffer<Follows>());
        for (int i = 0; i < Players; i++)
            for (int k = 1; k <= Links; k++)
                _players[i].AddRelation(new Follows(_players[(i + k * 97) % Players]));
        _followers = _world.Relations<Follows>();
        for (int i = 0; i < 20; i++) FollowAndCount();
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark]
    public int FollowAndCount()
    {
        for (int i = 0; i < 100; i++)
        {
            _next = (_next + 7919) % Players;
            var player = _players[_next];
            ref readonly var links = ref player.Read<DynamicBuffer<Follows>>();
            var dropped = links[(_next + i) % links.Count].Target;
            player.RemoveRelation<Follows>(dropped);
            player.AddRelation(new Follows(_players[(_next + i * 31 + 1) % Players]));
        }

        int total = 0;
        for (int i = 0; i < 100; i++)
        {
            var star = _players[(_next + i * 13) % Players];
            total += _followers.Count(star);
            foreach (var follower in _followers.Find(star)) total += follower.Id.Index & 1;
        }

        return total;
    }

    public record struct Follows(EntityId Target) : IRelation;
    public record struct Fame(int Value) : IComponent;
}
