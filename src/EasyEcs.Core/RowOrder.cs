using System;
using System.Buffers;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Threading;

namespace EasyEcs;

/// <summary>
/// A component whose tables keep their rows in the order of <see cref="OrderKey"/>, with a box per block of rows (8, or 4
/// on ARM64) over <see cref="BoxPoint"/> (<see cref="RowBlocks"/>). Declared with
/// <see cref="OrderByAttribute">[OrderBy]</see> on the key member, which makes the generator write both members
/// explicitly and register the order for every world: the interface alone orders nothing.
/// </summary>
/// <typeparam name="TKey">
/// The key: an integer type, <c>char</c>, <c>bool</c>, <c>float</c>, <c>double</c> or an enum (a radix sort orders its
/// bits). A spatial order is a Morton or Hilbert code of a quantized position, computed by the component.
/// </typeparam>
public interface IOrderKey<TKey> : IComponent where TKey : unmanaged
{
    /// <summary>The key rows are ordered by, ascending (rows with equal keys keep their order).</summary>
    TKey OrderKey { get; }

    /// <summary>The point the box of a block covers: up to three axes (the declaration's), the others 0.</summary>
    Vector3 BoxPoint { get; }
}

/// <summary>
/// How an order reads a row of <typeparamref name="T"/>: its key (rows ascending by it) and the point the boxes of
/// blocks cover. Written by the generator: for a pair method's distance test (the Morton code of the position,
/// <see cref="RowKeys.Morton"/>, quantized by the cutoff) and for <see cref="OrderByAttribute">[OrderBy]</see>; the
/// world asks for the order with <see cref="World.OrderRows{T, TOrder}"/>. Static members, so the sort calls them
/// without a delegate.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IRowOrder<T> where T : struct, IComponent
{
    /// <summary>The low bytes of <see cref="Key"/> that can differ (1 to 8): the sort's passes.</summary>
    static abstract int KeyBytes { get; }

    /// <summary>The row's key as an unsigned integer that orders like it (<see cref="RowKeys"/>), given the order's cell.</summary>
    static abstract ulong Key(in T row, float cell);

    /// <summary>The point the box of the row's block covers (up to three axes, the others 0).</summary>
    static abstract Vector3 Point(in T row);
}

/// <summary>Keys of generated orders (<see cref="IRowOrder{T}"/>).</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class RowKeys
{
    /// <summary>
    /// The Morton code (Z-order) of <paramref name="point"/>'s first <paramref name="axes"/> axes (1 to 3), each
    /// quantized to cells of <paramref name="cell"/>: rows near each other in space get near keys, so a sorted table's
    /// blocks of 8 rows are compact boxes. 63, 32 or 21 bits per axis (clamped past them; NaN is cell 0). O(1).
    /// </summary>
    public static ulong Morton(Vector3 point, int axes, float cell)
    {
        float scale = 1 / cell;
        switch (axes)
        {
            case 1: return Quantize(point.X * scale, 63);
            case 2: return Spread2(Quantize(point.X * scale, 32)) | Spread2(Quantize(point.Y * scale, 32)) << 1;
            default:
                return Spread3(Quantize(point.X * scale, 21)) | Spread3(Quantize(point.Y * scale, 21)) << 1
                    | Spread3(Quantize(point.Z * scale, 21)) << 2;
        }
    }

    /// <summary>The bits of any supported key (<see cref="OrderByAttribute">[OrderBy]</see>) as an unsigned integer that orders like it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Of<TKey>(TKey key) where TKey : unmanaged => OrderBits<TKey>.Of(key);

    // floor(v) as a signed integer of `bits` bits (at most 63; clamped in integers, as a double cannot hold 2^62 - 1),
    // offset to unsigned: cells order as their coordinates.
    private static ulong Quantize(float v, int bits)
    {
        long half = 1L << (bits - 1);
        double f = Math.Floor((double)v);
        long q = double.IsNaN(f) ? 0 : f <= -half ? -half : f >= half ? half - 1 : (long)f;
        return (ulong)(q + half);
    }

    // The low 32 bits spread to the even bits.
    private static ulong Spread2(ulong v)
    {
        v &= 0xFFFF_FFFF;
        v = (v | v << 16) & 0x0000_FFFF_0000_FFFF;
        v = (v | v << 8) & 0x00FF_00FF_00FF_00FF;
        v = (v | v << 4) & 0x0F0F_0F0F_0F0F_0F0F;
        v = (v | v << 2) & 0x3333_3333_3333_3333;
        return (v | v << 1) & 0x5555_5555_5555_5555;
    }

    // The low 21 bits spread to every third bit.
    private static ulong Spread3(ulong v)
    {
        v &= 0x1F_FFFF;
        v = (v | v << 32) & 0x1F_0000_0000_FFFF;
        v = (v | v << 16) & 0x1F_0000_FF00_00FF;
        v = (v | v << 8) & 0x100F_00F0_0F00_F00F;
        v = (v | v << 4) & 0x10C3_0C30_C30C_30C3;
        return (v | v << 2) & 0x1249_2492_4924_9249;
    }
}

/// <summary>[OrderBy]'s order: the key and point the generator wrote into the component (<see cref="IOrderKey{TKey}"/>).</summary>
internal readonly struct DeclaredOrder<T, TKey> : IRowOrder<T> where T : struct, IOrderKey<TKey> where TKey : unmanaged
{
    public static int KeyBytes => Unsafe.SizeOf<TKey>();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Key(in T row, float cell) => OrderBits<TKey>.Of(row.OrderKey);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 Point(in T row) => row.BoxPoint;
}

/// <summary>
/// The order of one table's rows and the boxes of its blocks (Archetype.Order): created with a table that holds a
/// component its world orders (<see cref="World.OrderRows{T, TOrder}"/>, which a generated pair method's setup calls,
/// or <see cref="OrderByAttribute">[OrderBy]</see> for every world), re-sorted by its world on the cadence (<see cref="World.SortRows{T}"/>
/// for one now). Between sorts the rows drift: a row that moves into a block's row (a swap-remove, closed tombstones)
/// widens that block's box to hold it, rows that join the table wait past <see cref="SortedRows"/>, and a row's own
/// point may move up to the margin before its block's box stops covering it.
/// <br/>
/// Owner thread only, under the structural gate: sorts, widenings and shrinks happen where rows move, never during a
/// parallel pass, so readers (<see cref="RowBlocks"/>) on a pass's threads see boxes that do not change under them.
/// Memory: the boxes, 2 floats per axis per block, kept with the table and trimmed with it
/// (<see cref="World.TrimExcess"/>); the sort's keys and gathers are buffers of the shared array pools.
/// </summary>
internal abstract class RowOrder
{
    /// <summary>
    /// Rows per block unless an order asks for another size: a vector of floats, so a kernel holds a block's
    /// coordinates in one register per axis: 8 where 256-bit vectors are hardware (8 x 8 pairs of blocks), else 4
    /// (ARM64's NEON: 4 x 4). Decision D135 has the measurement against 16.
    /// </summary>
    public static int DefaultBlockRows => Vector256.IsHardwareAccelerated ? 8 : 4;

    /// <summary>Rows per block of this order: a power of two from 1 to 64.</summary>
    public readonly int BlockRows;

    private static int _instances;

    /// <summary>The ordered component's bit.</summary>
    public readonly ushort Bit;

    /// <summary>Axes of <see cref="IOrderKey{TKey}.BoxPoint"/> the boxes cover (0 to 3).</summary>
    public readonly int Axes;

    /// <summary>Updates between sorts (<see cref="World.Update()"/> calls).</summary>
    public readonly int Interval;

    /// <summary>What each box is widened by on every side: how far a row may move between sorts and stay covered.</summary>
    public readonly float Margin;

    /// <summary>The cell the key is quantized by (a generated order's cutoff), passed to <see cref="IRowOrder{T}.Key"/>.</summary>
    public readonly float Cell;

    private readonly int _id = Interlocked.Increment(ref _instances);
    private int _changes;

    /// <summary>Rows in blocks: [0, SortedRows), at most the table's count. The rows past them joined since the sort.</summary>
    public int SortedRows;

    /// <summary>The update of the last sort (<see cref="World.DefaultUpdateCount"/>).</summary>
    public long SortedAt;

    /// <summary>Sort at the next update whatever the cadence: a new table, or rows restored from a snapshot or a save.</summary>
    public bool Stale = true;

    /// <summary>The boxes: the minimum of axis a in [a * BoxCapacity, +blocks), its maximum in [(Axes + a) * BoxCapacity, +blocks).</summary>
    public float[] Boxes = [];

    public int BoxCapacity;

    protected RowOrder(ushort bit, int axes, float cell, int interval, float margin, int blockRows)
    {
        BlockRows = blockRows == 0 ? DefaultBlockRows : blockRows;
        Bit = bit;
        Axes = axes;
        Cell = cell;
        Interval = interval;
        Margin = margin;
    }

    /// <summary>Throws for an order no table can keep (cold: a request or a registration).</summary>
    public static void Check(int axes, float cell, int interval, float margin, int blockRows)
    {
        if (blockRows != 0 && (blockRows > 64 || !BitOperations.IsPow2(blockRows)))
            throw new ArgumentOutOfRangeException(nameof(blockRows), blockRows, "A block holds a power of two of rows up to 64, or 0 for the machine's");
        if ((uint)axes > 3) throw new ArgumentOutOfRangeException(nameof(axes), axes, "A box covers up to 3 axes");
        if (!(cell > 0) || float.IsInfinity(cell)) throw new ArgumentOutOfRangeException(nameof(cell), cell, "A cell is a finite size above 0");
        if (interval < 1) throw new ArgumentOutOfRangeException(nameof(interval), interval, "Rows are sorted every 1 update or more");
        if (!(margin >= 0) || float.IsInfinity(margin)) throw new ArgumentOutOfRangeException(nameof(margin), margin, "A margin is a finite distance, 0 or more");
    }

    /// <summary>Blocks over the sorted rows (the last one may hold fewer than <see cref="BlockRows"/>).</summary>
    public int BlockCount => (SortedRows + BlockRows - 1) / BlockRows;

    /// <summary>Changes whenever the blocks do (a sort, a widened box, fewer sorted rows), unique across tables.</summary>
    public long Version => (long)_id << 32 | (uint)_changes;

    /// <summary>
    /// Restore the order: the keys of every row (rows lacking the component last), sorted by an LSD radix sort of
    /// (key, row), the rows moved once to their places (<see cref="Archetype.Permute"/>) unless they already were in
    /// order, then the boxes drawn. Returns whether rows moved. Inside the structural gate, outside passes, on a dense
    /// table. O(rows × (key bytes + columns)).
    /// </summary>
    public abstract bool Sort(Archetype table, EntityMeta[] meta);

    /// <summary>Widen the box of the block of row <paramref name="row"/> (in the table's column) to hold the row.</summary>
    protected abstract void Widen(Archetype table, int row);

    /// <summary>A row of the table moved into row <paramref name="row"/>: its block's box grows to hold it.</summary>
    public void Moved(Archetype table, int row)
    {
        if (row >= SortedRows) return;
        Widen(table, row);
        _changes++;
    }

    /// <summary>The table has <paramref name="count"/> rows now: the blocks end there.</summary>
    public void Shrink(int count)
    {
        if (count >= SortedRows) return;
        SortedRows = count;
        _changes++;
    }

    /// <summary>The rows were replaced (a restore, a load): no block until the next update sorts them.</summary>
    public void Reset()
    {
        SortedRows = 0;
        Stale = true;
        _changes++;
    }

    /// <summary>Give back the box room past the current blocks (<see cref="World.TrimExcess"/>).</summary>
    public void Trim()
    {
        int blocks = BlockCount;
        if (blocks >= BoxCapacity) return;
        var boxes = blocks == 0 ? [] : new float[2 * Axes * blocks];
        for (int a = 0; a < 2 * Axes; a++) Array.Copy(Boxes, a * BoxCapacity, boxes, a * blocks, blocks);
        Boxes = boxes;
        BoxCapacity = blocks;
    }

    /// <summary>The minima (<paramref name="max"/> false) or maxima of axis <paramref name="axis"/>, one per block.</summary>
    public Span<float> Side(int axis, bool max) => Boxes.AsSpan((max ? Axes + axis : axis) * BoxCapacity, BlockCount);

    /// <summary>Room for <paramref name="blocks"/> boxes (cold: only when the table grew past the last sort's blocks).</summary>
    protected void Reserve(int blocks)
    {
        if (blocks <= BoxCapacity) return;
        int capacity = Math.Max(blocks, BoxCapacity * 2);
        Boxes = new float[2 * Axes * capacity];
        BoxCapacity = capacity;
    }

    protected void Sorted(int rows)
    {
        SortedRows = rows;
        Stale = false;
        _changes++;
    }
}

/// <summary>The order of a table by <typeparamref name="T"/>, its key and point read by <typeparamref name="TOrder"/>.</summary>
internal sealed class RowOrder<T, TOrder>(int axes, float cell, int interval, float margin, int blockRows)
    : RowOrder(ComponentType<T>.Bit, axes, cell, interval, margin, blockRows) where T : struct, IComponent where TOrder : struct, IRowOrder<T>
{
    public override bool Sort(Archetype table, EntityMeta[] meta)
    {
        int column = table.ColumnIndex(Bit);
        bool moved = TOrder.KeyBytes <= 4 ? Order<uint>(table, column, meta) : Order<ulong>(table, column, meta);
        Draw(table, column);
        Sorted(table.Count);
        return moved;
    }

    /// <summary>
    /// The keys as <typeparamref name="TBits"/> (an unsigned integer as wide as the key or wider), their byte histograms
    /// in the same pass, then one stable scatter per key byte whose values differ (a byte every row shares is skipped:
    /// a Morton key of a small map sorts in its low bytes only), ping-ponging between two buffers of the array pool.
    /// </summary>
    private bool Order<TBits>(Archetype table, int column, EntityMeta[] meta)
        where TBits : unmanaged, IBinaryInteger<TBits>, IUnsignedNumber<TBits>
    {
        int n = table.Count, bytes = Math.Clamp(TOrder.KeyBytes, 1, Unsafe.SizeOf<TBits>());
        float cell = Cell;
        if (n < 2) return false;
        var keys = ArrayPool<TBits>.Shared.Rent(n);
        var rows = ArrayPool<int>.Shared.Rent(n);
        Span<int> counts = stackalloc int[bytes * 256];
        counts.Clear();
        ref T row0 = ref table.Data<T>(column);
        var present = table.Bits(column);
        TBits previous = TBits.Zero;
        bool sorted = true;
        for (int i = 0; i < n; i++)
        {
            // A row lacking the component (removed or disabled since it joined) sorts last: no box holds it.
            TBits key = present == null || (present[i >> 6] >> i & 1) != 0 // shift is mod 64
                ? TBits.CreateTruncating(TOrder.Key(in Unsafe.Add(ref row0, i), cell))
                : TBits.AllBitsSet;
            keys[i] = key;
            rows[i] = i;
            sorted &= key >= previous;
            previous = key;
            for (int b = 0; b < bytes; b++) counts[(b << 8) + (int.CreateTruncating(key >> (b << 3)) & 0xFF)]++;
        }

        if (sorted)
        {
            ArrayPool<TBits>.Shared.Return(keys);
            ArrayPool<int>.Shared.Return(rows);
            return false;
        }

        var keys2 = ArrayPool<TBits>.Shared.Rent(n);
        var rows2 = ArrayPool<int>.Shared.Rent(n);
        TBits[] fromKeys = keys, toKeys = keys2;
        int[] fromRows = rows, toRows = rows2;
        for (int b = 0; b < bytes; b++)
        {
            var count = counts.Slice(b << 8, 256);
            int shift = b << 3;
            if (count[int.CreateTruncating(fromKeys[0] >> shift) & 0xFF] == n) continue; // every row has this byte
            for (int v = 0, sum = 0; v < 256; v++)
            {
                int c = count[v];
                count[v] = sum;
                sum += c;
            }

            for (int i = 0; i < n; i++)
            {
                TBits key = fromKeys[i];
                int at = count[int.CreateTruncating(key >> shift) & 0xFF]++;
                toKeys[at] = key;
                toRows[at] = fromRows[i];
            }

            (fromKeys, toKeys) = (toKeys, fromKeys);
            (fromRows, toRows) = (toRows, fromRows);
        }

        table.Permute(fromRows.AsSpan(0, n), meta);
        ArrayPool<TBits>.Shared.Return(keys);
        ArrayPool<TBits>.Shared.Return(keys2);
        ArrayPool<int>.Shared.Return(rows);
        ArrayPool<int>.Shared.Return(rows2);
        return true;
    }

    /// <summary>The box of every block over the points of its rows that have the component, widened by the margin.</summary>
    private void Draw(Archetype table, int column)
    {
        int n = table.Count, blocks = (n + BlockRows - 1) / BlockRows;
        Reserve(blocks);
        if (Axes == 0) return;
        ref T row0 = ref table.Data<T>(column);
        var present = table.Bits(column);
        int capacity = BoxCapacity;
        ref float boxes = ref MemoryMarshal.GetArrayDataReference(Boxes);
        for (int b = 0; b < blocks; b++)
        {
            var low = new Vector3(float.PositiveInfinity);
            var high = new Vector3(float.NegativeInfinity);
            for (int row = b * BlockRows, end = Math.Min(row + BlockRows, n); row < end; row++)
            {
                if (present != null && (present[row >> 6] >> row & 1) == 0) continue;
                var point = TOrder.Point(in Unsafe.Add(ref row0, row));
                low = Vector3.Min(low, point);
                high = Vector3.Max(high, point);
            }

            // An empty block (every row lacks the component) keeps an inverted box, which no search overlaps.
            var margin = new Vector3(Margin);
            low -= margin;
            high += margin;
            for (int a = 0; a < Axes; a++)
            {
                Unsafe.Add(ref boxes, a * capacity + b) = low[a];
                Unsafe.Add(ref boxes, (Axes + a) * capacity + b) = high[a];
            }
        }
    }

    protected override void Widen(Archetype table, int row)
    {
        int column = table.ColumnIndex(Bit);
        if (Axes == 0 || !table.Has(column, row)) return;
        var p = TOrder.Point(in Unsafe.Add(ref table.Data<T>(column), row));
        int block = row / BlockRows;
        for (int a = 0; a < Axes; a++)
        {
            ref float low = ref Boxes[a * BoxCapacity + block];
            ref float high = ref Boxes[(Axes + a) * BoxCapacity + block];
            low = MathF.Min(low, p[a] - Margin);
            high = MathF.Max(high, p[a] + Margin);
        }
    }
}

/// <summary>
/// A key's bits as an unsigned integer that orders like the key: unsigned types as they are, signed ones with the sign
/// bit flipped, floats with the sign bit flipped (positive) or every bit flipped (negative), enums as their underlying
/// type. The type code is a static readonly field, so Tier1 code keeps one branch per key type.
/// </summary>
internal static class OrderBits<TKey> where TKey : unmanaged
{
    private static readonly TypeCode Code = Type.GetTypeCode(typeof(TKey));

    /// <summary>Whether the key type can order rows (an integer, char, bool, float, double or an enum of them).</summary>
    public static readonly bool Supported = Code is >= TypeCode.Boolean and <= TypeCode.Double && Unsafe.SizeOf<TKey>() <= 8;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Of(TKey key)
    {
        switch (Code)
        {
            case TypeCode.Boolean or TypeCode.Byte: return Unsafe.As<TKey, byte>(ref key);
            case TypeCode.SByte: return (byte)(Unsafe.As<TKey, byte>(ref key) ^ 0x80);
            case TypeCode.Char or TypeCode.UInt16: return Unsafe.As<TKey, ushort>(ref key);
            case TypeCode.Int16: return (ushort)(Unsafe.As<TKey, ushort>(ref key) ^ 0x8000);
            case TypeCode.UInt32: return Unsafe.As<TKey, uint>(ref key);
            case TypeCode.Int32: return Unsafe.As<TKey, uint>(ref key) ^ 0x8000_0000u;
            case TypeCode.UInt64: return Unsafe.As<TKey, ulong>(ref key);
            case TypeCode.Int64: return Unsafe.As<TKey, ulong>(ref key) ^ 0x8000_0000_0000_0000UL;
            case TypeCode.Single:
            {
                uint bits = Unsafe.As<TKey, uint>(ref key);
                return (bits & 0x8000_0000u) != 0 ? ~bits : bits | 0x8000_0000u;
            }
            default: // Double
            {
                ulong bits = Unsafe.As<TKey, ulong>(ref key);
                return (bits & 0x8000_0000_0000_0000UL) != 0 ? ~bits : bits | 0x8000_0000_0000_0000UL;
            }
        }
    }
}
