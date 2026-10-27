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

// The prefetching twins with the gate forced on (a last-level cache of 1 byte, as the unit tests set it): index probes
// (the dictionary's arrays read through UnsafeAccessor and reflection), a buffer's chases, a parallel probe; each
// against the same pass with the gate off.
var cache = Type.GetType("EasyEcs.LastLevelCache, EasyEcs.Core")!.GetField("Bytes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
long machine = (long)cache.GetValue(null)!;
using (var keyed = new World())
{
    var badges = new Entity[300];
    for (int i = 0; i < badges.Length; i++) badges[i] = keyed.CreateEntity(new Badge(i * 3), new Crew("c" + i % 7), new Life { Value = i });
    for (int i = 0; i < 500; i++)
    {
        var seeker = keyed.CreateEntity(new Want(i * 5 % 1000, "c" + i % 9), new Got(), new DynamicBuffer<Mate>());
        ref var mates = ref seeker.WriteMate();
        mates.Add(new Mate(badges[i % badges.Length].Id));
        mates.Add(new Mate(badges[i * 7 % badges.Length].Id));
    }

    int[] Run()
    {
        AotChases.Probe(keyed);
        AotChases.Mates(keyed);
        AotChases.ProbeParallel(keyed);
        var got = new List<int>();
        foreach (var item in keyed.Read<Got>()) got.Add(item.Got.A * 31 + item.Got.B * 7 + item.Got.C);
        return got.ToArray();
    }

    cache.SetValue(null, 0L);
    var off = Run();
    cache.SetValue(null, 1L);
    var on = Run();
    cache.SetValue(null, machine);
    Check(off.AsSpan().SequenceEqual(on) && off.Any(v => v != 0), "prefetched probes and buffer chases find what today's passes do");
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

// A pair method: the generated order (Morton keys of the neighbors' tables), the pair lists and the vector loop of a
// window of rows against each neighbor, in native code; each row counts the others within 1.5 of it.
using (var flock = new World())
{
    for (int i = 0; i < 200; i++) flock.CreateEntity(new Bird(i % 20, i / 20), new Near());
    Flock.Count(flock);
    bool right = true;
    foreach (var item in flock.Query<Bird, Near>())
    {
        var (x, y) = (item.Bird.X, item.Bird.Y);
        int expected = (x > 0 ? 1 : 0) + (x < 19 ? 1 : 0) + (y > 0 ? 1 : 0) + (y < 9 ? 1 : 0)
                       + (x > 0 && y > 0 ? 1 : 0) + (x < 19 && y > 0 ? 1 : 0) + (x > 0 && y < 9 ? 1 : 0) + (x < 19 && y < 9 ? 1 : 0);
        right &= item.Near.Value == expected;
    }

    Check(right, "a pair method counts each row's neighbors");
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

record struct Bird(float X, float Y) : IComponent;
record struct Near(int Value) : IComponent;

static class Flock
{
    [ForEach]
    public static void Count(in Bird p, ref Near near, [Neighbor] in Bird q)
    {
        float dx = p.X - q.X, dy = p.Y - q.Y, d2 = dx * dx + dy * dy;
        if (d2 >= 1.5f * 1.5f || d2 == 0) return;
        near.Value++;
    }
}

partial record struct Badge([UniqueKey] int Id) : IComponent;

[NotSaved]
partial record struct Crew([LookupKey] string Name) : IComponent;

[NotSaved]
record struct Want(int Badge, string Crew) : IComponent;
record struct Got(int A, int B, int C) : IComponent;
record struct Mate(EntityId Target);

static class AotChases
{
    [ForEach]
    public static void Probe(in Want want, ref Got got, Lookup<Badge> badges, Lookup<Crew> crews) =>
        got = got with { A = badges.TryFind(want.Badge, out var found) ? found.Id.Index : -1, B = crews.Count(want.Crew) };

    [ForEach]
    public static void Mates(in DynamicBuffer<Mate> mates, ref Got got, Lookup<Life> lives)
    {
        int sum = 0;
        foreach (var mate in mates) sum += lives.Has(mate.Target) ? lives.Read(mate.Target).Value : -1;
        got.C = sum;
    }

    [ForEach(Parallel = true)]
    public static void ProbeParallel(in Want want, ref Got got, Lookup<Badge> badges) =>
        got.A += badges.TryFind(want.Badge, out var found) ? found.Badge.Id : 0;
}

struct Life : EasyEcs.Aot.Library.ILife
{
    public int Value { get; set; }
}
