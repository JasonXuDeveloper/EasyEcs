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
public partial class IndexBenchmark
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
        _chunks?.Dispose();
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
        world.Reserve<UniqueSession>(10_000);
        for (int i = 0; i < 10_000; i++) world.CreateEntity(new UniqueSession(i + 1));
        return world.EntityCount;
    }

    // Chunks of a voxel world by position: 600 loaded in a 30 x 20 area, 8,192 lookups of positions in and around it
    // (a fifth not loaded). By a unique index (a hash probe), by a direct-indexed one (a 64 x 64 slot array), and by
    // a hand-written grid of the same slots beside the unique index (what a game writes without the direct index).
    private World _chunks;
    private ValueIndex<UniqueChunk, (int, int)> _uniqueChunks;
    private ValueIndex<DirectChunk, (int, int)> _directChunks;
    private ((int, int) Pos, EntityId Id)[] _grid;
    private (int, int)[] _lookups;

    [GlobalSetup(Targets = [nameof(LookupUnique), nameof(LookupDirect), nameof(LookupGrid)])]
    public void SetupChunks()
    {
        _chunks = new World();
        _uniqueChunks = _chunks.Index<UniqueChunk, (int, int)>();
        _directChunks = _chunks.Index<DirectChunk, (int, int)>();
        _grid = new ((int, int), EntityId)[64 * 64];
        for (int x = 0; x < 30; x++)
        for (int z = 0; z < 20; z++)
        {
            var chunk = _chunks.CreateEntity(new UniqueChunk(x, z), new DirectChunk(x, z));
            _grid[DirectChunk.Slot((x, z))] = ((x, z), chunk);
        }

        var random = new System.Random(1);
        _lookups = new (int, int)[8192];
        for (int i = 0; i < _lookups.Length; i++) _lookups[i] = (random.Next(-3, 33), random.Next(-2, 22));
    }

    /// <summary>8,192 chunk lookups by position through a unique index.</summary>
    [Benchmark]
    public int LookupUnique()
    {
        int found = 0;
        foreach (var pos in _lookups)
            if (_uniqueChunks.TryGet(pos, out _)) found++;
        return found;
    }

    /// <summary>The same lookups through a direct-indexed component's index.</summary>
    [Benchmark]
    public int LookupDirect()
    {
        int found = 0;
        foreach (var pos in _lookups)
            if (_directChunks.TryGet(pos, out _)) found++;
        return found;
    }

    /// <summary>The same lookups through a hand-written grid of slots, the unique index behind it.</summary>
    [Benchmark]
    public int LookupGrid()
    {
        int found = 0;
        var grid = _grid;
        foreach (var pos in _lookups)
        {
            ref var slot = ref grid[DirectChunk.Slot(pos)];
            if (slot.Pos == pos && _chunks.TryGetEntity(slot.Id, out _) || _uniqueChunks.TryGet(pos, out _)) found++;
        }

        return found;
    }

    public record struct UniqueChunk(int X, int Z) : IComponent, IUniqueIndexed<(int, int)>
    {
        public (int, int) Key => (X, Z);
    }

    public record struct DirectChunk(int X, int Z) : IComponent, IDirectIndexed<(int, int)>
    {
        public (int, int) Key => (X, Z);
        static int IDirectIndexed<(int, int)>.Slots => 64 * 64;
        static int IDirectIndexed<(int, int)>.SlotOf((int, int) key) => Slot(key);
        public static int Slot((int X, int Z) key) => (key.Z & 63) * 64 + (key.X & 63);
    }

    public partial record struct Session([IndexKey] long Token) : IComponent;

    public partial record struct UniqueSession([UniqueKey] long Token) : IComponent;

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

    public partial record struct ItemOf([IndexKey] EntityId Owner) : IComponent;

    public record struct Gold(int Amount) : IComponent;
}
