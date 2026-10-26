using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace EasyEcs.Benchmark.Locality;

/// <summary>
/// Phase 0 of docs/plans/locality.md: 2D points ordered along a Morton curve, cut into blocks of 8 (one 256-bit float
/// vector) with an axis-aligned box each, and a tree of boxes over consecutive blocks (fan-out 8: the Morton order makes
/// consecutive blocks neighbors, so the tree is a linear BVH for free). The prototype keeps the sorted points in side
/// arrays; the core version (L1) would permute the table's columns in place and keep only the boxes.
/// <para>
/// <see cref="Sort"/> computes the keys, sorts (key, source index) pairs by an LSD radix sort (4 passes of 8 bits,
/// Polychroniou's partitioning without the vector histograms), copies X and Y into sorted order, pads the last block with
/// far points, and builds the boxes. Every array is sized by <see cref="Reserve"/>: no allocation per sort.
/// </para>
/// </summary>
public sealed class SortedBlocks
{
    /// <summary>The rows of a block: one Vector256 of floats.</summary>
    public const int Lanes = 8;

    /// <summary>Where padding rows sit: far from everything, finite so 0 * (far - x) stays 0 (no NaN).</summary>
    public const float Far = 1e15f;

    private const int FanOut = 8;

    public int Count;               // real rows
    public int BlockCount;          // ceil(Count / 8)
    public float[] X, Y;            // sorted, padded to BlockCount * 8
    public int[] Order;             // sorted slot -> source index
    public int[] Slot;              // source index -> sorted slot
    public uint[] Keys;             // sorted Morton keys (Count of them)

    // Box levels: level 0 is one box per block, level k + 1 one per 8 nodes of level k; the last level has <= 8 nodes.
    public float[][] MinX, MinY, MaxX, MaxY;
    public int[] LevelCount;
    public int Levels;

    private uint[] _keys2;
    private int[] _order2;
    private readonly int[] _histogram = new int[4 * 256];
    private float _scale;

    /// <summary>Size every array for up to <paramref name="capacity"/> rows.</summary>
    public void Reserve(int capacity)
    {
        int blocks = (capacity + Lanes - 1) / Lanes;
        X = new float[blocks * Lanes];
        Y = new float[blocks * Lanes];
        Order = new int[capacity];
        Slot = new int[capacity];
        Keys = new uint[capacity];
        _keys2 = new uint[capacity];
        _order2 = new int[capacity];
        int levels = 1;
        for (int n = blocks; n > FanOut; n = (n + FanOut - 1) / FanOut) levels++;
        MinX = new float[levels][];
        MinY = new float[levels][];
        MaxX = new float[levels][];
        MaxY = new float[levels][];
        LevelCount = new int[levels];
        for (int level = 0, n = Math.Max(blocks, 1); level < levels; level++, n = (n + FanOut - 1) / FanOut)
        {
            MinX[level] = new float[n];
            MinY[level] = new float[n];
            MaxX[level] = new float[n];
            MaxY[level] = new float[n];
        }
    }

    /// <summary>
    /// Order the points (<paramref name="x"/>, <paramref name="y"/>) of a square map [0, <paramref name="extent"/>)
    /// along the Morton curve and build the blocks and their boxes. O(n).
    /// </summary>
    public void Sort(ReadOnlySpan<float> x, ReadOnlySpan<float> y, float extent)
    {
        int n = x.Length;
        Count = n;
        BlockCount = (n + Lanes - 1) / Lanes;
        _scale = 65535f / extent;

        // Keys and the 4 byte histograms in one pass.
        var histogram = _histogram.AsSpan();
        histogram.Clear();
        var keys = Keys;                                     // 4 passes ping-pong Keys -> scratch -> Keys -> scratch -> Keys
        var order = Order;
        for (int i = 0; i < n; i++)
        {
            uint key = KeyOf(x[i], y[i]);
            keys[i] = key;
            order[i] = i;
            histogram[(int)(key & 0xFF)]++;
            histogram[256 + (int)(key >> 8 & 0xFF)]++;
            histogram[512 + (int)(key >> 16 & 0xFF)]++;
            histogram[768 + (int)(key >> 24)]++;
        }

        // LSD radix: 4 stable scatters, ping-ponging between the result and the scratch arrays (an even count ends in Keys).
        uint[] fromKeys = keys, toKeys = _keys2;
        int[] fromOrder = order, toOrder = _order2;
        for (int pass = 0; pass < 4; pass++)
        {
            var counts = histogram.Slice(pass * 256, 256);
            int sum = 0;
            for (int b = 0; b < 256; b++)
            {
                int c = counts[b];
                counts[b] = sum;
                sum += c;
            }

            int shift = pass * 8;
            for (int i = 0; i < n; i++)
            {
                uint key = fromKeys[i];
                int at = counts[(int)(key >> shift & 0xFF)]++;
                toKeys[at] = key;
                toOrder[at] = fromOrder[i];
            }

            (fromKeys, toKeys) = (toKeys, fromKeys);
            (fromOrder, toOrder) = (toOrder, fromOrder);
        }

        // After 4 passes the sorted data is back in Keys / Order.
        for (int s = 0; s < n; s++)
        {
            int source = Order[s];
            Slot[source] = s;
            X[s] = x[source];
            Y[s] = y[source];
        }

        for (int s = n; s < BlockCount * Lanes; s++)
        {
            X[s] = Far;
            Y[s] = Far;
        }

        BuildBoxes();
    }

    /// <summary>The Morton key of a point: 16 bits per axis, interleaved (x in the even bits).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint KeyOf(float x, float y)
    {
        uint qx = (uint)Math.Clamp((int)(x * _scale), 0, 65535);
        uint qy = (uint)Math.Clamp((int)(y * _scale), 0, 65535);
        return Spread(qx) | Spread(qy) << 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Spread(uint v)
    {
        v = (v | v << 8) & 0x00FF00FF;
        v = (v | v << 4) & 0x0F0F0F0F;
        v = (v | v << 2) & 0x33333333;
        v = (v | v << 1) & 0x55555555;
        return v;
    }

    private void BuildBoxes()
    {
        var minX = MinX[0];
        var minY = MinY[0];
        var maxX = MaxX[0];
        var maxY = MaxY[0];
        for (int b = 0; b < BlockCount; b++)
        {
            int first = b * Lanes, last = Math.Min(first + Lanes, Count);
            float x0 = X[first], x1 = x0, y0 = Y[first], y1 = y0;
            for (int s = first + 1; s < last; s++)
            {
                x0 = MathF.Min(x0, X[s]);
                x1 = MathF.Max(x1, X[s]);
                y0 = MathF.Min(y0, Y[s]);
                y1 = MathF.Max(y1, Y[s]);
            }

            minX[b] = x0;
            maxX[b] = x1;
            minY[b] = y0;
            maxY[b] = y1;
        }

        LevelCount[0] = BlockCount;
        int level = 0;
        while (LevelCount[level] > FanOut)
        {
            int below = LevelCount[level], count = (below + FanOut - 1) / FanOut;
            var cx0 = MinX[level];
            var cy0 = MinY[level];
            var cx1 = MaxX[level];
            var cy1 = MaxY[level];
            var px0 = MinX[level + 1];
            var py0 = MinY[level + 1];
            var px1 = MaxX[level + 1];
            var py1 = MaxY[level + 1];
            for (int p = 0; p < count; p++)
            {
                int first = p * FanOut, last = Math.Min(first + FanOut, below);
                float x0 = cx0[first], y0 = cy0[first], x1 = cx1[first], y1 = cy1[first];
                for (int c = first + 1; c < last; c++)
                {
                    x0 = MathF.Min(x0, cx0[c]);
                    y0 = MathF.Min(y0, cy0[c]);
                    x1 = MathF.Max(x1, cx1[c]);
                    y1 = MathF.Max(y1, cy1[c]);
                }

                px0[p] = x0;
                py0[p] = y0;
                px1[p] = x1;
                py1[p] = y1;
            }

            LevelCount[++level] = count;
        }

        Levels = level + 1;
    }

    /// <summary>The squared distance between a box and a point (0 inside).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float BoxDistance(float x0, float y0, float x1, float y1, float px, float py)
    {
        float dx = MathF.Max(0, MathF.Max(x0 - px, px - x1));
        float dy = MathF.Max(0, MathF.Max(y0 - py, py - y1));
        return dx * dx + dy * dy;
    }

    /// <summary>The squared distance between two boxes (0 when they overlap).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float BoxDistance(float ax0, float ay0, float ax1, float ay1, float bx0, float by0, float bx1, float by1)
    {
        float dx = MathF.Max(0, MathF.Max(ax0 - bx1, bx0 - ax1));
        float dy = MathF.Max(0, MathF.Max(ay0 - by1, by0 - ay1));
        return dx * dx + dy * dy;
    }

    /// <summary>
    /// The blocks other than <paramref name="block"/> whose box is closer than <paramref name="range"/> to its box,
    /// appended to <paramref name="into"/> from <paramref name="at"/>; returns the new end. A DFS over the tree with an
    /// explicit stack (no recursion, no allocation).
    /// </summary>
    public int BlocksNear(int block, float range, int[] into, int at)
    {
        float r2 = range * range;
        float ax0 = MinX[0][block], ay0 = MinY[0][block], ax1 = MaxX[0][block], ay1 = MaxY[0][block];
        Span<int> stack = stackalloc int[2 * FanOut * 8];      // (level, node) pairs; depth <= 8 levels of 8 children
        int top = 0;
        int rootLevel = Levels - 1;
        for (int node = 0; node < LevelCount[rootLevel]; node++)
        {
            stack[top++] = rootLevel;
            stack[top++] = node;
        }

        while (top > 0)
        {
            int node = stack[--top], level = stack[--top];
            if (BoxDistance(ax0, ay0, ax1, ay1, MinX[level][node], MinY[level][node], MaxX[level][node], MaxY[level][node]) >= r2) continue;
            if (level == 0)
            {
                if (node != block) into[at++] = node;
                continue;
            }

            int first = node * FanOut, last = Math.Min(first + FanOut, LevelCount[level - 1]);
            for (int c = last - 1; c >= first; c--)
            {
                stack[top++] = level - 1;
                stack[top++] = c;
            }
        }

        return at;
    }

    /// <summary>
    /// The sorted slot of the nearest point to (<paramref name="px"/>, <paramref name="py"/>) closer than
    /// sqrt(<paramref name="limit"/>) (squared distance strictly below it), -1 when none. Ties (equal squared distances)
    /// go to the smaller <paramref name="tieKeys"/> value (by sorted slot), so the answer does not depend on the order
    /// the blocks are visited in. A first guess from the block holding the point's own key, then a DFS that skips nodes
    /// whose box is farther than the best so far; each block's 8 distances in one Vector256.
    /// </summary>
    public int Nearest(float px, float py, float limit, int[] tieKeys)
    {
        if (Count == 0) return -1;
        float best = limit;
        int bestSlot = -1, bestKey = int.MaxValue;

        // First guess: the block where the point's key would be inserted.
        int guess = Keys.AsSpan(0, Count).BinarySearch(KeyOf(px, py));
        if (guess < 0) guess = ~guess;
        int seed = Math.Min(guess, Count - 1) / Lanes;
        Leaf(seed, px, py, tieKeys, ref best, ref bestSlot, ref bestKey);

        Span<int> stack = stackalloc int[2 * FanOut * 8];
        int top = 0;
        int rootLevel = Levels - 1;
        for (int node = 0; node < LevelCount[rootLevel]; node++)
        {
            stack[top++] = rootLevel;
            stack[top++] = node;
        }

        while (top > 0)
        {
            int node = stack[--top], level = stack[--top];
            float d = BoxDistance(MinX[level][node], MinY[level][node], MaxX[level][node], MaxY[level][node], px, py);
            if (d > best || bestSlot < 0 && d >= best) continue;     // a tie at the best distance is still visited
            if (level == 0)
            {
                if (node != seed) Leaf(node, px, py, tieKeys, ref best, ref bestSlot, ref bestKey);
                continue;
            }

            int first = node * FanOut, last = Math.Min(first + FanOut, LevelCount[level - 1]);
            for (int c = last - 1; c >= first; c--)
            {
                stack[top++] = level - 1;
                stack[top++] = c;
            }
        }

        return bestSlot;
    }

    // The 8 squared distances of a block in one vector; the lanes at or below the best go through the exact rule.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Leaf(int block, float px, float py, int[] tieKeys, ref float best, ref int bestSlot, ref int bestKey)
    {
        int first = block * Lanes;
        var dx = Vector256.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(X), (nuint)first) - Vector256.Create(px);
        var dy = Vector256.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(Y), (nuint)first) - Vector256.Create(py);
        var d2 = dx * dx + dy * dy;
        uint hits = Vector256.LessThanOrEqual(d2, Vector256.Create(best)).ExtractMostSignificantBits();
        for (; hits != 0; hits &= hits - 1)
        {
            int lane = BitOperations.TrailingZeroCount(hits), slot = first + lane;
            if (slot >= Count) break;
            float d = d2.GetElement(lane);
            int key = tieKeys[slot];
            if (d < best || d == best && bestSlot >= 0 && key < bestKey)
            {
                best = d;
                bestSlot = slot;
                bestKey = key;
            }
        }
    }
}
