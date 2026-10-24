using System;

namespace EasyEcs;

/// <summary>Configuration of a <see cref="World"/>.</summary>
public sealed class WorldOptions
{
    /// <summary>
    /// Run systems with no order between them at the same time (on the process's helper threads) when their generated access does not conflict
    /// (<see cref="SystemAccess"/>). Default false: one owner thread per world is the recommended model.
    /// </summary>
    public bool ParallelSystems { get; init; }

    /// <summary>
    /// Threads one parallel run uses at most (default: processor count): systems run by <see cref="ParallelSystems"/>, and the
    /// row chunks of parallel <c>[ForEach]</c> methods, <c>ForEachParallel</c> and <c>ForEachEntityParallel</c> (which
    /// run in chunks whether <see cref="ParallelSystems"/> is on or not). The calling thread is one of them; the others are
    /// the process's helper threads, not the thread pool's: a fixed set of background threads, as many as the largest
    /// value a world asked for minus one, started when a world first runs work in parallel and shared by every world. No
    /// thread starts in steady state, and the per-thread memory of the core (a tile's texture, a <c>Scratch&lt;T&gt;</c>)
    /// is set up on all of them as soon as one thread sets up its own.
    /// </summary>
    public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount;

    /// <summary>Pre-sized entity capacity (avoid growth reallocations at runtime).</summary>
    public int InitialEntityCapacity { get; init; } = 1024;

    /// <summary>
    /// Measure every system run into <see cref="SystemHandle.Stats"/>: its time and allocated bytes (two timestamp and two
    /// allocation-counter reads per run, and per helper chunk of a parallel <c>[ForEach]</c>), and the entities its
    /// <c>[ForEach]</c> methods visited (one count per pass when it starts). Default false.
    /// </summary>
    public bool MeasureSystems { get; init; }

    /// <summary>
    /// What an exception thrown by a system does (default <see cref="SystemErrorPolicy.Throw"/>): propagate out of
    /// <see cref="World.Update()"/>, or switch the system off and record it in <see cref="SystemHandle.Error"/>.
    /// </summary>
    public SystemErrorPolicy SystemErrors { get; init; }

    /// <summary>
    /// Capacity of the destroy log (default 0: off): the ids of the last this many destroyed entities (no longer alive: a
    /// retired one is logged when it is freed), each with the change tick of its destruction, which
    /// <see cref="World.DestroyedSince"/> reports to readers that each keep their own tick (one replication mark per
    /// client). Preallocated (16 bytes per record); a destroy then writes one record, and with the
    /// log off it costs nothing. Size it to the destroys of the longest gap a reader may leave between two reads: an
    /// older reader is told to resynchronize (<see cref="World.DestroyLogOldestTick"/>).
    /// </summary>
    public int DestroyLogCapacity { get; init; }
}

/// <summary>What an exception thrown by a system does (<see cref="WorldOptions.SystemErrors"/>).</summary>
public enum SystemErrorPolicy
{
    /// <summary>Propagate out of the update (and out of <see cref="World.Dispose"/> for <c>OnDestroy</c>).</summary>
    Throw,

    /// <summary>
    /// Switch the failing system off (<see cref="SystemHandle.Enabled"/> false), keep the exception in
    /// <see cref="SystemHandle.Error"/> and go on with the update: a failure is data the application reads from
    /// <see cref="World.Systems"/>.
    /// </summary>
    Disable,
}
