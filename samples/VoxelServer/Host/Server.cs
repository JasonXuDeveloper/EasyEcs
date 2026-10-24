using System.Diagnostics;
using EasyEcs;

namespace VoxelServer;

// Not part of the lesson: the tick loop and the report. The world's state is printed every ten seconds of game time
// (deterministic, so CI compares it with the README); the time each system took, what it allocated, and the check that
// a tick in steady state allocates nothing go to stderr.
public static class Server
{
    public static void Run(World world, int ticks, bool realtime)
    {
        var budget = TimeSpan.FromSeconds(1.0 / Rules.TicksPerSecond);
        long start = Stopwatch.GetTimestamp();
        var warmUp = new long[world.Systems.Count];
        long busy = 0;                                                     // the ticks' wall time, sleeps left out
        for (int i = 1; i <= ticks; i++)
        {
            long begin = Stopwatch.GetTimestamp();
            Tick(world);
            busy += Stopwatch.GetTimestamp() - begin;
            if (i == WarmUp) Allocated(world, warmUp);
            if (i % (10 * Rules.TicksPerSecond) == 0) Report(world);
            // A vanilla server sleeps until the next 50 ms boundary; without --realtime the run goes flat out.
            if (realtime)
            {
                var wait = start + (long)(i * budget.TotalSeconds * Stopwatch.Frequency) - Stopwatch.GetTimestamp();
                if (wait > 0) Thread.Sleep(TimeSpan.FromSeconds((double)wait / Stopwatch.Frequency));
            }
        }
        Totals(world);
        Timings(world, ticks, budget, warmUp, Stopwatch.GetElapsedTime(0, busy));
    }

    public static void Tick(World world)
    {
        // #region tick
        world.Update();                      // no system in the default group: it advances EntityRandom's tick
        world.Update<ClientInput>();
        world.Update<Simulation>();
        world.Update<Replication>();
        // #endregion tick
    }

    // The first fifteen seconds build what the world uses from then on: its queries, the tables of its kinds of
    // entities, the chunks' buffers until the pool has them back. Program.cs reserves the peak the world grows to, so
    // what a tick allocates after them is a new peak only (more entities in one chunk than any chunk held before, a
    // kind of chunk save not seen yet), a few bytes now and then; Steady.Check replays ticks to show the rest is 0.
    const int WarmUp = 15 * Rules.TicksPerSecond;

    // What each system allocated so far.
    static void Allocated(World world, long[] bytes)
    {
        int i = 0;
        foreach (var system in world.Systems) bytes[i++] = system.Stats.TotalAllocated;
    }

    static void Report(World world)
    {
        ref readonly var clock = ref world.Clock;
        Console.WriteLine(
            $"tick {clock.Tick,5} ({(Rules.IsNight(clock) ? "night" : "day  ")}): {world.Count<Player>()} players, " +
            $"{world.Count<Chunk>()} chunks, {WaterIn(world)} water, {world.Count<Zombie>()} zombies, " +
            $"{world.Count<Item>()} items");
    }

    // The blocks of water in the world.
    public static int WaterIn(World world)
    {
        int count = 0;
        foreach (var chunk in world.Read<FluidCount>()) count += chunk.FluidCount.Blocks;
        return count;
    }

    static void Totals(World world)
    {
        ref readonly var s = ref world.Stats;
        Console.WriteLine($"chunks: {s.ChunksGenerated} generated, {s.ChunksUnloaded} unloaded, {s.ChunksLoaded} loaded back");
        Console.WriteLine($"blocks dug: {s.BlocksDug}; items: {s.ItemsPickedUp} picked up, {s.ItemsExpired} expired");
        Console.WriteLine($"zombies: {s.ZombiesSpawned} spawned, {s.ZombiesBurned} burned, {s.Hits} hits; player deaths: {s.PlayerDeaths}");
        Console.WriteLine($"water: {s.WaterCells} cells filled, {s.Drifts} ticks of bodies carried");
        Console.WriteLine($"view: {s.ChunksSent} chunks sent, {s.ChunksForgotten} forgotten; {s.CameIntoView} entities came into view, {s.LeftView} left it, {s.Despawns} despawned");
        Console.WriteLine($"sent: {s.BlockUpdates} block updates, {s.EntityUpdates} entity updates ({s.BytesSent / 1024} KiB)");
    }

    // WorldOptions.MeasureSystems keeps the time and the allocations of every system. Each is shown per tick, so a system
    // that runs every hundred ticks counts a hundredth of its runs. Systems of one stage run at once, so their times add
    // up to more than the tick's wall time, shown first.
    static void Timings(World world, int ticks, TimeSpan budget, long[] warmUp, TimeSpan busy)
    {
        double total = 0;
        long first = 0, after = 0;
        int i = 0;
        foreach (var system in world.Systems)
        {
            total += system.Stats.TotalTime.TotalMilliseconds;
            first += warmUp[i];
            after += system.Stats.TotalAllocated - warmUp[i++];
        }
        Console.Error.WriteLine($"average tick: {busy.TotalMilliseconds / ticks:F3} ms of the {budget.TotalMilliseconds:F0} ms budget " +
                                $"(the systems {total / ticks:F3} ms); allocated " +
                                $"{first / 1024} KiB in the first {WarmUp} ticks, {after} bytes in the {Math.Max(0, ticks - WarmUp)} after");
        i = 0;
        foreach (var system in world.Systems)
        {
            long late = system.Stats.TotalAllocated - warmUp[i];
            Console.Error.WriteLine($"  {system.Name,-22} {system.Stats.TotalTime.TotalMilliseconds / ticks:F4} ms, " +
                                    $"{warmUp[i++] / 1024} KiB warming up{(late != 0 ? $", {late} bytes after" : "")}");
        }
    }
}
