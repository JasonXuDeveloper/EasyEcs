using EasyEcs;

namespace VoxelServer;

// Not part of the lesson: the check that a tick in steady state allocates nothing. A world that grows to a new peak
// (more entities in one chunk than any chunk held before, a longer list of packets, a bigger chunk read back) still
// allocates the room for it, once, so a tick is in steady state when the world has been through it before. The check
// makes such ticks: it captures the world (a snapshot) and copies its region files, runs Ticks ticks, a rehearsal that
// reaches every peak of those ticks, then puts the world and the files back and runs the same ticks again, counting
// the bytes the threads that run the world allocated: this one, and the helper threads that run systems beside it and
// the chunks of parallel passes (the world measures its systems on whichever thread runs them). A restore keeps the
// lists, tables and indexes the rehearsal grew, and every pool keeps what it was given back, so the replay allocates
// nothing; a byte that does fails the run. The helper threads are a fixed set, and what one sets up for a method (a
// tile's texture) they all have, so it does not matter which of them runs what. There are two rehearsals: a capture
// leaves the world's buffers sharing their lists with the snapshot until written, and a restore gives the rows written
// since lists of their own, so the first run after the capture starts from other rows than every run after it. Not
// counted: the runtime's own threads (the JIT compiles a method that turned hot on a thread of its own, and may
// allocate doing so).
public static class Steady
{
    public const int Ticks = 5 * Rules.TicksPerSecond;

    public static void Check(World world, RegionFiles regions, in Capacity capacity)
    {
        var snapshot = world.Capture();
        using var files = new RegionFiles(capacity.Chunks * 4);
        regions.CopyTo(files);
        long allocated = 0;
        for (int round = 0; round < 3; round++)        // the rehearsals, then the replay
        {
            files.CopyTo(regions);
            world.Restore(snapshot);
            long systems = SystemBytes(world), mine = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Ticks; i++) Server.Tick(world);
            // The systems run here count in both: the sum is 0 only when nothing allocated.
            allocated = GC.GetAllocatedBytesForCurrentThread() - mine + SystemBytes(world) - systems;
        }
        Console.Error.WriteLine($"steady state: {allocated} bytes allocated in {Ticks} ticks replayed");
        if (allocated == 0) return;
        Console.Error.WriteLine("error: a tick in steady state allocated");
        Environment.ExitCode = 1;
    }

    // What the systems allocated so far, on every thread that ran them or the chunks of their passes.
    static long SystemBytes(World world)
    {
        long bytes = 0;
        foreach (var system in world.Systems) bytes += system.Stats.TotalAllocated;
        return bytes;
    }
}
