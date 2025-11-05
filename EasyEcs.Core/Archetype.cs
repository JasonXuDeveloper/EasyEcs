using System;
using System.Runtime.CompilerServices;
using EasyEcs.Core.Components;

namespace EasyEcs.Core;

/// <summary>
/// Archetype stores entities with the same component configuration.
/// Uses tombstone pattern (-1) for safe iteration during modifications.
/// </summary>
internal class Archetype
{
    public Tag ComponentMask;
    public int[] EntityIds;
    public int Count;          // Total slots (including tombstones)
    public int AliveCount;     // Living entities only

    private const int Tombstone = -1;

    public Archetype(Tag componentMask, int initialCapacity = 1024)
    {
        ComponentMask = componentMask;
        EntityIds = new int[initialCapacity];
        Count = 0;
        AliveCount = 0;
    }

    /// <summary>
    /// Add an entity to this archetype.
    /// Reuses tombstone slots first, then appends.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public void Add(int entityId)
    {
        // Try to find a tombstone slot for reuse
        var span = EntityIds.AsSpan(0, Count);
        int emptySlot = span.IndexOf(Tombstone);

        if (emptySlot >= 0)
        {
            // Reuse tombstone slot
            EntityIds[emptySlot] = entityId;
            AliveCount++;
        }
        else
        {
            // No tombstones available, append to end
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
    /// Marks the slot as tombstone (-1) for safe iteration.
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

            // Optional: Compact if heavily fragmented (>50% tombstones)
            // This prevents memory waste while keeping iteration safe
            if (AliveCount > 0 && AliveCount < Count / 2)
            {
                Compact();
            }
        }
    }

    /// <summary>
    /// Compact the array by removing all tombstones.
    /// Only called when heavily fragmented (>50% dead slots).
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
