using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace EasyEcs;

/// <summary>
/// An ECS world: entities, their components, singletons and the systems that update them. Worlds are fully
/// isolated; the recommended server layout is one world per thread, which needs no synchronization at all.
/// </summary>
public sealed partial class World : IDisposable
{
    // Never shrinks (handles index it without bounds checks); grows by doubling under the structural gate.
    internal EntityMeta[] Meta;

    // Next never-used id, and recycled ids (LIFO, so hot ids stay hot and arrays stay bounded by peak population).
    private int _nextId;
    private int[] _freeIds = new int[64];
    private int _freeIdCount;
    private int _entityCount;

    private readonly WorldOptions _options;
    private bool _disposed;

    public World(WorldOptions options = null)
    {
        _options = options ?? new WorldOptions();
        Meta = new EntityMeta[Math.Max(1, _options.InitialEntityCapacity)];
        _emptyArchetype = GetOrCreateArchetype(default);
    }

    /// <summary>Number of alive entities. O(1).</summary>
    public int EntityCount => _entityCount;

    /// <summary>All alive entities in id order (diagnostics: O(capacity)).</summary>
    public EntityEnumerator Entities => new(this);

    /// <summary>Pre-size entity storage so creating up to <paramref name="capacity"/> entities never grows it.</summary>
    public void EnsureEntityCapacity(int capacity)
    {
        using (Structural())
        {
            if (Meta.Length < capacity)
                Grow(capacity);
        }
    }

    /// <summary>Create an entity without components. O(1) amortized. Thread-safe.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)] // structural change: keeps the loops that call it lean
    public Entity CreateEntity()
    {
        using (Structural())
        {
            int id = AllocateId();
            Place(id, _emptyArchetype);
            return new Entity(this, id, Meta[id].Version);
        }
    }

    // CreateEntity(in T1..Tn) is generated (World.Structural.g.cs): one archetype placement, no intermediate moves.

    /// <summary>Destroy <paramref name="entity"/>; a stale or foreign handle is ignored. O(components). Thread-safe.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)] // structural change: keeps the loops that call it lean
    public void DestroyEntity(Entity entity)
    {
        if (!ReferenceEquals(entity.World, this)) return;
        using (Structural())
        {
            int id = entity.Id;
            ref var meta = ref MetaRef(id); // ids of this world's handles are always < Meta.Length
            if (meta.Archetype == null || meta.Version != entity.Version) return;
            Free(ref meta, id);
        }
    }

    /// <summary>Destroy alive entity <paramref name="id"/> (<paramref name="meta"/> is its record). Inside the structural gate.</summary>
    private void Free(ref EntityMeta meta, int id)
    {
        var archetype = meta.Archetype;
        meta.Version++; // invalidates every outstanding handle
        meta.Archetype = null;
        RemoveRow(archetype, meta.Slot);
        StorageVersion++;

        _entityCount--;
        if (_freeIdCount == _freeIds.Length) Array.Resize(ref _freeIds, _freeIds.Length * 2);
        _freeIds[_freeIdCount++] = id;
    }

    /// <summary>Destroy every alive row of <paramref name="match"/> that passes its filter; returns how many.</summary>
    private int DestroyMatching(QueryMatch match)
    {
        int destroyed = 0;
        using (Structural())
        {
            var archetypes = match.Archetypes;
            for (int a = 0; a < archetypes.Length; a++)
            {
                var archetype = archetypes[a];
                var rows = match.Filters[a];
                if (archetype.AliveCount == 0 || rows.Mode(archetype) == RowFilter.None) continue;
                // From the last row down: outside passes a removed row is filled by the last one, already visited.
                for (int slot = archetype.Count - 1; slot >= 0; slot--)
                {
                    int id = archetype.EntityIds[slot];
                    if (id < 0 || !rows.Passes(archetype, slot)) continue;
                    Free(ref MetaRef(id), id);
                    destroyed++;
                }
            }
        }

        return destroyed;
    }

    /// <summary>Make room for <paramref name="count"/> more entities (one growth). Inside the structural gate.</summary>
    private void ReserveIds(int count)
    {
        int needed = _nextId + Math.Max(0, count - _freeIdCount);
        if (needed > Meta.Length) Grow((int)BitOperations.RoundUpToPowerOf2((uint)needed));
    }

    /// <summary>Handle of the alive entity with <paramref name="id"/>. O(1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetEntity(int id, out Entity entity)
    {
        var meta = Meta;
        if ((uint)id < (uint)meta.Length && meta[id].Archetype != null)
        {
            entity = new Entity(this, id, meta[id].Version);
            return true;
        }

        entity = default;
        return false;
    }

    /// <summary>Handle of the alive entity with <paramref name="id"/>; throws if there is none. O(1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Entity GetEntity(int id)
    {
        if (!TryGetEntity(id, out var entity)) Entity.ThrowStale();
        return entity;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref EntityMeta MetaRef(int id) => ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Meta), id);

    /// <summary>
    /// The meta record of <paramref name="entity"/>, which must be alive (throws otherwise). Must be called inside the
    /// structural gate; the ref stays valid for the rest of it (only entity creation reallocates <see cref="Meta"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref EntityMeta Live(in Entity entity)
    {
        if (!ReferenceEquals(entity.World, this)) Entity.ThrowStale();
        ref var meta = ref MetaRef(entity.Id); // ids of this world's handles are always < Meta.Length
        if (meta.Version != entity.Version || meta.Archetype == null) Entity.ThrowStale();
        return ref meta;
    }

    /// <summary>Take a recycled id or a new one (growing storage). Must be called inside the structural gate.</summary>
    private int AllocateId()
    {
        _entityCount++;
        if (_freeIdCount > 0) return _freeIds[--_freeIdCount];
        int id = _nextId++;
        if (id == Meta.Length) Grow(Meta.Length * 2);
        return id;
    }

    /// <summary>Grow entity metadata to <paramref name="capacity"/> (cold path).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow(int capacity)
    {
        Array.Resize(ref Meta, capacity);
        StorageVersion++;
    }

    private readonly Archetype _emptyArchetype;

    /// <summary>Enumerates alive entities in id order; ids beyond the capacity captured at start are not visited.</summary>
    public struct EntityEnumerator
    {
        private readonly World _world;
        private readonly EntityMeta[] _meta;
        private int _index;

        internal EntityEnumerator(World world)
        {
            _world = world;
            _meta = world.Meta;
            _index = -1;
        }

        public readonly Entity Current => new(_world, _index, _meta[_index].Version);

        public readonly EntityEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            var meta = _meta;
            while (++_index < meta.Length)
                if (meta[_index].Archetype != null)
                    return true;
            return false;
        }
    }
}
