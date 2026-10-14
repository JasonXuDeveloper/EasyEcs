using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EasyEcs;

/// <summary>
/// An ECS world: entities, their components, singletons, events and the systems that update them. Worlds are fully
/// isolated; the recommended server layout is one world per thread (shard), which needs no synchronization at all.
/// </summary>
public sealed partial class World : IDisposable
{
    // Never shrinks (handles index it without bounds checks); grows by doubling under the structural lock.
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

    /// <summary>Pre-size entity storage (and existing columns) so spawning up to <paramref name="capacity"/> never grows arrays.</summary>
    public void EnsureEntityCapacity(int capacity)
    {
        lock (StructuralLock)
        {
            if (Meta.Length < capacity)
                Grow(capacity);
        }
    }

    /// <summary>Create an entity without components. O(1) amortized. Thread-safe.</summary>
    public Entity CreateEntity()
    {
        lock (StructuralLock)
        {
            int id = AllocateId();
            MoveToArchetype(id, _emptyArchetype);
            return new Entity(this, id, Meta[id].Version);
        }
    }

    // CreateEntity(in T1..Tn) is generated (World.Structural.g.cs): one archetype placement, no intermediate moves.

    /// <summary>Destroy <paramref name="entity"/>; a stale or foreign handle is ignored. O(components). Thread-safe.</summary>
    public void DestroyEntity(Entity entity)
    {
        if (!ReferenceEquals(entity.World, this)) return;
        lock (StructuralLock)
        {
            int id = entity.Id;
            ref var meta = ref Meta[id];
            var archetype = meta.Archetype;
            if (archetype == null || meta.Version != entity.Version) return;

            meta.Version++; // invalidates every outstanding handle
            archetype.RemoveAt(meta.Slot);
            meta.Archetype = null;
            ReleaseComponents(id, archetype.Components);

            _entityCount--;
            if (_freeIdCount == _freeIds.Length) Array.Resize(ref _freeIds, _freeIds.Length * 2);
            _freeIds[_freeIdCount++] = id;
        }
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

    /// <summary>Handle of an id known to be alive (query iteration). No checks.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Entity EntityAt(int id) => new(this, id, MetaRef(id).Version);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref EntityMeta MetaRef(int id) => ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Meta), id);

    /// <summary>Throw unless <paramref name="entity"/> is alive. Must be called inside the structural lock.</summary>
    internal void ThrowIfStale(in Entity entity)
    {
        if (!ReferenceEquals(entity.World, this)) Entity.ThrowStale();
        ref var meta = ref MetaRef(entity.Id); // ids of this world's handles are always < Meta.Length
        if (meta.Version != entity.Version || meta.Archetype == null) Entity.ThrowStale();
    }

    /// <summary>Take a recycled id or a new one (growing storage). Must be called inside the structural lock.</summary>
    private int AllocateId()
    {
        _entityCount++;
        if (_freeIdCount > 0) return _freeIds[--_freeIdCount];
        int id = _nextId++;
        if (id == Meta.Length) Grow(Meta.Length * 2);
        return id;
    }

    /// <summary>Grow entity metadata and every column to <paramref name="capacity"/> (cold path).</summary>
    private void Grow(int capacity)
    {
        Array.Resize(ref Meta, capacity);
        var columns = Components;
        for (int i = 0; i < columns.Length; i++)
        {
            var column = columns[i];
            if (column == null || column.Length >= capacity) continue;
            var grown = Array.CreateInstance(column.GetType().GetElementType()!, capacity);
            Array.Copy(column, grown, column.Length);
            columns[i] = grown;
        }

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
