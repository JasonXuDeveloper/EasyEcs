using System;
using System.Threading;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>
/// The helper threads of parallel work: a fixed set per process (not the thread pool), and the per-thread state of the
/// core set up on all of them as soon as one thread sets up its own, so a thread that runs a method for the first time,
/// a thread started after the state was made among them, allocates nothing.
/// </summary>
public class WorkersTests
{
    public record struct Load(int Value) : IComponent;
    public record struct Done(int Value) : IComponent;

    /// <summary>The threads that ran rows of the marked methods (managed ids), each once.</summary>
    private static readonly int[] Seen = new int[64];

    private static int _owner, _helperRows, _poolRows, _strangerRows;

    /// <summary>
    /// Note the thread running a row: a helper when it is not the test's thread. <paramref name="kind"/>: also whether it
    /// is a pool thread or one of the helper threads by name (asking the thread allocates on its first call, so only
    /// the method whose allocations are not measured asks).
    /// </summary>
    private static void Note(bool kind = false)
    {
        int id = Environment.CurrentManagedThreadId;
        for (int i = 0; i < Seen.Length; i++)
        {
            int seen = Volatile.Read(ref Seen[i]);
            if (seen == id) break;
            if (seen == 0 && Interlocked.CompareExchange(ref Seen[i], id, 0) == 0) break;
        }

        if (id != Volatile.Read(ref _owner)) Interlocked.Increment(ref _helperRows);
        if (!kind) return;
        if (Thread.CurrentThread.IsThreadPoolThread) Interlocked.Increment(ref _poolRows);
        if (id != Volatile.Read(ref _owner) && Thread.CurrentThread.Name != "EasyEcs helper") Interlocked.Increment(ref _strangerRows);
    }

    private static void Forget()
    {
        Array.Clear(Seen);
        _owner = Environment.CurrentManagedThreadId;
        _helperRows = _poolRows = _strangerRows = 0;
    }

    /// <summary>Rows heavy enough that every helper joins a run.</summary>
    [EcsSystem]
    public ref struct Busy
    {
        [ForEach(Parallel = true, ChunkRows = 64)]
        static void Work(ref Load load)
        {
            Thread.SpinWait(200);
            load.Value++;
            Note(kind: true);
        }
    }

    /// <summary>A scratch value no other test uses: its box is created by the first thread that runs the method.</summary>
    [EcsSystem]
    public ref struct Counted
    {
        [ForEach(Parallel = true, ChunkRows = 64)]
        static void Work(in Load load, ref Done done, Scratch<long> rows)
        {
            Thread.SpinWait(200);
            done.Value = load.Value + (int)(++rows.Value & 0);
            Note();
        }
    }

    /// <summary>The same with a scratch value of its own, for the thread started after it was set up.</summary>
    [EcsSystem]
    public ref struct CountedLater
    {
        [ForEach(Parallel = true, ChunkRows = 64)]
        static void Work(in Load load, ref Done done, Scratch<short> rows)
        {
            Thread.SpinWait(200);
            done.Value = load.Value + (++rows.Value & 0);
            Note();
        }
    }

    private static World Loaded(int threads, int rows = 64 * 64)
    {
        var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads, MeasureSystems = true });
        world.CreateEntities(new Entity[rows], new Load(1), new Done());
        return world;
    }

    /// <summary>
    /// What the helper threads allocated running the chunks of the passes <paramref name="run"/> made (measured worlds
    /// count them for the system that ran the pass: another thread of the process, the JIT's, is not counted).
    /// </summary>
    private static long HelperBytes(Action run)
    {
        long before = World.HelperAllocated;
        run();
        return World.HelperAllocated - before;
    }

    /// <summary>Whether the helper threads have run every step (they run them at once, woken if parked).</summary>
    private static void AwaitSetup()
    {
        var spinner = new SpinWait();
        long end = Environment.TickCount64 + 5_000;
        while (Workers.Behind != 0 && Environment.TickCount64 < end) spinner.SpinOnce();
        Assert.That(Workers.Behind, Is.Zero, "a helper thread did not set up its state");
    }

    [Test]
    public void HelpersAreAFixedSetOfThreadsNotThePool()
    {
        using var world = Loaded(4);
        world.AddSystem<Busy>();
        world.Update();
        int threads = Workers.Count;
        Forget();
        for (int i = 0; i < 50; i++) world.Update();
        Assert.That(_poolRows, Is.Zero);
        Assert.That(_strangerRows, Is.Zero);
        Assert.That(_helperRows, Is.GreaterThan(0));
        int distinct = 0;
        foreach (int id in Seen)
            if (id != 0 && id != Environment.CurrentManagedThreadId)
                distinct++;
        Assert.That(distinct, Is.InRange(1, threads), "helpers ran on threads outside the fixed set");
        Assert.That(Workers.Count, Is.EqualTo(threads), "a run started a thread");
        Assert.That(threads, Is.GreaterThanOrEqualTo(3));
    }

    [Test]
    public void StepsRunOnEveryHelperThread()
    {
        using (var world = Loaded(4))
        {
            world.AddSystem<Busy>();
            world.Update(); // the threads exist (and park after the run)
        }

        int ran = 0, added = 0;
        ThreadSetup.Add(ref added, () => Interlocked.Increment(ref ran), typeof(WorkersTests));
        AwaitSetup();
        Assert.That(ran, Is.GreaterThanOrEqualTo(Workers.Count)); // every helper thread, parked ones woken for it
        ThreadSetup.CatchUp(); // and this one, which runs the step when it next runs a world (the adding thread too)
        int once = ran;
        ThreadSetup.Add(ref added, () => Interlocked.Increment(ref ran), typeof(WorkersTests)); // added once
        ThreadSetup.CatchUp();
        Assert.That(ran, Is.EqualTo(once));
    }

    [Test]
    public void AHelperRunningAMethodForTheFirstTimeAllocatesNothing()
    {
        using var parallel = Loaded(4);
        parallel.AddSystem<Busy>();
        for (int i = 0; i < 10; i++) parallel.Update(); // the runner and its threads, warm

        // The method's first rows, all on this thread: its scratch box is made here, and so on every helper thread.
        using (var alone = Loaded(1, 64))
        {
            alone.AddSystem<Counted>();
            alone.Update();
        }

        AwaitSetup();
        parallel.AddSystem<Counted>();
        Forget();
        long allocated = HelperBytes(parallel.Update); // the helpers' first rows of the method
        Assert.That(_helperRows, Is.GreaterThan(0), "no helper ran a row");
        Assert.That(allocated, Is.Zero);
    }

    [Test]
    public void AThreadStartedAfterTheStateWasMadeAllocatesNothing()
    {
        using (var alone = Loaded(1, 64))
        {
            alone.AddSystem<CountedLater>();
            alone.Update();
        }

        // More threads than the process has: the new ones start, and set up the state the others have, as they start.
        int threads = Workers.Count + 3;
        using var wide = Loaded(threads + 1);
        wide.AddSystem<Busy>();
        wide.Update();
        AwaitSetup();
        Assert.That(Workers.Count, Is.EqualTo(threads));
        wide.AddSystem<CountedLater>();
        Forget();
        long allocated = HelperBytes(wide.Update);
        Assert.That(_helperRows, Is.GreaterThan(0), "no helper ran a row");
        Assert.That(allocated, Is.Zero);
    }
}
