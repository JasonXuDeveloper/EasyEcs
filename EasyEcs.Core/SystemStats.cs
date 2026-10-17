using System;
using System.Diagnostics;

namespace EasyEcs;

/// <summary>
/// How often and how long a system ran (<see cref="WorldOptions.MeasureSystems"/>): <see cref="OnUpdate"/> plus its
/// <c>[ForEach]</c> methods, per run. Read it between updates; with <see cref="WorldOptions.ParallelSystems"/> each
/// system records its own runs.
/// </summary>
public readonly struct SystemStats
{
    private readonly long _total;
    private readonly long _last;

    private SystemStats(long runs, long total, long last)
    {
        Runs = runs;
        _total = total;
        _last = last;
    }

    /// <summary>Runs measured so far.</summary>
    public long Runs { get; }

    /// <summary>Time of the latest run.</summary>
    public TimeSpan Last => Stopwatch.GetElapsedTime(0, _last);

    /// <summary>Time of all measured runs.</summary>
    public TimeSpan Total => Stopwatch.GetElapsedTime(0, _total);

    /// <summary>Average time per run (zero before the first).</summary>
    public TimeSpan Average => Runs == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(0, _total / Runs);

    internal SystemStats Add(long ticks) => new(Runs + 1, _total + ticks, ticks);
}
