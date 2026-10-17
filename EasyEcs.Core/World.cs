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

    // Change tick: stamped into the rows of tracked components (ITrackChanges) on every write. Grows by one per system
    // stage, once after each update and once per index catch-up outside a pass that found written rows; 64 bits never
    // wrap.
    private ulong _changeTick = 1;

    /// <summary>
    /// The current change tick, stamped into tracked components' rows on every write (<see cref="ITrackChanges"/>).
    /// </summary>
    public ulong ChangeTick => _changeTick;

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

    /// <summary>Create an entity without components. O(1) amortized. Owner thread only (see doc/guide/threading.md).</summary>
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

    /// <summary>
    /// Create an entity with a copy of every component of <paramref name="prefab"/>. Values are copied as they are: a
    /// <see cref="DynamicBuffer{T}"/> gets a list of its own, indexes learn the new entity, tracked components count as
    /// written now. O(components): one row copy, no table search after the first instance. Owner thread only (see
    /// doc/guide/threading.md).
    /// </summary>
    public Entity Instantiate(Prefab prefab) => Copy(prefab.Row, false);

    /// <summary>
    /// <see cref="Instantiate(Prefab)"/> for <c>entities.Length</c> copies of <paramref name="prefab"/>, whose handles
    /// are written to <paramref name="entities"/>: one pass through the structural gate and at most one table growth.
    /// O(count × components). Owner thread only.
    /// </summary>
    public void Instantiate(Prefab prefab, Span<Entity> entities)
    {
        using (Structural())
        {
            if (entities.IsEmpty) return;
            var (source, from) = Source(prefab.Row);
            var target = TableFor(source);
            int first = PlaceRows(target, entities);
            target.CloneRows(source, from, first, entities.Length, _changeTick);
            if (_indexByBit != null)
                for (int e = 0; e < entities.Length; e++) IndexRow(target, first + e, entities[e].Index, entities[e].Version);
            if (_groupByBit != null)
                foreach (var entity in entities) RegroupRow(entity.Index); // the prefab's values, this world's groups
        }
    }

    /// <summary>
    /// A new entity of this world with a copy of <paramref name="original"/>'s components (<c>Entity.Clone</c> is the
    /// public call), or of a prefab's row (<paramref name="prefab"/>). Inside the structural gate it takes.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)] // structural change: keeps the loops that call it lean
    internal Entity Copy(Entity original, bool sameWorld)
    {
        using (Structural())
        {
            var (source, from) = Source(original);
            var target = sameWorld ? source : TableFor(source);
            int id = AllocateId(); // may grow Meta: the original's row was read first
            int slot = Place(id, target);
            target.CloneRows(source, from, slot, 1, _changeTick);
            int version = MetaRef(id).Version;
            if (_indexByBit != null) IndexRow(target, slot, id, version);
            if (!sameWorld && _groupByBit != null) RegroupRow(id);
            return new Entity(this, id, version);
        }
    }

    /// <summary>
    /// Place <c>entities.Length</c> new entities in consecutive rows at the end of <paramref name="target"/> (one
    /// growth of ids and rows) and write their handles; returns the first row. Their values are the caller's to write.
    /// Inside the structural gate.
    /// </summary>
    private int PlaceRows(Archetype target, Span<Entity> entities)
    {
        ReserveIds(entities.Length);
        target.Reserve(target.Count + entities.Length);
        int first = target.Count; // new rows go to the end, one after another
        for (int e = 0; e < entities.Length; e++)
        {
            int id = AllocateId();
            Place(id, target);
            entities[e] = new Entity(this, id, MetaRef(id).Version);
        }

        return first;
    }

    /// <summary>The table and row of <paramref name="entity"/> (an alive entity of any world).</summary>
    internal static (Archetype Table, int Row) Source(Entity entity)
    {
        var world = entity.World;
        if (world == null) Entity.ThrowStale();
        ref var meta = ref world!.Live(entity);
        return (meta.Archetype, meta.Slot);
    }

    /// <summary>This world's table with the components of <paramref name="source"/>, a prefab's table (cached per table).</summary>
    private Archetype TableFor(Archetype source)
    {
        _prefabTables ??= new();
        if (!_prefabTables.TryGetValue(source, out var target))
            _prefabTables[source] = target = GetOrCreateArchetype(source.ComponentMask.Clone(), source);
        return target;
    }

    // A prefab's table -> this world's table with its components, found once.
    private System.Collections.Generic.Dictionary<Archetype, Archetype> _prefabTables;

    /// <summary>
    /// Destroy <paramref name="entity"/> (a handle of this world; <c>Entity.Destroy</c> is the public call); a stale
    /// handle is ignored. An entity holding <see cref="ICleanupComponent"/>s is retired instead (see <see cref="Retire"/>).
    /// O(components). Owner thread only (see doc/guide/threading.md).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)] // structural change: keeps the loops that call it lean
    internal void DestroyEntity(Entity entity)
    {
        PrepareCascade();
        using (Structural())
        {
            ref var meta = ref MetaRef(entity.Index); // ids of this world's handles are always < Meta.Length
            if (meta.Archetype == null || meta.Version != entity.Version) return;
            Destroy(ref meta, entity.Id);
            if (_parentCount != 0) DestroyChildren();
        }
    }

    /// <summary>
    /// Destroy alive <paramref name="entity"/> (<paramref name="meta"/> is its record): retire it when it holds cleanup
    /// components (see <see cref="Retire"/>), else free it, then remember it as a parent whose children follow. False
    /// when it was retired already (its children went then). Inside the structural gate.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Destroy(ref EntityMeta meta, EntityId entity)
    {
        if (!meta.Archetype.HasCleanup) Free(ref meta, entity.Index);
        else if (!Retire(ref meta, entity.Index)) return false;
        Orphaned(entity);
        return true;
    }

    /// <summary>
    /// Destroy an entity whose table has cleanup columns: when the row holds a cleanup component, remove its other
    /// components (as Remove does) and tag it <see cref="Destroyed"/>, keeping it alive; else free it. Returns false
    /// when the entity was retired already. Inside the structural gate. Cold: tables with cleanup columns only.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool Retire(ref EntityMeta meta, int id)
    {
        if (!meta.Archetype.HoldsCleanup(meta.Slot))
        {
            Free(ref meta, id);
            return true;
        }

        bool retired = meta.Archetype.ColumnIndex(DestroyedBit) is var tag and >= 0 && meta.Archetype.Has(tag, meta.Slot);
        AddBit(ref meta, id, DestroyedBit);
        var archetype = meta.Archetype;
        int slot = meta.Slot;
        bool removed = false;
        for (int c = 0; c < archetype.Components.Length; c++)
        {
            // Change-tick columns follow their component; a Disabled<T> tag goes with the removal of its component.
            var info = archetype.Info(c);
            if (info.Cleanup || info.Owner >= 0 || info.DisabledOf >= 0 || archetype.Components[c] == DestroyedBit) continue;
            removed |= TakeBit(archetype, slot, archetype.Components[c]);
        }

        if (removed) StorageVersion++; // passes re-read presence bits
        return !retired;
    }

    /// <summary>Bit of the <see cref="Destroyed"/> tag.</summary>
    private static readonly ushort DestroyedBit = ComponentType<Destroyed>.Bit;

    /// <summary>Destroy alive entity <paramref name="id"/> (<paramref name="meta"/> is its record). Inside the structural gate.</summary>
    private void Free(ref EntityMeta meta, int id)
    {
        var archetype = meta.Archetype;
        if (_indexByBit != null) Unindex(archetype, id);
        meta.Version++; // invalidates every outstanding handle
        meta.Archetype = null;
        RemoveRow(archetype, meta.Slot);
        StorageVersion++;

        _entityCount--;
        if (_freeIdCount == _freeIds.Length) Array.Resize(ref _freeIds, _freeIds.Length * 2);
        _freeIds[_freeIdCount++] = id;
    }

    /// <summary>Destroy every alive row of <paramref name="match"/> that passes its filter; returns how many.</summary>
    private int DestroyMatching(QueryMatch match, ulong since)
    {
        int destroyed = 0;
        PrepareCascade();
        using (Structural())
        {
            var archetypes = match.Archetypes;
            for (int a = 0; a < archetypes.Length; a++)
            {
                var archetype = archetypes[a];
                var rows = new RowFilter(match.Filters[a], since);
                int mode = archetype.AliveCount == 0 ? RowFilter.None : rows.Mode(archetype);
                if (mode == RowFilter.None) continue;
                if (mode == RowFilter.All && archetype.Count == archetype.AliveCount && !archetype.HasCleanup &&
                    Volatile.Read(ref Passes) == 0)
                {
                    destroyed += FreeTable(archetype); // every row goes: no row moves, one clear
                    continue;
                }

                // From the last row down: outside passes a removed row is filled by the last one, already visited, so
                // the passing rows of each word (computed once per word) stay valid for the rows below.
                int count = archetype.Count;
                for (int w = (count - 1) >> 6; w >= 0; w--)
                {
                    ulong bits = rows.Word(archetype, w) & RowFilter.RowMask(w, count);
                    for (; bits != 0; bits &= ~(1UL << 63 >> BitOperations.LeadingZeroCount(bits)))
                    {
                        int slot = (w << 6) + 63 - BitOperations.LeadingZeroCount(bits);
                        var entity = archetype.Slots[slot];
                        if (entity.Index >= 0 && Destroy(ref MetaRef(entity.Index), entity)) destroyed++;
                    }
                }
            }

            if (_parentCount != 0) DestroyChildren();
        }

        return destroyed;
    }

    /// <summary>
    /// Destroy every entity of <paramref name="archetype"/>, which has no tombstones, while no pass runs: free them
    /// (as <see cref="Free"/> does), then empty the table at once. Returns how many. Inside the structural gate.
    /// </summary>
    private int FreeTable(Archetype archetype)
    {
        int count = archetype.Count;
        if (_freeIds.Length < _freeIdCount + count)
            Array.Resize(ref _freeIds, (int)BitOperations.RoundUpToPowerOf2((uint)(_freeIdCount + count)));
        var rows = archetype.Slots.AsSpan(0, count);
        for (int slot = count - 1; slot >= 0; slot--) // the order Free would push them in
        {
            int id = rows[slot].Index;
            Orphaned(rows[slot]);
            if (_indexByBit != null) Unindex(archetype, id);
            ref var meta = ref MetaRef(id);
            meta.Version++; // invalidates every outstanding handle
            meta.Archetype = null;
            _freeIds[_freeIdCount++] = id;
        }

        archetype.Clear();
        _entityCount -= count;
        StorageVersion++;
        return count;
    }

    /// <summary>Make room for <paramref name="count"/> more entities (one growth). Inside the structural gate.</summary>
    private void ReserveIds(int count)
    {
        int needed = _nextId + Math.Max(0, count - _freeIdCount);
        if (needed > Meta.Length) Grow((int)BitOperations.RoundUpToPowerOf2((uint)needed));
    }

    /// <summary>
    /// The handle of entity <paramref name="id"/> if that incarnation is alive; false for a destroyed entity (even when
    /// its index was reused) and for <see cref="EntityId.Null"/>. O(1).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetEntity(EntityId id, out Entity entity)
    {
        var meta = Meta;
        if ((uint)id.Index < (uint)meta.Length && meta[id.Index].Version == id.Version && meta[id.Index].Archetype != null)
        {
            entity = new Entity(this, id);
            return true;
        }

        entity = default;
        return false;
    }

    /// <summary>The handle of entity <paramref name="id"/>; throws if that incarnation is not alive. O(1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Entity GetEntity(EntityId id)
    {
        if (!TryGetEntity(id, out var entity)) Entity.ThrowStale();
        return entity;
    }

    /// <summary>Whether entity <paramref name="id"/> (that incarnation) is alive. O(1).</summary>
    public bool IsAlive(EntityId id) => TryGetEntity(id, out _);

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
        ref var meta = ref MetaRef(entity.Index); // ids of this world's handles are always < Meta.Length
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
        MetaRef(id).Version = 1; // versions start at 1: EntityId.Null (version 0) never names an entity
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

        /// <summary>The current living entity.</summary>
        public readonly Entity Current => new(_world, _index, _meta[_index].Version);

        /// <summary>The enumerator itself (<c>foreach</c> support, no allocation).</summary>
        public readonly EntityEnumerator GetEnumerator() => this;

        /// <summary>Move to the next living entity (ids in ascending order). O(1) amortized per id.</summary>
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
