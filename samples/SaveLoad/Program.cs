using System.Buffers;
using System.Text;
using EasyEcs;
using Nino.Core;

// Persistence and migration of entities:
//   1. Save a whole world to a file and Load it into a new world: same ids, components and singletons.
//   2. SaveEntities a player with what it owns, LoadEntities it into another world (another server): new ids, links kept.
//   3. MoveEntities a player with what it owns between two worlds of this thread, without encoding.
// Every step is verified; the process exits 1 if one fails.

string path = Path.Combine(Path.GetTempPath(), "easyecs-sample.sav");
bool ok = true;

using var zone = new World();
zone.SetClock(1234);
var alice = SpawnPlayer(zone, "alice", 1, 2, swords: 1);
var bob = SpawnPlayer(zone, "bob", 5, 5, swords: 2);
var carol = SpawnPlayer(zone, "carol", -3, 7, swords: 0);
Console.WriteLine($"zone:     {Describe(zone)}");

// 1. The whole world to a file and back.
var output = new ArrayBufferWriter<byte>();
zone.Save(output);
File.WriteAllBytes(path, output.WrittenSpan.ToArray());
using (var restarted = new World())
{
    restarted.Load(File.ReadAllBytes(path));
    Console.WriteLine($"reloaded: {Describe(restarted)} ({new FileInfo(path).Length} bytes on disk)");
    ok &= Check("a reload has the same entities, values and clock", Describe(restarted) == Describe(zone));
    ok &= Check("a handle's id names the same entity after a reload", restarted.GetEntity(bob.Id).Read<Name>().Value == "bob");
}

File.Delete(path);

// 2. A group of entities to another world through bytes: bob and the swords he holds (his children).
using var other = new World();
other.CreateEntity(new Name("someone already there"));            // ids taken there: the arrivals get others
var group = new List<EntityId> { bob.Id };
foreach (var child in zone.Children(bob.Id)) group.Add(child.Id);
output.ResetWrittenCount();
zone.SaveEntities(output, group.ToArray());
var arrived = new Entity[group.Count];
other.LoadEntities(output.WrittenSpan, arrived);
var bobThere = arrived[0];
ok &= Check("the copy has bob's values", bobThere.Read<Name>().Value == "bob" && bobThere.Read<Health>().Value == 100);
ok &= Check("his swords point at his new id", CountChildren(other, bobThere.Id) == 2 && bobThere.Id != bob.Id);
bob.Destroy();                                                      // he left: his swords go with him (ChildOf cascade)
Console.WriteLine($"other:    {Describe(other)}");

// 3. Move carol to the other world directly: she and her (no) swords leave the zone.
var moved = new Entity[1];
zone.MoveEntities(other, [carol.Id], moved);
ok &= Check("carol left the zone", !carol.IsAlive && zone.Count<Name>() == 1);
ok &= Check("and arrived with her values", moved[0].Read<Position>() == new Position(-3, 7));
Console.WriteLine($"zone:     {Describe(zone)}");
Console.WriteLine($"other:    {Describe(other)}");
ok &= Check("alice stayed", alice.IsAlive && CountChildren(zone, alice.Id) == 1);

Console.WriteLine(ok ? "all checks passed" : "a check failed");
return ok ? 0 : 1;

static Entity SpawnPlayer(World world, string name, float x, float y, int swords)
{
    var player = world.CreateEntity(new Name(name), new Position(x, y), new Health(100), new DynamicBuffer<Item>());
    ref var bag = ref player.Get<DynamicBuffer<Item>>();
    bag.Add(new Item(1, 3));                                         // three potions
    bag.Add(new Item(2, 1));                                         // a map
    for (int i = 0; i < swords; i++) world.CreateEntity(new Sword(10 + i), new ChildOf(player));
    return player;
}

static int CountChildren(World world, EntityId parent)
{
    int count = 0;
    foreach (var _ in world.Children(parent)) count++;
    return count;
}

// One line per player, sorted by name: what the checks compare.
static string Describe(World world)
{
    var players = new List<string>();
    foreach (var item in world.Query<Name, Position, Health>())
    {
        var text = new StringBuilder($"{item.Name.Value}@({item.Position.X},{item.Position.Y}) hp {item.Health.Value}");
        if (item.Entity.TryGet<DynamicBuffer<Item>>(out var bag))
            foreach (var stack in bag) text.Append($" item{stack.Kind}x{stack.Count}");
        foreach (var child in world.Children(item.Entity.Id)) text.Append($" sword+{child.Read<Sword>().Damage}");
        players.Add(text.ToString());
    }

    players.Sort(StringComparer.Ordinal);
    return $"[{string.Join("; ", players)}] clock {(world.HasClock ? world.Clock.Tick : 0)}";
}

static bool Check(string what, bool passed)
{
    Console.WriteLine($"  {(passed ? "ok  " : "FAIL")} {what}");
    return passed;
}

// Unmanaged components are saved as raw rows; a managed one (a string field) needs a Nino formatter: [NinoType].
public record struct Position(float X, float Y) : IComponent;
public record struct Health(int Value) : IComponent;
public record struct Sword(int Damage) : IComponent;
public record struct Item(int Kind, int Count);                     // an element of a DynamicBuffer<Item>
public record struct Clock(long Tick) : ISingletonComponent;

[NinoType]
public partial record struct Name(string Value) : IComponent;
