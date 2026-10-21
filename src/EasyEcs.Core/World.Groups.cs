using System;
using System.Runtime.CompilerServices;

namespace EasyEcs;

// Groups: tables split by the value of an IGroup component (World.GroupBy), and the moves that keep rows in their group.
public sealed partial class World
{
    // By component bit: the grouping of that component in this world. Null until the first GroupBy.
    private IGroupTables[] _groupByBit;

    internal interface IGroupTables
    {
        /// <summary>The group of the value in row <paramref name="slot"/> of <paramref name="table"/>, which has the component.</summary>
        int GroupOf(Archetype table, int slot);
    }

    private interface IGroupOf<T>
    {
        int GroupOf(in T value);
    }

    private sealed class GroupTables<T> : IGroupTables, IGroupOf<T> where T : struct, IComponent, IGroup
    {
        public int GroupOf(Archetype table, int slot) => table.Ref<T>(ComponentType<T>.Bit, slot).Group;

        public int GroupOf(in T value) => value.Group;
    }

    /// <summary>
    /// Split this world's tables by <typeparamref name="T"/>'s <see cref="IGroup.Group"/>: entities of one group share
    /// tables, so <see cref="QueryFilter.InGroup{T}"/> walks only that group's rows, contiguous in memory. Setting a
    /// new value moves the entity to its group's tables (one row copy). Existing entities move now: call it at setup.
    /// O(entities with <typeparamref name="T"/>) once; idempotent. Owner thread only, not during a pass.
    /// </summary>
    public void GroupBy<T>() where T : struct, IComponent, IGroup
    {
        int bit = ComponentType<T>.Bit;
        using (Structural())
        {
            ThrowIfIterating(nameof(GroupBy)); // the existing rows move
            _groupByBit ??= [];
            if (bit >= _groupByBit.Length) Array.Resize(ref _groupByBit, bit + 1);
            if (_groupByBit[bit] != null) return;
            _groupByBit[bit] = new GroupTables<T>();
            RegroupAll(bit);
        }
    }

    /// <summary>Move every entity with grouped component <paramref name="bit"/> to its value's group. Inside the structural gate.</summary>
    private void RegroupAll(int bit)
    {
        var tables = _allArchetypes; // a snapshot: tables created by the moves start in their group
        foreach (var table in tables)
        {
            int column = table.ColumnIndex(bit);
            if (column < 0) continue;
            for (int slot = table.Count - 1; slot >= 0; slot--) // from the last row: a moved row is filled by a visited one
            {
                int id = table.Slots[slot].Index;
                if (id >= 0 && table.Has(column, slot)) RegroupRow(id);
            }
        }
    }

    /// <summary>Entity <paramref name="id"/> holds <paramref name="value"/> of grouped component <paramref name="bit"/>: move it to the value's group if this world groups by it. Inside the structural gate.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Regroup<T>(int id, int bit, in T value) where T : struct, IComponent
    {
        if (GroupTablesOf(bit) is not { } grouping) return;
        int group = Unsafe.As<IGroupOf<T>>(grouping).GroupOf(in value);
        ref var meta = ref MetaRef(id);
        if (meta.Archetype.GroupOf(bit) != group) MoveTo(ref meta, id, RegroupEdge(meta.Archetype, bit, group));
    }

    /// <summary>Move entity <paramref name="id"/> to the groups of its grouped components' values (copied rows). Inside the structural gate.</summary>
    private void RegroupRow(int id)
    {
        ref var meta = ref MetaRef(id);
        var table = meta.Archetype;
        var components = table.Components;
        for (int c = 0; c < components.Length; c++)
        {
            ushort bit = components[c];
            if (GroupTablesOf(bit) is not { } grouping || !table.Has(c, meta.Slot)) continue;
            int group = grouping.GroupOf(table, meta.Slot);
            if (table.GroupOf(bit) == group) continue;
            MoveTo(ref meta, id, RegroupEdge(table, bit, group));
            table = meta.Archetype;
        }
    }

    /// <summary>
    /// Move copied rows [<paramref name="first"/>, +<paramref name="count"/>) of <paramref name="table"/> (Instantiate,
    /// Clone, migrations) to the groups of their values. From the last row: a move fills its row with the table's last
    /// one, which was visited. Their indexes first (<see cref="IndexRows"/>): a move changes rows. Inside the
    /// structural gate.
    /// </summary>
    private void RegroupRows(Archetype table, int first, int count)
    {
        if (_groupByBit == null) return;
        for (int r = first + count - 1; r >= first; r--) RegroupRow(table.Slots[r].Index);
    }

    /// <summary>
    /// The table a new entity with <paramref name="value"/> of grouped component <paramref name="bit"/> belongs in,
    /// starting from <paramref name="target"/> (CreateEntity / CreateEntities place it there, no move). Folded away for
    /// other components. Inside the structural gate.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Archetype GroupTarget<T>(Archetype target, ushort bit, in T value) where T : struct, IComponent
    {
        if (!ComponentType<T>.Grouped || GroupTablesOf(bit) is not { } grouping) return target;
        int group = Unsafe.As<IGroupOf<T>>(grouping).GroupOf(in value);
        return target.GroupOf(bit) == group ? target : RegroupEdge(target, bit, group).Target;
    }

    /// <summary>How this world groups component <paramref name="bit"/>, or null when it does not.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private IGroupTables GroupTablesOf(int bit) => _groupByBit is { } byBit && (uint)bit < (uint)byBit.Length ? byBit[bit] : null;

    private Transition RegroupEdge(Archetype from, int bit, int group)
    {
        int column = from.ColumnIndex(bit);
        var edge = from.GetRegroupEdge(column, group);
        if (edge != null) return edge;
        edge = new Transition(from, GetOrCreateArchetype(from.ComponentMask.Clone(), from, bit, group));
        from.SetRegroupEdge(column, group, edge);
        return edge;
    }
}
