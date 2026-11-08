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
/// <param name="Absent">Living rows of the table without the component (removed or disabled: no row moved).</param>
/// <param name="Group">The group every row of the table is in, for an <see cref="IGroup"/> component; else 0.</param>
public readonly record struct ColumnInfo(Type Type, int Absent, int Group);

/// <summary>A table: the entities with one set of component columns (<see cref="World.DescribeTables"/>).</summary>
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
    public TableInfo[] DescribeTables()
    {
        var all = Volatile.Read(ref _allArchetypes);
        var tables = new TableInfo[all.Length];
        for (int i = 0; i < all.Length; i++) tables[i] = DescribeTable(all[i]);
        return tables;
    }

    private static TableInfo DescribeTable(Archetype table)
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

    /// <summary>
    /// How the world runs its systems, for diagnostics (a startup log, a dashboard, a docs diagram): per group its stages,
    /// each system's reads and writes by name, why a system waits for the stage before its own (an ordering, or the
    /// data it conflicts on), and the conflicting pairs that only registration order orders, whose order flips when they
    /// are registered the other way round. Cold: allocates the result, O(systems³) per group; any thread (it reads the
    /// schedule published by the latest <see cref="AddSystem{T}"/> / <see cref="RemoveSystem{T}"/>).
    /// </summary>
    public ScheduleInfo DescribeSchedule()
    {
        var groups = new List<GroupInfo>();
        foreach (var group in _groups)
            if (group != null && group.Systems.Length > 0) groups.Add(DescribeGroup(group));
        return new ScheduleInfo(groups.ToArray());
    }

    private GroupInfo DescribeGroup(SystemSchedule group)
    {
        var stages = group.Stages;
        var order = new SystemCode.Entry[group.Systems.Length]; // the stages' systems, stage after stage
        for (int i = 0; i < order.Length; i++) order[i] = SystemRows[group.Systems[i].Index].Code;
        int n = order.Length;
        var names = new string[n];
        for (int i = 0; i < n; i++) names[i] = ScheduleInfo.NameOf(order[i].Type); // a type is added once per world

        // Orderings, transitively: reach[i, j] when a chain of [UpdateBefore] / [UpdateAfter] puts i before j.
        var reach = new bool[n, n];
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
            reach[i, j] = i != j && SystemSchedule.Precedes(order[i], order[j]);
        for (int k = 0; k < n; k++)
        for (int i = 0; i < n; i++)
            if (reach[i, k])
                for (int j = 0; j < n; j++)
                    reach[i, j] |= reach[k, j];

        var level = new int[n];
        for (int s = 0, at = 0; s < stages.Length; s++)
            for (int i = 0; i < stages[s].Systems.Length; i++) level[at++] = s;

        var infos = new StageInfo[stages.Length];
        for (int s = 0, at = 0; s < stages.Length; s++)
        {
            var systems = new SystemInfo[stages[s].Systems.Length];
            for (int i = 0; i < systems.Length; i++, at++)
            {
                var waits = new List<StageWait>();
                for (int j = 0; j < at; j++)
                {
                    if (level[j] != s - 1) continue;
                    bool ordered = SystemSchedule.Precedes(order[j], order[at]);
                    var conflicts = Conflicts(in order[j].Access, in order[at].Access);
                    if (ordered || conflicts.Length > 0) waits.Add(new StageWait(names[j], ordered, conflicts));
                }

                ref readonly var access = ref order[at].Access;
                systems[i] = new SystemInfo(names[at], ComponentNames(access.ReadComponents), ComponentNames(access.WriteComponents),
                    SingletonNamesOf(access.ReadSingletons), SingletonNamesOf(access.WriteSingletons), access.Alone, waits.ToArray())
                    { Loops = order[at].Loops, FusedWith = FusedWithNext(group, at) ? names[at + 1] : null };
            }

            infos[s] = new StageInfo(systems);
        }

        var pairs = new List<SystemPair>();
        for (int i = 0; i < n; i++)
        for (int j = i + 1; j < n; j++)
        {
            if (reach[i, j] || reach[j, i]) continue;
            var conflicts = Conflicts(in order[i].Access, in order[j].Access);
            if (conflicts.Length > 0) pairs.Add(new SystemPair(names[i], names[j], conflicts));
        }

        return new GroupInfo(group.Name, infos, pairs.ToArray());
    }

    /// <summary>
    /// Whether system <paramref name="at"/> of <paramref name="group"/> runs as one walk with the next one when both run
    /// (pass fusion): one thread runs the group and nothing measures systems apart.
    /// </summary>
    private bool FusedWithNext(SystemSchedule group, int at) =>
        !_options.ParallelSystems && !_options.MeasureSystems && group.Serial.Fused is var fused
        && at + 1 < fused.Length && fused[at] >= 0 && at + 1 < group.Systems.Length;

    /// <summary>What two systems conflict on (<see cref="SystemAccess.ConflictsWith"/>), by name.</summary>
    private static string[] Conflicts(in SystemAccess first, in SystemAccess second)
    {
        var names = new List<string>();
        if (first.Alone || second.Alone) names.Add("runs alone");
        Shared(names, first.WriteComponents, first.ReadComponents, second.WriteComponents, second.ReadComponents, false);
        Shared(names, first.WriteSingletons, first.ReadSingletons, second.WriteSingletons, second.ReadSingletons, true);
        return names.ToArray();
    }

    // The bits one side writes and the other reads or writes.
    private static void Shared(List<string> names, in BitMask writes, in BitMask reads, in BitMask otherWrites, in BitMask otherReads, bool singletons)
    {
        int capacity = Math.Max(Math.Max(writes.Capacity, reads.Capacity), Math.Max(otherWrites.Capacity, otherReads.Capacity));
        for (int bit = 0; bit < capacity; bit++)
            if ((writes.HasBit(bit) && (otherWrites.HasBit(bit) || otherReads.HasBit(bit))) || (reads.HasBit(bit) && otherWrites.HasBit(bit)))
                names.Add(singletons ? EasyEcs.SingletonNames.Of(bit) + " (singleton)" : ScheduleInfo.NameOf(ComponentInfo.Of(bit).Type));
    }

    private static string[] ComponentNames(in BitMask components)
    {
        var bits = components.ToBitArray();
        var names = new string[bits.Length];
        for (int i = 0; i < bits.Length; i++) names[i] = ScheduleInfo.NameOf(ComponentInfo.Of(bits[i]).Type);
        return names;
    }

    private static string[] SingletonNamesOf(in BitMask singletons)
    {
        var slots = singletons.ToBitArray();
        var names = new string[slots.Length];
        for (int i = 0; i < slots.Length; i++) names[i] = EasyEcs.SingletonNames.Of(slots[i]);
        return names;
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
