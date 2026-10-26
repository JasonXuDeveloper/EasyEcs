using EasyEcs;
using EasyEcs.Benchmark.Showcase;
using Nino.Core;

// Plays the battle with every operation the server offers and checks what a JIT build checks in BattleServerTests:
// a binary the AOT compiler cut short (a generic expansion it aborted) fails here, not in production.
using var server = new BattleServer(3_000);
using var other = new BattleServer(300);
for (int i = 0; i < 300; i++) server.Tick();

var battle = server.Battle;
Check(battle.Hits > 0 && battle.Deaths > 0 && battle.Shots > 0 && battle.Replicated > 0 && battle.Afflicted > 0, "the battle is busy");
Check(server.Failure is null, "no system failed");

var census = server.Census();
Check(census.Players > 0 && census.Alive > 0, "players are counted");
var nearest = server.NearestPlayer(new Position(0, 0));
Check(nearest.IsAlive, "a nearest player");
var top = new Entity[3];
Check(server.TopKillers(top) > 0, "a leaderboard");

Entity from = default, to = default;
foreach (var item in server.World.Read<DynamicBuffer<Loot>>())
{
    if (from == default && item.Component1.Count > 0) from = item.Entity;
    else if (to == default && item.Component1.Count < BattleServer.BagSize) to = item.Entity;
}

Check(server.Give(from, to), "loot changes hands");
Check(server.Hit(nearest.Id, 1), "a hit on one entity");
Check(server.FriendsOf(to) >= 0 && server.Leave(to) && !to.IsAlive, "a player leaves, its links go");

server.Checkpoint();
server.Tick();
server.Rewind();
Check(server.Battle.Tick == battle.Tick, "a snapshot rewinds the battle");

var moved = server.Transfer(nearest, other);
Check(moved.IsAlive && moved.World == other.World, "a player moves to another battle with its subtree");

var save = new NinoArrayBufferWriter();
int entities = server.World.EntityCount;
server.Save(save);
server.NewRound();
server.Load(save.WrittenSpan);
Check(server.World.EntityCount == entities, "a save loads back");
for (int i = 0; i < 30; i++) server.Tick();
Check(server.Failure is null, "the loaded battle plays on");
Check(server.Tables().Length > 0 && server.Slowest() is not null, "introspection");

// A generic system of another assembly closed here: this assembly's generator registers the closed code (no
// MakeGenericMethod, which Native AOT cannot run for a value type it never compiled).
using (var healing = new World())
{
    healing.AddSystem<EasyEcs.Aot.Library.Regen<Life>>();
    var patient = healing.CreateEntity(new Life { Value = 0 });
    for (int i = 0; i < 4; i++) healing.Update();
    Check(patient.Life.Value == 2, "a library's generic system runs on every second update");
}

// The prefetch gate reads the last-level cache from the operating system once per process (sysfs or sysconf,
// sysctl, GetLogicalProcessorInformationEx): in native code too. A world of 2 entities is in any cache.
using (var small = new World())
{
    small.CreateEntities(new Entity[2], new Life { Value = 1 });
    Check(!Lookup<Life>.Prefetches(small), "a world in the cache does not prefetch");
}

// An ordered table: the sort instantiated for the key type the generated part declares, its blocks and block pairs.
using (var crowd = new World())
{
    for (int i = 0; i < 100; i++) crowd.CreateEntity(new Spot((i * 37) % 10, i % 10));
    crowd.SortRows<Spot>();
    int blocks = 0, pairs = 0;
    Span<BlockPair> found = stackalloc BlockPair[1024];
    foreach (var table in crowd.Blocks<Spot>())
    {
        blocks += table.Count;
        pairs += table.Pairs(1, found);
    }

    Check(blocks > 0 && pairs >= blocks, "an ordered table's blocks and block pairs");
}

Console.WriteLine($"AOT battle: {battle.Tick} ticks, {battle.Hits} hits, {battle.Deaths} deaths, {server.World.EntityCount} entities");
return 0;

static void Check(bool ok, string what)
{
    if (ok) return;
    Console.Error.WriteLine("failed: " + what);
    Environment.Exit(1);
}

partial record struct Spot(float X, float Y) : IComponent
{
    [OrderBy(nameof(X), nameof(Y))]
    public readonly uint Cell => (uint)X << 16 | (uint)Y;
}

struct Life : EasyEcs.Aot.Library.ILife
{
    public int Value { get; set; }
}
