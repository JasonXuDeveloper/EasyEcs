using System.Diagnostics;
using EasyEcs;

// One world per thread: a server's zones (maps, shards, rooms) share nothing, so each zone's world is owned by one
// long-lived thread and needs no synchronization. Each thread builds its world, runs --updates updates, and reports.
int updates = args.Length == 2 && args[0] == "--updates" ? int.Parse(args[1]) : 1_000;
int zones = Environment.ProcessorCount;
const int UnitsPerZone = 20_000;

var results = new ZoneResult[zones];
var threads = new Thread[zones];
var clock = Stopwatch.StartNew();
for (int zone = 0; zone < zones; zone++)
{
    int id = zone;                                                  // each thread writes only its own slot
    threads[zone] = new Thread(() => results[id] = RunZone(id, updates)) { Name = $"zone {zone}" };
    threads[zone].Start();
}

foreach (var thread in threads) thread.Join();
clock.Stop();

long entityUpdates = 0;
foreach (var result in results)
{
    Console.WriteLine($"zone {result.Zone,2}: {result.Units:N0} units, {result.Deaths,7:N0} deaths, {result.Elapsed.TotalMilliseconds,6:F1} ms");
    entityUpdates += (long)result.Units * updates;
}

Console.WriteLine($"{zones} worlds on {zones} threads, {updates} updates each, {zones * UnitsPerZone:N0} units: " +
                  $"{clock.Elapsed.TotalMilliseconds:F0} ms, {entityUpdates / clock.Elapsed.TotalSeconds / 1e6:F1} M entity-updates/s");

static ZoneResult RunZone(int zone, int updates)
{
    using var world = new World(new WorldOptions { InitialEntityCapacity = UnitsPerZone });
    world.AddSingleton<Tally>();
    world.AddSystem<MoveSystem>();
    world.AddSystem<DamageSystem>();
    world.AddSystem<RespawnSystem>();

    var units = new Entity[UnitsPerZone];
    world.CreateEntities(units, new Position(0, 0), new Velocity(1, 0), new Health(100));
    for (int i = 0; i < units.Length; i++) units[i].Velocity = new Velocity((i + zone) % 7 - 3, i % 5 - 2);   // spread out

    var watch = Stopwatch.StartNew();
    for (int i = 0; i < updates; i++) world.Update();
    return new ZoneResult(zone, world.Count<Health>(), world.Tally.Deaths, watch.Elapsed);
}

readonly record struct ZoneResult(int Zone, int Units, int Deaths, TimeSpan Elapsed);

public record struct Position(float X, float Y) : IComponent;
public record struct Velocity(float X, float Y) : IComponent;
public record struct Health(int Value) : IComponent;
public record struct Tally(int Deaths) : ISingletonComponent;

public class MoveSystem : EcsSystem
{
    [ForEach]
    static void Move(ref Position p, in Velocity v) => p = new Position(p.X + v.X, p.Y + v.Y);
}

// Units far from the centre take damage: the zone's hazard.
public class DamageSystem : EcsSystem
{
    [ForEach]
    static void Hurt(in Position p, ref Health hp, ref Tally tally)
    {
        if (p.X * p.X + p.Y * p.Y < 250_000) return;
        if (--hp.Value == 0) tally.Deaths++;
    }
}

// The dead come back at the centre.
public class RespawnSystem : EcsSystem
{
    [ForEach]
    static void Respawn(ref Position p, ref Health hp)
    {
        if (hp.Value > 0) return;
        p = new Position(0, 0);
        hp = new Health(100);
    }
}
