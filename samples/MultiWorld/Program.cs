using EasyEcs;
using MultiWorld;

// A server splits its map into zones side by side, and every zone is a world owned by one thread. Units near a border
// must still see the units beyond it: between two updates the zones exchange their border rows as ghosts, and the
// units that crossed a border move to the zone they now stand in.
int updates = 200;
if (args.Length == 2 && args[0] == "--updates")
{
    updates = int.Parse(args[1]);
}

var zones = new World[Map.Zones];
for (int z = 0; z < zones.Length; z++)
{
    zones[z] = Zone.Create();
}

// The same map as one world, to check that the zones see what one world sees.
using var whole = Zone.Create();
for (int i = 0; i < Map.Units; i++)
{
    var (position, velocity) = Map.Unit(i);
    zones[Map.ZoneOf(position.X)].CreateEntity(position, velocity);
    whole.CreateEntity(position, velocity);
}

var border = new Border(zones);
border.Exchange(); // the first ghosts, before the first update

// The barrier runs the exchange on the last thread to arrive, while the others wait for it.
using var barrier = new Barrier(Map.Zones, _ => border.Exchange());
var sightings = new long[Map.Zones];
var threads = new Thread[Map.Zones];
for (int z = 0; z < zones.Length; z++)
{
    // Each thread updates its own zone and writes only its own slot of sightings.
    int id = z;
    threads[z] = new Thread(() => sightings[id] = Zone.Run(zones[id], updates, barrier)) { Name = $"zone {z}" };
    threads[z].Start();
}

foreach (var thread in threads)
{
    thread.Join();
}

long total = 0;
for (int z = 0; z < zones.Length; z++)
{
    int units = zones[z].Count<Velocity>(); // ghosts have no velocity
    int ghosts = zones[z].Count<Ghost>();
    Console.WriteLine($"zone {z}: {units,5:N0} units, {ghosts,3} ghosts, {border.Arrivals[z],5:N0} arrivals, {sightings[z],10:N0} sightings");
    total += sightings[z];
    zones[z].Dispose();
}

long seen = Zone.Run(whole, updates);
Console.WriteLine($"{Map.Zones} zones on {Map.Zones} threads: {total,10:N0} sightings in {updates} updates");
Console.WriteLine($"one world:            {seen,10:N0} sightings");
Console.WriteLine(total == seen ? "the zones saw what one world sees" : "the zones missed what one world sees");
return total == seen ? 0 : 1;
