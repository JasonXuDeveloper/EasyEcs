using System;
using System.Numerics;
using System.Threading;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
namespace EasyEcs;

/// <summary>
/// Extra query constraints: components that must be present without being read (<see cref="With{T}"/>) or must be
/// absent (<see cref="Without{T}"/>). Build once and keep it (e.g. in a static readonly field):
/// <code>static readonly QueryFilter Alive = QueryFilter.Create().Without&lt;Dead&gt;();</code>
/// </summary>
[StructLayout(LayoutKind.Sequential)] // Add steps from _all to the other sets: they must stay adjacent, in this order
public readonly partial struct QueryFilter
{
    // The five sets' first 256 bits, inline. No object reference: a filter is copied by plain moves (no write barrier,
    // no zeroed frame), so building one at the call costs little more than holding one in a field.
    private readonly Vector256<long> _all, _none, _any, _tracked, _removals;

    /// <summary>The change tick <see cref="Changed{T}"/> and <see cref="Removed{T}"/> rows must have reached.</summary>
    internal readonly ulong Tick;

    /// <summary><see cref="InGroup{T}"/>: bit + 1 of the grouped component (0: no group), and the group.</summary>
    internal readonly int GroupBit, Group;

    /// <summary>The sets' bits past 255: an interned <see cref="FilterOverflow"/> id (0: none).</summary>
    private readonly int _overflow;

    /// <summary>The <c>ChangedAny</c> and <c>RemovedAny</c> groups: an interned <see cref="FilterGroups"/> id (0: none).</summary>
    private readonly int _groups;

    /// <summary><see cref="Slice"/>: the slice to visit and how many there are (0: the whole tables).</summary>
    internal readonly int SliceIndex, SliceCount;

    internal BitMask All => new(_all, FilterOverflow.Of(_overflow, 0));
    internal BitMask None => new(_none, FilterOverflow.Of(_overflow, 1));
    internal BitMask Any => new(_any, FilterOverflow.Of(_overflow, 2));
    internal BitMask Tracked => new(_tracked, FilterOverflow.Of(_overflow, 3));
    internal BitMask Removals => new(_removals, FilterOverflow.Of(_overflow, 4));
    /// <summary>The <c>ChangedAny</c> / <c>RemovedAny</c> groups' interned id (0: none): equal groups, equal ids.</summary>
    internal int Groups => _groups;

    /// <summary>Whether any set names a component bit past 255.</summary>
    internal bool HasOverflow => _overflow != 0;

    /// <summary>A filter that matches every entity with the queried components: the start of a chain (<c>Create().Without&lt;Dead&gt;()</c>). O(1).</summary>
    public static QueryFilter Create() => default;

    /// <summary>Whether <paramref name="other"/> selects the same rows (its tick aside): one query key.</summary>
    internal bool SameQuery(in QueryFilter other) =>
        _all.Equals(other._all) && _none.Equals(other._none) && _any.Equals(other._any) && _tracked.Equals(other._tracked)
        && _removals.Equals(other._removals) && _groups == other._groups && _overflow == other._overflow && GroupBit == other.GroupBit && Group == other.Group;

    /// <summary>Require <typeparamref name="T"/> to be present (not passed to the query callback).</summary>
    public QueryFilter With<T>() where T : struct, IComponent => Adding(0, ComponentType<T>.Bit);

    /// <summary>
    /// Require <typeparamref name="T"/> with group <paramref name="group"/>: only the tables of that group are walked
    /// (<see cref="World.GroupBy{T}"/>; in a world that does not group by <typeparamref name="T"/>, every table holds
    /// group 0). One group per filter.
    /// </summary>
    public QueryFilter InGroup<T>(int group) where T : struct, IComponent, IGroup
    {
        var filter = Adding(0, ComponentType<T>.Bit);
        Unsafe.AsRef(in filter.GroupBit) = ComponentType<T>.Bit + 1;
        Unsafe.AsRef(in filter.Group) = group;
        return filter;
    }

    /// <summary>Require <typeparamref name="T"/> to be absent.</summary>
    public QueryFilter Without<T>() where T : struct, IComponent => Adding(1, ComponentType<T>.Bit);

    /// <summary>
    /// Require at least one of the components named by <c>WithAny</c> calls: <c>WithAny&lt;Burning&gt;().WithAny&lt;Poisoned&gt;()</c>
    /// matches entities with Burning, Poisoned or both.
    /// </summary>
    public QueryFilter WithAny<T>() where T : struct, IComponent => Adding(2, ComponentType<T>.Bit);

    /// <summary>
    /// Require <typeparamref name="T"/> and only rows whose <typeparamref name="T"/> was written at or after change tick
    /// <paramref name="since"/>: a <see cref="World.ChangeTick"/> read earlier (<c>[Changed&lt;T&gt;]</c> methods select
    /// the rows written since their system last ran on their own). A filter has one tick: its <c>Changed</c> and
    /// <c>Removed</c> filters must name the same one.
    /// </summary>
    /// <exception cref="ArgumentException">An earlier <c>Changed</c> or <c>Removed</c> filter named another tick.</exception>
    public QueryFilter Changed<T>(ulong since) where T : struct, IComponent, ITrackChanges
    {
        CheckTick(since);
        var filter = this;
        Add(ref filter, 0, ComponentType<T>.Bit);
        Add(ref filter, 3, ComponentType<T>.Bit);
        Unsafe.AsRef(in filter.Tick) = since;
        return filter;
    }

    /// <summary>
    /// Only entities that lost <typeparamref name="T"/> at or after change tick <paramref name="since"/> and have not
    /// got it back (<c>[Removed&lt;T&gt;]</c> methods select the removals since their system last ran on their own).
    /// Removing a component keeps the entity's row, so the removal is a row like any other: its presence bit is clear
    /// and its change tick says when. Destroyed entities have no row and are not visited. A filter has one tick: its
    /// <c>Changed</c> and <c>Removed</c> filters must name the same one.
    /// </summary>
    /// <exception cref="ArgumentException">An earlier <c>Changed</c> or <c>Removed</c> filter named another tick.</exception>
    public QueryFilter Removed<T>(ulong since) where T : struct, IComponent, ITrackChanges
    {
        CheckTick(since);
        var filter = Adding(4, ComponentType<T>.Bit);
        Unsafe.AsRef(in filter.Tick) = since;
        return filter;
    }

    /// <summary>
    /// The shared core of the generated <c>ChangedAny&lt;T1..Tn&gt;</c> and <c>RemovedAny&lt;T1..Tn&gt;</c>
    /// (<paramref name="removed"/>): the components join the filter's one group of that kind, at tick
    /// <paramref name="since"/>. The groups are interned (one lookup, allocation-free once the pair is known).
    /// </summary>
    private QueryFilter AddingAny(bool removed, ulong since, ReadOnlySpan<ushort> bits)
    {
        CheckTick(since);
        var filter = this;
        Unsafe.AsRef(in filter._groups) = FilterGroups.With(_groups, removed, bits);
        Unsafe.AsRef(in filter.Tick) = since;
        return filter;
    }

    /// <summary>
    /// The same filter with its <see cref="Changed{T}"/> and <see cref="Removed{T}"/> filters (and its <c>ChangedAny</c> and
    /// <c>RemovedAny</c> ones) moved to tick <paramref name="since"/>, without rebuilding it: a filter kept in a field,
    /// used again after each update, or once per client with each client's acknowledged tick.
    /// </summary>
    public QueryFilter Since(ulong since)
    {
        var filter = this;
        Unsafe.AsRef(in filter.Tick) = since;
        return filter;
    }

    /// <summary>
    /// Whether row <paramref name="slot"/> of <paramref name="archetype"/> passes the filter, tested as a query tests a
    /// row (<c>entity.Run</c>): its <c>With</c> components present, its <c>Without</c> ones absent, one of its
    /// <c>WithAny</c> ones present, its <c>Changed</c> ones written and its <c>Removed</c> ones removed at or after
    /// <see cref="Tick"/>, its group. O(components named). Filters without bits past 255 only (<see cref="HasOverflow"/>).
    /// </summary>
    internal bool Passes(Archetype archetype, int slot)
    {
        if (GroupBit != 0 && archetype.GroupOf(GroupBit - 1) != Group) return false;
        if (_all != Vector256<long>.Zero && !Each(_all, archetype, slot, true)) return false;
        if (_none != Vector256<long>.Zero && !Each(_none, archetype, slot, false)) return false;
        if (((_tracked | _removals) != Vector256<long>.Zero || _groups != 0) && !Ticked(archetype, slot)) return false;
        if (_any == Vector256<long>.Zero) return true;
        for (int w = 0; w < 4; w++)
            for (ulong bits = (ulong)_any.GetElement(w); bits != 0; bits &= bits - 1)
                if (Present(archetype, (w << 6) + BitOperations.TrailingZeroCount(bits), slot)) return true;
        return false;
    }

    /// <summary>Whether every component of <paramref name="set"/> is present in the row (or every one absent).</summary>
    private static bool Each(Vector256<long> set, Archetype archetype, int slot, bool present)
    {
        for (int w = 0; w < 4; w++)
            for (ulong bits = (ulong)set.GetElement(w); bits != 0; bits &= bits - 1)
                if (Present(archetype, (w << 6) + BitOperations.TrailingZeroCount(bits), slot) != present) return false;
        return true;
    }

    /// <summary>The <c>Changed</c> components written, the <c>Removed</c> ones removed, at or after <see cref="Tick"/>.</summary>
    private bool Ticked(Archetype archetype, int slot)
    {
        ulong since = Math.Max(Tick, 1); // tick 0: never written nor removed (see RowFilter)
        for (int w = 0; w < 4; w++)
        {
            int at = w << 6;
            for (ulong bits = (ulong)_tracked.GetElement(w); bits != 0; bits &= bits - 1)
                if (TickOf(archetype, at + BitOperations.TrailingZeroCount(bits), slot) < since) return false;
            for (ulong bits = (ulong)_removals.GetElement(w); bits != 0; bits &= bits - 1)
            {
                int bit = at + BitOperations.TrailingZeroCount(bits);
                // A removal keeps the column (a table without it never had the component) and stamps the row.
                if (archetype.ColumnIndex(bit) < 0 || Present(archetype, bit, slot) || TickOf(archetype, bit, slot) < since) return false;
            }
        }

        if (_groups == 0) return true;
        var groups = FilterGroups.Of(_groups);
        return (groups.ChangedAnyBits.Length == 0 || AnyTicked(groups.ChangedAnyBits, archetype, slot, since, true))
               && (groups.RemovedAnyBits.Length == 0 || AnyTicked(groups.RemovedAnyBits, archetype, slot, since, false));
    }

    /// <summary>
    /// Whether one component of <paramref name="set"/> was written (<paramref name="written"/>: present with a new tick)
    /// or removed (its column kept, absent with a new tick) at or after <paramref name="since"/>.
    /// </summary>
    private static bool AnyTicked(ushort[] set, Archetype archetype, int slot, ulong since, bool written)
    {
        foreach (ushort bit in set)
            if (archetype.ColumnIndex(bit) >= 0 && Present(archetype, bit, slot) == written && TickOf(archetype, bit, slot) >= since) return true;
        return false;
    }

    private static bool Present(Archetype archetype, int bit, int slot) =>
        archetype.ColumnIndex(bit) is var column and >= 0 && archetype.Has(column, slot);

    /// <summary>The change tick of row <paramref name="slot"/> of tracked component <paramref name="bit"/> (0 when the table lacks it).</summary>
    private static ulong TickOf(Archetype archetype, int bit, int slot) =>
        archetype.ColumnIndex(ComponentInfo.Of(bit).Companion) is var column and >= 0
            ? Unsafe.Add(ref archetype.Data<ChangeTick>(column), slot).Value
            : 0;

    /// <summary>
    /// Sets <paramref name="bit"/> in set <paramref name="set"/> (0 All, 1 None, 2 Any, 3 Tracked, 4 Removals, 5 ChangedAny,
    /// 6 RemovedAny) of a
    /// filter copy being built: its fields are readonly to users, not to the builder, so each builder call copies the
    /// filter once. A bit past 255 interns the new overflow sets (cold: worlds with more than 256 component types).
    /// </summary>
    /// <summary>This filter with <paramref name="bit"/> added to set <paramref name="set"/> (a builder's copy).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly QueryFilter Adding(int set, int bit)
    {
        var filter = this;
        Add(ref filter, set, bit);
        return filter;
    }

    private static void Add(ref QueryFilter filter, int set, int bit)
    {
        if (bit < 256)
        {
            // A vector OR, not a store into one of its longs: the filter is then read whole, and a narrow store read back
            // wide stalls store forwarding. The bit is a JIT-time constant, so the vector is one too.
            ref var bits = ref Unsafe.AsRef(in Unsafe.Add(ref Unsafe.AsRef(in filter._all), set));
            bits |= Vector256<long>.Zero.WithElement(bit >> 6, 1L << (bit & 63));
        }
        else Unsafe.AsRef(in filter._overflow) = FilterOverflow.With(filter._overflow, set, bit);
    }

    private void CheckTick(ulong since)
    {
        if (since != Tick && (!Tracked.IsEmpty || !Removals.IsEmpty || _groups != 0)) ThrowTwoTicks();
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void ThrowTwoTicks() =>
        throw new ArgumentException("Changed, Removed, ChangedAny and RemovedAny filters of one QueryFilter must name the same tick", "since");
}

/// <summary>Cache key of a query: required bits, excluded bits and the bits of which one is required.</summary>
internal readonly struct QueryKey : IEquatable<QueryKey>
{
    private readonly BitMask _all;
    private readonly BitMask _none;
    private readonly BitMask _any;
    private readonly BitMask _changed;
    private readonly BitMask _removed;
    private readonly int _groups; // QueryFilter.Groups: interned ChangedAny / RemovedAny groups (equal groups, equal ids)
    private readonly int _groupBit, _group; // QueryFilter.GroupBit / Group

    // Detached keys only (not part of equality): the required and excluded bits as arrays, for the per-archetype
    // presence tests (a table with a column can still have rows without the component).
    private readonly ushort[] _required;
    private readonly ushort[] _excluded;
    private readonly ushort[] _anyOf;
    private readonly ushort[] _changedOf;
    private readonly ushort[] _removedOf;
    private readonly FilterGroups.Entry _changedAndRemovedAny;

    // Lookup keys of queries on component bits >= 256 keep their overflow here instead of allocating it per query.
    [ThreadStatic] private static Vector256<long>[] _scratch;

    /// <summary>Whether the query names component <paramref name="bit"/> (in any of its sets, or as its group's component).</summary>
    public bool Mentions(int bit) => _all.HasBit(bit) || _none.HasBit(bit) || _any.HasBit(bit) || _changed.HasBit(bit)
                                     || _removed.HasBit(bit) || _groupBit == bit
                                     || (_groups != 0 && (FilterGroups.Of(_groups).ChangedAny.HasBit(bit) || FilterGroups.Of(_groups).RemovedAny.HasBit(bit)));

    public QueryKey(in BitMask all, in QueryFilter filter)
    {
        _all = all;
        _none = filter.None;
        _any = filter.Any;
        _changed = filter.Tracked;
        _removed = filter.Removals;
        _groups = filter.Groups;
        _groupBit = filter.GroupBit;
        _group = filter.Group;
        _required = _excluded = _anyOf = _changedOf = _removedOf = [];
    }

    private QueryKey(in QueryKey key, in BitMask all, in BitMask none, in BitMask any, in BitMask changed, in BitMask removed)
    {
        _groups = key._groups;
        _changedAndRemovedAny = FilterGroups.Of(key._groups);
        _groupBit = key._groupBit;
        _group = key._group;
        _all = all;
        _none = none;
        _any = any;
        _changed = changed;
        _removed = removed;
        _required = all.ToBitArray();
        _excluded = none.ToBitArray();
        _anyOf = any.ToBitArray();
        _changedOf = changed.ToBitArray();
        _removedOf = removed.ToBitArray();
    }

    /// <summary>
    /// Key to look a query up with: allocation-free, but it shares this thread's scratch buffer, so it is valid only
    /// until the next lookup on the thread. Store <see cref="Detached"/> instead.
    /// </summary>
    public static QueryKey ForLookup(ReadOnlySpan<ushort> bits, in QueryFilter filter)
    {
        var all = Required(bits, in filter);
        return new QueryKey(in all, in filter);
    }

    /// <summary>
    /// The required set of a query: the filter's and the call's <paramref name="bits"/>. Allocation-free; past 255 it
    /// shares this thread's scratch buffer (valid until the next lookup on the thread).
    /// </summary>
    public static BitMask Required(ReadOnlySpan<ushort> bits, in QueryFilter filter)
    {
        // Common case: every bit fits the inline 256 bits, so a plain copy allocates nothing.
        if (!filter.HasOverflow)
        {
            bool inline = true;
            for (int i = 0; i < bits.Length; i++) inline &= bits[i] < 256;
            if (inline)
            {
                var all = filter.All;
                for (int i = 0; i < bits.Length; i++) all.SetBit(bits[i]);
                return all;
            }
        }

        return RequiredWithOverflow(bits, in filter);
    }

    // Cold path kept out of line: inlining the thread-static access into ForEach measurably slowed its entity loop.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static BitMask RequiredWithOverflow(ReadOnlySpan<ushort> bits, in QueryFilter filter)
    {
        int maxBit = 0;
        for (int i = 0; i < bits.Length; i++) maxBit = Math.Max(maxBit, bits[i]);
        var all = filter.All.CopyWithScratch(maxBit, ref _scratch);
        for (int i = 0; i < bits.Length; i++) all.SetBit(bits[i]);
        return all;
    }

    /// <summary>A copy that owns its storage (cache entries).</summary>
    public QueryKey Detached()
    {
        var all = _all.Clone();
        var none = _none.Clone();
        var any = _any.Clone();
        var changed = _changed.Clone();
        var removed = _removed.Clone();
        return new QueryKey(in this, in all, in none, in any, in changed, in removed);
    }

    /// <summary>
    /// Whether <paramref name="archetype"/> can hold matching rows (detached keys only): they have
    /// every required column, the column of every <c>Removed</c> component (a removal keeps the row) and, with
    /// <c>WithAny</c>, <c>ChangedAny</c> or <c>RemovedAny</c>, at least one column of each of those groups. Excluded
    /// columns do not rule a table out, its rows may lack those components.
    /// </summary>
    public bool Matches(Archetype archetype) =>
        archetype.ComponentMask.ContainsAll(in _all) && archetype.ComponentMask.ContainsAll(in _removed)
        && (_anyOf.Length == 0 || archetype.ComponentMask.Intersects(in _any))
        && (_changedAndRemovedAny.ChangedAnyBits.Length == 0 || archetype.ComponentMask.Intersects(in _changedAndRemovedAny.ChangedAny))
        && (_changedAndRemovedAny.RemovedAnyBits.Length == 0 || archetype.ComponentMask.Intersects(in _changedAndRemovedAny.RemovedAny))
        && (_groupBit == 0 || archetype.GroupOf(_groupBit - 1) == _group);

    /// <summary>The presence tests of a matching <paramref name="archetype"/>: its required and excluded columns.</summary>
    public RowPlan FilterFor(Archetype archetype)
    {
        var require = new short[_required.Length];
        for (int i = 0; i < require.Length; i++) require[i] = (short)archetype.ColumnIndex(_required[i]);
        var exclude = new System.Collections.Generic.List<short>();
        foreach (ushort bit in _excluded)
        {
            int column = archetype.ColumnIndex(bit);
            if (column >= 0) exclude.Add((short)column);
        }

        var any = new System.Collections.Generic.List<short>();
        foreach (ushort bit in _anyOf)
        {
            int column = archetype.ColumnIndex(bit);
            if (column >= 0) any.Add((short)column);
        }

        // A changed component's ticks live in its companion column, which every table having the component has. A
        // removed one is a row whose presence bit is clear (an excluded column) and whose tick is new: the removal
        // stamped it.
        var changed = new System.Collections.Generic.List<short>();
        foreach (ushort bit in _changedOf) changed.Add((short)archetype.ColumnIndex(ComponentInfo.Of(bit).Companion));
        foreach (ushort bit in _removedOf)
        {
            exclude.Add((short)archetype.ColumnIndex(bit));
            changed.Add((short)archetype.ColumnIndex(ComponentInfo.Of(bit).Companion));
        }

        return new RowPlan(require, exclude.ToArray(), any.ToArray(), changed.ToArray(), _changedOf.Length,
            Pairs(archetype, _changedAndRemovedAny.ChangedAnyBits), Pairs(archetype, _changedAndRemovedAny.RemovedAnyBits));
    }

    /// <summary>The (component column, change-tick column) pairs of the components of a group the table has.</summary>
    private static short[] Pairs(Archetype archetype, ushort[] bits)
    {
        var pairs = new System.Collections.Generic.List<short>();
        foreach (ushort bit in bits)
        {
            int column = archetype.ColumnIndex(bit);
            if (column < 0) continue; // the table never had it: no row of it can have written or removed it
            pairs.Add((short)column);
            pairs.Add((short)archetype.ColumnIndex(ComponentInfo.Of(bit).Companion));
        }

        return pairs.ToArray();
    }

    public bool Equals(QueryKey other) =>
        _all.Equals(in other._all) && _none.Equals(in other._none) && _any.Equals(in other._any) &&
        _changed.Equals(in other._changed) && _removed.Equals(in other._removed) && _groups == other._groups
        && _groupBit == other._groupBit && _group == other._group;

    public override bool Equals(object obj) => obj is QueryKey other && Equals(other);

    public override int GetHashCode() => Hash(in _all, in _none, in _any, in _changed, in _removed, _groups, _groupBit, _group);

    /// <summary>Whether this is the key of <paramref name="lookup"/> (no key is built).</summary>
    public bool Is(in QueryLookup lookup)
    {
        ref readonly var filter = ref lookup.Filter;
        return _all.Equals(in lookup.All) && _groupBit == filter.GroupBit && _group == filter.Group && _none.Equals(filter.None)
               && _any.Equals(filter.Any) && _changed.Equals(filter.Tracked) && _removed.Equals(filter.Removals)
               && _groups == filter.Groups;
    }

    public static int Hash(in QueryLookup lookup)
    {
        ref readonly var filter = ref lookup.Filter;
        return Hash(in lookup.All, filter.None, filter.Any, filter.Tracked, filter.Removals, filter.Groups, filter.GroupBit, filter.Group);
    }

    private static int Hash(in BitMask all, in BitMask none, in BitMask any, in BitMask changed, in BitMask removed, int groups,
        int groupBit, int group) =>
        ((((all.GetHashCode() * 31 + none.GetHashCode()) * 31 + any.GetHashCode()) * 31 + changed.GetHashCode()) * 31
         + removed.GetHashCode()) * 31 + groups * 104729 + groupBit * 397 + group * 7919;
}

/// <summary>
/// A query's cache key as its call holds it (the call's bits joined into the required set, the rest read from the
/// filter): the query cache is probed with it, so a hit builds and copies no <see cref="QueryKey"/>.
/// </summary>
internal readonly ref struct QueryLookup(ReadOnlySpan<ushort> bits, ref readonly QueryFilter filter)
{
    public readonly BitMask All = QueryKey.Required(bits, in filter);
    public readonly ref readonly QueryFilter Filter = ref filter;
}

/// <summary>Equality of query keys, and of a key and a <see cref="QueryLookup"/> (the cache's alternate lookup).</summary>
internal sealed class QueryKeyComparer : System.Collections.Generic.IEqualityComparer<QueryKey>,
    System.Collections.Generic.IAlternateEqualityComparer<QueryLookup, QueryKey>
{
    public static readonly QueryKeyComparer Instance = new();

    public bool Equals(QueryKey x, QueryKey y) => x.Equals(y);

    public int GetHashCode(QueryKey key) => key.GetHashCode();

    public bool Equals(QueryLookup lookup, QueryKey key) => key.Is(in lookup);

    public int GetHashCode(QueryLookup lookup) => QueryKey.Hash(in lookup);

    public QueryKey Create(QueryLookup lookup)
    {
        ref readonly var filter = ref lookup.Filter;
        return new QueryKey(in lookup.All, in filter).Detached();
    }
}

/// <summary>
/// The archetypes a query matches and, parallel to them, the presence tests of each. Immutable: the query cache
/// publishes a new instance when an archetype is added.
/// </summary>
internal sealed class QueryMatch(Archetype[] archetypes, RowPlan[] filters)
{
    public readonly Archetype[] Archetypes = archetypes;
    public readonly RowPlan[] Filters = filters;

    /// <summary>This match without the tables of <paramref name="dropped"/> (itself when it has none of them; cold).</summary>
    public QueryMatch Without(System.Collections.Generic.HashSet<Archetype> dropped)
    {
        var archetypes = new System.Collections.Generic.List<Archetype>();
        var filters = new System.Collections.Generic.List<RowPlan>();
        for (int i = 0; i < Archetypes.Length; i++)
            if (!dropped.Contains(Archetypes[i]))
            {
                archetypes.Add(Archetypes[i]);
                filters.Add(Filters[i]);
            }

        return archetypes.Count == Archetypes.Length ? this : new QueryMatch(archetypes.ToArray(), filters.ToArray());
    }

    public QueryMatch Append(Archetype archetype, RowPlan filter)
    {
        var archetypes = new Archetype[Archetypes.Length + 1];
        var filters = new RowPlan[Filters.Length + 1];
        Array.Copy(Archetypes, archetypes, Archetypes.Length);
        Array.Copy(Filters, filters, Filters.Length);
        archetypes[^1] = archetype;
        filters[^1] = filter;
        return new QueryMatch(archetypes, filters);
    }

    /// <summary>Alive matching entities: O(matching archetypes), plus O(rows / 64) for tables with rows to test.</summary>
    public int CountAlive(ulong since)
    {
        int count = 0;
        for (int i = 0; i < Archetypes.Length; i++) count += new RowFilter(Filters[i], since).CountAlive(Archetypes[i]);
        return count;
    }

    /// <summary>Whether any alive matching entity exists: stops at the first one.</summary>
    public bool AnyAlive(ulong since)
    {
        for (int i = 0; i < Archetypes.Length; i++)
            if (new RowFilter(Filters[i], since).AnyAlive(Archetypes[i]))
                return true;
        return false;
    }
}

/// <summary>
/// A row of the hidden change-tick column of a tracked component: the <see cref="World.ChangeTick"/> of the row's last
/// write. Each tracked type has its own column (bit) of these, never registered through <c>ComponentType</c>.
/// </summary>
internal struct ChangeTick : IComponent
{
#pragma warning disable CS0649 // only written through refs into the columns
    public ulong Value;
#pragma warning restore CS0649
}

/// <summary>
/// What a query tests on the rows of one archetype (cached with the query): the presence bits of <see cref="Require"/>
/// must be set, those of <see cref="Exclude"/> clear, at least one of <see cref="AnyOf"/> set when it is not empty, the
/// change ticks in the <see cref="Changed"/> columns newer than the pass's tick, and, for each of the
/// <see cref="AnyChanged"/> and <see cref="AnyRemoved"/> groups that is not empty, one of its components written (or
/// removed) since that tick (column indices of the archetype; a column without presence bits is full). The parts
/// combine with AND; inside a group, its components with OR.
/// </summary>
internal sealed class RowPlan(short[] require, short[] exclude, short[] anyOf, short[] changed, int removed, short[] anyChanged, short[] anyRemoved)
{
    public readonly short[] Require = require;
    public readonly short[] Exclude = exclude;
    public readonly short[] AnyOf = anyOf;
    public readonly short[] Changed = changed;

    /// <summary>
    /// <c>ChangedAny</c>: (component column, change-tick column) pairs; a row passes when one component is present with a
    /// tick at or after the pass's. Only the components the table has.
    /// </summary>
    public readonly short[] AnyChanged = anyChanged;

    /// <summary><c>RemovedAny</c>: pairs like <see cref="AnyChanged"/>; a row passes when one component is absent with a new tick.</summary>
    public readonly short[] AnyRemoved = anyRemoved;

    /// <summary>Rows are selected by change ticks: a Changed or Removed column, or a ChangedAny or RemovedAny group.</summary>
    public readonly bool Ticked = changed.Length + anyChanged.Length + anyRemoved.Length != 0;

    /// <summary>
    /// The tick column whose summaries skip words (-1 without Changed columns): a removed component's when there is one
    /// (removals are sparser than writes, and its summaries leave writes out), else the first changed one.
    /// </summary>
    public readonly short Lead = changed.Length == 0 ? (short)-1 : changed[removed < changed.Length ? removed : 0];

    /// <summary>Whether <see cref="Lead"/> skips by its removal summaries.</summary>
    public readonly bool LeadRemoved = removed < changed.Length;
}

/// <summary>The row loops of a pass that may change structure are compiled for a table that may have tombstones...</summary>
internal struct Tombstones;

/// <summary>...and for one that has none (its rows test no id): the typeof tests on them are JIT-time constants.</summary>
internal struct NoTombstones;

/// <summary>
/// The row test of one pass: a table's <see cref="RowPlan"/> and the change tick its <see cref="RowPlan.Changed"/>
/// columns must reach (a pass value: plans are shared by every pass of the query).
/// </summary>
internal readonly struct RowFilter(RowPlan plan, ulong since)
{
    private readonly RowPlan _plan = plan;
    // At least 1: tick 0 marks rows never written nor removed (a copy's absent components), which no Removed<T> filter
    // may select; every row holding a tracked component has a tick of at least 1, so Changed<T> filters see no difference.
    private readonly ulong _since = Math.Max(since, 1);

    /// <summary>
    /// No row of the table passes: every alive row has an excluded component or lacks every WithAny component, or no
    /// Changed column was written since the pass's tick.
    /// </summary>
    public const int None = -1;

    /// <summary>Every alive row passes: every row has the required components and lacks the excluded ones.</summary>
    public const int All = 0;

    /// <summary>Rows must be tested (<see cref="Word"/>).</summary>
    public const int Test = 1;

    /// <summary>
    /// How the rows of <paramref name="archetype"/> pass right now: <see cref="None"/>, <see cref="All"/> or
    /// <see cref="Test"/>, from the per-column counts of rows lacking a component (O(columns)). Every change of those
    /// bumps the world's storage version, so a pass re-checks this after a structural change. A Changed column not
    /// written since the pass's tick gives <see cref="None"/>; otherwise change ticks always need a test.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Mode(Archetype archetype)
    {
        var absent = archetype.Absent;
        var plan = _plan;
        var changed = plan.Changed;
        // A table not written since the pass's tick has no changed row.
        for (int i = 0; i < changed.Length; i++)
            if (archetype.LastWrite[changed[i]] < _since) return None;
        // A group none of whose columns was written (or had a removal: it stamps) since the tick has no passing row.
        if (plan.AnyChanged.Length != 0 && !AnyNewer(archetype, plan.AnyChanged)) return None;
        if (plan.AnyRemoved.Length != 0 && !AnyNewer(archetype, plan.AnyRemoved)) return None;
        bool test = plan.Ticked;
        var exclude = plan.Exclude;
        for (int i = 0; i < exclude.Length; i++)
        {
            int lacking = absent[exclude[i]];
            if (lacking == 0) return None;
            test |= lacking != archetype.AliveCount;
        }

        var require = plan.Require;
        for (int i = 0; i < require.Length; i++) test |= absent[require[i]] != 0;

        var anyOf = plan.AnyOf;
        if (anyOf.Length != 0)
        {
            // Some column every row has: no test. No column any row has: no row passes. Else rows differ.
            bool full = false, empty = true;
            for (int i = 0; i < anyOf.Length; i++)
            {
                int lacking = absent[anyOf[i]];
                full |= lacking == 0;
                empty &= lacking == archetype.AliveCount;
            }

            if (empty) return None;
            test |= !full;
        }

        return test ? Test : All;
    }

    /// <summary>Whether a tick column of <paramref name="pairs"/> (a ChangedAny or RemovedAny group) was stamped since the pass's tick.</summary>
    private bool AnyNewer(Archetype archetype, short[] pairs)
    {
        for (int i = 1; i < pairs.Length; i += 2)
            if (archetype.LastWrite[pairs[i]] >= _since) return true;
        return false;
    }

    /// <summary>Alive rows of <paramref name="archetype"/> that pass.</summary>
    public int CountAlive(Archetype archetype) => Mode(archetype) switch
    {
        None => 0,
        All => archetype.AliveCount,
        _ => Counted(archetype) is var counted and >= 0 ? counted : Count(archetype),
    };

    /// <summary>Whether any alive row of <paramref name="archetype"/> passes (stops at the first one).</summary>
    public bool AnyAlive(Archetype archetype) => Mode(archetype) switch
    {
        None => false,
        All => archetype.AliveCount > 0,
        _ => Counted(archetype) is var counted and >= 0 ? counted > 0 : Any(archetype),
    };

    /// <summary>
    /// The passing alive rows from the counts of rows lacking a component, when one required or excluded column is
    /// the only one whose rows differ (every other required column full, every other excluded column empty): its
    /// count is the answer, no presence bits read. -1 when rows must be tested. O(columns).
    /// </summary>
    private int Counted(Archetype archetype)
    {
        var plan = _plan;
        if (plan.Ticked || plan.AnyOf.Length != 0) return -1;
        var absent = archetype.Absent;
        int alive = archetype.AliveCount, counted = alive, varying = 0;
        var require = plan.Require;
        for (int i = 0; i < require.Length; i++)
        {
            int lacking = absent[require[i]];
            if (lacking == 0) continue;
            varying++;
            counted = alive - lacking;
        }

        var exclude = plan.Exclude;
        for (int i = 0; i < exclude.Length; i++)
        {
            int lacking = absent[exclude[i]];
            if (lacking == alive) continue;
            varying++;
            counted = lacking;
        }

        return varying == 1 ? counted : -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Passes(Archetype archetype, int slot) => (Word(archetype, slot >> 6) & (1UL << slot)) != 0;

    /// <summary>Rows <c>64 * word</c> .. <c>64 * word + 63</c> that pass, one bit each (tombstones may pass: check ids).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Word(Archetype archetype, int word)
    {
        ulong bits = ulong.MaxValue;
        var plan = _plan;
        var require = plan.Require;
        for (int i = 0; i < require.Length; i++)
            if (archetype.Bits(require[i]) is { } present) bits &= At(present, word);
        var exclude = plan.Exclude;
        for (int i = 0; i < exclude.Length; i++)
            bits &= archetype.Bits(exclude[i]) is { } present ? ~At(present, word) : 0;
        var anyOf = plan.AnyOf;
        if (anyOf.Length != 0)
        {
            ulong any = 0;
            for (int i = 0; i < anyOf.Length; i++)
                any |= archetype.Bits(anyOf[i]) is { } present ? At(present, word) : ulong.MaxValue;
            bits &= any;
        }

        var changed = plan.Changed;
        for (int i = 0; i < changed.Length && bits != 0; i++) bits &= Newer(archetype, changed[i], word, _since);
        if (plan.AnyChanged.Length != 0 && bits != 0) bits &= AnyNewer(archetype, plan.AnyChanged, word, true);
        if (plan.AnyRemoved.Length != 0 && bits != 0) bits &= AnyNewer(archetype, plan.AnyRemoved, word, false);
        return bits;
    }

    /// <summary>
    /// Rows of word <paramref name="word"/> for which one component of group <paramref name="pairs"/> is present
    /// (<paramref name="written"/>) or absent (a removal) with a tick at or after the pass's: the OR of the group.
    /// </summary>
    private ulong AnyNewer(Archetype archetype, short[] pairs, int word, bool written)
    {
        ulong any = 0;
        for (int i = 0; i < pairs.Length; i += 2)
        {
            ulong newer = Newer(archetype, pairs[i + 1], word, _since);
            if (newer == 0) continue;
            // Without presence bits every row has the component: it was written, never removed.
            ulong present = archetype.Bits(pairs[i]) is { } bits ? At(bits, word) : ulong.MaxValue;
            any |= newer & (written ? present : ~present);
        }

        return any;
    }

    /// <summary>Word <paramref name="word"/> of presence bits, which cover the table's capacity (words of rows are below it).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong At(ulong[] present, int word) => Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(present), word);

    /// <summary>
    /// The first word at or after <paramref name="word"/> (below <paramref name="words"/>) that may have a passing row,
    /// skipping words with none 4 per compare when one presence column decides (see <see cref="Decider"/>); otherwise
    /// <paramref name="word"/>. Returns <paramref name="words"/> when none is left.
    /// </summary>
    public int NextPassing(Archetype archetype, int word, int words)
    {
        if (word >= words || Decider(archetype, out bool excluded) is not { } bits) return word;
        int found = bits.AsSpan(word, words - word).IndexOfAnyExcept(excluded ? ulong.MaxValue : 0);
        return found < 0 ? words : word + found;
    }

    /// <summary>
    /// The last word at or below <paramref name="word"/> that may have a passing row, like <see cref="NextPassing"/>
    /// walking down; -1 when none is left.
    /// </summary>
    public int PrevPassing(Archetype archetype, int word)
    {
        if (word < 0 || Decider(archetype, out bool excluded) is not { } bits) return word;
        return bits.AsSpan(0, word + 1).LastIndexOfAnyExcept(excluded ? ulong.MaxValue : 0);
    }

    /// <summary>
    /// The first word at or after <paramref name="word"/> (below <paramref name="words"/>, whole words of rows) whose
    /// rows do not all pass, 4 words per compare when one presence column decides (see <see cref="Decider"/>);
    /// otherwise <paramref name="word"/>. Returns <paramref name="words"/> when all pass.
    /// </summary>
    public int WholeEnd(Archetype archetype, int word, int words)
    {
        if (word >= words || Decider(archetype, out bool excluded) is not { } bits) return word;
        int found = bits.AsSpan(word, words - word).IndexOfAnyExcept(excluded ? 0 : ulong.MaxValue);
        return found < 0 ? words : word + found;
    }

    /// <summary>
    /// The presence bits that alone decide which rows pass: the plan has no WithAny or Changed column and exactly one of
    /// its required or excluded columns has bits (the others are held by every row); null otherwise (cold: per run).
    /// </summary>
    private ulong[] Decider(Archetype archetype, out bool excluded)
    {
        excluded = false;
        var plan = _plan;
        if (plan.AnyOf.Length != 0 || plan.Ticked) return null;
        ulong[] decider = null;
        foreach (short column in plan.Require)
        {
            if (archetype.Bits(column) is not { } bits) continue;
            if (decider != null) return null;
            decider = bits;
        }

        foreach (short column in plan.Exclude)
        {
            // Without bits every row has it and none passes (the caller's mode is None then).
            if (decider != null || archetype.Bits(column) is not { } bits) return null;
            (decider, excluded) = (bits, true);
        }

        return decider;
    }

    /// <summary>
    /// The first word at or after <paramref name="word"/> (below <paramref name="words"/>) that may have passing rows:
    /// for a <c>Changed</c> or <c>Removed</c> filter, blocks of 64 words, then words, whose <see cref="RowPlan.Lead"/>
    /// column was not written (or had no removal) since the pass's tick are skipped (one bit scan per block when its other
    /// words are older, else one compare per word); without one, those that no column of a <c>RemovedAny</c> (else
    /// <c>ChangedAny</c>) group reaches (the nearest of the columns' next words: the group is an OR); otherwise
    /// <paramref name="word"/>. Returns <paramref name="words"/> when none is left.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int NextWord(Archetype archetype, int word, int words)
    {
        var plan = _plan;
        if (plan.Lead < 0) return plan.AnyChanged.Length + plan.AnyRemoved.Length == 0 ? word : NextOfGroup(archetype, word, words);
        return NextWritten(plan.LeadRemoved ? archetype.Removals[plan.Lead] : archetype.Blocks[plan.Lead],
            archetype.Words[plan.Lead], plan.LeadRemoved, word, words, _since);
    }

    /// <summary>
    /// <see cref="NextWord"/> for a plan whose only tick tests are ChangedAny / RemovedAny groups: the first word one
    /// column of the group reaches (removals are sparser than writes, so a RemovedAny group decides when there is one).
    /// </summary>
    private int NextOfGroup(Archetype archetype, int word, int words)
    {
        var plan = _plan;
        bool removed = plan.AnyRemoved.Length != 0;
        var pairs = removed ? plan.AnyRemoved : plan.AnyChanged;
        int next = words;
        for (int i = 1; i < pairs.Length && next > word; i += 2)
        {
            int column = pairs[i];
            next = NextWritten(removed ? archetype.Removals[column] : archetype.Blocks[column], archetype.Words[column], removed, word, next, _since);
        }

        return next;
    }

    private static int NextWritten(BlockTicks[] blocks, WordTicks[] summary, bool removed, int word, int words, ulong since)
    {
        // words <= summary.Length and (words + 63) / 64 <= blocks.Length: the summaries cover the column's capacity,
        // words cover the table's rows.
        ref var first = ref MemoryMarshal.GetArrayDataReference(blocks);
        while (word < words)
        {
            int index = word >> 6;
            int end = Math.Min(words, (index + 1) << 6);
            ref var block = ref Unsafe.Add(ref first, index);
            if (block.Latest >= since)
            {
                if (block.Rest < since)
                {
                    // Exactly the block's Words are newer than since (shift is mod 64: word & 63).
                    ulong newer = block.Words & (ulong.MaxValue << word);
                    if (newer != 0) return Math.Min(end, (index << 6) + BitOperations.TrailingZeroCount(newer));
                }
                else if (ScanWords(summary, removed, word, end, since) is var found and >= 0) return found;
            }

            word = end;
        }

        return words;
    }

    /// <summary>
    /// The first word of [<paramref name="word"/>, <paramref name="end"/>) whose write (or removal) tick is at least
    /// <paramref name="since"/>, -1 when none.
    /// </summary>
    private static int ScanWords(WordTicks[] summary, bool removed, int word, int end, ulong since)
    {
        // end <= summary.Length (see NextWritten): no bounds checks.
        ref var s = ref MemoryMarshal.GetArrayDataReference(summary);
        for (; word < end; word++)
            if ((removed ? Unsafe.Add(ref s, word).Removed : Unsafe.Add(ref s, word).Write) >= since) return word;
        return -1;
    }

    /// <summary>
    /// Rows of word <paramref name="word"/> whose tick in column <paramref name="column"/> (a change-tick
    /// column: 8-byte unmanaged rows, 64-byte aligned) is at least <paramref name="since"/>.
    /// </summary>
    private static ulong Newer(Archetype archetype, int column, int word, ulong since)
    {
        // Rows past the column's capacity do not exist (their bits are masked by the callers' row counts).
        ref var summary = ref archetype.Words[column][word];
        if (summary.Write < since) return 0; // no row of the word written since
        // The rows of the word's latest tick, when every other row is older than since: no tick is read.
        if (summary.Rest < since) return summary.Rows;
        int first = word << 6;
        int rows = Math.Min(64, archetype.Slots.Length - first);
        ref ulong ticks = ref Unsafe.Add(ref Unsafe.As<ChangeTick, ulong>(ref archetype.Data<ChangeTick>(column)), first);
        // tick >= since, as tick > since - 1: ticks stay below 2^63, so a signed compare is exact (since 0 gives -1).
        return Above(ref Unsafe.As<ulong, long>(ref ticks), rows, (long)since - 1);
    }

    /// <summary>
    /// Bit i set when tick i of the <paramref name="count"/> (at most 64) at <paramref name="ticks"/> is above
    /// <paramref name="threshold"/>: 4 ticks per compare with AVX2, 2 where 256-bit vectors are emulated (Arm64); the
    /// IsHardwareAccelerated tests are JIT-time constants, so one loop remains. The tail is scalar. Not 512-bit: 8 ticks
    /// per compare measured slower than 4 on AVX-512 (ChangeTrackingBenchmark).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Above(ref long ticks, int count, long threshold)
    {
        ulong bits = 0;
        int row = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            var limit = Vector256.Create(threshold);
            for (; row + 4 <= count; row += 4)
                bits |= (ulong)Vector256.GreaterThan(Vector256.LoadUnsafe(ref ticks, (nuint)row), limit).ExtractMostSignificantBits() << row;
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            var limit = Vector128.Create(threshold);
            for (; row + 2 <= count; row += 2)
                bits |= (ulong)Vector128.GreaterThan(Vector128.LoadUnsafe(ref ticks, (nuint)row), limit).ExtractMostSignificantBits() << row;
        }

        for (; row < count; row++)
            if (Unsafe.Add(ref ticks, row) > threshold) bits |= 1UL << row;
        return bits;
    }

    /// <summary>Alive rows of <paramref name="archetype"/> that pass (O(rows / 64) plus one id check per passing row).</summary>
    public int Count(Archetype archetype)
    {
        int rows = archetype.Count;
        if (rows == archetype.AliveCount && !_plan.Ticked && _plan.AnyOf.Length == 0) return CountDense(archetype, rows);
        int count = 0;
        var slots = archetype.Slots;
        int words = (rows + 63) >> 6;
        for (int w = NextWord(archetype, 0, words); w < words; w = NextWord(archetype, w + 1, words))
        {
            ulong bits = Word(archetype, w) & RowMask(w, rows);
            if (bits == 0) w = NextPassing(archetype, w + 1, words) - 1; // words without a passing row
            while (bits != 0)
            {
                if (slots[(w << 6) + BitOperations.TrailingZeroCount(bits)].Index >= 0) count++;
                bits &= bits - 1;
            }
        }

        return count;
    }

    /// <summary>
    /// <see cref="Count"/> of a table without tombstones (every row alive, as outside a pass) and without change ticks:
    /// the passing rows are the set bits of the words, no row id is read. 8 words per step with AVX-512, else
    /// <see cref="Vector{T}"/> (4 on AVX2, 2 on Arm64); 100,000 rows: 1.25 μs, 2.4 μs, 5.2 μs one word at a time.
    /// </summary>
    private int CountDense(Archetype archetype, int rows)
    {
        var plan = _plan;
        var require = plan.Require;
        var exclude = plan.Exclude;
        int full = rows >> 6, count = 0, w = 0;
        // w + lanes <= full below: presence bits cover the table's capacity, so the words exist in every column.
        if (Vector512.IsHardwareAccelerated)
            for (; w + 8 <= full; w += 8)
            {
                var bits = Vector512<ulong>.AllBitsSet;
                for (int i = 0; i < require.Length; i++)
                    if (archetype.Bits(require[i]) is { } present) bits &= Vector512.LoadUnsafe(ref At(present), (nuint)w);
                for (int i = 0; i < exclude.Length; i++)
                    bits = archetype.Bits(exclude[i]) is { } present ? Vector512.AndNot(bits, Vector512.LoadUnsafe(ref At(present), (nuint)w)) : default;
                count += PopCount(bits);
            }
        else if (Vector.IsHardwareAccelerated)
            for (int lanes = Vector<ulong>.Count; w + lanes <= full; w += lanes)
            {
                var bits = Vector<ulong>.AllBitsSet;
                for (int i = 0; i < require.Length; i++)
                    if (archetype.Bits(require[i]) is { } present) bits &= Vector.LoadUnsafe(ref At(present), (nuint)w);
                for (int i = 0; i < exclude.Length; i++)
                    bits = archetype.Bits(exclude[i]) is { } present ? Vector.AndNot(bits, Vector.LoadUnsafe(ref At(present), (nuint)w)) : default;
                for (int lane = 0; lane < lanes; lane++) count += BitOperations.PopCount(bits[lane]);
            }

        for (; w < full; w++) count += BitOperations.PopCount(Word(archetype, w));
        if (full << 6 < rows) count += BitOperations.PopCount(Word(archetype, full) & RowMask(full, rows));
        return count;

        static ref ulong At(ulong[] present) => ref MemoryMarshal.GetArrayDataReference(present);
    }

    // Lane by lane, unrolled: an indexed loop over a Vector512 goes through memory (7.6 μs instead of 1.25 μs above).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int PopCount(Vector512<ulong> bits)
    {
        var (low, high) = (bits.GetLower(), bits.GetUpper());
        return BitOperations.PopCount(low.GetElement(0)) + BitOperations.PopCount(low.GetElement(1)) + BitOperations.PopCount(low.GetElement(2))
               + BitOperations.PopCount(low.GetElement(3)) + BitOperations.PopCount(high.GetElement(0)) + BitOperations.PopCount(high.GetElement(1))
               + BitOperations.PopCount(high.GetElement(2)) + BitOperations.PopCount(high.GetElement(3));
    }

    /// <summary>Whether any alive row passes: <see cref="Count"/> stopping at the first.</summary>
    public bool Any(Archetype archetype)
    {
        int rows = archetype.Count;
        var slots = archetype.Slots;
        int words = (rows + 63) >> 6;
        for (int w = NextWord(archetype, 0, words); w < words; w = NextWord(archetype, w + 1, words))
        {
            ulong bits = Word(archetype, w) & RowMask(w, rows);
            if (bits == 0) w = NextPassing(archetype, w + 1, words) - 1; // words without a passing row
            for (; bits != 0; bits &= bits - 1)
                if (slots[(w << 6) + BitOperations.TrailingZeroCount(bits)].Index >= 0) return true;
        }

        return false;
    }

    /// <summary>
    /// Next run of consecutive alive rows that pass, at or after <paramref name="start"/> among the first
    /// <paramref name="count"/> rows (batch iteration): false when there is none.
    /// </summary>
    public bool NextRun(Archetype archetype, ref int start, int count, out int length)
    {
        var slots = archetype.Slots;
        // First passing alive row: skip 64 rows per word that has none.
        while (true)
        {
            if (start >= count)
            {
                length = 0;
                return false;
            }

            int w = start >> 6;
            int next = NextWord(archetype, w, (count + 63) >> 6); // unwritten words of a Changed filter
            if (next != w)
            {
                start = next << 6;
                continue;
            }

            ulong bits = Word(archetype, w) & RowMask(w, count) & (ulong.MaxValue << start); // shift is mod 64
            if (bits == 0)
            {
                start = NextPassing(archetype, w + 1, (count + 63) >> 6) << 6;
                continue;
            }

            start = (w << 6) + BitOperations.TrailingZeroCount(bits);
            if (slots[start].Index >= 0) break;
            start++;
        }

        // Its end: the first row that fails, then cut at the first tombstone (vectorized).
        int end = start + 1;
        while (end < count)
        {
            int w = end >> 6;
            ulong fails = ~Word(archetype, w) & (ulong.MaxValue << end);
            if (fails != 0)
            {
                end = (w << 6) + BitOperations.TrailingZeroCount(fails);
                break;
            }

            end = WholeEnd(archetype, w + 1, count >> 6) << 6; // the whole words that follow
        }

        end = Math.Min(end, count);
        if (archetype.Count == archetype.AliveCount) // no tombstone: the scan would read every row's slot again
        {
            length = end - start;
            return true;
        }

        int tombstone = archetype.Rows64(end).Slice(start).IndexOfAnyInRange(long.MinValue, -1);
        length = tombstone < 0 ? end - start : tombstone;
        return true;
    }

    /// <summary>The bits of word <paramref name="word"/> that are below <paramref name="rows"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong RowMask(int word, int rows)
    {
        int left = rows - (word << 6);
        return left >= 64 ? ulong.MaxValue : (1UL << left) - 1;
    }
}
/// <summary>
/// Where a query walk stands after a <see cref="QueryWalk"/> call, returned by value: the enumerator copies it into its
/// own fields and is never passed by reference, so the JIT keeps its fields in registers in the caller's loop.
/// </summary>
internal ref struct QueryStep
{
    /// <summary>The walk's ticket (<see cref="QueryWalk.Ticket"/>); <see cref="QueryWalk.Ended"/> once it ended.</summary>
    public int Ticket;
    public QueryWalk Walk;
    public ref EntityId Rows; // the current table's entities
    public Archetype Table;     // the current table; null: no more rows
    public int Slot, Storage;
    public int Count; // rows to walk; negative when they must be tested (the sign spares the loop a mode register)
}

/// <summary>
/// The cold state of one walk over the alive matching rows of an archetype snapshot (the shared core of query
/// enumerators). Each archetype is walked up to the row count it had when the walk started, so entities that enter (or
/// move between) matching archetypes during the walk are not visited; rows that lose their entity or a queried
/// component are skipped. While walking, the world does not reuse tombstone rows (see <see cref="World.Passes"/>);
/// ending the walk (the enumerator's <c>Dispose</c>, or reaching the end) ends the pass. Walks are pooled per thread
/// (one per nesting level once warmed up, so no allocation in steady state); a ticket tells a walk's current use from
/// an enumerator copy of an earlier one.
/// </summary>
internal sealed class QueryWalk
{
    /// <summary>The ticket of an ended walk (0 means not started).</summary>
    public const int Ended = -1;

    [ThreadStatic] private static QueryWalk _free;
    private QueryWalk _next;
    private int _uses;

    private World _world;
    private QueryMatch _match;
    private ulong _since; // the filter's change tick
    private int[] _counts = new int[8]; // row counts of the matching tables at the start of the walk
    private int _archetype;
    private RowFilter _filter;
    private int _count;  // rows of the current table to walk
    private int _mode;   // RowFilter mode of the current table
    private int _word;   // Test mode: the row word _bits holds (-1: none)
    private ulong _bits; // Test mode: the passing rows of word _word (computed once per word, not per row)

    /// <summary>The current use (positive while walking).</summary>
    public int Ticket;

    /// <summary>Table of the current row.</summary>
    public Archetype Current;

    /// <summary>The world walked.</summary>
    public World World => _world;

    /// <summary>
    /// Enumerators with tracked components: the word of the current table whose change-tick summaries were noted at
    /// <see cref="StampTick"/> (rows come in order, so once per word and tick).
    /// </summary>
    public int StampWord;
    public ulong StampTick;

    /// <summary>
    /// Move a walk on from the enumerator's last row <paramref name="slot"/> (or start it, ticket 0): to the first row
    /// of the next tables, or, after a structural change (<paramref name="storage"/> is stale), to the next row of the
    /// current table read afresh. Not inlined: once per table, per structural change and at the end.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static QueryStep Step(QueryWalk walk, int ticket, World world, QueryMatch match, ulong since, int slot,
        int storage)
    {
        if (ticket == 0) return Rent(world, match, since).Advance();
        if (ticket < 0 || walk.Ticket != ticket) return new QueryStep { Ticket = Ended }; // ended (or a stale copy)
        return storage != world.StorageVersion ? walk.Refresh(slot) : walk.Advance();
    }

    /// <summary>End the walk of <paramref name="ticket"/> early (idempotent: an ended walk or a stale copy is ignored).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void End(QueryWalk walk, int ticket)
    {
        if (walk.Ticket == ticket) walk.Finish();
    }

    private static QueryWalk Rent(World world, QueryMatch match, ulong since)
    {
        var walk = _free ?? new QueryWalk();
        _free = walk._next;
        walk._next = null;
        walk._uses = (walk._uses + 1) & int.MaxValue;
        if (walk._uses == 0) walk._uses = 1; // a live ticket is never 0 (Finish sets 0)
        walk.Ticket = walk._uses;
        walk._world = world;
        walk._match = match;
        walk._since = since;
        walk._archetype = -1;
        var archetypes = match.Archetypes;
        if (walk._counts.Length < archetypes.Length) walk._counts = new int[Math.Max(archetypes.Length, walk._counts.Length * 2)];
        for (int i = 0; i < archetypes.Length; i++) walk._counts[i] = archetypes[i].Count;
        world.BeginPass();
        return walk;
    }

    private void Finish()
    {
        var world = _world;
        Ticket = 0;
        Current = null;
        _world = null;   // a pooled walk keeps no world or table alive
        _match = null;
        _filter = default;
        _next = _free;
        _free = this;
        world.EndPass();
    }

    private QueryStep Found(int slot) => new()
    {
        Ticket = Ticket,
        Walk = this,
        Rows = ref MemoryMarshal.GetArrayDataReference(Current.Slots),
        Table = Current,
        Slot = slot,
        Count = _mode == RowFilter.All ? _count : -1,
        Storage = _world.StorageVersion,
    };

    // The caller made a structural change: re-read the presence counts, then go on after the last row it saw.
    private QueryStep Refresh(int slot)
    {
        if (Current != null)
        {
            _mode = _filter.Mode(Current);
            _word = -1; // presence bits may have flipped
            StampWord = -1;
            if (_mode != RowFilter.None)
            {
                int next = _mode == RowFilter.All ? NextAlive(slot + 1) : NextTested(slot);
                if (next >= 0) return Found(next);
            }
        }

        return Advance();
    }

    // The first matching row of the next tables, or the end of the walk.
    private QueryStep Advance()
    {
        var archetypes = _match.Archetypes;
        while (++_archetype < archetypes.Length)
        {
            var archetype = archetypes[_archetype];
            _filter = new RowFilter(_match.Filters[_archetype], _since);
            _mode = _filter.Mode(archetype);
            if (_mode == RowFilter.None || archetype.AliveCount == 0) continue;
            Current = archetype;
            _count = _counts[_archetype];
            _word = -1;
            StampWord = -1;
            int next = _mode == RowFilter.All ? NextAlive(0) : NextTested(-1);
            if (next >= 0) return Found(next);
        }

        Finish();
        return new QueryStep { Ticket = Ended };
    }

    // All mode: the first alive row from slot on, or -1.
    private int NextAlive(int slot)
    {
        var rows = Current.Slots;
        for (; slot < _count; slot++)
            if (rows[slot].Index >= 0) return slot;
        return -1;
    }

    /// <summary>
    /// Test mode: the next alive passing row of the current table after <paramref name="slot"/>, or -1: one row-filter word per 64 rows (words with no passing row, such as unwritten words of a Changed
    /// filter, are skipped through the summaries).
    /// </summary>
    public int NextTested(int slot)
    {
        int count = _count, words = (count + 63) >> 6;
        ref EntityId rows = ref MemoryMarshal.GetArrayDataReference(Current.Slots); // slot < count <= rows
        slot++;
        while (slot < count)
        {
            int w = slot >> 6;
            if (w != _word)
            {
                int next = _filter.NextWord(Current, w, words);
                if (next >= words) break;
                if (next != w) slot = (w = next) << 6;
                _word = w;
                _bits = _filter.Word(Current, w) & RowFilter.RowMask(w, count);
            }

            ulong bits = _bits & (ulong.MaxValue << slot); // shift is mod 64: slot's position in its word
            if (bits == 0)
            {
                slot = _filter.NextPassing(Current, w + 1, words) << 6;
                continue;
            }

            slot = (w << 6) + BitOperations.TrailingZeroCount(bits);
            if (Unsafe.Add(ref rows, slot).Index >= 0) return slot;
            slot++;
        }

        return -1;
    }
}
