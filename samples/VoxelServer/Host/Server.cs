using System.Diagnostics;
using EasyEcs;

namespace VoxelServer;

// Not part of the lesson: the tick loop and the report. The world's state is printed every ten seconds of game time
// (deterministic, so CI compares it with the README); the time each system took goes to stderr.
public static class Server
{
    public static void Run(World world, int ticks, bool realtime)
    {
        var budget = TimeSpan.FromSeconds(1.0 / Rules.TicksPerSecond);
        long start = Stopwatch.GetTimestamp();
        for (int i = 1; i <= ticks; i++)
        {
            // #region tick
            world.Update<ClientInput>();
            world.Update<Simulation>();
            world.Update<Replication>();
            // #endregion tick
            if (i % (10 * Rules.TicksPerSecond) == 0) Report(world);
            // A vanilla server sleeps until the next 50 ms boundary; without --realtime the run goes flat out.
            if (realtime)
            {
                var wait = start + (long)(i * budget.TotalSeconds * Stopwatch.Frequency) - Stopwatch.GetTimestamp();
                if (wait > 0) Thread.Sleep(TimeSpan.FromSeconds((double)wait / Stopwatch.Frequency));
            }
        }
        Totals(world);
        Timings(world, ticks, budget);
    }

    static void Report(World world)
    {
        ref readonly var clock = ref world.Clock;
        Console.WriteLine(
            $"tick {clock.Tick,5} ({(Rules.IsNight(clock) ? "night" : "day  ")}): {world.Count<Player>()} players, " +
            $"{world.Count<Chunk>()} chunks, {world.Count<Water>()} water, {world.Count<Zombie>()} zombies, " +
            $"{world.Count<Item>()} items");
    }

    static void Totals(World world)
    {
        ref readonly var s = ref world.Stats;
        Console.WriteLine($"chunks: {s.ChunksLoaded} loaded, {s.ChunksUnloaded} unloaded, {s.ChunkUpdates} sent");
        Console.WriteLine($"blocks dug: {s.BlocksDug}; items: {s.ItemsPickedUp} picked up, {s.ItemsExpired} expired");
        Console.WriteLine($"zombies: {s.ZombiesSpawned} spawned, {s.ZombiesBurned} burned, {s.Hits} hits; player deaths: {s.PlayerDeaths}");
        Console.WriteLine($"water: {s.WaterCells} cells poured, {s.Drifts} ticks of humanoids carried");
        Console.WriteLine($"sent: {s.EntityUpdates} entity updates ({s.BytesSent / 1024} KiB), {s.Despawns} despawns");
    }

    // WorldOptions.MeasureSystems keeps the time and the allocations of every system.
    static void Timings(World world, int ticks, TimeSpan budget)
    {
        double total = 0;
        long allocated = 0;
        foreach (var system in world.Systems)
        {
            total += system.Stats.TotalTime.TotalMilliseconds;
            allocated += system.Stats.TotalAllocated;
        }
        Console.Error.WriteLine(
            $"average tick: {total / ticks:F3} ms of the {budget.TotalMilliseconds:F0} ms budget; " +
            $"allocated {allocated / 1024} KiB in {ticks} ticks");
        foreach (var system in world.Systems)
            Console.Error.WriteLine($"  {system.GetType().Name,-22} {system.Stats.AverageTime.TotalMilliseconds:F4} ms, " +
                                    $"{system.Stats.TotalAllocated / 1024} KiB");
    }
}
