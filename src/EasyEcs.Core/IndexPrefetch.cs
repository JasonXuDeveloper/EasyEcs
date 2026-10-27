using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EasyEcs;

// Prefetched index probes: a generated [ForEach] job finding entities by a key its rows hold (lookup.TryFind(x.Key, ...),
// Find, Count) asks for the lines of later rows' keys stage by stage, so several probes miss at once instead of one per
// row. The stages follow the index's representation, each one's address known only once the line of the stage before
// is in the cache: a direct-indexed key's slot (one stage); a relation key's slot by the target's index, then its list
// of ids; a hashed key's bucket, then its entry (holding a unique key's entity, or a shared key's count and list). A
// third stage for a shared key's list of ids was measured and dropped: finding its head is a second lookup per row
// (ProbePrefetchBenchmark.CountClan at 16M keys: 2.4 times as slow). Past the form's cache only (Probes: the last level
// for a pass on one thread, the L2 for a parallel one).
public sealed partial class ValueIndex<T, TKey>
{
    // The hash's arrays as the gate found them, for the passes that prefetch (read once per pass by Probes, on the owner
    // thread, before any row runs; the index holds still during a pass, so they stay its arrays until it ends).
    private int[] _probeBuckets;
    private Array _probeEntries;
    private int _probeStride;
    private IEqualityComparer<TKey> _probeComparer;

    /// <summary>
    /// Whether a pass probing this index prefetches its probes: where a prefetch exists, and when the index's lines a
    /// probe lands on (slots, buckets and entries, lists of ids) are past a cache of <paramref name="cache"/> bytes (the
    /// form's gate, <see cref="CacheSizes"/>; 0, unknown: never); in the cache a prefetch costs more than it hides
    /// (BatchProbeBenchmark: 1.3-1.6x at 1k and 100k keys). Reads the hash's arrays for <see cref="Prefetch"/>. Once
    /// per pass, on the owner thread.
    /// </summary>
    internal bool Probes(long cache)
    {
        if (!Prefetching.IsSupported || cache <= 0) return false;
        bool hashed = _heads.Count > 0 && HashSlots<TKey, IndexHead>.Works;
        int stride = hashed ? HashSlots<TKey, IndexHead>.Stride(_heads, out _probeBuckets, out _probeEntries, out _probeComparer) : 0;
        if (stride == 0) _probeBuckets = null;
        _probeStride = stride;
        long bytes = (long)(_slots?.Length ?? 0) * Unsafe.SizeOf<KeySlot>()
            + (long)_byTarget.Length * Unsafe.SizeOf<IndexHead>()
            + (long)_heads.Count * (sizeof(int) + Math.Max(stride, Unsafe.SizeOf<IndexHead>()))
            + (Unique ? 0 : (long)_nodes.Length * Unsafe.SizeOf<EntityId>()); // the lists of ids
        return LookupLanes.PastCache(bytes, cache);
    }

    /// <summary>
    /// Stage <paramref name="stage"/> (1 or 2) of a probe of <paramref name="key"/>: its first line, then the line the
    /// first locates (the comment on this file's partial); a stage the key's representation does not have does nothing. Reads
    /// nothing a miss would stall on: stage 2 reads only what stage 1 brought in. Never faults, never changes the index:
    /// a key no entity has only wastes it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal unsafe void Prefetch(TKey key, int stage)
    {
        if (Address(key, stage) is var line && line != null) Prefetching.Line(line);
    }

    /// <summary>The line stage <paramref name="stage"/> of a probe of <paramref name="key"/> prefetches, null for none (<see cref="Prefetch"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal unsafe void* Address(TKey key, int stage)
    {
        if (IsNull(key)) return null;
        if (Unique && _keySlots != null)
        {
            // A direct index finds the key in its slot (a key whose slot is out of range is the hash's).
            int slot = _keySlots.SlotOf(key);
            var slots = _slots;
            if ((uint)slot < (uint)slots.Length) return stage == 1 ? Unsafe.AsPointer(ref slots[slot]) : null;
        }

        if (typeof(TKey) == typeof(EntityId)) // a JIT-time constant: a relation's list by the target's index
            return _byTarget.Address(Unsafe.As<TKey, EntityId>(ref key), stage);

        var buckets = _probeBuckets;
        if (buckets == null) return null;
        var comparer = _probeComparer;
        uint hash = (uint)(typeof(TKey).IsValueType && comparer == null ? key.GetHashCode() : comparer!.GetHashCode(key));
        // The dictionary's bucket: its FastMod is the remainder (HashHelpers.FastMod, exact for 32-bit divisors).
        ref int bucket = ref buckets[hash % (uint)buckets.Length];
        if (stage == 1) return Unsafe.AsPointer(ref bucket);
        // The bucket's first entry (1-based; a chain of collisions past it is not followed).
        var entries = _probeEntries;
        int entry = bucket - 1;
        return (uint)entry < (uint)entries.Length
            ? Unsafe.AsPointer(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(entries), (nint)entry * _probeStride))
            : null;
    }
}

/// <summary>
/// Where a <see cref="Dictionary{TKey, TValue}"/> keeps a key, for prefetching a probe of it: its buckets
/// (<c>_buckets</c>, a key's bucket the hash modulo their count, 1 + its first entry's index) and its entries
/// (<c>_entries</c>, one struct per key). Private fields of the runtime's own dictionary: read through an
/// UnsafeAccessor (the buckets, the comparer) and reflection once per resize (the entries, whose element type is
/// private); <see cref="Works"/> checks once per process that they are there and agree, and is false otherwise (an
/// index then prefetches no hashed key: today's probe).
/// </summary>
internal static class HashSlots<TKey, TValue> where TKey : notnull
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_buckets")]
    private static extern ref int[] BucketsOf(Dictionary<TKey, TValue> dictionary);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_comparer")]
    private static extern ref IEqualityComparer<TKey> ComparerOf(Dictionary<TKey, TValue> dictionary);

    private static readonly FieldInfo EntriesField = typeof(Dictionary<TKey, TValue>).GetField("_entries", BindingFlags.NonPublic | BindingFlags.Instance);

    // The entries of the dictionary last read, and the buckets they go with: a resize replaces both arrays.
    [ThreadStatic] private static int[] t_buckets;
    [ThreadStatic] private static Array t_entries;

    /// <summary>The fields are there, of the shapes read here, on this runtime.</summary>
    public static readonly bool Works = Check();

    private static bool Check()
    {
        try
        {
            var probe = new Dictionary<TKey, TValue>(1);
            return BucketsOf(probe) is { Length: > 0 } buckets && EntriesField?.GetValue(probe) is Array { Length: > 0 } entries
                && buckets.Length == entries.Length && entries.GetType().GetElementType() is { IsValueType: true };
        }
        catch (Exception)
        {
            return false; // a field renamed or gone, or a runtime that refuses the accessor
        }
    }

    /// <summary>
    /// <paramref name="dictionary"/>'s buckets, entries and comparer, and the bytes of one entry; 0 when it has none
    /// allocated or they disagree. Reflection runs only when the buckets changed since the last call on this thread.
    /// </summary>
    public static int Stride(Dictionary<TKey, TValue> dictionary, out int[] buckets, out Array entries, out IEqualityComparer<TKey> comparer)
    {
        buckets = BucketsOf(dictionary);
        comparer = ComparerOf(dictionary);
        entries = null;
        if (buckets == null) return 0;
        if (!ReferenceEquals(buckets, t_buckets))
        {
            t_entries = EntriesField.GetValue(dictionary) as Array;
            t_buckets = buckets;
        }

        entries = t_entries;
        if (entries == null || entries.Length != buckets.Length) return 0;
        return RuntimeHelpers.SizeOf(entries.GetType().GetElementType()!.TypeHandle);
    }
}
