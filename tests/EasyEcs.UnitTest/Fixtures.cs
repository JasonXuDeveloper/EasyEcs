using EasyEcs;

namespace EasyEcs.UnitTest;

// Components carry values so tests can check that data lands in (and stays in) the right slot.
public record struct Position(float X, float Y) : IComponent;
public record struct Velocity(float X, float Y) : IComponent;
public record struct Health(int Value) : IComponent;
public record struct Name(string Value) : IComponent;   // managed reference inside a component
public struct Frozen : IComponent;                        // tag
public struct Dead : IComponent;                          // tag
public record struct Stun(int Ticks) : IComponent;        // gained and lost often
public record struct Armor(int Value) : IComponent, ITracked;        // tracked: rows record their last write
public record struct Seen(int Count) : IComponent;       // how often a Changed<Armor> method visited the entity
public record struct Shield(int Value) : IComponent, ITracked;       // a second tracked component
public record struct Follows(EntityId Target) : IComponent, IIndexed<EntityId> // a relation: indexed by its target
{
    public EntityId Key => Target;
}
public record struct Callsign(string Value) : IComponent, IIndexed<string> // a managed key
{
    public string Key => Value;
}
public record struct Login(int Id) : IComponent, IUniqueIndexed<int> // unique: one entity per login
{
    public int Key => Id;
}
public record struct Handle(string Value) : IComponent, IUniqueIndexed<string> // unique, a managed key (null: none)
{
    public string Key => Value;
}
public record struct GridCell(int X, int Y) : IComponent, IDirectMapped<long> // unique, its keys in 16 slots (x, y & 3)
{
    public long Key => ((long)X << 32) | (uint)Y;
    static int IDirectMapped<long>.Slots => 16;
    // A negative x is out of range (the hash alone); keys 4 apart share a slot.
    static int IDirectMapped<long>.SlotOf(long key) => (int)(key >> 32) < 0 ? -1 : (int)((key >> 32) & 3) * 4 + (int)(key & 3);
}
public struct Marked : IComponent;                        // tag
public record struct C1(int V) : IComponent;
public record struct C2(int V) : IComponent;
public record struct C3(int V) : IComponent;
public record struct C4(int V) : IComponent;
public record struct C5(int V) : IComponent;
public record struct C6(int V) : IComponent;
public record struct C7(int V) : IComponent;
public record struct C8(int V) : IComponent;
public record struct C9(int V) : IComponent;
public record struct Clock(long Tick) : ISingletonComponent;
public record struct Score(int Deaths, int Points) : ISingletonComponent;

/// <summary>
/// Allocation measurements: a thread's allocated bytes (<see cref="System.GC.GetAllocatedBytesForCurrentThread"/>) read
/// once no background collection runs.
/// </summary>
/// <remarks>
/// A background (concurrent) gen2 collection voids every thread's allocation context when its mark phase ends
/// (gc.cpp, <c>repair_allocation_contexts(FALSE)</c>: <c>alloc_ptr = alloc_limit = 0</c>, <c>alloc_bytes</c> left
/// as it is), so the unused rest of a thread's context, up to a few KB, shows up in its allocated bytes although
/// nothing was allocated. The large tables the tests create start such collections (reason AllocLarge) that may
/// still run when a measurement starts, more often on a loaded machine where their thread is slow. A blocking full
/// collection waits for a running one first, so a measurement that starts after it sees only what the code allocates
/// (nothing it measures allocates, so no collection starts meanwhile).
/// </remarks>
public static class Allocations
{
    /// <summary>
    /// The bytes one run of <paramref name="run"/> allocated (<paramref name="bytes"/>: this thread's allocated bytes, or
    /// another counter such as a system's stats). One run: helpers are the process's fixed threads (no thread pool
    /// thread starts while it runs) and their state is set up on all of them at once, so a run allocates nothing whichever
    /// thread runs which chunk.
    /// </summary>
    public static long Of(System.Action run, System.Func<long> bytes = null)
    {
        bytes ??= System.GC.GetAllocatedBytesForCurrentThread;
        Settle();
        long before = bytes();
        run();
        return bytes() - before;
    }

    /// <summary>This thread's allocated bytes, read once no background collection runs: the start of a measurement.</summary>
    public static long Baseline()
    {
        Settle();
        return System.GC.GetAllocatedBytesForCurrentThread();
    }

    /// <summary>
    /// Run <paramref name="step"/>, the code a test then measures, <paramref name="least"/> times, then until the JIT has
    /// settled: no method compiled anywhere in the process during the last <see cref="QuietRuns"/> runs and the last
    /// half second. A loop still running Tier0 code compiles its on-stack replacement on the thread that runs it, once
    /// its patchpoints have been hit often enough, and that compile can allocate on that thread (168 B for each of
    /// PrefetchTests' probe jobs): inside a measurement, a failure. Tiering moves a method on only after 30 calls, a
    /// 100 ms delay and a compile on its background thread, which in the full suite waits behind the methods of every
    /// test before: a fixed warm-up that is enough alone measures Tier0 loops there.
    /// </summary>
    public static void WarmUp(System.Action step, int least)
    {
        for (int i = 0; i < least; i++) step();
        long quiet = System.Diagnostics.Stopwatch.Frequency / 2;
        long start = System.Diagnostics.Stopwatch.GetTimestamp(), since = start;
        long compiled = System.Runtime.JitInfo.GetCompiledMethodCount();
        for (int runs = 0; ;)
        {
            step();
            long now = System.Diagnostics.Stopwatch.GetTimestamp(), count = System.Runtime.JitInfo.GetCompiledMethodCount();
            if (count != compiled) (compiled, since, runs) = (count, now, 0);
            else if (++runs >= QuietRuns && now - since >= quiet) return;
            // A JIT that never settles (a step compiling new code every run): the measurement shows what allocates.
            if (now - start > 60 * System.Diagnostics.Stopwatch.Frequency) return;
        }
    }

    /// <summary>Runs without a compile that end a warm-up: past the 30 calls after which tiering promotes a method.</summary>
    private const int QuietRuns = 40;

    // Waits for a running background collection (a blocking gen2 request does) and its finalizers.
    private static void Settle()
    {
        System.GC.Collect(2, System.GCCollectionMode.Forced, blocking: true);
        System.GC.WaitForPendingFinalizers();
    }
}
