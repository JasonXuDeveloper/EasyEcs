using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace EasyEcs;

// Bulk changes: one component set, removed, disabled or enabled on every entity a filter selects, in one pass through
// the structural gate (a round reset, a status lifted from everyone, a zone frozen).
public sealed partial class World
{
    /// <summary>
    /// Give every alive entity matching <paramref name="filter"/> component <typeparamref name="T"/> =
    /// <paramref name="value"/> (added, or replaced), as <c>Set</c> would one by one: tracked components count as
    /// written, indexes and groups follow. Returns how many entities. O(matching tables + rows); an entity that lacks
    /// <typeparamref name="T"/> moves once. Owner thread only.
    /// </summary>
    public int SetAll<T>(in T value, in QueryFilter filter = default) where T : struct, IComponent
    {
        var match = Volatile.Read(ref GetQueryEntry([], in filter).Match);
        int count = match.CountAlive(filter.Tick); // a moved row can be visited again in its new table: count first
        var op = new SetOp<T> { Value = value, Bit = ComponentType<T>.Bit };
        using (Structural()) WalkDown(match, filter.Tick, ref op);
        return count;
    }

    /// <summary>
    /// Remove component <typeparamref name="T"/> from every alive entity having it and matching
    /// <paramref name="filter"/>, as <c>Remove</c> would one by one (rows stay, presence bits clear). Returns how many.
    /// O(matching tables + rows). Owner thread only.
    /// </summary>
    public int RemoveAll<T>(in QueryFilter filter = default) where T : struct, IComponent
    {
        var op = new RemoveOp { Bit = ComponentType<T>.Bit };
        using (Structural())
        {
            WalkDown(Bulk<T>(in filter), filter.Tick, ref op);
            if (op.Count != 0) StorageVersion++; // passes re-read presence bits
            if (_parentCount != 0) DestroyChildren(); // a retired entity freed by losing its last cleanup component
        }

        return op.Count;
    }

    /// <summary>
    /// <see cref="Entity.Disable{T}"/> on every alive entity having <typeparamref name="T"/> and matching
    /// <paramref name="filter"/>: each keeps its value and gets <see cref="Disabled{T}"/>. Returns how many.
    /// O(matching tables + rows); a table's rows move once, to the table with the tag. Owner thread only.
    /// </summary>
    public int DisableAll<T>(in QueryFilter filter = default) where T : struct, IComponent
    {
        if (default(T) is ICleanupComponent || typeof(T) == typeof(ChildOf)) Entity.ThrowNotDisabled<T>(); // JIT-time constants
        var op = new DisableOp { Bit = ComponentType<T>.Bit, Tag = DisabledType<T>.Bit };
        using (Structural()) WalkDown(Bulk<T>(in filter), filter.Tick, ref op);
        return op.Count;
    }

    /// <summary>
    /// <see cref="Entity.Enable{T}"/> on every alive entity with <typeparamref name="T"/> disabled and matching
    /// <paramref name="filter"/>: each gets its kept value back (a write of a tracked component; indexes follow).
    /// Returns how many. O(matching tables + rows). Owner thread only.
    /// </summary>
    public int EnableAll<T>(in QueryFilter filter = default) where T : struct, IComponent
    {
        var op = new EnableOp<T> { Bit = ComponentType<T>.Bit, Tag = DisabledType<T>.Bit };
        using (Structural()) WalkDown(Bulk<Disabled<T>>(in filter), filter.Tick, ref op);
        return op.Count;
    }

    private QueryMatch Bulk<T>(in QueryFilter filter) where T : struct, IComponent =>
        GetMatchingArchetypes(TypeIndex<ComponentSignature, T>.Value, [ComponentType<T>.Bit], in filter);

    /// <summary>A change a bulk walk makes to one row (<see cref="WalkDown"/>); a struct, so the walk is specialized for it.</summary>
    private interface IRowOp
    {
        void Apply(World world, Archetype table, int slot, EntityId entity);
    }

    /// <summary>
    /// Apply <paramref name="op"/> to every alive row of <paramref name="match"/> passing its filter, from the last row
    /// of each table down: a row that moves away is filled by the table's last row, already visited, so the passing rows
    /// of each word (read once per word) stay valid for the rows below it. Inside the structural gate.
    /// </summary>
    private void WalkDown<TOp>(QueryMatch match, ulong since, ref TOp op) where TOp : struct, IRowOp
    {
        var tables = match.Archetypes;
        for (int t = 0; t < tables.Length; t++)
        {
            var table = tables[t];
            var rows = new RowFilter(match.Filters[t], since);
            if (table.AliveCount == 0 || rows.Mode(table) == RowFilter.None) continue;
            int count = table.Count;
            for (int w = (count - 1) >> 6; w >= 0; w--)
            {
                ulong bits = rows.Word(table, w) & RowFilter.RowMask(w, count);
                for (; bits != 0; bits &= ~(1UL << 63 >> BitOperations.LeadingZeroCount(bits)))
                {
                    int slot = (w << 6) + 63 - BitOperations.LeadingZeroCount(bits);
                    if (slot >= table.Count) continue; // the rows above moved away (a regroup emptied the end)
                    var entity = table.Slots[slot];
                    if (entity.Index >= 0) op.Apply(this, table, slot, entity);
                }
            }
        }
    }

    private struct SetOp<T> : IRowOp where T : struct, IComponent
    {
        public T Value;
        public ushort Bit;

        public void Apply(World world, Archetype table, int slot, EntityId entity)
        {
            ref var meta = ref world.MetaRef(entity.Index);
            world.AddBit(ref meta, entity.Index, Bit);
            Row<T>(ref meta, Bit) = Value;
            meta.Archetype.Stamp<T>(meta.Slot, world.ChangeTick);
            world.Written(entity.Index, entity.Version, in Value);
        }
    }

    private struct RemoveOp : IRowOp
    {
        public ushort Bit;
        public int Count;

        public void Apply(World world, Archetype table, int slot, EntityId entity)
        {
            if (!world.TakeBit(table, slot, Bit)) return;
            Count++;
            if (table.HasCleanup) world.Released(ref world.MetaRef(entity.Index));
        }
    }

    private struct DisableOp : IRowOp
    {
        public ushort Bit, Tag;
        public int Count;

        public void Apply(World world, Archetype table, int slot, EntityId entity)
        {
            if (world.DisableBit(ref world.MetaRef(entity.Index), entity.Index, Bit, Tag)) Count++;
        }
    }

    private struct EnableOp<T> : IRowOp where T : struct, IComponent
    {
        public ushort Bit, Tag;
        public int Count;

        public void Apply(World world, Archetype table, int slot, EntityId entity)
        {
            ref var meta = ref world.MetaRef(entity.Index);
            if (!world.EnableBit(ref meta, Bit, Tag)) return;
            meta.Archetype.Stamp<T>(meta.Slot, world.ChangeTick);
            world.Written(entity.Index, entity.Version, in Row<T>(ref meta, Bit));
            Count++;
        }
    }
}
