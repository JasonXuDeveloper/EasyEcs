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

Console.WriteLine($"AOT battle: {battle.Tick} ticks, {battle.Hits} hits, {battle.Deaths} deaths, {server.World.EntityCount} entities");
return 0;

static void Check(bool ok, string what)
{
    if (ok) return;
    Console.Error.WriteLine("failed: " + what);
    Environment.Exit(1);
}
