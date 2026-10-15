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

    /// <summary>
    /// At the start of every update, re-sort the archetypes whose entity order degraded through churn (as
    /// <see cref="World.CompactArchetypes"/> does for all of them), so iteration keeps walking columns forward.
    /// Costs O(archetypes) per update when nothing needs sorting. Default true. With it, do not call
    /// <c>Update</c> while a query of the same world is iterating.
    /// </summary>
    public bool SortArchetypesOnUpdate { get; init; } = true;
}
