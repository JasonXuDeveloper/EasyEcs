using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EasyEcs;

/// <summary>
/// The clusters of one ordered table (an order inferred for a pair method, or <see cref="OrderByAttribute">[OrderBy]</see>):
/// its rows in key order, cut into
/// clusters of <see cref="Size"/> rows, each with an axis-aligned box over the declared point of its rows, widened by
/// the margin. What a search over space reads: a box far from the query skips its rows at once, a near one is read
/// row by row (or a cluster per vector) from the table's columns (<see cref="Read{T}"/>). From
/// <see cref="World.Clusters{T}"/> (every table ordered by a component) or <see cref="EntitySpan.Clusters"/> (the table of a
/// batch method's run of rows, whose row 0 is <see cref="EntitySpan.FirstRow"/>).
/// <para>
/// The boxes are drawn by the sort and hold until the next one as long as no row moved farther than the margin: rows
/// [0, <see cref="SortedRows"/>) are in clusters; rows [<see cref="SortedRows"/>, <see cref="RowCount"/>) joined the table
/// since and are in none (a search reads them one by one, or waits for the next sort). A row that moved into a cluster's
/// row since the sort widened that cluster's box. Inside a pass a row destroyed or moved away by the pass is a
/// tombstone: its id's index is negative (<see cref="Ids"/>). A row of a cluster may lack the component since the sort
/// (<see cref="Has"/>). Valid until the next structural change of the world (a ref struct over the table's arrays).
/// </para>
/// </summary>
public readonly ref struct RowClusters
{
    private readonly World _world;
    private readonly Archetype _table;
    private readonly RowOrder _order;

    internal RowClusters(World world, Archetype table)
    {
        _world = world;
        _table = table;
        _order = table?.Order;
    }

    /// <summary>
    /// Rows per cluster (the last cluster of <see cref="SortedRows"/> may hold fewer): the order's, by default a vector of
    /// floats, 8 where 256-bit vectors are hardware and 4 otherwise (ARM64). O(1).
    /// </summary>
    public int Size => _order?.ClusterSize ?? RowOrder.DefaultClusterSize;

    /// <summary>Clusters: <c>ceil(SortedRows / Size)</c>; 0 for a table without an order. O(1).</summary>
    public int Count => _order?.ClusterCount ?? 0;

    /// <summary>Rows in clusters, from row 0. O(1).</summary>
    public int SortedRows => _order?.SortedRows ?? 0;

    /// <summary>Rows of the table, tombstones of a pass included: those from <see cref="SortedRows"/> on are in no cluster. O(1).</summary>
    public int RowCount => _table?.Count ?? 0;

    /// <summary>Axes the boxes cover (0 to 3: the members the declaration names). O(1).</summary>
    public int Axes => _order?.Axes ?? 0;

    /// <summary>What every box was widened by on each side (the declaration's margin). O(1).</summary>
    public float Margin => _order?.Margin ?? 0;

    /// <summary>
    /// Changes whenever the clusters do (a sort, a widened box, fewer rows) and differs between tables: a cache built from
    /// the clusters (a list of cluster pairs) is valid while it holds. O(1).
    /// </summary>
    public long Version => _order?.Version ?? 0;

    /// <summary>The first row of cluster <paramref name="cluster"/>: <c>cluster * Size</c>. O(1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Start(int cluster) => cluster * Size;

    /// <summary>The row after the last of cluster <paramref name="cluster"/>: at most <see cref="SortedRows"/>. O(1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int End(int cluster) => Math.Min(cluster * Size + Size, SortedRows);

    /// <summary>The low side of every cluster's box on axis <paramref name="axis"/>, one float per cluster. O(1).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="axis"/> is not below <see cref="Axes"/>.</exception>
    public ReadOnlySpan<float> Min(int axis) => Side(axis, false);

    /// <summary>The high side of every cluster's box on axis <paramref name="axis"/>, one float per cluster. O(1).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="axis"/> is not below <see cref="Axes"/>.</exception>
    public ReadOnlySpan<float> Max(int axis) => Side(axis, true);

    private ReadOnlySpan<float> Side(int axis, bool max)
    {
        if ((uint)axis >= (uint)Axes) ThrowAxis(axis, Axes);
        return _order.Side(axis, max);
    }

    /// <summary>
    /// The table's column of <typeparamref name="T"/>, every row (<see cref="RowCount"/>), read-only: row <c>r</c> of a
    /// cluster is element <c>r</c>. A system that reads it declares the read like any other (a parameter of the component
    /// or a <see cref="Lookup{T}"/>), so no writer of it shares its stage. O(1).
    /// </summary>
    /// <exception cref="InvalidOperationException">The table has no column of <typeparamref name="T"/>.</exception>
    public ReadOnlySpan<T> Read<T>() where T : struct, IComponent
    {
        int column = _table == null ? -1 : _table.ColumnIndex(ComponentType<T>.Bit);
        if (column < 0) ThrowColumn(typeof(T));
        return MemoryMarshal.CreateReadOnlySpan(ref _table.Data<T>(column), _table.Count);
    }

    /// <summary>The ids of the table's rows (<see cref="RowCount"/>); a tombstone of a pass has a negative index. O(1).</summary>
    public ReadOnlySpan<EntityId> Ids => _table == null ? default : _table.Slots.AsSpan(0, _table.Count);

    /// <summary>The entity of row <paramref name="row"/> (a row below <see cref="RowCount"/> that is no tombstone). O(1).</summary>
    public Entity this[int row] => new(_world, _table.Slots[row]);

    /// <summary>
    /// Whether row <paramref name="row"/> has the ordered component: a row removed from it or disabled since the sort
    /// stays in its cluster without it. O(1).
    /// </summary>
    public bool Has(int row) => _order != null && _table.Has(_table.ColumnIndex(_order.Bit), row);

    /// <summary>Whether every row of the table has the ordered component (no row needs <see cref="Has"/>). O(1).</summary>
    public bool AllHave => _order != null && _table.Absent[_table.ColumnIndex(_order.Bit)] == 0;

    /// <summary>
    /// The pairs of this table's clusters whose boxes come within <paramref name="cutoff"/> of each other on every axis:
    /// the candidates of a search for the rows within <paramref name="cutoff"/> of each other, which a kernel then reads
    /// <see cref="Size"/> × <see cref="Size"/> rows per pair (every other pair of rows is farther apart). Each cluster is
    /// paired with itself (a cluster whose rows all lack the component with none), and each other pair is written once,
    /// smaller cluster first. Written to
    /// <paramref name="destination"/> as far as it holds; returns how many there are, so a caller whose buffer was
    /// short grows it and calls again. A sweep over the clusters sorted by their low side on axis 0: O(clusters × log clusters
    /// + candidates). The sort's buffers come from the shared array pools: allocation-free in steady state, and safe on
    /// several threads at once (it changes nothing).
    /// </summary>
    /// <param name="cutoff">The distance, on each axis, beyond which two rows never interact: 0 or more.</param>
    /// <param name="destination">Where the pairs go.</param>
    /// <returns>The number of pairs, which may exceed the length of <paramref name="destination"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cutoff"/> is negative, infinite or NaN.</exception>
    /// <exception cref="InvalidOperationException">The boxes cover no axis.</exception>
    public int Pairs(float cutoff, Span<ClusterPair> destination) => Find(this, this, true, cutoff, destination);

    /// <summary>
    /// The pairs of a cluster of this table and a cluster of <paramref name="other"/> (another table ordered by the same
    /// component, say of another group) whose boxes come within <paramref name="cutoff"/> of each other on every axis,
    /// this table's cluster first, as <see cref="Pairs(float, Span{ClusterPair})"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The boxes cover no axis, or the tables' boxes cover different axes.</exception>
    /// <inheritdoc cref="Pairs(float, Span{ClusterPair})"/>
    public int Pairs(RowClusters other, float cutoff, Span<ClusterPair> destination) => Find(this, other, false, cutoff, destination);

    // The cluster boxes of one table, axis by axis (unused axes empty).
    private readonly ref struct Sides(RowClusters clusters)
    {
        public readonly ReadOnlySpan<float> Min0 = clusters.Min(0), Max0 = clusters.Max(0);
        public readonly ReadOnlySpan<float> Min1 = clusters.Axes > 1 ? clusters.Min(1) : default, Max1 = clusters.Axes > 1 ? clusters.Max(1) : default;
        public readonly ReadOnlySpan<float> Min2 = clusters.Axes > 2 ? clusters.Min(2) : default, Max2 = clusters.Axes > 2 ? clusters.Max(2) : default;
    }

    // The pairs of clusters of a and b (the same table when self) whose boxes overlap once widened by cutoff: the clusters'
    // low sides on axis 0 sorted (a's clusters as their index, b's as its complement), the other sides gathered into
    // that order (high sides widened by cutoff once), then for each cluster every cluster after it in that order whose
    // low side is within its high side plus cutoff, tested on the other axes. The inner loop reads five arrays in
    // order: no table lookup, no copy of a table's sides per candidate.
    private static int Find(RowClusters a, RowClusters b, bool self, float cutoff, Span<ClusterPair> into)
    {
        if (!(cutoff >= 0) || float.IsPositiveInfinity(cutoff)) ThrowCutoff(cutoff);
        int axes = a.Axes;
        if (axes == 0 || b.Axes != axes) ThrowAxes(axes, b.Axes);
        var sa = new Sides(a);
        int na = a.Count, nb = self ? 0 : b.Count;
        if (na + nb == 0) return 0;
        int n = na + nb;
        var keys = ArrayPool<float>.Shared.Rent(n);
        var ids = ArrayPool<int>.Shared.Rent(n);
        int m = 0;
        for (int i = 0; i < na; i++)
            if (sa.Min0[i] <= sa.Max0[i]) // an inverted box holds no row
            {
                keys[m] = sa.Min0[i];
                ids[m++] = i;
            }

        if (!self)
        {
            var sb = new Sides(b);
            for (int i = 0; i < nb; i++)
                if (sb.Min0[i] <= sb.Max0[i])
                {
                    keys[m] = sb.Min0[i];
                    ids[m++] = ~i;
                }
        }

        Array.Sort(keys, ids, 0, m);
        // The sides in the sorted order: high0 + cutoff, then low and high + cutoff of axes 1 and 2 (0 where unused).
        var sides = ArrayPool<float>.Shared.Rent(5 * m);
        Span<float> high0 = sides.AsSpan(0, m), low1 = sides.AsSpan(m, m), high1 = sides.AsSpan(2 * m, m),
            low2 = sides.AsSpan(3 * m, m), high2 = sides.AsSpan(4 * m, m);
        Gather(new Sides(a), self ? new Sides(a) : new Sides(b), ids.AsSpan(0, m), axes, cutoff, high0, low1, high1, low2, high2);
        int found = 0;
        for (int i = 0; i < m; i++)
        {
            int p = ids[i];
            if (self) Add(into, ref found, p, p);
            float high = high0[i], l1 = low1[i], h1 = high1[i], l2 = low2[i], h2 = high2[i];
            for (int j = i + 1; j < m && keys[j] <= high; j++)
            {
                if (low1[j] > h1 || l1 > high1[j] || low2[j] > h2 || l2 > high2[j]) continue;
                int q = ids[j];
                if (self) Add(into, ref found, Math.Min(p, q), Math.Max(p, q));
                else if ((q >= 0) == (p >= 0)) continue; // two clusters of one table
                else if (p >= 0) Add(into, ref found, p, ~q);
                else Add(into, ref found, q, ~p);
            }
        }

        ArrayPool<float>.Shared.Return(sides);
        ArrayPool<float>.Shared.Return(keys);
        ArrayPool<int>.Shared.Return(ids);
        return found;
    }

    // The sides of the clusters ids name (a's as their index, b's as its complement) in that order; the high sides plus
    // cutoff. Axes a table's boxes do not cover are 0 on both sides, so their test always passes.
    private static void Gather(Sides a, Sides b, ReadOnlySpan<int> ids, int axes, float cutoff, Span<float> high0,
        Span<float> low1, Span<float> high1, Span<float> low2, Span<float> high2)
    {
        for (int k = 0; k < ids.Length; k++)
        {
            int id = ids[k];
            var s = id >= 0 ? a : b;
            int i = id >= 0 ? id : ~id;
            high0[k] = s.Max0[i] + cutoff;
            low1[k] = axes > 1 ? s.Min1[i] : 0;
            high1[k] = axes > 1 ? s.Max1[i] + cutoff : cutoff;
            low2[k] = axes > 2 ? s.Min2[i] : 0;
            high2[k] = axes > 2 ? s.Max2[i] + cutoff : cutoff;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Add(Span<ClusterPair> into, ref int found, int first, int second)
    {
        if ((uint)found < (uint)into.Length) into[found] = new ClusterPair(first, second);
        found++;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowCutoff(float cutoff) =>
        throw new ArgumentOutOfRangeException(nameof(cutoff), cutoff, "The cutoff is a finite distance, 0 or more");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowAxes(int axes, int other) =>
        throw new InvalidOperationException(axes == 0 ? "The boxes cover no axis" : $"The boxes cover {axes} and {other} axes");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowAxis(int axis, int axes) =>
        throw new ArgumentOutOfRangeException(nameof(axis), axis, $"The boxes cover {axes} axes");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowColumn(Type type) => throw new InvalidOperationException($"The table has no {type.Name} column");
}

/// <summary>
/// Two clusters whose boxes are near (<see cref="RowClusters.Pairs(float, Span{ClusterPair})"/>): the rows of
/// <see cref="First"/> against the rows of <see cref="Second"/>, which may be the same cluster.
/// </summary>
/// <param name="First">A cluster of the table whose <see cref="RowClusters.Pairs(float, Span{ClusterPair})"/> found the pair.</param>
/// <param name="Second">A cluster of the same table (not below <paramref name="First"/>), or of the other one.</param>
public readonly record struct ClusterPair(int First, int Second);
