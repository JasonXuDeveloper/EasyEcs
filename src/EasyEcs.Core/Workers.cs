using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace EasyEcs;

/// <summary>
/// The helper threads of every world's parallel work (the systems of a stage, the chunks of a parallel pass): one fixed
/// set per process, as many as the largest <see cref="WorldOptions.MaxDegreeOfParallelism"/> - 1 a world asked for,
/// started when a world first runs work in parallel and kept for the process's lifetime (background threads). Not the
/// thread pool: the pool adds a thread when its threads stay busy (a parallel pass inside a stage's system keeps them
/// so) and retires the ones left idle, and a thread that starts allocates, then sets up its own per-thread state (a
/// tile's texture, a <see cref="Scratch{T}"/>) the first time it runs a method: a host could not keep zero allocation
/// in steady state. A fixed set starts once, and <see cref="ThreadSetup"/> prepares the state of every one of them at
/// once.
/// <para>
/// Queued items wait in one list (FIFO, intrusive: an item is queued at most once at a time, which its runner ensures)
/// that any idle thread takes from, so the worlds of a process share the threads as they share the pool's. A thread
/// with nothing to run spins for <see cref="SpinTicks"/> first (the next pass of a tick finds it awake, as the pool's
/// threads do), then parks on an event of its own; a post wakes as many parked threads as it queued items, the last
/// parked first (its cache is the warmest). One lock per post and per item taken.
/// </para>
/// </summary>
internal static class Workers
{
    /// <summary>Work a helper thread runs: queued by <see cref="Post"/>, linked through <see cref="Next"/> while queued.</summary>
    internal abstract class Item
    {
        internal Item Next;

        public abstract void Execute();
    }

    private sealed class Worker
    {
        public readonly ManualResetEventSlim Wake = new(false, 1);
        public Worker NextIdle; // the next parked thread (under Gate)
        public int SetUp;       // the ThreadSetup steps the thread ran (tests wait for it: Behind)
    }

    private static readonly Lock Gate = new();
    private static Worker[] _all = [];  // started threads (copied on write, under Gate)
    private static Item _head, _tail; // queued items (under Gate)
    private static int _queued;      // how many: written under Gate, read without it by spinning threads
    private static Worker _idle;     // parked threads, the last parked on top (under Gate)
    private static int _threads;     // started threads (written under Gate)

    /// <summary>
    /// How long a thread with nothing to run looks for an item before it parks (timestamp ticks: 100 µs). Longer than the
    /// gap between the runs of a busy tick, so back-to-back runs find their helpers awake: with 50 µs, a stage of four
    /// systems of 35 µs each (ParallelSystemsBenchmark) parked a helper every other tick and ran a quarter slower than on
    /// the pool; 200 µs gained nothing there and cost the battle server a few percent (helpers spinning beside the owner's
    /// serial systems). A spinning thread yields its core to other runnable threads between its looks.
    /// </summary>
    private static readonly long SpinTicks = Stopwatch.Frequency / 10_000;

    /// <summary>The threads started so far.</summary>
    internal static int Count => Volatile.Read(ref _threads);

    /// <summary>The threads that have not run every <see cref="ThreadSetup"/> step yet (they do at once; tests wait for it).</summary>
    internal static int Behind
    {
        get
        {
            int behind = 0, steps = ThreadSetup.Count;
            foreach (var worker in Volatile.Read(ref _all))
                if (Volatile.Read(ref worker.SetUp) < steps)
                    behind++;
            return behind;
        }
    }

    /// <summary>Start threads until the process has <paramref name="count"/> (cold: a runner's creation).</summary>
    public static void Ensure(int count)
    {
        if (Volatile.Read(ref _threads) >= count) return;
        lock (Gate)
        {
            while (_threads < count)
            {
                var worker = new Worker();
                var thread = new Thread(static state => Loop((Worker)state!))
                {
                    IsBackground = true,
                    Name = "EasyEcs helper"
                };
                thread.UnsafeStart(worker); // no execution context flows: the items carry what they need
                Volatile.Write(ref _all, [.. _all, worker]);
                Volatile.Write(ref _threads, _threads + 1);
            }
        }
    }

    /// <summary>
    /// Queue the <paramref name="count"/> items linked from <paramref name="first"/> to <paramref name="last"/> (through
    /// <see cref="Item.Next"/>, null after <paramref name="last"/>) and wake a parked thread for each, as far as some are.
    /// </summary>
    public static void Post(Item first, Item last, int count)
    {
        Worker wake = null;
        lock (Gate)
        {
            if (_tail == null) _head = first;
            else _tail.Next = first;
            _tail = last;
            _queued += count;
            for (int i = 0; i < count && _idle != null; i++)
            {
                var worker = _idle;
                _idle = worker.NextIdle;
                worker.NextIdle = wake;
                wake = worker;
            }
        }

        Release(wake);
    }

    /// <summary>Wake every parked thread (cold: new per-thread state to set up, <see cref="ThreadSetup"/>).</summary>
    public static void WakeAll()
    {
        Worker wake;
        lock (Gate)
        {
            wake = _idle;
            _idle = null;
        }

        Release(wake);
    }

    /// <summary>Set the events of the threads linked from <paramref name="wake"/>; once set, a thread owns its link again.</summary>
    private static void Release(Worker wake)
    {
        while (wake != null)
        {
            var next = wake.NextIdle;
            wake.NextIdle = null;
            wake.Wake.Set();
            wake = next;
        }
    }

    private static Item Take()
    {
        if (Volatile.Read(ref _queued) == 0) return null;
        lock (Gate)
        {
            var item = _head;
            if (item == null) return null;
            _head = item.Next;
            if (_head == null) _tail = null;
            item.Next = null;
            _queued--;
            return item;
        }
    }

    private static void Loop(Worker worker)
    {
        // What a thread allocates once, here at its start rather than in an item long after warm-up: the event's lock
        // (a blocking wait creates it the first time the thread parks, which may be when the passes stop coming back to
        // back) and the runtime's Thread.CurrentThread of the thread (made on the first call, which
        // Environment.CurrentManagedThreadId in a row would make).
        worker.Wake.Wait(1);
        GC.KeepAlive(Thread.CurrentThread);
        while (true)
        {
            ThreadSetup.CatchUp();
            Volatile.Write(ref worker.SetUp, ThreadSetup.Done);
            var item = Take();
            if (item != null)
            {
                item.Execute();
                continue;
            }

            if (Spin()) continue;
            lock (Gate)
            {
                // Queued, or a step added, meanwhile: the post (or the step's wake) found no parked thread to wake. Both
                // publish before they take the lock, so a thread that parks after them sees them here.
                if (_head != null || ThreadSetup.Pending) continue;
                worker.NextIdle = _idle;
                _idle = worker;
            }

            worker.Wake.Wait();
            worker.Wake.Reset();
        }
    }

    /// <summary>Look for an item (or state to set up) for <see cref="SpinTicks"/>; false when none came.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Spin()
    {
        long end = Stopwatch.GetTimestamp() + SpinTicks;
        var spinner = new SpinWait();
        do
        {
            if (Volatile.Read(ref _queued) != 0 || ThreadSetup.Pending) return true;
            spinner.SpinOnce(sleep1Threshold: -1);
        } while (Stopwatch.GetTimestamp() < end);

        return false;
    }
}

/// <summary>
/// The per-thread state of the core (a tile's texture, a <see cref="Scratch{T}"/>'s box, a buffer pool's own list, a
/// query walk), set up on every thread that runs parallel work as soon as one thread sets it up for itself: the state
/// stays <c>[ThreadStatic]</c> (one read on the hot path, released with a collectible context's types), and the thread
/// that creates one first adds a step that creates it, which every other <see cref="Workers"/> thread runs at once (a
/// parked one is woken for it) and every thread that runs a world's update or a parallel run runs when it next does.
/// So a thread that runs a method for the first time long after warm-up finds its state ready: the allocation happened
/// when the first thread made its own, on every thread together. Steps are idempotent (<c>??=</c>) and cold.
/// </summary>
internal static class ThreadSetup
{
    private static readonly Lock Gate = new();
    private static Action[] _steps = new Action[16];
    private static Type[] _types = new Type[16]; // what each step sets up, to forget it with its context
    private static int _count;                    // published after its step (Volatile)

    /// <summary>The steps this thread ran.</summary>
    [ThreadStatic] private static int _done;

    /// <summary>Whether this thread has steps to run.</summary>
    public static bool Pending => _done != Volatile.Read(ref _count);

    /// <summary>The steps this thread ran.</summary>
    public static int Done => _done;

    /// <summary>The steps added so far.</summary>
    public static int Count => Volatile.Read(ref _count);

    /// <summary>Run the steps added since this thread last ran them.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CatchUp()
    {
        if (_done != Volatile.Read(ref _count)) Run();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run()
    {
        int count = Volatile.Read(ref _count);
        var steps = Volatile.Read(ref _steps); // published before the count: holds the first `count` steps
        for (int i = _done; i < count; i++) Volatile.Read(ref steps[i])?.Invoke();
        _done = count;
    }

    /// <summary>
    /// Add <paramref name="step"/>, which sets up this thread's state of <paramref name="type"/>, once
    /// (<paramref name="added"/>: the caller's flag) and wake the helper threads to run it.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Add(ref int added, Action step, Type type)
    {
        if (Interlocked.Exchange(ref added, 1) == 0) Add(step, type);
    }

    /// <summary>Add <paramref name="step"/> (again: state that grew, such as a wider list) and wake the helper threads to run it.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Add(Action step, Type type)
    {
        Collectible.Watch(type); // the step goes when the context of its type unloads
        lock (Gate)
        {
            int count = _count;
            if (count == _steps.Length)
            {
                var steps = new Action[count * 2];
                var types = new Type[count * 2];
                Array.Copy(_steps, steps, count);
                Array.Copy(_types, types, count);
                _types = types;
                Volatile.Write(ref _steps, steps);
            }

            _steps[count] = step;
            _types[count] = type;
            Volatile.Write(ref _count, count + 1);
        }

        Workers.WakeAll();
    }

    /// <summary>Drop the steps of the types <paramref name="from"/> matches (their context unloads): nothing here roots it.</summary>
    public static void Forget(Func<Type, bool> from)
    {
        lock (Gate)
        {
            for (int i = 0; i < _count; i++)
                if (_types[i] != null && from(_types[i]))
                {
                    Volatile.Write(ref _steps[i], null);
                    _types[i] = null;
                }
        }
    }
}
