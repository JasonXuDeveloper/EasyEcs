using EasyEcs;

namespace VoxelServer;

// Not part of the lesson: the check that a tick in steady state allocates nothing. A world that grows to a new peak
// (more entities in one chunk than any chunk held before, a longer list of packets, a bigger chunk read back) still
// allocates the room for it, once, so a tick is in steady state when the world has been through it before. The check
// makes such ticks: it captures the world (a snapshot) and copies its region files, runs Ticks ticks, a rehearsal that
// reaches every peak of those ticks, then puts the world and the files back and runs the same ticks again, counting
// the bytes every thread allocated. A restore keeps the lists, tables and indexes the rehearsal grew, and every pool
// keeps what it was given back, so the replay allocates nothing; a byte that does fails the run. One thing besides the
// world allocates once: a thread of the pool that runs a parallel pass for the first time (the pool adds threads under
// load) sets up its own scratch, such as a tile's texture. That happens in one replay, not in every one, so the check
// takes the least of up to three replays, as the core's allocation tests do; what a tick itself allocates, it allocates
// in each.
public static class Steady
{
    public const int Ticks = 5 * Rules.TicksPerSecond;

    public static void Check(World world, RegionFiles regions, in Capacity capacity)
    {
        var snapshot = world.Capture();
        using var files = new RegionFiles(capacity.Chunks * 4);
        regions.CopyTo(files);
        long allocated = long.MaxValue;
        for (int round = 0; round < 4 && allocated != 0; round++)        // the rehearsal, then the replays
        {
            files.CopyTo(regions);
            world.Restore(snapshot);
            long before = GC.GetTotalAllocatedBytes(precise: true);
            for (int i = 0; i < Ticks; i++) Server.Tick(world);
            long bytes = GC.GetTotalAllocatedBytes(precise: true) - before;
            if (round > 0) allocated = Math.Min(allocated, bytes);
        }
        Console.Error.WriteLine($"steady state: {allocated} bytes allocated in {Ticks} ticks replayed");
        if (allocated == 0) return;
        Console.Error.WriteLine("error: a tick in steady state allocated");
        Environment.ExitCode = 1;
    }
}
