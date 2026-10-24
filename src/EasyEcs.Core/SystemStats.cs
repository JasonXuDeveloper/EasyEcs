using System;
using System.Diagnostics;

namespace EasyEcs;

/// <summary>
/// How often and how long a system ran (<see cref="WorldOptions.MeasureSystems"/>): its <c>OnUpdate</c> method plus its
/// <c>[ForEach]</c> methods, per run, with the bytes it allocated and the entities its <c>[ForEach]</c> methods visited.
/// Read it between updates; with <see cref="WorldOptions.ParallelSystems"/> each system records its own runs.
/// </summary>
public readonly struct SystemStats
{
    private readonly long _total;
    private readonly long _last;

    private SystemStats(long runs, long total, long last, long allocated, long totalAllocated, long entities, long totalEntities)
    {
        Runs = runs;
        _total = total;
        _last = last;
        LastAllocated = allocated;
        TotalAllocated = totalAllocated;
        LastEntities = entities;
        TotalEntities = totalEntities;
    }

    /// <summary>Runs measured so far.</summary>
    public long Runs { get; }

    /// <summary>Time of the latest run.</summary>
    public TimeSpan LastTime => Stopwatch.GetElapsedTime(0, _last);

    /// <summary>Time of all measured runs.</summary>
    public TimeSpan TotalTime => Stopwatch.GetElapsedTime(0, _total);

    /// <summary>Average time per run (zero before the first).</summary>
    public TimeSpan AverageTime => Runs == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(0, _total / Runs);

    /// <summary>
    /// Bytes the latest run allocated on the managed heap: on the thread that ran the system, plus on the threads that
    /// ran chunks of its parallel <c>[ForEach]</c> methods (<c>GC.GetAllocatedBytesForCurrentThread</c> around each).
    /// </summary>
    public long LastAllocated { get; }

    /// <summary>Bytes all measured runs allocated (see <see cref="LastAllocated"/>).</summary>
    public long TotalAllocated { get; }

    /// <summary>
    /// Entities the latest run's <c>[ForEach]</c> methods visited: the entities each method's query matched when its
    /// pass started (rows passing its filters and, for <c>[Changed&lt;T&gt;]</c> / <c>[Removed&lt;T&gt;]</c>, written or
    /// removed since the system last ran), summed over the methods; a <c>[ForEach(Slices = N)]</c> method counts the
    /// rows of the slice it visited. Queries made in its OnUpdate method are not counted.
    /// </summary>
    public long LastEntities { get; }

    /// <summary>Entities all measured runs visited (see <see cref="LastEntities"/>).</summary>
    public long TotalEntities { get; }

    internal SystemStats Add(long ticks, long allocated, long entities) =>
        new(Runs + 1, _total + ticks, ticks, allocated, TotalAllocated + allocated, entities, TotalEntities + entities);
}
