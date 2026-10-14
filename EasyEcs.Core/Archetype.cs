using System;
using System.Runtime.CompilerServices;
namespace EasyEcs;

/// <summary>
/// Archetype stores entities with the same component configuration.
/// Uses tombstone pattern (-1) for safe iteration during modifications.
/// Uses free list for O(1) tombstone slot reuse.
/// <br/>
/// The slot of each entity is tracked by the owning <see cref="World"/> (entity id -> slot),
/// so removal is a plain array write instead of a hash lookup.
/// </summary>
internal class Archetype
{
    public readonly Tag ComponentMask;

    /// <summary>Component bits of this archetype, ascending (lets destroy/remove visit only its own columns).</summary>
    public readonly ushort[] Components;

    public int[] EntityIds;
    public int Count; // Total slots (including tombstones)
    public int AliveCount; // Living entities only

    // Free list for O(1) tombstone reuse (stack-based)
    private int[] _freeSlots;
    private int _freeCount;

    // Transition cache: component bit index -> archetype reached by adding / removing that component.
    // Avoids hashing a 256-bit Tag on every single-component structural change.
    private Archetype[] _addEdges = Array.Empty<Archetype>();
    private Archetype[] _removeEdges = Array.Empty<Archetype>();

    private const int Tombstone = -1;

    public Archetype(in Tag componentMask, int initialCapacity = 64)
    {
        ComponentMask = componentMask;
        Components = componentMask.ToBitArray();
        EntityIds = new int[initialCapacity];
        _freeSlots = new int[16];
        Count = 0;
        AliveCount = 0;
        _freeCount = 0;
    }

    /// <summary>
    /// Add an entity to this archetype and return the slot it occupies.
    /// Reuses tombstone slots (O(1) via free list), then appends.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public int Add(int entityId)
    {
        int slot;

        if (_freeCount > 0)
        {
            slot = _freeSlots[--_freeCount];
        }
        else
        {
            if (Count >= EntityIds.Length)
                Array.Resize(ref EntityIds, EntityIds.Length * 2);

            slot = Count++;
        }

        EntityIds[slot] = entityId;
        AliveCount++;
        return slot;
    }

    /// <summary>
    /// Remove the entity occupying <paramref name="slot"/>.
    /// Marks the slot as tombstone (-1) and adds to free list for O(1) reuse.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public void RemoveAt(int slot)
    {
        EntityIds[slot] = Tombstone;
        AliveCount--;

        if (_freeCount >= _freeSlots.Length)
            Array.Resize(ref _freeSlots, _freeSlots.Length * 2);

        _freeSlots[_freeCount++] = slot;

        // Note: Auto-compaction is disabled to avoid unpredictable performance spikes mid-frame.
        // Call World.CompactArchetypes() manually during loading screens or maintenance windows
        // to remove tombstones and reduce fragmentation.
    }

    /// <summary>
    /// Remove tombstones and sort the remaining ids ascending, rewriting each entity's slot. Columns are indexed by
    /// entity id, so ascending ids turn iteration into a forward, prefetch-friendly walk over every column.
    /// </summary>
    public void Compact(EntityMeta[] meta)
    {
        int alive = 0;
        for (int i = 0; i < Count; i++)
            if (EntityIds[i] != Tombstone)
                EntityIds[alive++] = EntityIds[i];

        Array.Sort(EntityIds, 0, alive);
        for (int slot = 0; slot < alive; slot++)
            meta[EntityIds[slot]].Slot = slot;

        Count = alive;
        _freeCount = 0;
    }

    /// <summary>
    /// Cached archetype reached by adding component <paramref name="bit"/>; null when not cached yet.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Archetype GetAddEdge(int bit) => bit < _addEdges.Length ? _addEdges[bit] : null;

    /// <summary>
    /// Cached archetype reached by removing component <paramref name="bit"/>; null when not cached yet.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Archetype GetRemoveEdge(int bit) => bit < _removeEdges.Length ? _removeEdges[bit] : null;

    public void SetAddEdge(int bit, Archetype target) => SetEdge(ref _addEdges, bit, target);

    public void SetRemoveEdge(int bit, Archetype target) => SetEdge(ref _removeEdges, bit, target);

    private static void SetEdge(ref Archetype[] edges, int bit, Archetype target)
    {
        if (bit >= edges.Length)
            Array.Resize(ref edges, Math.Max(bit + 1, edges.Length * 2));
        edges[bit] = target;
    }

    /// <summary>
    /// Get a read-only span of entity IDs for iteration.
    /// Includes tombstones - iterator must skip -1 values.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<int> GetEntitySpan() => new ReadOnlySpan<int>(EntityIds, 0, Count);

}
