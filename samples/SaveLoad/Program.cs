using System.Buffers;
using EasyEcs;
using SaveLoad;

// Three ways to take entities out of a world and put them into another:
//   1. Save a whole world to a file and Load it into a new world: same ids, values and singletons.
//   2. SaveEntities a player with what it owns, LoadEntities it into another world:
//      new ids, links kept.
//   3. MoveEntities a player between two worlds of the same thread, without any bytes in between.
// Every step is checked; the process exits with 1 if a check fails.
var checks = new Checks();

using var zone = new World();
zone.SetClock(Tick: 1234);
Entity alice = Players.Spawn(zone, "alice", x: 1, y: 2, swords: 1);
Entity bob = Players.Spawn(zone, "bob", x: 5, y: 5, swords: 2);
Entity carol = Players.Spawn(zone, "carol", x: -3, y: 7, swords: 0);
Console.WriteLine($"zone:     {WorldText.Describe(zone)}");

// ---- 1. The whole world to a file and back ----
string path = Path.Combine(Path.GetTempPath(), "easyecs-sample.sav");
var bytes = new ArrayBufferWriter<byte>();
zone.Save(bytes);
File.WriteAllBytes(path, bytes.WrittenSpan.ToArray());

using (var restarted = new World())
{
    restarted.Load(File.ReadAllBytes(path));
    long size = new FileInfo(path).Length;
    Console.WriteLine($"reloaded: {WorldText.Describe(restarted)} ({size} bytes on disk)");

    bool same = WorldText.Describe(restarted) == WorldText.Describe(zone);
    checks.That("a reload has the same entities, values and clock", same);

    // Ids survive a whole-world load, so a saved id still names the same entity.
    Entity bobAgain = restarted.GetEntity(bob.Id);
    bool sameEntity = bobAgain.Read<Name>().Value == "bob";
    checks.That("a handle's id names the same entity after a reload", sameEntity);
}

File.Delete(path);

// ---- 2. A group of entities to another world, through bytes ----
using var other = new World();

// An entity already there takes ids, so the arrivals get different ones.
other.CreateEntity(new Name("someone already there"));

// The group: bob and the swords he holds (his children).
var group = new List<EntityId> { bob.Id };
foreach (var sword in zone.Children(bob.Id))
{
    group.Add(sword.Id);
}

bytes.ResetWrittenCount();
zone.SaveEntities(bytes, group.ToArray());
var arrived = new Entity[group.Count];
other.LoadEntities(bytes.WrittenSpan, arrived);

Entity bobThere = arrived[0];
bool sameValues = bobThere.Read<Name>().Value == "bob" && bobThere.Read<Health>().Value == 100;
checks.That("the copy has bob's values", sameValues);
// ChildOf is an indexed component: its index counts the children of a parent.
int swordsThere = other.Index<ChildOf, EntityId>().Count(bobThere.Id);
bool linksFollow = swordsThere == 2 && bobThere.Id != bob.Id;
checks.That("his swords point at his new id", linksFollow);

// Bob left the zone. Destroying a parent destroys its children, so his swords go with him.
bob.Destroy();
Console.WriteLine($"other:    {WorldText.Describe(other)}");

// ---- 3. Move carol to the other world directly ----
var moved = new Entity[1];
zone.MoveEntities(other, [carol.Id], moved);
checks.That("carol left the zone", !carol.IsAlive && zone.Count<Name>() == 1);
checks.That("and arrived with her values", moved[0].Read<Position>() == new Position(-3, 7));
Console.WriteLine($"zone:     {WorldText.Describe(zone)}");
Console.WriteLine($"other:    {WorldText.Describe(other)}");
int aliceSwords = zone.Index<ChildOf, EntityId>().Count(alice.Id);
checks.That("alice stayed", alice.IsAlive && aliceSwords == 1);

Console.WriteLine(checks.AllPassed ? "all checks passed" : "a check failed");
return checks.AllPassed ? 0 : 1;
