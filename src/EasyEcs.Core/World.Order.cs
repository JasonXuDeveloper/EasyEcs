using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EasyEcs;

// Ordered tables ([OrderBy], RowOrder): the cadence that re-sorts them, the sort on request, and the read path of their
// clusters.
public sealed partial class World
{
    // Whether any table of this world was ever ordered: an update looks for due sorts only then.
    private bool _ordered;

    // The orders this world was asked for (OrderRows), by component bit; null until the first request.
    private Func<RowOrder>[] _orderRequests;

    /// <summary>The order this world was asked to keep tables holding component <paramref name="bit"/> in, or null.</summary>
    private Func<RowOrder> RequestedOrder(int bit) =>
        _orderRequests != null && bit < _orderRequests.Length ? _orderRequests[bit] : null;

    /// <summary>
    /// Keep every table holding <typeparamref name="T"/> (those there now and those to come) in the order of
    /// <typeparamref name="TOrder"/>'s key, its boxes over <paramref name="axes"/> axes of its point: what the setup of
    /// a generated pair method calls for the component it compares positions of (the key the Morton code of the
    /// position, <paramref name="cell"/> the cutoff). The rows are sorted at the next update, then every
    /// <paramref name="interval"/> updates; between sorts a row may move <paramref name="margin"/> and stay inside its
    /// cluster's box. A cluster holds <paramref name="clusterSize"/> rows: 0 for the machine's vector of floats (8 where
    /// 256-bit vectors are hardware, 4 on ARM64), or a power of two up to 64. The first request for a component stands; <see cref="OrderByAttribute">[OrderBy]</see> on it
    /// overrides every request; a table holding several ordered components keeps one of their orders. A table
    /// no request or declaration orders pays nothing. Owner thread, between passes (throws during one). O(tables);
    /// allocates the request once.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">An axis count above 3, a cell that is not a finite size above 0, an interval below 1, a margin that is not a finite distance of 0 or more, or a cluster size that is neither 0 nor a power of two up to 64.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void OrderRows<T, TOrder>(int axes, float cell, int interval, float margin, int clusterSize = 0)
        where T : struct, IComponent where TOrder : struct, IRowOrder<T>
    {
        if (_disposed) throw new ObjectDisposedException(nameof(World));
        ThrowIfIterating(nameof(OrderRows));
        RowOrder.Check(axes, cell, interval, margin, clusterSize);
        int bit = ComponentType<T>.Bit;
        _orderRequests ??= [];
        if (bit >= _orderRequests.Length) Array.Resize(ref _orderRequests, Math.Max(bit + 1, _orderRequests.Length * 2));
        if (_orderRequests[bit] != null) return;
        _orderRequests[bit] = () => new RowOrder<T, TOrder>(axes, cell, interval, margin, clusterSize);
        if (ComponentInfo.Of(bit).Defaults?.Order != null) return; // [OrderBy] orders its tables already
        foreach (var table in _allArchetypes)
            if (table.Order == null && table.ColumnIndex(bit) >= 0)
            {
                table.Order = _orderRequests[bit]();
                _ordered = true;
                _orderedFrom = null; // the list of ordered tables is read again
            }
    }

    // The ordered tables, rebuilt when the registry changes (_orderedFrom is the registry they were read from).
    private Archetype[] _orderedTables = [];
    private Archetype[] _orderedFrom;

    /// <summary>The tables with an order (<see cref="Archetype.Order"/>), read again only after a table was added or dropped.</summary>
    private Archetype[] OrderedTableList()
    {
        var all = _allArchetypes;
        if (ReferenceEquals(all, _orderedFrom)) return _orderedTables;
        int count = 0;
        foreach (var table in all)
            if (table.Order != null) count++;
        var ordered = new Archetype[count];
        count = 0;
        foreach (var table in all)
            if (table.Order != null) ordered[count++] = table;
        _orderedTables = ordered;
        _orderedFrom = all;
        return ordered;
    }

    /// <summary>
    /// Update <paramref name="update"/> of the default group starts: sort the ordered tables whose interval came round
    /// since their last sort, and those restored or created since. O(ordered tables) when none is due.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void SortDue(long update)
    {
        var tables = OrderedTableList();
        for (int i = 0; i < tables.Length; i++)
        {
            var order = tables[i].Order;
            if (order.Stale || update - order.SortedAt >= order.Interval) Sort(tables[i], update);
        }
    }

    /// <summary>Sort <paramref name="table"/> now (a sort of an empty table only draws no box). Outside passes.</summary>
    private void Sort(Archetype table, long update)
    {
        using (Structural())
        {
            if (table.Count != table.AliveCount) FillDirtyTables(); // a pass an exception interrupted left tombstones
            if (table.Order.Sort(table, Meta)) StorageVersion++; // rows moved: passes and marks see it
            table.Order.SortedAt = update;
        }
    }

    /// <summary>
    /// Sort every table holding <typeparamref name="T"/> by its key now, whatever their cadence: the rows in key order,
    /// the boxes of their clusters drawn again (an order inferred for a pair method, <see cref="OrderRows{T, TOrder}"/>, or
    /// <see cref="OrderByAttribute">[OrderBy]</see>). The next sort of each table comes its interval after this one. For a
    /// component no table is ordered by it does nothing. Rows move, so refs
    /// into the tables, handles' cached rows and marked rows end here, as at any structural change. Owner thread only,
    /// between passes (throws during one). O(rows × (key bytes + columns)) of the tables that were out of order, O(rows)
    /// for the others; allocation-free in steady state.
    /// </summary>
    public void SortRows<T>() where T : struct, IComponent
    {
        if (_disposed) throw new ObjectDisposedException(nameof(World));
        ThrowIfIterating(nameof(SortRows));
        if (!_ordered) return;
        var tables = OrderedTableList();
        int bit = ComponentType<T>.Bit;
        for (int i = 0; i < tables.Length; i++)
            if (tables[i].Order.Bit == bit) Sort(tables[i], DefaultUpdateCount);
    }

    /// <summary>
    /// The clusters of every table ordered by <typeparamref name="T"/> (an inferred order or
    /// <see cref="OrderByAttribute">[OrderBy]</see>), one <see cref="RowClusters"/> per table:
    /// <c>foreach (var clusters in world.Clusters&lt;Position&gt;())</c>. None for a component no table is ordered by. A
    /// search over the boxes finds the rows near a point or a box without visiting
    /// the others. O(1) to start, O(tables) to walk; allocation-free.
    /// </summary>
    public OrderedTables Clusters<T>() where T : struct, IComponent =>
        new(_ordered ? OrderedTableList() : [], ComponentType<T>.Bit, this);

    /// <summary>
    /// The tables ordered by one component (<see cref="Clusters{T}"/>), each as its <see cref="RowClusters"/>. Walk it with
    /// <c>foreach</c>; valid until the next structural change.
    /// </summary>
    public readonly ref struct OrderedTables
    {
        private readonly Archetype[] _tables;
        private readonly int _bit;
        private readonly World _world;

        internal OrderedTables(Archetype[] tables, int bit, World world)
        {
            _tables = tables;
            _bit = bit;
            _world = world;
        }

        /// <summary>The enumerator over the tables.</summary>
        public Enumerator GetEnumerator() => new(_tables, _bit, _world);

        /// <summary>Walks the tables ordered by the component.</summary>
        public ref struct Enumerator
        {
            private readonly Archetype[] _tables;
            private readonly int _bit;
            private readonly World _world;
            private int _index;

            internal Enumerator(Archetype[] tables, int bit, World world)
            {
                _tables = tables;
                _bit = bit;
                _world = world;
                _index = -1;
            }

            /// <summary>Move to the next table ordered by the component.</summary>
            public bool MoveNext()
            {
                while (++_index < _tables.Length)
                    if (_tables[_index].Order.Bit == _bit) return true;
                return false;
            }

            /// <summary>The clusters of the current table.</summary>
            public readonly RowClusters Current => new(_world, _tables[_index]);
        }
    }
}
