using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace EasyEcs.Benchmark.Locality;

/// <summary>
/// What a generated pair method sets up for <see cref="Crowd"/>'s distance test (L2 writes it): the order of the
/// Position tables, the Morton code of the position quantized by the cutoff (<see cref="RowKeys.Morton"/>), boxes over
/// (X, Y).
/// </summary>
public readonly struct CrowdOrder : IRowOrder<Crowd.Position>
{
    public static int KeyBytes => 8;

    public static ulong Key(in Crowd.Position row, float cell) => RowKeys.Morton(Point(row), 2, cell);

    public static Vector3 Point(in Crowd.Position row) => new(row.X, row.Y, 0);
}

/// <summary>
/// (c) on the core's order (L1): the cluster-pair kernel of <see cref="ClusterRows"/> reading the table's own columns
/// in their sorted order (<see cref="RowClusters"/>), its cluster pairs from <see cref="RowClusters.Pairs(float, Span{ClusterPair})"/>
/// turned into a list per cluster (cached until the clusters change), and the pushes written into the Push column: no side
/// array, no copy in or out. What a generated kernel does: 8 x 8 over clusters of 8 where 256-bit vectors are hardware,
/// else 4 lanes over clusters of 4 (ARM64's NEON, the core's default there; Kernel4 takes clusters of any multiple of 4),
/// and a 16 x 16 form over pairs of clusters of 8 (512-bit vectors) to choose the cluster size.
/// <para>
/// The world must hold one table of the crowd, every row sorted (the benchmark sorts in setup and adds no row). The
/// pushes go through the span <see cref="RowClusters.Read{T}"/> returns: the prototype's shortcut for the write access a
/// generated kernel gets from its pass.
/// </para>
/// </summary>
public sealed class CoreClusters
{
    // The boxes carry the margin (the order's), so a pair of clusters is a candidate when their boxes come within the cutoff.
    private const float Reach = Crowd.Range;

    private long _version = -1;
    private ClusterPair[] _pairs = new ClusterPair[256];
    private int[] _start = new int[2], _list = new int[256], _cursor = [];
    private int _clusters;

    // The 16-row form: lists over pairs of clusters.
    private long _version16 = -1;
    private int[] _start16 = new int[2], _list16 = new int[256], _mark16 = [];
    private int _clusters16;

    /// <summary>Ask <paramref name="world"/> to keep the crowd ordered as a generated pair method would, then sort it now.</summary>
    public static void Order(World world, int interval)
    {
        world.OrderRows<Crowd.Position, CrowdOrder>(2, Crowd.Range, interval, Crowd.Range * (Crowd.Margin - 1));
        world.SortRows<Crowd.Position>();
    }

    /// <summary>One tick: the pair lists if the clusters changed, then the kernel of the machine's width.</summary>
    public void Interact(World world)
    {
        foreach (var clusters in world.Clusters<Crowd.Position>())
        {
            Lists(clusters);
            if (Vector256.IsHardwareAccelerated && clusters.Size == 8) Kernel8(clusters);
            else Kernel4(clusters);
        }
    }

    /// <summary>One tick of the 16 x 16 form (512-bit vectors; the 8-wide one where they are not hardware).</summary>
    public void Interact16(World world)
    {
        foreach (var clusters in world.Clusters<Crowd.Position>())
        {
            Lists(clusters);
            if (!Vector512.IsHardwareAccelerated || clusters.Size != 8)
            {
                if (clusters.Size == 8 && Vector256.IsHardwareAccelerated) Kernel8(clusters);
                else Kernel4(clusters);
                continue;
            }

            Lists16(clusters);
            Kernel16(clusters);
        }
    }

    // The cluster pairs as a list per cluster (both directions, self pairs left out: the kernel runs each cluster against
    // itself first), rebuilt when the clusters' version changed (a sort, a widened box).
    private void Lists(RowClusters clusters)
    {
        if (clusters.Version == _version) return;
        int n = clusters.Pairs(Reach, _pairs);
        if (n > _pairs.Length)
        {
            _pairs = new ClusterPair[n * 2]; // warm-up only
            n = clusters.Pairs(Reach, _pairs);
        }

        _clusters = clusters.Count;
        if (_start.Length < _clusters + 1) _start = new int[_clusters * 2 + 1];
        Array.Clear(_start, 0, _clusters + 1);
        int links = 0;
        for (int p = 0; p < n; p++)
        {
            var pair = _pairs[p];
            if (pair.First == pair.Second) continue;
            _start[pair.First + 1]++;
            _start[pair.Second + 1]++;
            links += 2;
        }

        for (int b = 0; b < _clusters; b++) _start[b + 1] += _start[b];
        if (_list.Length < links) _list = new int[links * 2];
        if (_cursor.Length < _clusters) _cursor = new int[_clusters * 2]; // warm-up only
        Span<int> cursor = _cursor.AsSpan(0, _clusters);
        _start.AsSpan(0, _clusters).CopyTo(cursor);
        for (int p = 0; p < n; p++)
        {
            var pair = _pairs[p];
            if (pair.First == pair.Second) continue;
            _list[cursor[pair.First]++] = pair.Second;
            _list[cursor[pair.Second]++] = pair.First;
        }

        _version = clusters.Version;
    }

    // Pairs of clusters (2s, 2s + 1) as one cluster of 16: its list is the union of both lists, by pair of clusters.
    private void Lists16(RowClusters clusters)
    {
        if (clusters.Version == _version16) return;
        _clusters16 = (_clusters + 1) / 2;
        if (_start16.Length < _clusters16 + 1) _start16 = new int[_clusters16 * 2 + 1];
        if (_mark16.Length < _clusters16) _mark16 = new int[_clusters16 * 2];
        Array.Fill(_mark16, -1);
        if (_list16.Length < _list.Length) _list16 = new int[_list.Length];
        int at = 0;
        for (int s = 0; s < _clusters16; s++)
        {
            _start16[s] = at;
            _mark16[s] = s; // itself: run first, masked
            for (int b = 2 * s; b < Math.Min(2 * s + 2, _clusters); b++)
                for (int k = _start[b]; k < _start[b + 1]; k++)
                {
                    int t = _list[k] >> 1;
                    if (_mark16[t] == s) continue;
                    _mark16[t] = s;
                    _list16[at++] = t;
                }
        }

        _start16[_clusters16] = at;
        _version16 = clusters.Version;
    }

    // The far point a lane past the table's rows holds: every distance to it is past the cutoff.
    private const float Far = 1e18f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float X(ReadOnlySpan<Crowd.Position> p, int row, int end) => row < end ? p[row].X : Far;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Y(ReadOnlySpan<Crowd.Position> p, int row, int end) => row < end ? p[row].Y : Far;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float VX(ReadOnlySpan<Crowd.Velocity> v, int row, int end) => row < end ? v[row].X : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float VY(ReadOnlySpan<Crowd.Velocity> v, int row, int end) => row < end ? v[row].Y : 0;

    // 8 i lanes per cluster (the columns hold (X, Y) pairs, so the lanes are loaded one row at a time), against the 8 rows
    // of the cluster itself (self masked) and of each cluster of its list, j broadcast.
    private void Kernel8(RowClusters clusters)
    {
        var p = clusters.Read<Crowd.Position>();
        var v = clusters.Read<Crowd.Velocity>();
        var push = MemoryMarshal.CreateSpan(ref MemoryMarshal.GetReference(clusters.Read<Crowd.Push>()), clusters.RowCount);
        int rows = clusters.RowCount;
        var lane = Vector256.Create(0, 1, 2, 3, 4, 5, 6, 7);
        var all = Vector256<float>.AllBitsSet;
        for (int a = 0; a < _clusters; a++)
        {
            int s = clusters.Start(a), e = clusters.End(a);
            var xi = Vector256.Create(X(p, s, e), X(p, s + 1, e), X(p, s + 2, e), X(p, s + 3, e), X(p, s + 4, e), X(p, s + 5, e), X(p, s + 6, e), X(p, s + 7, e));
            var yi = Vector256.Create(Y(p, s, e), Y(p, s + 1, e), Y(p, s + 2, e), Y(p, s + 3, e), Y(p, s + 4, e), Y(p, s + 5, e), Y(p, s + 6, e), Y(p, s + 7, e));
            var vxi = Vector256.Create(VX(v, s, e), VX(v, s + 1, e), VX(v, s + 2, e), VX(v, s + 3, e), VX(v, s + 4, e), VX(v, s + 5, e), VX(v, s + 6, e), VX(v, s + 7, e));
            var vyi = Vector256.Create(VY(v, s, e), VY(v, s + 1, e), VY(v, s + 2, e), VY(v, s + 3, e), VY(v, s + 4, e), VY(v, s + 5, e), VY(v, s + 6, e), VY(v, s + 7, e));
            Vector256<float> ax = default, ay = default;
            for (int j = s; j < e; j++)
                Pair(xi, yi, vxi, vyi, p[j], v[j], ~Vector256.Equals(lane, Vector256.Create(j - s)).AsSingle(), ref ax, ref ay);
            for (int k = _start[a]; k < _start[a + 1]; k++)
                for (int j = clusters.Start(_list[k]), end = clusters.End(_list[k]); j < end; j++)
                    Pair(xi, yi, vxi, vyi, p[j], v[j], all, ref ax, ref ay);
            for (int i = s; i < e && i < rows; i++) push[i] = new Crowd.Push(ax.GetElement(i - s), ay.GetElement(i - s));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Pair(Vector256<float> xi, Vector256<float> yi, Vector256<float> vxi, Vector256<float> vyi,
        in Crowd.Position q, in Crowd.Velocity u, Vector256<float> keep, ref Vector256<float> ax, ref Vector256<float> ay)
    {
        var dx = xi - Vector256.Create(q.X);
        var dy = yi - Vector256.Create(q.Y);
        var d2 = dx * dx + dy * dy;
        var r2 = Vector256.Create(Crowd.Range2);
        var w = (r2 - d2) & Vector256.LessThan(d2, r2) & keep;
        var drag = Vector256.Create(Crowd.Drag);
        ax += w * (dx + drag * (Vector256.Create(u.X) - vxi));
        ay += w * (dy + drag * (Vector256.Create(u.Y) - vyi));
    }

    // 4 i lanes per half cluster, as Kernel8.
    private void Kernel4(RowClusters clusters)
    {
        var p = clusters.Read<Crowd.Position>();
        var v = clusters.Read<Crowd.Velocity>();
        var push = MemoryMarshal.CreateSpan(ref MemoryMarshal.GetReference(clusters.Read<Crowd.Push>()), clusters.RowCount);
        var all = Vector128<float>.AllBitsSet;
        for (int a = 0; a < _clusters; a++)
        {
            int start = clusters.Start(a), e = clusters.End(a);
            for (int s = start; s < e; s += 4)
            {
                var lane = Vector128.Create(s, s + 1, s + 2, s + 3);
                var xi = Vector128.Create(X(p, s, e), X(p, s + 1, e), X(p, s + 2, e), X(p, s + 3, e));
                var yi = Vector128.Create(Y(p, s, e), Y(p, s + 1, e), Y(p, s + 2, e), Y(p, s + 3, e));
                var vxi = Vector128.Create(VX(v, s, e), VX(v, s + 1, e), VX(v, s + 2, e), VX(v, s + 3, e));
                var vyi = Vector128.Create(VY(v, s, e), VY(v, s + 1, e), VY(v, s + 2, e), VY(v, s + 3, e));
                Vector128<float> ax = default, ay = default;
                for (int j = start; j < e; j++)
                    Pair(xi, yi, vxi, vyi, p[j], v[j], ~Vector128.Equals(lane, Vector128.Create(j)).AsSingle(), ref ax, ref ay);
                for (int k = _start[a]; k < _start[a + 1]; k++)
                    for (int j = clusters.Start(_list[k]), end = clusters.End(_list[k]); j < end; j++)
                        Pair(xi, yi, vxi, vyi, p[j], v[j], all, ref ax, ref ay);
                for (int i = s; i < Math.Min(s + 4, e); i++) push[i] = new Crowd.Push(ax.GetElement(i - s), ay.GetElement(i - s));
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Pair(Vector128<float> xi, Vector128<float> yi, Vector128<float> vxi, Vector128<float> vyi,
        in Crowd.Position q, in Crowd.Velocity u, Vector128<float> keep, ref Vector128<float> ax, ref Vector128<float> ay)
    {
        var dx = xi - Vector128.Create(q.X);
        var dy = yi - Vector128.Create(q.Y);
        var d2 = dx * dx + dy * dy;
        var r2 = Vector128.Create(Crowd.Range2);
        var w = (r2 - d2) & Vector128.LessThan(d2, r2) & keep;
        var drag = Vector128.Create(Crowd.Drag);
        ax += w * (dx + drag * (Vector128.Create(u.X) - vxi));
        ay += w * (dy + drag * (Vector128.Create(u.Y) - vyi));
    }

    // 16 i lanes per pair of clusters (512-bit vectors), against the 16 rows of the pair itself and of each pair of its
    // list: the cluster size of 16 the decision weighs against 8 (the same boxes merged two by two).
    private void Kernel16(RowClusters clusters)
    {
        var p = clusters.Read<Crowd.Position>();
        var v = clusters.Read<Crowd.Velocity>();
        var push = MemoryMarshal.CreateSpan(ref MemoryMarshal.GetReference(clusters.Read<Crowd.Push>()), clusters.RowCount);
        int sorted = clusters.SortedRows;
        var lane = Vector512.Create(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15);
        var all = Vector512<float>.AllBitsSet;
        Span<float> x = stackalloc float[16], y = stackalloc float[16], vx = stackalloc float[16], vy = stackalloc float[16];
        for (int a = 0; a < _clusters16; a++)
        {
            int s = a * 16, e = Math.Min(s + 16, sorted);
            for (int i = 0; i < 16; i++)
            {
                x[i] = X(p, s + i, e);
                y[i] = Y(p, s + i, e);
                vx[i] = VX(v, s + i, e);
                vy[i] = VY(v, s + i, e);
            }

            var xi = Vector512.Create<float>(x);
            var yi = Vector512.Create<float>(y);
            var vxi = Vector512.Create<float>(vx);
            var vyi = Vector512.Create<float>(vy);
            Vector512<float> ax = default, ay = default;
            for (int j = s; j < e; j++)
                Pair(xi, yi, vxi, vyi, p[j], v[j], ~Vector512.Equals(lane, Vector512.Create(j - s)).AsSingle(), ref ax, ref ay);
            for (int k = _start16[a]; k < _start16[a + 1]; k++)
                for (int j = _list16[k] * 16, end = Math.Min(j + 16, sorted); j < end; j++)
                    Pair(xi, yi, vxi, vyi, p[j], v[j], all, ref ax, ref ay);
            for (int i = s; i < e; i++) push[i] = new Crowd.Push(ax.GetElement(i - s), ay.GetElement(i - s));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Pair(Vector512<float> xi, Vector512<float> yi, Vector512<float> vxi, Vector512<float> vyi,
        in Crowd.Position q, in Crowd.Velocity u, Vector512<float> keep, ref Vector512<float> ax, ref Vector512<float> ay)
    {
        var dx = xi - Vector512.Create(q.X);
        var dy = yi - Vector512.Create(q.Y);
        var d2 = dx * dx + dy * dy;
        var r2 = Vector512.Create(Crowd.Range2);
        var w = (r2 - d2) & Vector512.LessThan(d2, r2) & keep;
        var drag = Vector512.Create(Crowd.Drag);
        ax += w * (dx + drag * (Vector512.Create(u.X) - vxi));
        ay += w * (dy + drag * (Vector512.Create(u.Y) - vyi));
    }
}
