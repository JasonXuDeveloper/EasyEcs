using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EasyEcs.Core.Components;

namespace EasyEcs.Core;

/// <summary>
/// Struct callback for <see cref="Context.ForEach{T1, TJob}(ref TJob)"/>.
/// Being a struct constrained generic, calls are devirtualized and usually inlined by the JIT.
/// </summary>
public interface IForEach<T1>
    where T1 : struct, IComponent
{
    void Execute(int entityId, ref T1 c1);
}

/// <summary>
/// Struct callback for <see cref="Context.ForEach{T1, T2, TJob}(ref TJob)"/>.
/// Being a struct constrained generic, calls are devirtualized and usually inlined by the JIT.
/// </summary>
public interface IForEach<T1, T2>
    where T1 : struct, IComponent
    where T2 : struct, IComponent
{
    void Execute(int entityId, ref T1 c1, ref T2 c2);
}

/// <summary>
/// Struct callback for <see cref="Context.ForEach{T1, T2, T3, TJob}(ref TJob)"/>.
/// Being a struct constrained generic, calls are devirtualized and usually inlined by the JIT.
/// </summary>
public interface IForEach<T1, T2, T3>
    where T1 : struct, IComponent
    where T2 : struct, IComponent
    where T3 : struct, IComponent
{
    void Execute(int entityId, ref T1 c1, ref T2 c2, ref T3 c3);
}

/// <summary>
/// Struct callback for <see cref="Context.ForEach{T1, T2, T3, T4, TJob}(ref TJob)"/>.
/// Being a struct constrained generic, calls are devirtualized and usually inlined by the JIT.
/// </summary>
public interface IForEach<T1, T2, T3, T4>
    where T1 : struct, IComponent
    where T2 : struct, IComponent
    where T3 : struct, IComponent
    where T4 : struct, IComponent
{
    void Execute(int entityId, ref T1 c1, ref T2 c2, ref T3 c3, ref T4 c4);
}

/// <summary>
/// Struct callback for <see cref="Context.ForEach{T1, T2, T3, T4, T5, TJob}(ref TJob)"/>.
/// Being a struct constrained generic, calls are devirtualized and usually inlined by the JIT.
/// </summary>
public interface IForEach<T1, T2, T3, T4, T5>
    where T1 : struct, IComponent
    where T2 : struct, IComponent
    where T3 : struct, IComponent
    where T4 : struct, IComponent
    where T5 : struct, IComponent
{
    void Execute(int entityId, ref T1 c1, ref T2 c2, ref T3 c3, ref T4 c4, ref T5 c5);
}

/// <summary>
/// Struct callback for <see cref="Context.ForEach{T1, T2, T3, T4, T5, T6, TJob}(ref TJob)"/>.
/// Being a struct constrained generic, calls are devirtualized and usually inlined by the JIT.
/// </summary>
public interface IForEach<T1, T2, T3, T4, T5, T6>
    where T1 : struct, IComponent
    where T2 : struct, IComponent
    where T3 : struct, IComponent
    where T4 : struct, IComponent
    where T5 : struct, IComponent
    where T6 : struct, IComponent
{
    void Execute(int entityId, ref T1 c1, ref T2 c2, ref T3 c3, ref T4 c4, ref T5 c5, ref T6 c6);
}

public partial class Context
{
    // Bumped whenever component storage arrays may be reallocated; lets ForEach cache array refs safely.
    internal int StorageVersion;

    /// <summary>
    /// Run <paramref name="job"/> for every entity having all of <c>T1</c>.
    /// Zero allocation and faster than <c>GroupOf</c> enumeration; structural changes inside the job are allowed
    /// (entities added during the pass may or may not be visited, removed ones are skipped).
    /// Do not call <see cref="CompactArchetypes"/> from inside the job.
    /// </summary>
    public void ForEach<T1, TJob>(ref TJob job)
        where T1 : struct, IComponent
        where TJob : struct, IForEach<T1>
    {
        if (!TagRegistry.TryGetTagBitIndex<T1>(out var b1)) return;
        var query = new Tag();
        query.SetBit(b1);
        var archetypes = GetMatchingArchetypes(in query);
        if (archetypes.Count == 0) return; // also guarantees every component array below exists
        int version = StorageVersion;
        var a1 = Unsafe.As<T1[]>(Components[b1]);

        for (int ai = 0; ai < archetypes.Count; ai++)
        {
            var archetype = archetypes[ai];
            if (archetype.AliveCount == 0) continue;

            // Snapshot hot fields; refreshed only when storage may have been reallocated by the job.
            int[] ids = archetype.EntityIds;
            int count = archetype.Count;
            for (int i = 0; i < count; i++)
            {
                int id = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(ids), i);
                if (id < 0) continue;

                job.Execute(id,
                    ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a1), id));

                if (version != StorageVersion)
                {
                    version = StorageVersion;
                    a1 = Unsafe.As<T1[]>(Components[b1]);
                    ids = archetype.EntityIds;
                    count = archetype.Count;
                }
            }
        }
    }

    /// <summary>
    /// Run <paramref name="job"/> for every entity having all of <c>T1, T2</c>.
    /// Zero allocation and faster than <c>GroupOf</c> enumeration; structural changes inside the job are allowed
    /// (entities added during the pass may or may not be visited, removed ones are skipped).
    /// Do not call <see cref="CompactArchetypes"/> from inside the job.
    /// </summary>
    public void ForEach<T1, T2, TJob>(ref TJob job)
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where TJob : struct, IForEach<T1, T2>
    {
        if (!TagRegistry.TryGetTagBitIndex<T1>(out var b1)) return;
        if (!TagRegistry.TryGetTagBitIndex<T2>(out var b2)) return;
        var query = new Tag();
        query.SetBit(b1);
        query.SetBit(b2);
        var archetypes = GetMatchingArchetypes(in query);
        if (archetypes.Count == 0) return; // also guarantees every component array below exists
        int version = StorageVersion;
        var a1 = Unsafe.As<T1[]>(Components[b1]);
        var a2 = Unsafe.As<T2[]>(Components[b2]);

        for (int ai = 0; ai < archetypes.Count; ai++)
        {
            var archetype = archetypes[ai];
            if (archetype.AliveCount == 0) continue;

            // Snapshot hot fields; refreshed only when storage may have been reallocated by the job.
            int[] ids = archetype.EntityIds;
            int count = archetype.Count;
            for (int i = 0; i < count; i++)
            {
                int id = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(ids), i);
                if (id < 0) continue;

                job.Execute(id,
                    ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a1), id),
                    ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a2), id));

                if (version != StorageVersion)
                {
                    version = StorageVersion;
                    a1 = Unsafe.As<T1[]>(Components[b1]);
                    a2 = Unsafe.As<T2[]>(Components[b2]);
                    ids = archetype.EntityIds;
                    count = archetype.Count;
                }
            }
        }
    }

    /// <summary>
    /// Run <paramref name="job"/> for every entity having all of <c>T1, T2, T3</c>.
    /// Zero allocation and faster than <c>GroupOf</c> enumeration; structural changes inside the job are allowed
    /// (entities added during the pass may or may not be visited, removed ones are skipped).
    /// Do not call <see cref="CompactArchetypes"/> from inside the job.
    /// </summary>
    public void ForEach<T1, T2, T3, TJob>(ref TJob job)
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where TJob : struct, IForEach<T1, T2, T3>
    {
        if (!TagRegistry.TryGetTagBitIndex<T1>(out var b1)) return;
        if (!TagRegistry.TryGetTagBitIndex<T2>(out var b2)) return;
        if (!TagRegistry.TryGetTagBitIndex<T3>(out var b3)) return;
        var query = new Tag();
        query.SetBit(b1);
        query.SetBit(b2);
        query.SetBit(b3);
        var archetypes = GetMatchingArchetypes(in query);
        if (archetypes.Count == 0) return; // also guarantees every component array below exists
        int version = StorageVersion;
        var a1 = Unsafe.As<T1[]>(Components[b1]);
        var a2 = Unsafe.As<T2[]>(Components[b2]);
        var a3 = Unsafe.As<T3[]>(Components[b3]);

        for (int ai = 0; ai < archetypes.Count; ai++)
        {
            var archetype = archetypes[ai];
            if (archetype.AliveCount == 0) continue;

            // Snapshot hot fields; refreshed only when storage may have been reallocated by the job.
            int[] ids = archetype.EntityIds;
            int count = archetype.Count;
            for (int i = 0; i < count; i++)
            {
                int id = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(ids), i);
                if (id < 0) continue;

                job.Execute(id,
                    ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a1), id),
                    ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a2), id),
                    ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a3), id));

                if (version != StorageVersion)
                {
                    version = StorageVersion;
                    a1 = Unsafe.As<T1[]>(Components[b1]);
                    a2 = Unsafe.As<T2[]>(Components[b2]);
                    a3 = Unsafe.As<T3[]>(Components[b3]);
                    ids = archetype.EntityIds;
                    count = archetype.Count;
                }
            }
        }
    }

    /// <summary>
    /// Run <paramref name="job"/> for every entity having all of <c>T1, T2, T3, T4</c>.
    /// Zero allocation and faster than <c>GroupOf</c> enumeration; structural changes inside the job are allowed
    /// (entities added during the pass may or may not be visited, removed ones are skipped).
    /// Do not call <see cref="CompactArchetypes"/> from inside the job.
    /// </summary>
    public void ForEach<T1, T2, T3, T4, TJob>(ref TJob job)
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
        where TJob : struct, IForEach<T1, T2, T3, T4>
    {
        if (!TagRegistry.TryGetTagBitIndex<T1>(out var b1)) return;
        if (!TagRegistry.TryGetTagBitIndex<T2>(out var b2)) return;
        if (!TagRegistry.TryGetTagBitIndex<T3>(out var b3)) return;
        if (!TagRegistry.TryGetTagBitIndex<T4>(out var b4)) return;
        var query = new Tag();
        query.SetBit(b1);
        query.SetBit(b2);
        query.SetBit(b3);
        query.SetBit(b4);
        var archetypes = GetMatchingArchetypes(in query);
        if (archetypes.Count == 0) return; // also guarantees every component array below exists
        int version = StorageVersion;
        var a1 = Unsafe.As<T1[]>(Components[b1]);
        var a2 = Unsafe.As<T2[]>(Components[b2]);
        var a3 = Unsafe.As<T3[]>(Components[b3]);
        var a4 = Unsafe.As<T4[]>(Components[b4]);

        for (int ai = 0; ai < archetypes.Count; ai++)
        {
            var archetype = archetypes[ai];
            if (archetype.AliveCount == 0) continue;

            // Snapshot hot fields; refreshed only when storage may have been reallocated by the job.
            int[] ids = archetype.EntityIds;
            int count = archetype.Count;
            for (int i = 0; i < count; i++)
            {
                int id = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(ids), i);
                if (id < 0) continue;

                job.Execute(id,
                    ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a1), id),
                    ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a2), id),
                    ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a3), id),
                    ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a4), id));

                if (version != StorageVersion)
                {
                    version = StorageVersion;
                    a1 = Unsafe.As<T1[]>(Components[b1]);
                    a2 = Unsafe.As<T2[]>(Components[b2]);
                    a3 = Unsafe.As<T3[]>(Components[b3]);
                    a4 = Unsafe.As<T4[]>(Components[b4]);
                    ids = archetype.EntityIds;
                    count = archetype.Count;
                }
            }
        }
    }

    /// <summary>
    /// Run <paramref name="job"/> for every entity having all of <c>T1, T2, T3, T4, T5</c>.
    /// Zero allocation and faster than <c>GroupOf</c> enumeration; structural changes inside the job are allowed
    /// (entities added during the pass may or may not be visited, removed ones are skipped).
    /// Do not call <see cref="CompactArchetypes"/> from inside the job.
    /// </summary>
    public void ForEach<T1, T2, T3, T4, T5, TJob>(ref TJob job)
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
        where T5 : struct, IComponent
        where TJob : struct, IForEach<T1, T2, T3, T4, T5>
    {
        if (!TagRegistry.TryGetTagBitIndex<T1>(out var b1)) return;
        if (!TagRegistry.TryGetTagBitIndex<T2>(out var b2)) return;
        if (!TagRegistry.TryGetTagBitIndex<T3>(out var b3)) return;
        if (!TagRegistry.TryGetTagBitIndex<T4>(out var b4)) return;
        if (!TagRegistry.TryGetTagBitIndex<T5>(out var b5)) return;
        var query = new Tag();
        query.SetBit(b1);
        query.SetBit(b2);
        query.SetBit(b3);
        query.SetBit(b4);
        query.SetBit(b5);
        var archetypes = GetMatchingArchetypes(in query);
        if (archetypes.Count == 0) return; // also guarantees every component array below exists
        int version = StorageVersion;
        var a1 = Unsafe.As<T1[]>(Components[b1]);
        var a2 = Unsafe.As<T2[]>(Components[b2]);
        var a3 = Unsafe.As<T3[]>(Components[b3]);
        var a4 = Unsafe.As<T4[]>(Components[b4]);
        var a5 = Unsafe.As<T5[]>(Components[b5]);

        for (int ai = 0; ai < archetypes.Count; ai++)
        {
            var archetype = archetypes[ai];
            if (archetype.AliveCount == 0) continue;

            // Snapshot hot fields; refreshed only when storage may have been reallocated by the job.
            int[] ids = archetype.EntityIds;
            int count = archetype.Count;
            for (int i = 0; i < count; i++)
            {
                int id = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(ids), i);
                if (id < 0) continue;

                job.Execute(id,
                    ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a1), id),
                    ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a2), id),
                    ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a3), id),
                    ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a4), id),
                    ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a5), id));

                if (version != StorageVersion)
                {
                    version = StorageVersion;
                    a1 = Unsafe.As<T1[]>(Components[b1]);
                    a2 = Unsafe.As<T2[]>(Components[b2]);
                    a3 = Unsafe.As<T3[]>(Components[b3]);
                    a4 = Unsafe.As<T4[]>(Components[b4]);
                    a5 = Unsafe.As<T5[]>(Components[b5]);
                    ids = archetype.EntityIds;
                    count = archetype.Count;
                }
            }
        }
    }

    /// <summary>
    /// Run <paramref name="job"/> for every entity having all of <c>T1, T2, T3, T4, T5, T6</c>.
    /// Zero allocation and faster than <c>GroupOf</c> enumeration; structural changes inside the job are allowed
    /// (entities added during the pass may or may not be visited, removed ones are skipped).
    /// Do not call <see cref="CompactArchetypes"/> from inside the job.
    /// </summary>
    public void ForEach<T1, T2, T3, T4, T5, T6, TJob>(ref TJob job)
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
        where T5 : struct, IComponent
        where T6 : struct, IComponent
        where TJob : struct, IForEach<T1, T2, T3, T4, T5, T6>
    {
        if (!TagRegistry.TryGetTagBitIndex<T1>(out var b1)) return;
        if (!TagRegistry.TryGetTagBitIndex<T2>(out var b2)) return;
        if (!TagRegistry.TryGetTagBitIndex<T3>(out var b3)) return;
        if (!TagRegistry.TryGetTagBitIndex<T4>(out var b4)) return;
        if (!TagRegistry.TryGetTagBitIndex<T5>(out var b5)) return;
        if (!TagRegistry.TryGetTagBitIndex<T6>(out var b6)) return;
        var query = new Tag();
        query.SetBit(b1);
        query.SetBit(b2);
        query.SetBit(b3);
        query.SetBit(b4);
        query.SetBit(b5);
        query.SetBit(b6);
        var archetypes = GetMatchingArchetypes(in query);
        if (archetypes.Count == 0) return; // also guarantees every component array below exists
        int version = StorageVersion;
        var a1 = Unsafe.As<T1[]>(Components[b1]);
        var a2 = Unsafe.As<T2[]>(Components[b2]);
        var a3 = Unsafe.As<T3[]>(Components[b3]);
        var a4 = Unsafe.As<T4[]>(Components[b4]);
        var a5 = Unsafe.As<T5[]>(Components[b5]);
        var a6 = Unsafe.As<T6[]>(Components[b6]);

        for (int ai = 0; ai < archetypes.Count; ai++)
        {
            var archetype = archetypes[ai];
            if (archetype.AliveCount == 0) continue;

            // Snapshot hot fields; refreshed only when storage may have been reallocated by the job.
            int[] ids = archetype.EntityIds;
            int count = archetype.Count;
            for (int i = 0; i < count; i++)
            {
                int id = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(ids), i);
                if (id < 0) continue;

                job.Execute(id,
                    ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a1), id),
                    ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a2), id),
                    ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a3), id),
                    ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a4), id),
                    ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a5), id),
                    ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a6), id));

                if (version != StorageVersion)
                {
                    version = StorageVersion;
                    a1 = Unsafe.As<T1[]>(Components[b1]);
                    a2 = Unsafe.As<T2[]>(Components[b2]);
                    a3 = Unsafe.As<T3[]>(Components[b3]);
                    a4 = Unsafe.As<T4[]>(Components[b4]);
                    a5 = Unsafe.As<T5[]>(Components[b5]);
                    a6 = Unsafe.As<T6[]>(Components[b6]);
                    ids = archetype.EntityIds;
                    count = archetype.Count;
                }
            }
        }
    }
}
