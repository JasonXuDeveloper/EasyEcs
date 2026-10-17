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

    /// <summary>
    /// A change a bulk walk makes (<see cref="WalkDown"/>), to one row or, where a table allows it, to the rows of one
    /// presence word at once; a struct, so the walk is specialized for it.
    /// </summary>
    private interface IRowOp
    {
        /// <summary>Whether <see cref="ApplyWord"/> may change the rows of <paramref name="table"/> (no row moves).</summary>
        bool Words(World world, Archetype table);

        void ApplyWord(World world, Archetype table, int word, ulong rows);

        void Apply(World world, Archetype table, int slot, EntityId entity);
    }

    /// <summary>
    /// Apply <paramref name="op"/> to every alive row of <paramref name="match"/> passing its filter. A table without
    /// tombstones whose rows the op changes in place goes a presence word (64 rows) at a time; the others row by row,
    /// from the last row down: a row that moves away is filled by the table's last row, already visited, so the passing
    /// rows of each word (read once per word) stay valid for the rows below it. Inside the structural gate.
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
            if (count == table.AliveCount && op.Words(this, table))
            {
                for (int w = 0; w <= (count - 1) >> 6; w++)
                    if ((rows.Word(table, w) & RowFilter.RowMask(w, count)) is var passing and not 0) op.ApplyWord(this, table, w, passing);
                continue;
            }

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

        StorageVersion++; // passes re-read presence bits
    }

    /// <summary>Whether this world has a value index or relation index of component <paramref name="bit"/>.</summary>
    private bool IndexedBit(int bit) => _indexByBit is { } byBit && (uint)bit < (uint)byBit.Length && byBit[bit] != null;

    /// <summary>The tick column of the tracked component of <paramref name="column"/> in <paramref name="table"/>, or -1.</summary>
    private static int TicksOf(Archetype table, int column) =>
        table.Tracked && table.Info(column).Companion is var ticks and >= 0 ? table.ColumnIndex(ticks) : -1;

    private struct SetOp<T> : IRowOp where T : struct, IComponent
    {
        public T Value;
        public ushort Bit;

        // In place: the table has the column, no group moves the rows, no Disabled<T> tag goes with a new value.
        public readonly bool Words(World world, Archetype table) =>
            table.ColumnIndex(Bit) is var column and >= 0 && (!ComponentType<T>.Grouped || world.GroupingOf(Bit) == null) &&
            (!table.HasDisabled || table.DisablerColumn(column) < 0);

        public void ApplyWord(World world, Archetype table, int word, ulong rows)
        {
            table.MarkPresentWord(table.ColumnIndex(Bit), word, rows);
            ref T row0 = ref table.Ref<T>(Bit, 0);
            for (ulong r = rows; r != 0; r &= r - 1)
            {
                int slot = (word << 6) + BitOperations.TrailingZeroCount(r);
                Unsafe.Add(ref row0, slot) = Value;
                if (default(T) is ITrackChanges) table.Stamp<T>(slot, world.ChangeTick);
                if (ComponentType<T>.Watched) world.Written(table.Slots[slot].Index, table.Slots[slot].Version, in Value);
            }
        }

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

        // A retiring entity may be freed by losing a cleanup component: row by row.
        public readonly bool Words(World world, Archetype table) => !table.HasCleanup;

        public void ApplyWord(World world, Archetype table, int word, ulong rows)
        {
            int column = table.ColumnIndex(Bit);
            rows = table.MarkAbsentWord(column, word, rows);
            Count += BitOperations.PopCount(rows);
            world.GoneWord(table, column, word, rows, Bit);
        }

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

        // In place once the table has the tag's column (after the first disable, which moves the rows).
        public readonly bool Words(World world, Archetype table) => table.ColumnIndex(Tag) >= 0;

        public void ApplyWord(World world, Archetype table, int word, ulong rows)
        {
            int column = table.ColumnIndex(Bit);
            rows = table.MarkAbsentWord(column, word, rows, keep: true);
            table.MarkPresentWord(table.ColumnIndex(Tag), word, rows);
            Count += BitOperations.PopCount(rows);
            world.GoneWord(table, column, word, rows, Bit);
        }

        public void Apply(World world, Archetype table, int slot, EntityId entity)
        {
            if (world.DisableBit(ref world.MetaRef(entity.Index), entity.Index, Bit, Tag)) Count++;
        }
    }

    private struct EnableOp<T> : IRowOp where T : struct, IComponent
    {
        public ushort Bit, Tag;
        public int Count;

        public readonly bool Words(World world, Archetype table) => !ComponentType<T>.Grouped || world.GroupingOf(Bit) == null;

        public void ApplyWord(World world, Archetype table, int word, ulong rows)
        {
            rows = table.MarkAbsentWord(table.ColumnIndex(Tag), word, rows);
            table.MarkPresentWord(table.ColumnIndex(Bit), word, rows);
            Count += BitOperations.PopCount(rows);
            if (default(T) is not ITrackChanges && !ComponentType<T>.Watched) return;
            for (ulong r = rows; r != 0; r &= r - 1)
            {
                int slot = (word << 6) + BitOperations.TrailingZeroCount(r);
                if (default(T) is ITrackChanges) table.Stamp<T>(slot, world.ChangeTick);
                if (ComponentType<T>.Watched) world.Written(table.Slots[slot].Index, table.Slots[slot].Version, in table.Ref<T>(Bit, slot));
            }
        }

        public void Apply(World world, Archetype table, int slot, EntityId entity)
        {
            ref var meta = ref world.MetaRef(entity.Index);
            if (!world.EnableBit(ref meta, Bit, Tag)) return;
            meta.Archetype.Stamp<T>(meta.Slot, world.ChangeTick);
            world.Written(entity.Index, entity.Version, in Row<T>(ref meta, Bit));
            Count++;
        }
    }

    /// <summary><see cref="Gone"/> for the rows of presence word <paramref name="word"/> set in <paramref name="rows"/>.</summary>
    private void GoneWord(Archetype table, int column, int word, ulong rows, ushort bit)
    {
        int ticks = TicksOf(table, column);
        if (ticks < 0 && !IndexedBit(bit)) return;
        for (ulong r = rows; r != 0; r &= r - 1) Gone(table, column, (word << 6) + BitOperations.TrailingZeroCount(r), bit);
    }
}
