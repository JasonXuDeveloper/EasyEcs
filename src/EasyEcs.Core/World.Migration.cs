using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace EasyEcs;

// Arrivals: entities of another world become new entities of this one. Their rows are copied table by table, like an
// instantiated prefab; EntityId fields that pointed at an entity that came along point at its new entity, the others
// become EntityId.Null. Four callers share it: LoadEntities (the bytes load into a world of their own first),
// MoveEntities (rows of a live world), CopyEntities (some of their components, the originals staying) and prefab
// groups (the prefab's own world).
public sealed partial class World
{
    // The world the bytes of a LoadEntities load into, reused by every migration.
    private World _arrivals;

    // Per id of the source world: the entity it became, valid while its stamp is the current arrival's (so a source
    // entity that stayed behind is never mistaken for one an earlier arrival copied, and nothing is cleared per arrival).
    private EntityId[] _arrived = [];
    private int[] _arrivedStamp = [];
    private int _arrival;
    private readonly List<(Archetype Table, int First, int Count)> _arrivedRows = [];
    private readonly List<Entity> _roots = [];

    /// <summary>
    /// Create the entities <c>SaveEntities</c> wrote (in this process or another), with new ids, and write their
    /// handles in save order into <paramref name="entities"/> (as many as fit); returns how many were created.
    /// <see cref="EntityId"/> fields of their components and buffer elements that named a saved entity name
    /// its new entity, every other one is <see cref="EntityId.Null"/> (the generator tells the world where those fields
    /// are; ids inside collections are not rewritten). A <see cref="ChildOf"/> whose parent stayed behind is removed:
    /// the entity is a root here. Indexes and groups learn the entities; tracked components count as written.
    /// O(entities × components). Not while a pass iterates.
    /// </summary>
    /// <exception cref="System.IO.InvalidDataException">The bytes are not a save, or give a unique key to several entities.</exception>
    /// <exception cref="InvalidOperationException">An entity of the save has a unique key (<see cref="IUniqueIndexed{TKey}"/>) an entity of this world has: nothing is created.</exception>
    public int LoadEntities(ReadOnlySpan<byte> input, Span<Entity> entities = default)
    {
        var source = LoadArrivals(input, nameof(LoadEntities));
        int created = ArriveAll(source, take: true);
        int order = Math.Min(entities.Length, source._loadOrderCount);
        for (int i = 0; i < order; i++) entities[i] = Arrived(source._loadOrder[i]);
        return created;
    }

    /// <summary>
    /// Put the components <see cref="SaveEntities(System.Buffers.IBufferWriter{byte}, ReadOnlySpan{EntityId})"/> wrote
    /// onto entities that exist already (an actor whose entity was made before its record arrived): the components of
    /// the i-th saved entity are set on <paramref name="targets"/>[i], added or replacing the values it has, each entity
    /// moving table at most once; components the save does not hold stay as they are. Indexes, groups and change ticks
    /// follow as with <c>Set</c> (every loaded component counts as written); a disabled component arrives disabled
    /// with its value. <see cref="EntityId"/> fields that named a saved entity name the entity of
    /// <paramref name="targets"/> it became, every other one is <see cref="EntityId.Null"/>, and a <see cref="ChildOf"/>
    /// whose parent stayed behind is not set. Returns how many entities were loaded. O(entities × components). Not
    /// while a pass iterates.
    /// <br/>
    /// With <paramref name="markWritten"/> false, the loaded components do not count as written: each one the save holds
    /// has no change tick afterwards, also when it replaces a value written before (a default from a prefab), so
    /// <c>[Changed&lt;T&gt;]</c> systems and <c>Since</c> filters do not visit the entity for the load (an actor restored
    /// from storage holds what storage has, and "save what changed" saves nothing of it until code writes it).
    /// Components the save does not hold keep their ticks.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="targets"/> does not hold one living entity of this world per saved entity, each once.</exception>
    /// <exception cref="System.IO.InvalidDataException">The bytes are not a save, or give a unique key to several entities.</exception>
    /// <exception cref="InvalidOperationException">
    /// A loaded unique key (<see cref="IUniqueIndexed{TKey}"/>) is held by another entity than the one it is loaded onto
    /// (checked against the world before the load): nothing is written.
    /// </exception>
    public int LoadEntitiesInto(ReadOnlySpan<byte> input, ReadOnlySpan<Entity> targets, bool markWritten = true)
    {
        var source = LoadArrivals(input, nameof(LoadEntitiesInto));
        int count = source._loadOrderCount;
        if (targets.Length != count)
            throw new ArgumentException($"The save holds {count} entities and {targets.Length} were given", nameof(targets));
        using (Structural())
        {
            foreach (var entity in targets)
                if (entity.World != this || !IsAlive(entity.Id))
                    throw new ArgumentException("An entity to load into is stale or of another world", nameof(targets));
            ThrowIfRepeated(targets);
            // Unique keys first, against the world as it is before the load: nothing is written when one is taken.
            if (ComponentInfo.AnyUnique)
                for (int i = 0; i < count; i++)
                {
                    ref var saved = ref source.MetaRef(source._loadOrder[i].Index);
                    ClaimRows(saved.Archetype, saved.Slot, 1, 1, targets[i].Index);
                }

            // Every saved entity maps to its entity of targets: the loaded values' references are rewritten in the source
            // first, then copied.
            BeginArrival(source);
            for (int i = 0; i < count; i++)
            {
                var saved = source._loadOrder[i];
                _arrived[saved.Index] = targets[i].Id;
                Stamp(saved);
            }

            var map = new IdMap(source, _arrived, _arrivedStamp, _arrival);
            foreach (var table in source._allArchetypes)
                for (int c = 0; c < table.Columns.Length && table.Count != 0; c++)
                    table.Info(c).Remap(table.Columns[c], 0, table.Count, map);
            for (int i = 0; i < count; i++)
            {
                ref var from = ref source.MetaRef(source._loadOrder[i].Index);
                LoadInto(from.Archetype, from.Slot, targets[i].Index, markWritten);
            }

            StorageVersion++;
        }

        return count;
    }

    /// <summary>
    /// Throws when an entity appears twice in <paramref name="targets"/>: two saved entities would be written onto one, the
    /// second overwriting the first. The arrival stamps of this world mark each entity once (no set is allocated).
    /// Inside the structural gate.
    /// </summary>
    private void ThrowIfRepeated(ReadOnlySpan<Entity> targets)
    {
        if (targets.Length < 2) return;
        BeginArrival(this);
        foreach (var entity in targets)
        {
            if (HasArrived(entity.Id)) throw new ArgumentException("An entity to load into is given twice", nameof(targets));
            Stamp(entity.Id);
        }
    }

    /// <summary>
    /// The world of the arrivals of <paramref name="call"/> (reused), holding the group the bytes of a
    /// <c>SaveEntities</c> wrote. Throws, before reading them, while a pass iterates this world.
    /// </summary>
    private World LoadArrivals(ReadOnlySpan<byte> input, string call)
    {
        ThrowIfIterating(call);
        // Sized like this world: a group saved here names ids below its capacity, so loads never grow the staging world
        // past the first (a group of another world's ids grows it by doubling, to the largest id seen).
        var source = _arrivals ??= new World(new WorldOptions { InitialEntityCapacity = Meta.Length });
        source.Load(input);
        return source;
    }

    /// <summary>
    /// Set the components of row <paramref name="row"/> of <paramref name="source"/> (a loaded table, references
    /// rewritten) on entity <paramref name="id"/>: one move to the table with both sets of components, then each value
    /// as <c>Set</c> writes it (stamped as written unless <paramref name="markWritten"/> is false). Inside the
    /// structural gate.
    /// </summary>
    private void LoadInto(Archetype source, int row, int id, bool markWritten)
    {
        ref var meta = ref MetaRef(id);
        var before = meta.Archetype;
        if (_indexByBit != null) // the values the load replaces leave their indexes (they come back with the new ones)
            for (int sc = 0; sc < source.Columns.Length; sc++)
                if (IndexOf(source.Components[sc]) is { } index && Loads(source, sc, row))
                    index.Removed(id);

        if (source.LoadSignature < 0) source.LoadSignature = Interlocked.Increment(ref TypeIndex<ComponentSignature>.Count) - 1;
        MoveTo(ref meta, id, before.GetAddSetEdge(source.LoadSignature) ?? CreateSetEdge(before, source.Components, source.LoadSignature));
        var table = meta.Archetype;
        int slot = meta.Slot;
        for (int sc = 0; sc < source.Columns.Length; sc++)
        {
            var info = source.Info(sc);
            if (info.Owner >= 0) continue; // a change-tick column: stamped with its component
            ushort bit = source.Components[sc];
            int c = table.ColumnIndex(bit);
            if (!Loads(source, sc, row))
            {
                if (before.ColumnIndex(bit) < 0) table.MarkAbsent(c, slot); // the move added the column: the entity lacks it
                continue;
            }

            bool present = source.Has(sc, row);
            if (info.Managed)
            {
                info.Discard(table.Columns[c], slot, 1); // the value it replaces (a buffer's list goes back to its pool)
                info.CopyManaged(source.Columns[sc], row, table.Columns[c], slot);
                if (info.IsBuffer) info.Clear(source.Columns[sc], row, 1); // the list moves: the next load takes another
            }
            else Archetype.CopyElement(info, source.Columns[sc], row, table.Columns[c], slot);

            if (present)
            {
                table.MarkPresent(c, slot);
                if (table.HasDisabled) Undisable(table, c, slot);
            }
            else table.MarkAbsent(c, slot, keep: true); // disabled: its tag is a column of the source too

            if (table.Tracked && info.Companion is var ticks and >= 0) // unmarked: no tick, the value is the record's
                table.SetTick(table.ColumnIndex(ticks), slot, markWritten ? _changeTick : 0);
            if (present && IndexOf(bit) is { } index) index.AddedRows(table, c, slot, 1);
        }

        if (_groupByBit != null) RegroupRow(id);
    }

    /// <summary>
    /// Whether column <paramref name="column"/> of loaded row <paramref name="row"/> is put on the entity: the row has
    /// the component (or keeps it disabled), and a <see cref="ChildOf"/> names a parent that came along.
    /// </summary>
    private static bool Loads(Archetype source, int column, int row)
    {
        if (!source.Has(column, row) && !(source.HasDisabled && source.Keeps(column, row))) return false;
        return source.Components[column] != ChildOfBit || !Unsafe.Add(ref Archetype.Row0<ChildOf>(source.Columns[column]), row).Parent.IsNull;
    }

    /// <summary>
    /// Move <paramref name="entities"/> (stale ids, repeats and retired entities, kept alive for their cleanup components, are
    /// skipped) into <paramref name="target"/>, another world of this thread: the target gets new entities with their
    /// components, as <see cref="LoadEntities"/> makes them from a save of the group, without encoding it; then they are
    /// destroyed here (their children with them, so move a hierarchy together). Cleanup components
    /// (<see cref="ICleanupComponent"/>) do not move: they stay behind on the entity, now retired, for the systems of this
    /// world to release what they hold, and the new entity does not have them. The new handles are written in order
    /// into <paramref name="moved"/> (as many as fit, <see cref="Entity"/> default for a skipped id); returns how many
    /// moved. A list of ids goes as <c>CollectionsMarshal.AsSpan(list)</c>, no copy. O(entities × components). Owner thread of both worlds; not while a pass iterates in either. Throws
    /// <see cref="InvalidOperationException"/>, moving nothing, when a moving entity has a unique key
    /// (<see cref="IUniqueIndexed{TKey}"/>) an entity of <paramref name="target"/> has.
    /// </summary>
    public int MoveEntities(World target, ReadOnlySpan<EntityId> entities, Span<Entity> moved = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (ReferenceEquals(target, this)) throw new ArgumentException("The entities are in this world already", nameof(target));
        int count = 0;
        PrepareCascade();
        using (Structural())
        {
            ThrowIfIterating(nameof(MoveEntities));
            FillDirtyTables();
            using (target.Structural())
            {
                target.ThrowIfIterating(nameof(MoveEntities));
                if (ComponentInfo.AnyUnique) // the moving entities' unique keys, before the first row moves
                    foreach (var id in entities)
                        if (Moves(id, out var table, out int slot))
                            target.ClaimRows(table, slot, 1, 1, cleanup: false);
                target.BeginArrival(this);
                Archetype run = null;
                int first = 0, length = 0;
                foreach (var id in entities)
                {
                    if (!Moves(id, out var table, out int slot) || target.HasArrived(id)) continue;
                    if (table != run || slot != first + length) // consecutive rows of one table go as one run
                    {
                        if (length != 0) target.ArriveRows(run, first, length, moving: true);
                        (run, first, length) = (table, slot, 0);
                    }

                    target.Stamp(id); // a repeat later in the list is skipped
                    length++;
                    count++;
                }

                if (length != 0) target.ArriveRows(run, first, length, moving: true);
                target.EndArrival(this);
            }

            for (int i = 0; i < Math.Min(entities.Length, moved.Length); i++)
                moved[i] = IsAlive(entities[i]) && target.HasArrived(entities[i]) ? target.Arrived(entities[i]) : default;
            // Then the originals go, in one pass through the gate and last first: a run's rows then leave from the end
            // of their table (no row fills a hole), and children listed after their parent go before it (no cascade).
            for (int i = entities.Length - 1; i >= 0; i--)
            {
                var id = entities[i];
                if (Moves(id, out _, out _)) Destroy(ref MetaRef(id.Index), id);
            }
            if (_targetCount != 0) FollowLinks();
        }

        target.RootOrphans();
        return count;
    }

    /// <summary>
    /// Copy <paramref name="entities"/> (stale ids, repeats and retired entities are skipped) into
    /// <paramref name="target"/>, another world of this thread, as new entities holding only the components of
    /// <paramref name="components"/> they have (a <see cref="Disabled{T}"/> tag goes with its component), plus
    /// <typeparamref name="TTag"/> at its default value: ghost rows of a neighbor's border, a read replica, an area of
    /// interest copied to a client's world. The originals stay as they are. Rows that follow each other in a table are
    /// copied as one run, a block copy per column. <see cref="EntityId"/> fields of the copies (components and buffer
    /// elements) that named a copied entity name its copy, every other one is <see cref="EntityId.Null"/>, and a
    /// <see cref="ChildOf"/> whose parent was not copied is removed, as <see cref="LoadEntities"/> and
    /// <see cref="MoveEntities"/> do. Cleanup components (<see cref="ICleanupComponent"/>) are never copied: the
    /// original releases what they hold. Indexes and groups learn the copies, and their components count as written,
    /// as with <c>CreateEntity</c>. The new handles are written in order into <paramref name="copied"/> (as many as fit,
    /// <see cref="Entity"/> default for a skipped id); returns how many were copied. A list of ids goes as
    /// <c>CollectionsMarshal.AsSpan(list)</c>, no copy; keep the set in a <c>static readonly</c> field. Allocates
    /// nothing once the target's tables exist and the buffers are large enough. O(entities × components of the set). Owner thread of both worlds; a pass
    /// may iterate either (the copies are new rows, which a pass that started earlier does not visit).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="target"/> is this world, <paramref name="components"/> holds a singleton type, or <typeparamref name="TTag"/> is a unique component.</exception>
    /// <exception cref="InvalidOperationException">A copied entity has a unique key (<see cref="IUniqueIndexed{TKey}"/>) an entity of <paramref name="target"/> has: nothing is copied.</exception>
    public int CopyEntities<TTag>(World target, ReadOnlySpan<EntityId> entities, in ComponentSet components, Span<Entity> copied = default)
        where TTag : struct, IComponent =>
        CopyEntities(target, entities, in components, ComponentType<TTag>.Bit, copied);

    private int CopyEntities(World target, ReadOnlySpan<EntityId> entities, in ComponentSet components, ushort tag, Span<Entity> copied)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (ReferenceEquals(target, this)) throw new ArgumentException("A copy goes to another world: use Clone in this one", nameof(target));
        if (components.HasSingletons)
            throw new ArgumentException("A copy of entities carries no singletons: its set names components only", nameof(components));
        if (ComponentInfo.Of(tag).Unique) throw new ArgumentException("The tag of a copy cannot be a unique component: every copy holds its default value");
        int count = 0;
        using (Structural())
        {
            using (target.Structural())
            {
                int shape = target.CopyShape(in components, tag);
                if (target._copyShapes[shape].Unique) // the copies' unique keys, before the first row is copied
                    foreach (var id in entities)
                        if (Moves(id, out var table, out int slot) && table.Unique)
                            target.ClaimCopied(table, slot, in components);
                target.BeginArrival(this);
                // Ids that follow each other in one table go as one gather (a run when their rows follow each other
                // too): the target's rows of a gather are one run, written column by column.
                if (_gatherRows.Length < entities.Length) _gatherRows = new int[Math.Max(entities.Length, _gatherRows.Length * 2)];
                var rows = _gatherRows;
                // Each id is mapped to its copy when something reads the map: the caller's handles, or a component of
                // the set whose references are rewritten. Without a map, ids in rising order cannot repeat, so they
                // need no stamp until one comes out of order (a query's ids mostly rise).
                bool map = !copied.IsEmpty || target._copyShapes[shape].Remaps;
                bool rising = !map;
                int last = -1;
                Archetype run = null;
                int length = 0;
                bool contiguous = true;
                for (int i = 0; i < entities.Length; i++)
                {
                    var id = entities[i];
                    if (!Moves(id, out var table, out int slot)) continue;
                    if (rising && id.Index > last) last = id.Index;
                    else
                    {
                        if (rising) // the first id out of order: the ones before it are stamped now
                        {
                            rising = false;
                            foreach (var before in entities[..i])
                                if (Moves(before, out _, out _)) target.Stamp(before);
                        }

                        if (target.HasArrived(id)) continue; // a repeat
                        target.Stamp(id);
                    }

                    if (table != run)
                    {
                        if (length != 0) target.ArriveCopies(run, rows.AsSpan(0, length), contiguous, shape, map);
                        (run, length, contiguous) = (table, 0, true);
                    }
                    else contiguous &= slot == rows[length - 1] + 1;

                    rows[length++] = slot;
                    count++;
                }

                if (length != 0) target.ArriveCopies(run, rows.AsSpan(0, length), contiguous, shape, map);
                target.EndArrival(this);
            }

            for (int i = 0; i < Math.Min(entities.Length, copied.Length); i++)
                copied[i] = IsAlive(entities[i]) && target.HasArrived(entities[i]) ? target.Arrived(entities[i]) : default;
        }

        target.RootOrphans();
        return count;
    }

    // The rows of the source table a gather of CopyEntities copies (reused: grows to the longest list of ids).
    private int[] _gatherRows = [];

    // Copies of the source rows of table: one run when the rows follow each other, else one gather.
    private void ArriveCopies(Archetype table, ReadOnlySpan<int> rows, bool contiguous, int shape, bool map)
    {
        if (contiguous) ArriveRows(table, rows[0], rows.Length, shape: shape, map: map);
        else ArriveRows(table, 0, rows.Length, shape: shape, rows: rows, map: map);
    }

    // The (component set, tag) pairs copies into this world were made with, whether the set holds a unique component
    // and whether it holds one whose references are rewritten (ComponentInfo.Remaps): a pair's index names its tables
    // in _prefabTables (as -2 - index), found once per source table. Grows by the distinct sets a program uses.
    private List<(BitMask Components, ushort Tag, bool Unique, bool Remaps)> _copyShapes;

    /// <summary>The index of (<paramref name="components"/>, <paramref name="tag"/>) in <see cref="_copyShapes"/>, added when new. Inside the structural gate.</summary>
    private int CopyShape(in ComponentSet components, ushort tag)
    {
        var shapes = _copyShapes ??= [];
        var bits = components.Components;
        for (int i = 0; i < shapes.Count; i++)
            if (shapes[i].Tag == tag && shapes[i].Components.Equals(in bits))
                return i;
        bool unique = false, remaps = false;
        foreach (ushort bit in bits.ToBitArray()) // cold: once per shape
        {
            unique |= ComponentInfo.Of(bit).Unique;
            remaps |= ComponentInfo.Of(bit).Remaps;
        }

        shapes.Add((bits, tag, unique, remaps));
        return shapes.Count - 1;
    }

    /// <summary>
    /// This world's table for copies of rows of <paramref name="source"/> (a table of another world) made with shape
    /// <paramref name="shape"/>: the source's components in the set (a <see cref="Disabled{T}"/> tag with its
    /// component, no cleanup component) and the tag, cached per both. Inside the structural gate.
    /// </summary>
    private Archetype CopyTableFor(Archetype source, int shape)
    {
        var key = new PrefabKey(source, -2 - shape);
        if (_prefabTables != null && _prefabTables.TryGetValue(key, out var target)) return target;
        return NewCopyTable(source, shape, key);
    }

    [MethodImpl(MethodImplOptions.NoInlining)] // once per source table and shape
    private Archetype NewCopyTable(Archetype source, int shape, PrefabKey key)
    {
        var (set, tag, _, _) = _copyShapes[shape];
        var mask = default(BitMask);
        foreach (ushort bit in source.Components)
        {
            var info = ComponentInfo.Of(bit);
            if (info.Owner >= 0) continue; // a change-tick column: the table gets those of its tracked components
            int component = info.DisabledOf >= 0 ? info.DisabledOf : bit;
            if (set.HasBit(component) && !ComponentInfo.Of(component).Cleanup) mask.SetBit(bit);
        }

        mask.SetBit(tag);
        return (_prefabTables ??= new())[key] = GetOrCreateArchetype(in mask, source);
    }

    /// <summary>
    /// Row <paramref name="slot"/> of <paramref name="table"/> (another world's) is about to be copied with the
    /// components of <paramref name="components"/>: claim the keys of its unique ones in this world. Inside the
    /// structural gate.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)] // tables with unique components only
    private void ClaimCopied(Archetype table, int slot, in ComponentSet components)
    {
        var bits = table.Components;
        for (int c = 0; c < bits.Length; c++)
            if (ComponentInfo.Of(bits[c]) is { Unique: true, Cleanup: false } && components.Contains(bits[c]))
                UniqueIndex(bits[c]).ClaimRows(table, c, slot, 1, 1, -1);
    }

    /// <summary>
    /// Whether <paramref name="id"/> names an entity <see cref="MoveEntities"/> moves (alive, not retired), and its
    /// table and row. Inside the structural gate.
    /// </summary>
    private bool Moves(EntityId id, out Archetype table, out int slot)
    {
        table = null;
        slot = 0;
        if ((uint)id.Index >= (uint)Meta.Length) return false;
        ref var meta = ref MetaRef(id.Index);
        if (meta.Archetype == null || meta.Version != id.Version) return false;
        (table, slot) = (meta.Archetype, meta.Slot);
        return !table.HasCleanup || !IsRetiredRow(table, slot);
    }

    /// <summary>
    /// Copy every entity of <paramref name="source"/>, a world without tombstones, into this one; returns how many. The
    /// copies are new rows, as <c>CreateEntities</c> makes them: a pass may be iterating this world (a prefab with members
    /// instantiated by a system). With <paramref name="take"/> the buffer lists of <paramref name="source"/> move here
    /// (a load's private world) instead of being copied (a prefab's, which stays as it is).
    /// </summary>
    internal int ArriveAll(World source, bool take = false)
    {
        int created = 0;
        using (Structural())
        {
            ClaimGroup(source, 1);
            BeginArrival(source);
            foreach (var table in source._allArchetypes)
            {
                if (table.Count == 0) continue;
                ArriveRows(table, 0, table.Count, take: take);
                created += table.Count;
            }

            EndArrival(source);
        }

        RootOrphans();
        return created;
    }

    /// <summary>
    /// Every entity of <paramref name="source"/> (a prefab's store, a loaded save) is about to arrive
    /// <paramref name="copies"/> times: claim their unique keys before any row is copied (<see cref="ClaimRows"/>).
    /// Inside the structural gate.
    /// </summary>
    private void ClaimGroup(World source, int copies)
    {
        if (!ComponentInfo.AnyUnique) return;
        foreach (var table in source._allArchetypes) ClaimRows(table, 0, table.Count, copies);
    }

    /// <summary>The entity <paramref name="id"/> of the last arrival's source became (<see cref="HasArrived"/>).</summary>
    internal Entity Arrived(EntityId id)
    {
        var to = _arrived[id.Index];
        return new Entity(this, to.Index, to.Version);
    }

    private bool HasArrived(EntityId id) =>
        (uint)id.Index < (uint)_arrivedStamp.Length && _arrivedStamp[id.Index] == _arrival;

    // Begin an arrival from source: a new stamp, room for its ids. Inside the structural gate.
    private void BeginArrival(World source)
    {
        _arrivedRows.Clear();
        _roots.Clear();
        if (++_arrival == 0) // the stamps wrapped: forget them all once
        {
            Array.Clear(_arrivedStamp);
            _arrival = 1;
        }

        if (_arrived.Length < source._nextId)
        {
            // As long as the source's storage: a staging world sized like this one is reached once, not step by step.
            int length = Math.Max(Math.Max(source._nextId, _arrived.Length * 2), source.Meta.Length);
            Array.Resize(ref _arrived, length);
            Array.Resize(ref _arrivedStamp, length);
        }
    }

    // Mark source entity id as part of this arrival (its new id is written when its row is copied).
    private void Stamp(EntityId id) => _arrivedStamp[id.Index] = _arrival;

    // Rows [first, +count) of a source table, as one run of new rows; moving ones (MoveEntities) leave their cleanup
    // components behind; taken ones (a load's private world) give up their buffer lists (Archetype.CopyRun); copies
    // (CopyEntities, shape >= 0) take the components of their shape and its tag. With rows, the count rows it lists
    // instead (a gather, first unused). Without map, the source ids are not mapped to the new ones (nothing reads
    // the map: no column rewrites references and the caller reads no handle).
    // Called once per run, not per row: inlined (with CopyRun) into both call sites of MoveEntities, it made that
    // method's Tier1 code 3.3x larger and the move 1.2x slower (MigrationBenchmark Move).
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ArriveRows(Archetype table, int first, int count, bool moving = false, bool take = false, int shape = -1,
        ReadOnlySpan<int> rows = default, bool map = true)
    {
        var target = shape < 0 ? TableFor(table) : CopyTableFor(table, shape);
        target.Reserve(target.Count + count);
        ReserveIds(count);
        int to = target.Count;
        var slots = table.Slots;
        for (int r = 0; r < count; r++)
        {
            int id = AllocateId(); // no growth: the ids and the rows were reserved
            ref var meta = ref MetaRef(id);
            meta.Slot = target.Add(id, meta.Version);
            meta.Archetype = target;
            if (!map) continue;
            int source = slots[rows.IsEmpty ? first + r : rows[r]].Index;
            _arrived[source] = new EntityId(id, meta.Version);
            _arrivedStamp[source] = _arrival;
        }

        StorageVersion++; // once for the run (Place bumps it per row)

        target.CopyRun(table, first, to, count, _changeTick, take, rows);
        if (moving && target.HasCleanup) target.DropCleanup(to, count);
        _arrivedRows.Add((target, to, count));
    }

    // Every id is known now: point the copies' references at them, note children whose parent did not come along
    // (roots here, RootOrphans), then let indexes and groups see the values: every run is indexed before a move
    // changes rows, and runs move last first, so rows of one table that later runs filled are visited before they fill
    // a hole. Inside the structural gate.
    private void EndArrival(World source)
    {
        var map = new IdMap(source, _arrived, _arrivedStamp, _arrival);
        foreach (var (target, first, count) in _arrivedRows)
        {
            for (int c = 0; c < target.Columns.Length; c++) target.Info(c).Remap(target.Columns[c], first, count, map);
            int column = target.ColumnIndex(ChildOfBit);
            if (column < 0) continue;
            ref var parents = ref Archetype.Row0<ChildOf>(target.Columns[column]);
            for (int r = first; r < first + count; r++)
                if (target.Has(column, r) && Unsafe.Add(ref parents, r).Parent.IsNull)
                    _roots.Add(new Entity(this, target.Slots[r].Index, target.Slots[r].Version));
        }

        foreach (var (target, first, count) in _arrivedRows) IndexRows(target, first, count);
        for (int i = _arrivedRows.Count - 1; i >= 0; i--)
            RegroupRows(_arrivedRows[i].Table, _arrivedRows[i].First, _arrivedRows[i].Count);
    }

    private void RootOrphans()
    {
        foreach (var root in _roots) root.Remove<ChildOf>();
        _roots.Clear();
    }
}

/// <summary>
/// The new id of each entity of an arrival's source world: an id of a living source entity that came along maps to
/// the entity it became, any other id to <see cref="EntityId.Null"/>.
/// </summary>
internal readonly struct IdMap(World source, EntityId[] arrived, int[] stamps, int stamp)
{
    public EntityId this[EntityId id] =>
        (uint)id.Index < (uint)stamps.Length && stamps[id.Index] == stamp && source.IsAlive(id) ? arrived[id.Index] : EntityId.Null;

    /// <summary>Rewrite the <see cref="EntityId"/> fields of <paramref name="values"/> (see <see cref="TypeRegistry.EntityIds{T}"/>).</summary>
    public void Apply<T>(Span<T> values)
    {
        var offsets = TypeRegistry.EntityIdOffsets<T>.Value;
        if (offsets.Length == 0) return;
        foreach (ref var value in values)
        {
            ref byte start = ref Unsafe.As<T, byte>(ref value);
            foreach (int offset in offsets)
            {
                ref var id = ref Unsafe.As<byte, EntityId>(ref Unsafe.Add(ref start, offset));
                id = this[id];
            }
        }
    }
}
