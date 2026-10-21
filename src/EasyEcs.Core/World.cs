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

    /// <summary>
    /// A new, empty world configured by <paramref name="options"/> (null: the defaults). O(<see cref="WorldOptions.InitialEntityCapacity"/>):
    /// allocates the entity array and the empty table.
    /// </summary>
    public World(WorldOptions options = null)
    {
        _options = options ?? new WorldOptions();
        Meta = AllocateMeta(Math.Max(1, _options.InitialEntityCapacity), true);
        _queryLookup = _queryCache.GetAlternateLookup<QueryLookup>();
        _emptyArchetype = GetOrCreateArchetype(default);
        ArgumentOutOfRangeException.ThrowIfNegative(_options.DestroyLog, nameof(options));
        if (_options.DestroyLog > 0) _destroyLog = new DestroyRecord[_options.DestroyLog];
        Collectible.Track(this); // held weakly: a world nobody holds stops mentioning its bits
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
                Grow(capacity, true);
        }
    }

    /// <summary>
    /// Pre-size the table <paramref name="prefab"/>'s instances go to, and the entity storage, so the next
    /// <paramref name="count"/> <see cref="Instantiate(Prefab)"/> calls never grow them: a wave of spawns pays its
    /// growth before the tick. Instances whose groups put them in another table (<see cref="GroupBy{T}"/>) grow that
    /// one. For a prefab with members, every member's table. O(components) plus one growth. Owner thread only.
    /// </summary>
    public void Reserve(Prefab prefab, int count)
    {
        using (Structural())
        {
            var tables = prefab.Store._allArchetypes;
            for (int i = 0; i < tables.Length; i++)
            {
                if (tables[i].Count == 0) continue;
                var target = TableFor(tables[i]);
                target.Reserve(target.Count + tables[i].Count * count);
            }

            ReserveIds(prefab.Store.EntityCount * count);
            StorageVersion++; // columns may have moved: a pass in progress refreshes its refs
        }
    }

    /// <summary>Create an entity without components. O(1) amortized. Owner thread only (see docs/content/guide/threading.md).</summary>
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
    /// written now. O(components): one row copy, no table search after the first instance. A prefab with members
    /// creates them all and returns the entity of the one passed (O(members × components)). Owner thread only (see
    /// docs/content/guide/threading.md).
    /// </summary>
    public Entity Instantiate(Prefab prefab) => prefab.IsGroup ? InstantiateGroup(prefab) : Copy(prefab.Row, false);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Entity InstantiateGroup(Prefab prefab)
    {
        ArriveAll(prefab.Store);
        return Arrived(prefab.Id);
    }

    /// <summary>
    /// <see cref="Instantiate(Prefab)"/> for <c>entities.Length</c> copies of <paramref name="prefab"/>, whose handles
    /// are written to <paramref name="entities"/>: one pass through the structural gate and at most one table growth.
    /// O(count × components). Owner thread only.
    /// </summary>
    public void Instantiate(Prefab prefab, Span<Entity> entities)
    {
        if (prefab.IsGroup)
        {
            if (entities.Length > 1) // before the first copy: a member's key, given twice
                using (Structural())
                    ClaimGroup(prefab.Store, entities.Length);
            for (int i = 0; i < entities.Length; i++) entities[i] = InstantiateGroup(prefab);
            return;
        }

        using (Structural())
        {
            if (entities.IsEmpty) return;
            var (source, from) = Source(prefab.Row);
            ClaimRows(source, from, 1, entities.Length);
            var target = TableFor(source);
            int first = PlaceRows(target, entities);
            target.CloneRows(source, from, first, entities.Length, _changeTick);
            IndexRows(target, first, entities.Length);
            RegroupRows(target, first, entities.Length); // the prefab's values, this world's groups
        }
    }

    // Instantiate(prefab, in T1..Tn) and Instantiate(prefab, entities, in T1..Tn) are generated (World.Structural.g.cs):
    // the table of the prefab's and the given components, found once per (prefab table, signature), one row copy, no moves.

    /// <summary>
    /// This world's table for instances of <paramref name="prefab"/> (not a group) with <paramref name="bits"/> given
    /// (the components of <paramref name="signature"/>), and the prefab's <paramref name="source"/> table and row.
    /// Inside the structural gate.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Archetype PrefabTable(Prefab prefab, int signature, ReadOnlySpan<ushort> bits, out Archetype source, out int from)
    {
        (source, from) = Source(prefab.Row);
        return TableFor(source, signature, bits);
    }

    /// <summary>
    /// Before <paramref name="count"/> copies of prefab row <paramref name="from"/> of <paramref name="source"/> are placed
    /// with <paramref name="given"/> written over them: the prefab's unique keys other than the given ones must be free
    /// (the given values claim theirs, ClaimAll). Folded to one test for tables without unique components. Inside the
    /// structural gate.
    /// </summary>
    private void ClaimPrefab(Archetype source, int from, int count, ReadOnlySpan<ushort> given)
    {
        if (!source.Unique || count == 0) return;
        var components = source.Components;
        for (int c = 0; c < components.Length; c++)
            if (ComponentInfo.Of(components[c]).Unique && !given.Contains(components[c]))
                UniqueIndex(components[c]).ClaimRows(source, c, from, 1, count, -1);
    }

    /// <summary>
    /// Finish new rows [<paramref name="first"/>, +<paramref name="count"/>) of <paramref name="target"/>: a prefab's
    /// copy whose given components (<paramref name="bits"/>) were written over it. Those the prefab lacked or kept
    /// disabled are present now and lose their <see cref="Disabled{T}"/> tag (as Set does), then indexes learn the rows
    /// and the prefab's values find their groups. Inside the structural gate.
    /// </summary>
    private void Instantiated(Archetype target, int first, int count, ReadOnlySpan<ushort> bits)
    {
        if (target.HasAbsent) Restore(target, first, count, bits);
        IndexRows(target, first, count);
        RegroupRows(target, first, count); // the given values chose their groups already: only the prefab's move
    }

    /// <summary>The given <paramref name="bits"/> of rows a prefab lacked or kept disabled come back (cold: such prefabs only).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Restore(Archetype target, int first, int count, ReadOnlySpan<ushort> bits)
    {
        bool restored = false;
        for (int i = 0; i < bits.Length; i++)
        {
            int column = target.ColumnIndex(bits[i]);
            for (int row = first; row < first + count; row++)
            {
                if (!target.MarkPresent(column, row)) continue;
                restored = true;
                if (target.HasDisabled) Undisable(target, column, row);
            }
        }

        if (restored) StorageVersion++; // passes re-read presence bits
    }

    /// <summary>
    /// A new entity of this world with a copy of <paramref name="original"/>'s components: an entity of this world
    /// (<paramref name="sameWorld"/>, <c>Entity.Clone</c> is the public call) or a prefab's row. Inside the structural
    /// gate it takes.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)] // structural change: keeps the loops that call it lean
    internal Entity Copy(Entity original, bool sameWorld)
    {
        using (Structural())
        {
            var (source, from) = Source(original);
            ClaimRows(source, from, 1, 1); // a clone's unique keys are its original's: taken (null keys aside)
            var target = sameWorld ? source : TableFor(source);
            int id = AllocateId(); // may grow Meta: the original's row was read first
            int slot = Place(id, target);
            target.CloneRows(source, from, slot, 1, _changeTick);
            int version = MetaRef(id).Version;
            IndexRows(target, slot, 1);
            if (!sameWorld) RegroupRows(target, slot, 1); // a clone has its original's groups
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
        if (!_prefabTables.TryGetValue(new PrefabKey(source, -1), out var target))
            _prefabTables[new PrefabKey(source, -1)] = target = GetOrCreateArchetype(source.ComponentMask.Clone(), source);
        return target;
    }

    /// <summary>
    /// This world's table with the components of <paramref name="source"/>, a prefab's table, and <paramref name="bits"/>
    /// (the components of <paramref name="signature"/>), cached per both: an instance with given components goes
    /// straight there, no table of the prefab's components alone is made.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Archetype TableFor(Archetype source, int signature, ReadOnlySpan<ushort> bits) =>
        _prefabTables != null && _prefabTables.TryGetValue(new PrefabKey(source, signature), out var target) ? target : NewTableFor(source, signature, bits);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Archetype NewTableFor(Archetype source, int signature, ReadOnlySpan<ushort> bits)
    {
        var mask = source.ComponentMask.Clone();
        for (int i = 0; i < bits.Length; i++) mask.SetBit(bits[i]);
        return (_prefabTables ??= new())[new PrefabKey(source, signature)] = GetOrCreateArchetype(in mask, source);
    }

    // (A prefab's table, a signature of given components or -1) -> this world's table with all of them, found once.
    private System.Collections.Generic.Dictionary<PrefabKey, Archetype> _prefabTables;

    /// <summary>A key of <see cref="_prefabTables"/>: compared by reference, hashed by table id (no ValueTuple comparer).</summary>
    internal readonly struct PrefabKey(Archetype source, int signature) : IEquatable<PrefabKey>
    {
        public readonly Archetype Source = source;
        public readonly int Signature = signature;

        public bool Equals(PrefabKey other) => ReferenceEquals(Source, other.Source) && Signature == other.Signature;
        public override bool Equals(object obj) => obj is PrefabKey other && Equals(other);
        public override int GetHashCode() => RuntimeHelpers.GetHashCode(Source) * 31 + Signature;
    }

    /// <summary>
    /// Destroy <paramref name="entity"/> (a handle of this world; <c>Entity.Destroy</c> is the public call); a stale
    /// handle is ignored. An entity holding <see cref="ICleanupComponent"/>s is retired instead (see <see cref="Retire"/>).
    /// O(components). Owner thread only (see docs/content/guide/threading.md).
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
            if (_targetCount != 0) FollowLinks();
        }
    }

    /// <summary>
    /// Destroy the entities of <paramref name="entities"/> (stale ids and repeats are skipped; an entity holding cleanup
    /// components is retired, as <see cref="Entity.Destroy"/> does) and return how many. One pass through the structural
    /// gate and one cascade for the whole list: a list of ids goes as <c>CollectionsMarshal.AsSpan(list)</c>, no copy.
    /// Destroy by filter with <c>DestroyAll</c>. O(entities). Owner thread only.
    /// </summary>
    public int DestroyEntities(params ReadOnlySpan<EntityId> entities)
    {
        int destroyed = 0;
        PrepareCascade();
        using (Structural())
        {
            for (int i = 0; i < entities.Length; i++)
            {
                var id = entities[i];
                if ((uint)id.Index >= (uint)Meta.Length) continue; // an id of a larger world: not one of these
                ref var meta = ref MetaRef(id.Index);
                if (meta.Archetype != null && meta.Version == id.Version && Destroy(ref meta, id)) destroyed++;
            }

            if (_targetCount != 0) FollowLinks();
        }

        return destroyed;
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
        if (_destroyLog != null) LogDestroy(_destroyLog, new EntityId(id, meta.Version));
        meta.Version++; // invalidates every outstanding handle
        meta.Archetype = null;
        archetype.ReleaseBuffers(meta.Slot, 1);
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
                if (archetype.AliveCount != 0 && archetype.Count == archetype.AliveCount && !archetype.HasCleanup &&
                    Volatile.Read(ref Passes) == 0 && new RowFilter(match.Filters[a], since).Mode(archetype) == RowFilter.All)
                    destroyed += FreeTable(archetype); // every row goes: no row moves, one clear
            }

            var op = new DestroyOp(); // the other tables row by row (the freed ones have no rows left)
            WalkDown(match, since, ref op);
            destroyed += op.Count;
            if (_targetCount != 0) FollowLinks();
        }

        return destroyed;
    }

    private struct DestroyOp : IRowOp
    {
        public int Count;

        public readonly bool Words(World world, Archetype table) => false;

        public readonly void ApplyWord(World world, Archetype table, int word, ulong rows) { }

        public void Apply(World world, Archetype table, int slot, EntityId entity)
        {
            if (world.Destroy(ref world.MetaRef(entity.Index), entity)) Count++;
        }
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
        var log = _destroyLog;
        for (int slot = count - 1; slot >= 0; slot--) // the order Free would push them in
        {
            int id = rows[slot].Index;
            Orphaned(rows[slot]);
            if (_indexByBit != null) Unindex(archetype, id);
            if (log != null) LogDestroy(log, rows[slot]);
            ref var meta = ref MetaRef(id);
            meta.Version++; // invalidates every outstanding handle
            meta.Archetype = null;
            _freeIds[_freeIdCount++] = id;
        }

        archetype.ReleaseBuffers(0, count);
        archetype.Clear();
        _entityCount -= count;
        StorageVersion++;
        return count;
    }

    /// <summary>
    /// Make room for <paramref name="count"/> more entities (one growth: to what they need, or to twice the capacity when
    /// that is more, as <see cref="Archetype.Reserve"/>). Inside the structural gate.
    /// </summary>
    private void ReserveIds(int count)
    {
        int needed = _nextId + Math.Max(0, count - _freeIdCount);
        if (needed <= Meta.Length) return;
        int doubled = Meta.Length * 2;
        Grow(Math.Max(needed, doubled), needed >= doubled);
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
        if (id == Meta.Length) Grow(Meta.Length * 2, false);
        MetaRef(id).Version = 1; // versions start at 1: EntityId.Null (version 0) never names an entity
        return id;
    }

    /// <summary>
    /// Grow entity metadata to <paramref name="capacity"/> (cold path): <paramref name="sized"/> when the capacity is
    /// the one asked for (a reservation, a restore), not a doubling step (see <see cref="AllocateMeta"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow(int capacity, bool sized)
    {
        var grown = AllocateMeta(capacity, sized);
        Meta.AsSpan().CopyTo(grown);
        Meta = grown;
        StorageVersion++;
    }

    /// <summary>
    /// Entity records for <paramref name="length"/> ids. A large array (85,000 bytes and up) sized by a reservation
    /// (<paramref name="sized"/>: <see cref="EnsureEntityCapacity"/>, <c>Reserve</c>, <c>CreateEntities</c>, the
    /// initial capacity, a restore) goes to the pinned object heap, beside the unmanaged columns; the steps of a doubling
    /// stay on the large object heap, which can still be compacted when asked to, so the arrays a doubling discards leave
    /// no hole for good. Neither heap compacts by default, so pinning moves nothing that would have moved. Each heap sets
    /// off a full collection once its own allocations since the last one pass its budget (3 MiB after a collection of a
    /// small heap): with the records on the pinned heap, a start-up of 100,000 entities stays under both, the large
    /// object heap left to the entity rows and the caller's own arrays (the handle span of <c>CreateEntities</c>). See
    /// decision D28 (docs/content/decisions.mdx).
    /// </summary>
    private static EntityMeta[] AllocateMeta(int length, bool sized) =>
        sized && length >= LargeMeta ? GC.AllocateArray<EntityMeta>(length, pinned: true) : new EntityMeta[length];

    /// <summary>Records from which an <see cref="EntityMeta"/> array is a large object (85,000 bytes).</summary>
    private const int LargeMeta = 85_000 / 16;

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
