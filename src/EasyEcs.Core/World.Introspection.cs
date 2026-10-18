using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace EasyEcs;

/// <summary>A component of an entity, as <see cref="Entity.Describe"/> reads it: a boxed copy of its value.</summary>
/// <param name="Value">The value, boxed.</param>
/// <param name="Enabled">False for a disabled component (<see cref="Entity.Disable{T}"/>), whose value is kept.</param>
[System.Diagnostics.DebuggerDisplay("{Value}{Enabled ? \"\" : \" (disabled)\",nq}")]
public readonly record struct ComponentValue(object Value, bool Enabled);

/// <summary>A column of a table (<see cref="TableInfo"/>).</summary>
/// <param name="Type">The component type.</param>
/// <param name="Without">Living rows of the table without the component (removed or disabled: no row moved).</param>
/// <param name="Group">The group every row of the table is in, for an <see cref="IGroup"/> component; else 0.</param>
public readonly record struct ColumnInfo(Type Type, int Without, int Group);

/// <summary>A table: the entities with one set of component columns (<see cref="World.Describe()"/>).</summary>
/// <param name="Columns">The component columns, in bit order (change-tick and <see cref="Disabled{T}"/> columns not listed).</param>
/// <param name="Entities">Living entities.</param>
/// <param name="Rows">Rows in use: the entities and the holes a query in progress left (closed when it ends).</param>
/// <param name="Capacity">Rows allocated.</param>
/// <param name="Bytes">Memory of the rows allocated: columns, change ticks, presence bits and entity slots (not the
/// objects managed components reference).</param>
public readonly record struct TableInfo(ColumnInfo[] Columns, int Entities, int Rows, int Capacity, long Bytes);

public partial class World
{
    /// <summary>
    /// The world's tables, for diagnostics (an admin endpoint, a dashboard): what each holds, how full it is and what it
    /// costs. Cold: allocates the result; any thread (a table being changed at the same time may be read half-way).
    /// </summary>
    public TableInfo[] Describe()
    {
        var all = Volatile.Read(ref _allArchetypes);
        var tables = new TableInfo[all.Length];
        for (int i = 0; i < all.Length; i++) tables[i] = Describe(all[i]);
        return tables;
    }

    private static TableInfo Describe(Archetype table)
    {
        int capacity = table.Slots.Length;
        long bytes = (long)capacity * Unsafe.SizeOf<EntityId>() + table.Components.Length * (capacity + 63) / 64 * sizeof(ulong);
        var columns = new List<ColumnInfo>(table.Components.Length);
        for (int c = 0; c < table.Components.Length; c++)
        {
            var info = table.Info(c);
            bytes += (long)capacity * info.Stride;
            if (info.Owner >= 0 || info.DisabledOf >= 0) continue; // a change-tick column, a Disabled<T> tag
            columns.Add(new ColumnInfo(info.Type, table.Absent[c], table.Groups?[c] ?? 0));
        }

        return new TableInfo(columns.ToArray(), table.AliveCount, table.Count, capacity, bytes);
    }

    /// <summary>The components of <paramref name="entity"/>, boxed (disabled ones marked); none when it is stale.</summary>
    internal static ComponentValue[] Describe(Entity entity)
    {
        if (!entity.IsAlive) return [];
        var (table, slot) = Source(entity);
        var values = new List<ComponentValue>(table.Components.Length);
        for (int c = 0; c < table.Components.Length; c++)
        {
            var info = table.Info(c);
            if (info.Owner >= 0 || info.DisabledOf >= 0) continue; // a change-tick column, a Disabled<T> tag
            if (table.Has(c, slot)) values.Add(new ComponentValue(info.Box(table.Columns[c], slot), true));
            else if (table.Keeps(c, slot)) values.Add(new ComponentValue(info.Box(table.Columns[c], slot), false));
        }

        return values.ToArray();
    }
}
