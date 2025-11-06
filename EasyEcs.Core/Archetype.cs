using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using EasyEcs.Core.Components;

namespace EasyEcs.Core;

/// <summary>
/// Archetype stores entities with the same component configuration.
/// Uses tombstone pattern (-1) for safe iteration during modifications.
/// Uses free list for O(1) tombstone slot reuse.
/// Uses reverse lookup dictionary for O(1) removal.
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

    // Reverse lookup: entityId -> index in EntityIds array (O(1) removal)
    private readonly Dictionary<int, int> _entityToIndex;

    private const int Tombstone = -1;

    public Archetype(in Tag componentMask, int initialCapacity = 1024)
    {
        ComponentMask = componentMask;
        EntityIds = new int[initialCapacity];
        _freeSlots = new int[initialCapacity]; // Match capacity to avoid early resizing
        _entityToIndex = new Dictionary<int, int>(initialCapacity);
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
        int slot;

        // Fast path: Reuse free slot from free list (O(1))
        if (_freeCount > 0)
        {
            slot = _freeSlots[--_freeCount];
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

            slot = Count++;
            EntityIds[slot] = entityId;
            AliveCount++;
        }

        // Update reverse lookup for O(1) removal
        _entityToIndex[entityId] = slot;
    }

    /// <summary>
    /// Remove an entity from this archetype.
    /// O(1) removal using reverse lookup dictionary.
    /// Marks the slot as tombstone (-1) and adds to free list for O(1) reuse.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public void Remove(int entityId)
    {
        // O(1) lookup using reverse index
        if (!_entityToIndex.TryGetValue(entityId, out int idx))
            return;

        // Tombstone the slot
        EntityIds[idx] = Tombstone;
        AliveCount--;

        // Remove from reverse lookup
        _entityToIndex.Remove(entityId);

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

    /// <summary>
    /// Compact the array by removing all tombstones.
    /// Clears the free list and rebuilds reverse lookup since all gaps are removed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Compact()
    {
        int writeIdx = 0;
        var span = EntityIds.AsSpan(0, Count);

        // Clear and rebuild reverse lookup
        _entityToIndex.Clear();

        for (int readIdx = 0; readIdx < Count; readIdx++)
        {
            int entityId = span[readIdx];
            if (entityId != Tombstone)
            {
                EntityIds[writeIdx] = entityId;
                _entityToIndex[entityId] = writeIdx;  // Rebuild reverse lookup
                writeIdx++;
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
