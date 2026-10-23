using System.Diagnostics;
using EasyEcs;

namespace MultiWorld;

// What one zone reports when its thread is done.
public readonly record struct ZoneResult(int Zone, int Units, int Deaths, TimeSpan Elapsed);

// One zone: a world of its own, built, updated and disposed by one thread.
// Nothing here is shared with the other zones, so nothing needs a lock.
public static class Zone
{
    public const int Units = 20_000;

    public static ZoneResult Run(int zone, int updates)
    {
        using var world = new World(new WorldOptions { InitialEntityCapacity = Units });
        world.AddSystem<MoveSystem>();
        world.AddSystem<DamageSystem>();
        world.AddSystem<RespawnSystem>();

        var units = new Entity[Units];
        world.CreateEntities(units, new Position(0, 0), new Velocity(1, 0), new Health(100));

        // Give the units different directions so they spread out over the zone.
        for (int i = 0; i < units.Length; i++)
        {
            float x = (i + zone) % 7 - 3;
            float y = i % 5 - 2;
            units[i].Velocity = new Velocity(x, y);
        }

        var watch = Stopwatch.StartNew();
        int deaths = 0;
        for (int i = 0; i < updates; i++)
        {
            world.Update();
            deaths += world.Tally.Deaths; // each update's count replaces the last one
        }

        return new ZoneResult(zone, world.Count<Health>(), deaths, watch.Elapsed);
    }
}
