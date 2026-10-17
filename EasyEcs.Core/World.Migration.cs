using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EasyEcs;

// Migrations: a group of entities written by SaveEntities becomes new entities of this world. The bytes load into a
// world of their own first (the same Load, so ids, versions and every component come back as saved); each of its
// tables is then copied row by row into this world, like an instantiated prefab. EntityId fields that pointed into the
// group point at the new entities, the others become EntityId.Null.
public sealed partial class World
{
    // The world the bytes load into, reused by every migration, and the new id of each of its ids.
    private World _arrivals;
    private EntityId[] _arrived = [];
    private List<(Archetype Table, int First, int Count)> _arrivedRows;
    private List<Entity> _roots;

    /// <summary>
    /// Create the entities <see cref="SaveEntities"/> wrote (in this process or another), with new ids, and write their
    /// handles in save order into <paramref name="entities"/> (as many as fit); returns how many were created.
    /// <see cref="EntityId"/> fields of their components and buffer elements that named an entity of the group name
    /// its new entity, every other one is <see cref="EntityId.Null"/> (the generator tells the world where those fields
    /// are; ids inside collections are not rewritten). A <see cref="ChildOf"/> whose parent stayed behind is removed:
    /// the entity is a root here. Indexes and groups learn the entities; tracked components count as written.
    /// O(entities × components). Not while a pass iterates.
    /// </summary>
    /// <exception cref="System.IO.InvalidDataException">The bytes are not a save.</exception>
    public int LoadEntities(ReadOnlySpan<byte> input, Span<Entity> entities = default)
    {
        var source = _arrivals ??= new World(new WorldOptions { InitialEntityCapacity = 16 });
        source.Load(input);
        var rows = _arrivedRows ??= [];
        rows.Clear();
        int created = 0;
        using (Structural())
        {
            ThrowIfIterating();
            if (_arrived.Length < source._nextId) _arrived = new EntityId[source._nextId];
            foreach (var table in source._allArchetypes)
            {
                int count = table.Count; // a loaded world is dense
                if (count == 0) continue;
                var target = TableFor(table);
                var handles = Handles(count);
                int first = PlaceRows(target, handles);
                target.CloneRows(table, 0, first, count, _changeTick);
                for (int r = 0; r < count; r++) _arrived[table.Slots[r].Index] = handles[r];
                rows.Add((target, first, count));
                created += count;
            }

            // Every id is known now: point the copies' references at them, then let indexes and groups see the values.
            var map = new IdMap(source, _arrived);
            foreach (var (target, first, count) in rows)
            {
                for (int c = 0; c < target.Columns.Length; c++) target.Info(c).Remap(target.Columns[c], first, count, map);
                for (int r = first; r < first + count; r++)
                {
                    var id = target.Slots[r];
                    if (_indexByBit != null) IndexRow(target, r, id.Index, id.Version);
                    if (_groupByBit != null) RegroupRow(id.Index);
                }
            }

            // Children whose parent did not come along are roots here.
            var roots = _roots ??= [];
            roots.Clear();
            foreach (var (target, first, count) in rows)
            {
                int column = target.ColumnIndex(ChildOfBit);
                if (column < 0) continue;
                ref var parents = ref Archetype.Row0<ChildOf>(target.Columns[column]);
                for (int r = first; r < first + count; r++)
                    if (Unsafe.Add(ref parents, r).Parent.IsNull) roots.Add(new Entity(this, target.Slots[r].Index, target.Slots[r].Version));
            }
        }

        foreach (var root in _roots) root.Remove<ChildOf>();
        int order = Math.Min(entities.Length, source._loadOrderCount);
        for (int i = 0; i < order; i++)
        {
            var id = _arrived[source._loadOrder[i].Index];
            entities[i] = new Entity(this, id.Index, id.Version);
        }

        return created;
    }

    // The handles PlaceRows writes for one table of a migration (reused).
    private Entity[] _arrivedHandles = [];

    private Span<Entity> Handles(int count)
    {
        if (_arrivedHandles.Length < count) _arrivedHandles = new Entity[Math.Max(count, _arrivedHandles.Length * 2)];
        return _arrivedHandles.AsSpan(0, count);
    }
}

/// <summary>
/// The new id of each entity of a migration's source world: an id of a living source entity maps to the entity it
/// became, any other id to <see cref="EntityId.Null"/>.
/// </summary>
internal readonly struct IdMap(World source, EntityId[] arrived)
{
    public EntityId this[EntityId id] => source.IsAlive(id) ? arrived[id.Index] : EntityId.Null;

    /// <summary>Rewrite the <see cref="EntityId"/> fields of <paramref name="values"/> (see <see cref="SavedTypes.EntityIds{T}"/>).</summary>
    public void Apply<T>(Span<T> values)
    {
        var offsets = SavedTypes.EntityIdOffsets<T>.Value;
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
