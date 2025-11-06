using System;
using System.Runtime.CompilerServices;
using EasyEcs.Core.Components;

namespace EasyEcs.Core;

/// <summary>
/// Archetype stores entities with the same component configuration.
/// Uses tombstone pattern (-1) for safe iteration during modifications.
/// Uses free list for O(1) tombstone slot reuse.
/// </summary>
internal class Archetype
{
    public Tag ComponentMask;
    public int[] EntityIds;
    public int Count;          // Total slots (including tombstones)
    public int AliveCount;     // Living entities only

    // Free list for O(1) tombstone reuse (stack-based)
    private int[] _freeSlots;
    private int _freeCount;

    private const int Tombstone = -1;

    public Archetype(in Tag componentMask, int initialCapacity = 1024)
    {
        ComponentMask = componentMask;
        EntityIds = new int[initialCapacity];
        _freeSlots = new int[initialCapacity / 4]; // Start smaller, grow as needed
        Count = 0;
        AliveCount = 0;
        _freeCount = 0;
    }

    /// <summary>
    /// Add an entity to this archetype.
    /// Reuses tombstone slots (O(1) via free list), then appends.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public void Add(int entityId)
    {
        // Fast path: Reuse free slot from free list (O(1))
        if (_freeCount > 0)
        {
            int slot = _freeSlots[--_freeCount];
            EntityIds[slot] = entityId;
            AliveCount++;
        }
        else
        {
            // No free slots, append to end
            if (Count >= EntityIds.Length)
            {
                // Grow array (doubling strategy)
                Array.Resize(ref EntityIds, EntityIds.Length * 2);
            }

            EntityIds[Count++] = entityId;
            AliveCount++;
        }
    }

    /// <summary>
    /// Remove an entity from this archetype.
    /// Marks the slot as tombstone (-1) and adds to free list for O(1) reuse.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public void Remove(int entityId)
    {
        // Find and tombstone the entity
        var span = EntityIds.AsSpan(0, Count);
        int idx = span.IndexOf(entityId);

        if (idx >= 0)
        {
            EntityIds[idx] = Tombstone;
            AliveCount--;

            // Add to free list for O(1) reuse
            if (_freeCount >= _freeSlots.Length)
            {
                Array.Resize(ref _freeSlots, _freeSlots.Length * 2);
            }
            _freeSlots[_freeCount++] = idx;

            // Note: Auto-compaction is disabled to avoid unpredictable performance spikes mid-frame.
            // Call Context.CompactArchetypes() manually during loading screens or maintenance windows
            // to remove tombstones and reduce fragmentation.
        }
    }

    /// <summary>
    /// Compact the array by removing all tombstones.
    /// Clears the free list since all gaps are removed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Compact()
    {
        int writeIdx = 0;
        var span = EntityIds.AsSpan(0, Count);

        for (int readIdx = 0; readIdx < Count; readIdx++)
        {
            if (span[readIdx] != Tombstone)
            {
                EntityIds[writeIdx++] = EntityIds[readIdx];
            }
        }

        Count = writeIdx;
        // AliveCount should already match Count after compaction

        // Clear free list since there are no more tombstones
        _freeCount = 0;
    }

    /// <summary>
    /// Get a read-only span of entity IDs for iteration.
    /// Includes tombstones - iterator must skip -1 values.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<int> GetEntitySpan() => new ReadOnlySpan<int>(EntityIds, 0, Count);

    /// <summary>
    /// Get fragmentation ratio (percentage of tombstones).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float GetFragmentation()
    {
        return Count > 0 ? 1.0f - (float)AliveCount / Count : 0f;
    }
}
