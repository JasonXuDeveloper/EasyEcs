using System;

namespace EasyEcs;

/// <summary>Configuration of a <see cref="World"/>.</summary>
public sealed class WorldOptions
{
    /// <summary>
    /// Run the systems of one priority bucket in parallel (thread pool) when the bucket holds more than one system.
    /// Such systems must touch disjoint data. Default false: one owner thread per world is the recommended model.
    /// </summary>
    public bool Parallel { get; init; }

    /// <summary>Max degree of parallelism for <see cref="Parallel"/> (default: processor count).</summary>
    public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount;

    /// <summary>Pre-sized entity capacity (avoid growth reallocations at runtime).</summary>
    public int InitialEntityCapacity { get; init; } = 1024;
}
