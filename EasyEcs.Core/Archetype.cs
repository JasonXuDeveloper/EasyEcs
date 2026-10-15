using System;
using System.Numerics;
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

    /// <summary>The subset of <see cref="Components"/> whose type holds managed references (cleared on destroy).</summary>
    public ushort[] ManagedComponents = [];

    /// <summary>
    /// Insertions that may have broken ascending id order since the last <see cref="Compact"/> (a reused tombstone
    /// slot, or an append of an id smaller than the last one). Drives the automatic re-sort in World.Update.
    /// </summary>
    public int Unordered;

    public int[] EntityIds;
    public int[] Versions; // parallel to EntityIds (same length): version of the entity in each slot, for ForEachEntity
    public int Count; // Total slots (including tombstones)
    public int AliveCount; // Living entities only

    // Free list for O(1) tombstone reuse (stack-based)
    private int[] _freeSlots;
    private int _freeCount;

    // Transition cache: component bit index -> archetype reached by adding / removing that component.
    // Avoids hashing a 256-bit Tag on every single-component structural change.
    private Archetype[] _addEdges = Array.Empty<Archetype>();
    private Archetype[] _removeEdges = Array.Empty<Archetype>();

    // Same for multi-component changes (Set(a, b), Remove<A, B>(), CreateEntity(a, b, ...)), indexed by the dense
    // index of the generic signature (TypeIndex<ComponentSignature, (T1, ..., Tn)>): no mask cloning or hashing per call.
    private Archetype[] _addSetEdges = Array.Empty<Archetype>();
    private Archetype[] _removeSetEdges = Array.Empty<Archetype>();

    private const int Tombstone = -1;

    public Archetype(in Tag componentMask, int initialCapacity = 64)
    {
        ComponentMask = componentMask;
        Components = componentMask.ToBitArray();
        EntityIds = new int[initialCapacity];
        Versions = new int[initialCapacity];
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
    public int Add(int entityId, int version)
    {
        int slot;

        if (_freeCount > 0)
        {
            slot = _freeSlots[--_freeCount];
            Unordered++;
        }
        else
        {
            if (Count >= EntityIds.Length)
            {
                Array.Resize(ref EntityIds, EntityIds.Length * 2);
                Array.Resize(ref Versions, EntityIds.Length);
            }

            slot = Count++;
            if (slot > 0 && EntityIds[slot - 1] > entityId) Unordered++;
        }

        EntityIds[slot] = entityId;
        Versions[slot] = version;
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
    /// Whether enough insertions broke id order (or enough tombstones piled up) that re-sorting pays for itself:
    /// more than an eighth of the alive entities, plus a floor so small archetypes are left alone.
    /// </summary>
    public bool NeedsSort
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Unordered + (Count - AliveCount) > (AliveCount >> 3) + 64;
    }

    /// <summary>
    /// Remove tombstones and sort the remaining ids ascending, rewriting each entity's slot. Columns are indexed by
    /// entity id, so ascending ids turn iteration into a forward, prefetch-friendly walk over every column.
    /// A bitmap sort: O(alive + (max id - min id) / 64), and the slot rewrites walk <paramref name="meta"/> forward.
    /// <paramref name="bits"/> must cover every entity id and be all zero; it is all zero again on return.
    /// </summary>
    public void Compact(EntityMeta[] meta, ulong[] bits)
    {
        int[] ids = EntityIds;
        int count = Count;
        int min = int.MaxValue, max = -1, alive = 0;
        for (int i = 0; i < count; i++)
        {
            int id = ids[i];
            if (id < 0) continue;
            bits[id >> 6] |= 1UL << id; // the shift count is taken mod 64
            min = Math.Min(min, id);
            max = Math.Max(max, id);
            alive++;
        }

        int slot = 0;
        for (int w = min >> 6, last = max >> 6; alive > 0 && w <= last; w++)
        {
            ulong word = bits[w];
            if (word == 0) continue;
            bits[w] = 0;
            do
            {
                int id = (w << 6) | BitOperations.TrailingZeroCount(word);
                ids[slot] = id;
                Versions[slot] = meta[id].Version;
                meta[id].Slot = slot++;
                word &= word - 1;
            } while (word != 0);
        }

        Count = alive;
        _freeCount = 0;
        Unordered = 0;
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

    /// <summary>Cached archetype reached by adding the components of signature <paramref name="signature"/>; null when not cached yet.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Archetype GetAddSetEdge(int signature) =>
        signature < _addSetEdges.Length ? _addSetEdges[signature] : null;

    /// <summary>Cached archetype reached by removing the components of signature <paramref name="signature"/>; null when not cached yet.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Archetype GetRemoveSetEdge(int signature) =>
        signature < _removeSetEdges.Length ? _removeSetEdges[signature] : null;

    public void SetAddSetEdge(int signature, Archetype target) => SetEdge(ref _addSetEdges, signature, target);

    public void SetRemoveSetEdge(int signature, Archetype target) => SetEdge(ref _removeSetEdges, signature, target);

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
