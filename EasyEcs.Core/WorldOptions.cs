using System;

namespace EasyEcs;

/// <summary>Configuration of a <see cref="World"/>.</summary>
public sealed class WorldOptions
{
    /// <summary>
    /// Run systems of one priority at the same time (thread pool) when their generated access does not conflict
    /// (<see cref="SystemAccess"/>). Default false: one owner thread per world is the recommended model.
    /// </summary>
    public bool ParallelSystems { get; init; }

    /// <summary>
    /// Threads one parallel run uses at most (default: processor count): systems run by <see cref="ParallelSystems"/>, and the
    /// row chunks of parallel <c>[ForEach]</c> methods, <c>ForEachParallel</c> and <c>ForEachEntityParallel</c> (which
    /// run in chunks whether <see cref="ParallelSystems"/> is on or not).
    /// </summary>
    public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount;

    /// <summary>Pre-sized entity capacity (avoid growth reallocations at runtime).</summary>
    public int InitialEntityCapacity { get; init; } = 1024;

    /// <summary>
    /// Time every system run into <see cref="EcsSystem.Stats"/> (two timestamp reads per run). Default false.
    /// </summary>
    public bool MeasureSystems { get; init; }
}
