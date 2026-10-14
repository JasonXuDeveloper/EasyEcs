using System;
using System.Runtime.CompilerServices;
using EasyEcs.Core.Components;
using EasyEcs.Core.Enumerators;

namespace EasyEcs.Core;

/// <summary>
/// An ECS world: entities, their components, singletons and the systems that update them.
/// Worlds are fully isolated from each other; the recommended server layout is one world per thread/shard.
/// </summary>
public sealed partial class World : IDisposable
{
    // Entity storage
    private int _entityIdCounter;

    // Recycled entity ids (LIFO). Reusing ids keeps every per-entity / per-component array bounded by the
    // peak number of live entities instead of growing forever in long-running processes.
    private int[] _freeEntityIds = new int[64];
    private int _freeEntityIdCount;

    internal Entity[] Entities;
    internal EntityMeta[] Meta; // version + archetype membership per entity id (see EntityMeta)
    private int _activeEntityCount;

    // Component storage: one column (T[]) per component type, indexed by entity id
    internal Array[] Components = [];

    private readonly WorldOptions _options;
    private bool _disposed;

    public World(WorldOptions options = null)
    {
        _options = options ?? new WorldOptions();
        int initialCapacity = Math.Max(1, _options.InitialEntityCapacity);
        Entities = new Entity[initialCapacity];
        Meta = new EntityMeta[initialCapacity];
    }

    /// <summary>
    /// Get count of active entities.
    /// </summary>
    public int EntityCount => _activeEntityCount;

    /// <summary>
    /// Ensure entity arrays can hold at least the specified capacity.
    /// </summary>
    public void EnsureEntityCapacity(int capacity)
    {
        if (Entities.Length >= capacity)
            return;

        lock (_structuralLock)
        {
            if (Entities.Length >= capacity)
                return;

            ResizeEntityArrays(capacity);
        }
    }

    /// <summary>
    /// Resize all entity and component arrays to the specified capacity.
    /// Must be called inside _structuralLock.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void ResizeEntityArrays(int newCapacity)
    {
        Array.Resize(ref Entities, newCapacity);
        Array.Resize(ref Meta, newCapacity);

        StorageVersion++;

        // Grow every component column to the new capacity (cold path)
        for (int i = 0; i < Components.Length; i++)
        {
            var column = Components[i];
            if (column == null || column.Length >= newCapacity) continue;
            var grown = Array.CreateInstance(column.GetType().GetElementType()!, newCapacity);
            Array.Copy(column, grown, column.Length);
            Components[i] = grown;
        }
    }

    /// <summary>
    /// Try to get an entity by ID.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetEntityById(int id, out EntityRef entityRef)
    {
        var meta = Meta;
        if ((uint)id < (uint)meta.Length && meta[id].Archetype != null)
        {
            entityRef = new EntityRef(id, meta[id].Version, this);
            return true;
        }

        entityRef = default;
        return false;
    }

    /// <summary>
    /// Get an entity by ID (throws if not found).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EntityRef GetEntityById(int id)
    {
        if (!TryGetEntityById(id, out var entity))
            ThrowEntityNotFound();
        return entity;
    }

    /// <summary>
    /// Create a new entity immediately.
    /// Thread-safe and returns entity directly (no callbacks).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public EntityRef CreateEntity()
    {
        lock (_structuralLock)
        {
            // Prefer a recycled id; versions were bumped on destroy so stale refs stay invalid.
            int id;
            if (_freeEntityIdCount > 0)
            {
                id = _freeEntityIds[--_freeEntityIdCount];
            }
            else
            {
                id = _entityIdCounter++;
                if (id >= Entities.Length)
                {
                    int newCapacity = Math.Max(Entities.Length * 2, id + 1);
                    ResizeEntityArrays(newCapacity);
                }
            }

            Entities[id] = new Entity(this, id, Meta[id].Version);
            _activeEntityCount++;

            // Add to empty archetype
            _emptyArchetype ??= GetOrCreateArchetype(in Tag.Empty);
            MoveToArchetype(id, _emptyArchetype);

            return new EntityRef(id, Meta[id].Version, this);
        }
    }

    /// <summary>
    /// Destroy an entity immediately.
    /// Thread-safe.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void DestroyEntity(Entity entity) => DestroyEntity(entity.Id, entity.Version);

    /// <summary>Destroy the incarnation (id, version); a stale or unknown incarnation is ignored.</summary>
    private void DestroyEntity(int id, int version)
    {
        lock (_structuralLock)
        {
            if ((uint)id >= (uint)Meta.Length) return;
            ref var meta = ref Meta[id];
            if (meta.Archetype == null || meta.Version != version)
                return;

            // Increment version to invalidate all existing references
            meta.Version++;

            DetachFromArchetype(id);

            // Release component data so managed references inside components do not outlive the entity
            ref var stored = ref Entities[id];
            var components = Components;
            for (int i = 0; i < components.Length; i++)
            {
                if (components[i] != null && stored.Tag.HasBit(i))
                    Array.Clear(components[i], id, 1);
            }

            stored = default;

            // Recycle the id (DetachFromArchetype already marked it inactive)
            _activeEntityCount--;

            if (_freeEntityIdCount >= _freeEntityIds.Length)
                Array.Resize(ref _freeEntityIds, _freeEntityIds.Length * 2);
            _freeEntityIds[_freeEntityIdCount++] = id;
        }
    }

    private Archetype _emptyArchetype;

    /// <summary>
    /// Throw when <paramref name="entity"/> is not alive or refers to an earlier occupant of a recycled id.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfStale(in Entity entity)
    {
        int id = entity.Id;
        var meta = Meta;
        if ((uint)id >= (uint)meta.Length || meta[id].Archetype == null || meta[id].Version != entity.Version)
            ThrowEntityNotFound();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowEntityNotFound() => throw new InvalidOperationException("Entity not found");

    /// <summary>
    /// Destroy an entity immediately (EntityRef overload).
    /// Thread-safe.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DestroyEntity(EntityRef entityRef) => DestroyEntity(entityRef.Id, entityRef.Version);

    /// <summary>
    /// Get all active entities (diagnostics: O(capacity)).
    /// </summary>
    public ActiveEntityEnumerator AllEntities => new ActiveEntityEnumerator(this);
}