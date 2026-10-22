using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// Inventories as a relation: 100,000 items, each pointing at one of 1,000 owners (<c>ItemOf(Entity Owner)</c>,
/// indexed by its owner). Per operation, 100 items change hands (trades: <c>Set</c>, which re-indexes the item at once)
/// and the inventories of 100 owners are counted through the index. Steady state allocates 0 bytes.
/// <br/>
/// Sessions by token, unique against not: 100,000 players each holding a <c>long</c> token; per operation 100 players
/// get a new token (<c>Set</c>) and 100 are found by theirs. <see cref="RekeyAndLookup"/> indexes the token with
/// <c>IIndexed&lt;long&gt;</c>, <see cref="RekeyAndLookupUnique"/> with <c>IUniqueIndexed&lt;long&gt;</c> (each write claims
/// its key first, each lookup is a <c>Get</c>): the difference is the price of the check.
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

    [GlobalSetup(Target = nameof(TradeAndCount))]
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
    public void Cleanup()
    {
        _world?.Dispose();
        _sessions?.Dispose();
        _uniqueSessions?.Dispose();
    }

    private World _sessions, _uniqueSessions;
    private Entity[] _players, _uniquePlayers;
    private long[] _tokens, _uniqueTokens;
    private ValueIndex<Session, long> _sessionIndex;
    private ValueIndex<UniqueSession, long> _uniqueIndex;
    private long _issued;
    private int _player;

    [GlobalSetup(Targets = [nameof(RekeyAndLookup), nameof(RekeyAndLookupUnique)])]
    public void SetupSessions()
    {
        _sessions = new World(new WorldOptions { InitialEntityCapacity = Items });
        _uniqueSessions = new World(new WorldOptions { InitialEntityCapacity = Items });
        _players = new Entity[Items];
        _uniquePlayers = new Entity[Items];
        _tokens = new long[Items];
        _uniqueTokens = new long[Items];
        _sessionIndex = _sessions.Index<Session, long>();
        _uniqueIndex = _uniqueSessions.Index<UniqueSession, long>();
        for (int i = 0; i < Items; i++)
        {
            _tokens[i] = _uniqueTokens[i] = ++_issued;
            _players[i] = _sessions.CreateEntity(new Session(_tokens[i]));
            _uniquePlayers[i] = _uniqueSessions.CreateEntity(new UniqueSession(_uniqueTokens[i]));
        }

        for (int i = 0; i < 20; i++)
        {
            RekeyAndLookup();
            RekeyAndLookupUnique();
        }
    }

    /// <summary>100 new tokens and 100 lookups by token, the token indexed (duplicates allowed).</summary>
    [Benchmark]
    public int RekeyAndLookup()
    {
        int player = _player;
        for (int i = 0; i < 100; i++)
        {
            player = (player + 7919) % _players.Length;
            _tokens[player] = ++_issued;
            _players[player].Set(new Session(_tokens[player]));
        }

        int found = 0;
        for (int i = 0; i < 100; i++)
            if (_sessionIndex.TryFind(_tokens[(player + i * 13) % _players.Length], out _)) found++;
        _player = player;
        return found;
    }

    /// <summary><see cref="RekeyAndLookup"/> with a unique token: each <c>Set</c> claims its key, each lookup is a <c>Get</c>.</summary>
    [Benchmark]
    public int RekeyAndLookupUnique()
    {
        int player = _player;
        for (int i = 0; i < 100; i++)
        {
            player = (player + 7919) % _uniquePlayers.Length;
            _uniqueTokens[player] = ++_issued;
            _uniquePlayers[player].Set(new UniqueSession(_uniqueTokens[player]));
        }

        int found = 0;
        for (int i = 0; i < 100; i++) found += _uniqueIndex.Get(_uniqueTokens[(player + i * 13) % _uniquePlayers.Length]).Id.Index & 1;
        _player = player;
        return found;
    }

    /// <summary>
    /// What unique keys cost in memory: a world of 10,000 players, each created with its own unique token, then
    /// disposed. The allocated bytes are the world's, its table's and the index's.
    /// </summary>
    [Benchmark]
    public int CreateUnique()
    {
        using var world = new World(new WorldOptions { InitialEntityCapacity = 10_000 });
        for (int i = 0; i < 10_000; i++) world.CreateEntity(new UniqueSession(i + 1));
        return world.EntityCount;
    }

    public record struct Session(long Token) : IComponent, IIndexed<long>
    {
        public long Key => Token;
    }

    public record struct UniqueSession(long Token) : IComponent, IUniqueIndexed<long>
    {
        public long Key => Token;
    }

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
