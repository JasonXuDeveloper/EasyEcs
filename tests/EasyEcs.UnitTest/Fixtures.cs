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
/// <para>
/// The runtime's cast cache (the table of type pairs a cast looked up: <c>castcache.cpp</c>) is dropped when a collectible
/// assembly's loader allocator is collected (<c>loaderallocator.cpp</c>, <c>CastCache::FlushCurrentCache</c>), and the
/// next cast that misses, on whatever thread, allocates the new table at the old size: an <c>int[(size + 1) * 6]</c>,
/// 98,352 B for the 4,096 entries the full suite grows it to. The generator and unload tests leave collectible
/// assemblies behind, which the blocking collection of a measurement collects; code that casts through the cache would
/// then pay another test's unload in its window (unoptimized code does: a Debug build's
/// <c>typeof(INoStructuralChanges).IsAssignableFrom(typeof(TJob))</c>, a JIT-time constant once optimized, made
/// ViewTests.APassThroughAViewAllocatesNothing allocate 98,352 B in the full Debug suite). So a measurement starts with
/// a cast through the cache after the collection (<see cref="RebuildCastCache"/>).
/// </para>
/// </remarks>
public static class Allocations
{
    /// <summary>
    /// The bytes one run of <paramref name="run"/> allocated (<paramref name="bytes"/>: this thread's allocated bytes, or
    /// another counter such as a system's stats). One run: helpers are the process's fixed threads (no thread pool
    /// thread starts while it runs) and their state is set up on all of them at once, so a run allocates nothing whichever
    /// thread runs which chunk. For code a test runs again and again, <see cref="Steady"/>.
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
    /// The bytes <paramref name="runs"/> runs of <paramref name="step"/> allocated once it is warm: <see cref="WarmUp"/>
    /// of the same delegate first, so the code measured is the code warmed (a lambda written around the step would be
    /// compiled, and its loops replaced on the stack, inside the window). Every 0 B check of code run again and again
    /// measures through it. A failure writes to the test's output how many methods this thread compiled in the window:
    /// some, the warm-up did not settle; none, the code allocated.
    /// </summary>
    public static long Steady(System.Action step, int least, int runs, System.Func<long> bytes = null)
    {
        WarmUp(step, least);
        Repeat(step, 0); // compiled outside the window (the first Steady of the process)
        bytes ??= System.GC.GetAllocatedBytesForCurrentThread;
        Settle();
        long compiled = System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: true);
        long before = bytes();
        Repeat(step, runs);
        long allocated = bytes() - before;
        compiled = System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: true) - compiled;
        if (allocated != 0)
            NUnit.Framework.TestContext.Out.WriteLine($"Allocations.Steady: {allocated} B in {runs} runs after the warm-up, "
                + $"{compiled} methods compiled on this thread in them, {Workers.Count} helper threads");
        return allocated;
    }

    // Out of line: one loop for every test, compiled and tiered long before a measurement reaches it.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void Repeat(System.Action step, int runs)
    {
        for (int i = 0; i < runs; i++) step();
    }

    /// <summary>
    /// Run <paramref name="step"/>, the code a test then measures, <paramref name="least"/> times, then until it has
    /// settled: no method compiled anywhere in the process and nothing allocated on this thread during the last
    /// <see cref="QuietRuns"/> runs, at least twice the longest stretch between two compiles on this thread, and the last
    /// half second; at most 30 s.
    /// <para>
    /// A compile on this thread inside a measurement allocates: an on-stack replacement of a Tier0 loop, 168 B (a string
    /// and a System.Reflection.Metadata.TypeName, the runtime parsing a type name while it compiles), which made
    /// NeighborTests' lists pass fail with 336 B. Tiering moves a method on only after 30 calls, a 100 ms delay and a
    /// compile on its background thread, which in the full suite waits behind the methods of every test before, so a
    /// fixed warm-up that is enough alone still runs Tier0 loops there. A method of a rare path (a pair pass's lists
    /// drawn again every few runs) stays at Tier0 longer and compiles its replacement late: at runs 12, 59, 71, 90 and 92
    /// of one loaded run, past a stretch of 40 quiet runs; hence the stretch follows the longest gap seen. And nothing
    /// allocated in it: what a rare path grows has grown before the measurement.
    /// </para>
    /// </summary>
    public static void WarmUp(System.Action step, int least)
    {
        for (int i = 0; i < least; i++) step();
        long quiet = System.Diagnostics.Stopwatch.Frequency / 2;
        long start = System.Diagnostics.Stopwatch.GetTimestamp(), since = start;
        long compiled = System.Runtime.JitInfo.GetCompiledMethodCount();
        long here = System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: true);
        int clean = 0, gap = 0, longest = 0; // settled runs; runs since this thread's last compile; the longest such gap
        while (true)
        {
            long before = System.GC.GetAllocatedBytesForCurrentThread();
            step();
            long allocated = System.GC.GetAllocatedBytesForCurrentThread() - before;
            long now = System.Diagnostics.Stopwatch.GetTimestamp(), count = System.Runtime.JitInfo.GetCompiledMethodCount();
            long countHere = System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: true);
            gap++;
            if (countHere != here) (here, longest, gap) = (countHere, System.Math.Max(longest, gap), 0);
            if (count != compiled || allocated != 0) (compiled, since, clean) = (count, now, 0);
            else if (++clean >= System.Math.Max(QuietRuns, 2 * longest) && now - since >= quiet) return;
            // A step that never settles (it compiles or allocates every run): the measurement shows it.
            if (now - start > 30 * System.Diagnostics.Stopwatch.Frequency) return;
        }
    }

    /// <summary>Runs without a compile that end a warm-up: past the 30 calls after which tiering promotes a method.</summary>
    private const int QuietRuns = 40;

    // Waits for a running background collection (a blocking gen2 request does) and its finalizers, then has the cast
    // cache rebuilt if they dropped it.
    private static void Settle()
    {
        System.GC.Collect(2, System.GCCollectionMode.Forced, blocking: true);
        System.GC.WaitForPendingFinalizers();
        RebuildCastCache();
    }

    /// <summary>
    /// A cast through the runtime's cast cache: when a collected collectible assembly dropped the cache, this thread
    /// allocates its new table here, outside the measurement (see the remarks). Otherwise a lookup that finds the pair.
    /// </summary>
    private static void RebuildCastCache() => _castInterface.IsAssignableFrom(_castType);

    // Not readonly: the JIT folds a test between two known types, and the cast must reach the cache.
    private static System.Type _castInterface = typeof(System.IDisposable), _castType = typeof(System.IO.MemoryStream);

    /// <summary>
    /// The arrays one run of <paramref name="run"/> rented from any <c>ArrayPool</c> on this thread
    /// (System.Buffers.ArrayPoolEventSource's BufferRented, raised on the renting thread). A 0 B check alone misses a
    /// rent in steady state: on a quiet machine the pool hands the same array back, but under high memory load
    /// (90 % of the GC's threshold) <c>ArrayPool.Shared</c> drops its arrays at every gen2 collection, so the rent
    /// allocates after each one, and <see cref="Of"/> starts with one (73,824 to 589,920 B in loaded runs).
    /// </summary>
    public static int PoolRents(System.Action run)
    {
        using var listener = new PoolRentListener();
        PoolRentListener.Count = 0;
        PoolRentListener.Counting = true;
        try
        {
            run();
        }
        finally
        {
            PoolRentListener.Counting = false;
        }

        return PoolRentListener.Count;
    }

    private sealed class PoolRentListener : System.Diagnostics.Tracing.EventListener
    {
        [System.ThreadStatic] public static int Count;
        [System.ThreadStatic] public static bool Counting;

        // Called by the base constructor for the sources that exist already (before this class's fields are set).
        protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
        {
            if (source.Name == "System.Buffers.ArrayPoolEventSource")
                EnableEvents(source, System.Diagnostics.Tracing.EventLevel.Verbose);
        }

        // Raised synchronously on the thread that rents.
        protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs e)
        {
            if (Counting && e.EventName == "BufferRented") Count++;
        }
    }
}
