using System.Diagnostics;
using MultiWorld;

// A server splits its game into zones (maps, rooms, matches) that share nothing.
// Here every zone is a world owned by one thread: one zone per processor.
int updates = 1_000;
if (args.Length == 2 && args[0] == "--updates")
{
    updates = int.Parse(args[1]);
}

int zones = Environment.ProcessorCount;
var results = new ZoneResult[zones];
var threads = new Thread[zones];
var clock = Stopwatch.StartNew();

for (int zone = 0; zone < zones; zone++)
{
    // Each thread writes only its own slot of results.
    int id = zone;
    threads[zone] = new Thread(() => results[id] = Zone.Run(id, updates))
    {
        Name = $"zone {zone}",
    };
    threads[zone].Start();
}

foreach (var thread in threads)
{
    thread.Join();
}

clock.Stop();

long entityUpdates = 0;
foreach (var result in results)
{
    double milliseconds = result.Elapsed.TotalMilliseconds;
    string units = $"{result.Units:N0} units";
    string deaths = $"{result.Deaths,7:N0} deaths";
    Console.WriteLine($"zone {result.Zone,2}: {units}, {deaths}, {milliseconds,6:F1} ms");
    entityUpdates += (long)result.Units * updates;
}

double perSecond = entityUpdates / clock.Elapsed.TotalSeconds / 1e6;
int total = zones * Zone.Units;
Console.WriteLine($"{zones} worlds on {zones} threads, {total:N0} units, {updates} updates each");
Console.WriteLine($"{clock.Elapsed.TotalMilliseconds:F0} ms, {perSecond:F1} M entity-updates/s");
