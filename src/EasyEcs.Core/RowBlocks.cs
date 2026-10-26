using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EasyEcs;

/// <summary>
/// The blocks of one ordered table (an order inferred for a pair method, or <see cref="OrderByAttribute">[OrderBy]</see>):
/// its rows in key order, cut into
/// blocks of <see cref="Size"/> rows, each with an axis-aligned box over the declared point of its rows, widened by
/// the margin. What a search over space reads: a box far from the query skips its rows at once, a near one is read
/// row by row (or a block per vector) from the table's columns (<see cref="Read{T}"/>). From
/// <see cref="World.Blocks{T}"/> (every table ordered by a component) or <see cref="EntitySpan.Blocks"/> (the table of a
/// batch method's run of rows, whose row 0 is <see cref="EntitySpan.FirstRow"/>).
/// <para>
/// The boxes are drawn by the sort and hold until the next one as long as no row moved farther than the margin: rows
/// [0, <see cref="SortedRows"/>) are in blocks; rows [<see cref="SortedRows"/>, <see cref="Rows"/>) joined the table
/// since and are in none (a search reads them one by one, or waits for the next sort). A row that moved into a block's
/// row since the sort widened that block's box. Inside a pass a row destroyed or moved away by the pass is a
/// tombstone: its id's index is negative (<see cref="Ids"/>). A row of a block may lack the component since the sort
/// (<see cref="Has"/>). Valid until the next structural change of the world (a ref struct over the table's arrays).
/// </para>
/// </summary>
public readonly ref struct RowBlocks
{
    private readonly World _world;
    private readonly Archetype _table;
    private readonly RowOrder _order;

    internal RowBlocks(World world, Archetype table)
    {
        _world = world;
        _table = table;
        _order = table?.Order;
    }

    /// <summary>
    /// Rows per block (the last block of <see cref="SortedRows"/> may hold fewer): the order's, by default a vector of
    /// floats, 8 where 256-bit vectors are hardware and 4 otherwise (ARM64). O(1).
    /// </summary>
    public int Size => _order?.BlockRows ?? RowOrder.DefaultBlockRows;

    /// <summary>Blocks: <c>ceil(SortedRows / Size)</c>; 0 for a table without an order. O(1).</summary>
    public int Count => _order?.BlockCount ?? 0;

    /// <summary>Rows in blocks, from row 0. O(1).</summary>
    public int SortedRows => _order?.SortedRows ?? 0;

    /// <summary>Rows of the table, tombstones of a pass included: those from <see cref="SortedRows"/> on are in no block. O(1).</summary>
    public int Rows => _table?.Count ?? 0;

    /// <summary>Axes the boxes cover (0 to 3: the members the declaration names). O(1).</summary>
    public int Axes => _order?.Axes ?? 0;

    /// <summary>What every box was widened by on each side (the declaration's margin). O(1).</summary>
    public float Margin => _order?.Margin ?? 0;

    /// <summary>
    /// Changes whenever the blocks do (a sort, a widened box, fewer rows) and differs between tables: a cache built from
    /// the blocks (a list of block pairs) is valid while it holds. O(1).
    /// </summary>
    public long Version => _order?.Version ?? 0;

    /// <summary>The first row of block <paramref name="block"/>: <c>block * Size</c>. O(1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Start(int block) => block * Size;

    /// <summary>The row after the last of block <paramref name="block"/>: at most <see cref="SortedRows"/>. O(1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int End(int block) => Math.Min(block * Size + Size, SortedRows);

    /// <summary>The low side of every block's box on axis <paramref name="axis"/>, one float per block. O(1).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="axis"/> is not below <see cref="Axes"/>.</exception>
    public ReadOnlySpan<float> Min(int axis) => Side(axis, false);

    /// <summary>The high side of every block's box on axis <paramref name="axis"/>, one float per block. O(1).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="axis"/> is not below <see cref="Axes"/>.</exception>
    public ReadOnlySpan<float> Max(int axis) => Side(axis, true);

    private ReadOnlySpan<float> Side(int axis, bool max)
    {
        if ((uint)axis >= (uint)Axes) ThrowAxis(axis, Axes);
        return _order.Side(axis, max);
    }

    /// <summary>
    /// The table's column of <typeparamref name="T"/>, every row (<see cref="Rows"/>), read-only: row <c>r</c> of a
    /// block is element <c>r</c>. A system that reads it declares the read like any other (a parameter of the component
    /// or a <see cref="Lookup{T}"/>), so no writer of it shares its stage. O(1).
    /// </summary>
    /// <exception cref="InvalidOperationException">The table has no column of <typeparamref name="T"/>.</exception>
    public ReadOnlySpan<T> Read<T>() where T : struct, IComponent
    {
        int column = _table == null ? -1 : _table.ColumnIndex(ComponentType<T>.Bit);
        if (column < 0) ThrowColumn(typeof(T));
        return MemoryMarshal.CreateReadOnlySpan(ref _table.Data<T>(column), _table.Count);
    }

    /// <summary>The ids of the table's rows (<see cref="Rows"/>); a tombstone of a pass has a negative index. O(1).</summary>
    public ReadOnlySpan<EntityId> Ids => _table == null ? default : _table.Slots.AsSpan(0, _table.Count);

    /// <summary>The entity of row <paramref name="row"/> (a row below <see cref="Rows"/> that is no tombstone). O(1).</summary>
    public Entity this[int row] => new(_world, _table.Slots[row]);

    /// <summary>
    /// Whether row <paramref name="row"/> has the ordered component: a row removed from it or disabled since the sort
    /// stays in its block without it. O(1).
    /// </summary>
    public bool Has(int row) => _order != null && _table.Has(_table.ColumnIndex(_order.Bit), row);

    /// <summary>Whether every row of the table has the ordered component (no row needs <see cref="Has"/>). O(1).</summary>
    public bool AllHave => _order != null && _table.Absent[_table.ColumnIndex(_order.Bit)] == 0;

    /// <summary>
    /// The pairs of this table's blocks whose boxes come within <paramref name="reach"/> of each other on every axis:
    /// the candidates of a search for the rows within <paramref name="reach"/> of each other, which a kernel then reads
    /// <see cref="Size"/> × <see cref="Size"/> rows per pair (every other pair of rows is farther apart). Each block is
    /// paired with itself (a block whose rows all lack the component with none), and each other pair is written once,
    /// smaller block first. Written to
    /// <paramref name="destination"/> as far as it holds; returns how many there are, so a caller whose buffer was
    /// short grows it and calls again. A sweep over the blocks sorted by their low side on axis 0: O(blocks × log blocks
    /// + candidates). The sort's buffers come from the shared array pools: allocation-free in steady state, and safe on
    /// several threads at once (it changes nothing).
    /// </summary>
    /// <param name="reach">The distance, on each axis, beyond which two rows never interact: 0 or more.</param>
    /// <param name="destination">Where the pairs go.</param>
    /// <returns>The number of pairs, which may exceed the length of <paramref name="destination"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="reach"/> is negative, infinite or NaN.</exception>
    /// <exception cref="InvalidOperationException">The boxes cover no axis.</exception>
    public int Pairs(float reach, Span<BlockPair> destination) => Find(this, this, true, reach, destination);

    /// <summary>
    /// The pairs of a block of this table and a block of <paramref name="other"/> (another table ordered by the same
    /// component, say of another group) whose boxes come within <paramref name="reach"/> of each other on every axis,
    /// this table's block first, as <see cref="Pairs(float, Span{BlockPair})"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The boxes cover no axis, or the tables' boxes cover different axes.</exception>
    /// <inheritdoc cref="Pairs(float, Span{BlockPair})"/>
    public int Pairs(RowBlocks other, float reach, Span<BlockPair> destination) => Find(this, other, false, reach, destination);

    // The block boxes of one table, axis by axis (unused axes empty).
    private readonly ref struct Sides(RowBlocks blocks)
    {
        public readonly ReadOnlySpan<float> Min0 = blocks.Min(0), Max0 = blocks.Max(0);
        public readonly ReadOnlySpan<float> Min1 = blocks.Axes > 1 ? blocks.Min(1) : default, Max1 = blocks.Axes > 1 ? blocks.Max(1) : default;
        public readonly ReadOnlySpan<float> Min2 = blocks.Axes > 2 ? blocks.Min(2) : default, Max2 = blocks.Axes > 2 ? blocks.Max(2) : default;
    }

    // The pairs of blocks of a and b (the same table when self) whose boxes overlap once widened by reach: the blocks'
    // low sides on axis 0 sorted (a's blocks as their index, b's as its complement), then for each block every block
    // after it in that order whose low side is within its high side plus reach, tested on the other axes.
    private static int Find(RowBlocks a, RowBlocks b, bool self, float reach, Span<BlockPair> into)
    {
        if (!(reach >= 0) || float.IsPositiveInfinity(reach)) ThrowReach(reach);
        int axes = a.Axes;
        if (axes == 0 || b.Axes != axes) ThrowAxes(axes, b.Axes);
        var sa = new Sides(a);
        var sb = self ? sa : new Sides(b);
        int na = a.Count, nb = self ? 0 : b.Count;
        if (na + nb == 0) return 0;
        var keys = ArrayPool<float>.Shared.Rent(na + nb);
        var ids = ArrayPool<int>.Shared.Rent(na + nb);
        int m = 0;
        for (int i = 0; i < na; i++)
            if (sa.Min0[i] <= sa.Max0[i]) // an inverted box holds no row
            {
                keys[m] = sa.Min0[i];
                ids[m++] = i;
            }

        for (int i = 0; i < nb; i++)
            if (sb.Min0[i] <= sb.Max0[i])
            {
                keys[m] = sb.Min0[i];
                ids[m++] = ~i;
            }

        Array.Sort(keys, ids, 0, m);
        int found = 0;
        for (int i = 0; i < m; i++)
        {
            int p = ids[i], pi = p >= 0 ? p : ~p;
            var sp = p >= 0 ? sa : sb;
            if (self) Add(into, ref found, pi, pi);
            float high = sp.Max0[pi] + reach;
            for (int j = i + 1; j < m && keys[j] <= high; j++)
            {
                int q = ids[j];
                if (!self && (q >= 0) == (p >= 0)) continue; // two blocks of one table
                int qi = q >= 0 ? q : ~q;
                var sq = q >= 0 ? sa : sb;
                if (axes > 1 && (sp.Min1[pi] > sq.Max1[qi] + reach || sq.Min1[qi] > sp.Max1[pi] + reach)) continue;
                if (axes > 2 && (sp.Min2[pi] > sq.Max2[qi] + reach || sq.Min2[qi] > sp.Max2[pi] + reach)) continue;
                if (self) Add(into, ref found, Math.Min(pi, qi), Math.Max(pi, qi));
                else if (p >= 0) Add(into, ref found, pi, qi);
                else Add(into, ref found, qi, pi);
            }
        }

        ArrayPool<float>.Shared.Return(keys);
        ArrayPool<int>.Shared.Return(ids);
        return found;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Add(Span<BlockPair> into, ref int found, int first, int second)
    {
        if ((uint)found < (uint)into.Length) into[found] = new BlockPair(first, second);
        found++;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowReach(float reach) =>
        throw new ArgumentOutOfRangeException(nameof(reach), reach, "The reach is a finite distance, 0 or more");

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
/// Two blocks whose boxes are near (<see cref="RowBlocks.Pairs(float, Span{BlockPair})"/>): the rows of
/// <see cref="First"/> against the rows of <see cref="Second"/>, which may be the same block.
/// </summary>
/// <param name="First">A block of the table whose <see cref="RowBlocks.Pairs(float, Span{BlockPair})"/> found the pair.</param>
/// <param name="Second">A block of the same table (not below <paramref name="First"/>), or of the other one.</param>
public readonly record struct BlockPair(int First, int Second);
