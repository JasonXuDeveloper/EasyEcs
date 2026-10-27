using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace EasyEcs.Benchmark.Locality;

/// <summary>How many neighbors an entity has within <see cref="Crowd.Range"/> on average.</summary>
public enum Density
{
    /// <summary>24 neighbors: a crowd (an army, a market square, a flock).</summary>
    Crowd,

    /// <summary>2 neighbors: a sparse map.</summary>
    Sparse,
}

/// <summary>
/// The pair question of docs/plans/locality.md 4.1: every entity sums a push from each neighbor within
/// <see cref="Range"/> (separation plus a share of the velocity difference, as boids and crowd steering do). The same
/// sum four ways: (a) the row loop over a hand-written uniform grid (ids per cell, neighbors read through lookups), (a2)
/// the same grid holding copies of the positions and velocities, (b) per-entity neighbor lists rebuilt on a cadence, and
/// (c) a cluster-pair kernel over Morton-sorted rows in clusters of 8 (<see cref="SortedClusters"/>).
/// <para>
/// Every version computes each pair's term with the same float operations, so the set of pairs and each term are bit
/// for bit the same; only the order of the sum differs (cell order, list order, cluster order). Results agree with a
/// double-precision sum within 1e-5 * (1 + the sum of the terms' magnitudes) (<see cref="Reference"/>).
/// </para>
/// </summary>
public static partial class Crowd
{
    /// <summary>The cutoff.</summary>
    public const float Range = 1f;

    public const float Range2 = Range * Range;

    /// <summary>The list range (GROMACS's rlist) over the cutoff: lists and cluster pairs stay valid while no row moves
    /// half the margin. Nothing moves in the benchmark, so the margin only adds candidate pairs, as it would.</summary>
    public const float Margin = 1.1f;

    /// <summary>The share of the velocity difference in a term.</summary>
    public const float Drag = 0.5f;

    public record struct Position(float X, float Y) : IComponent;
    public record struct Velocity(float X, float Y) : IComponent;
    public record struct Push(float X, float Y) : IComponent;

    /// <summary>(b): where an entity's neighbors start in <see cref="ListGrid.Ids"/> and how many.</summary>
    public record struct Neighbors(int Start, int Count) : IComponent;

    /// <summary>The side of the square map.</summary>
    public record struct Map(float Extent) : ISingletonComponent;

    /// <summary>(a), (a2): the grid of cells of side <see cref="Range"/>, rebuilt every tick.</summary>
    [NotSaved]
    public struct RowGrid : ISingletonComponent
    {
        public Cells Cells;
    }

    /// <summary>(b): the grid of cells of side Range * Margin the lists are built from, and the lists.</summary>
    [NotSaved]
    public struct ListGrid : ISingletonComponent
    {
        public Cells Cells;
        public EntityId[] Ids;
        public int Count;
    }

    /// <summary>(c): the sorted rows, their clusters and the cluster pairs.</summary>
    [NotSaved]
    public struct Clusters : ISingletonComponent
    {
        public ClusterRows Rows;
    }

    /// <summary>A uniform grid over the map: the ids of each cell side by side (a counting sort), and in (a2) copies of
    /// their positions and velocities in the same order.</summary>
    public struct Cells
    {
        public int Side;
        public float PerUnit;
        public int[] Start;          // Side * Side + 1: cell c holds Start[c] .. Start[c + 1]
        public int[] Cursor;
        public EntityId[] Ids;
        public float[] X, Y, VX, VY; // (a2) only

        public void Reserve(int entities, float extent, float cell)
        {
            Side = Math.Max(1, (int)MathF.Ceiling(extent / cell));
            PerUnit = 1 / cell;
            Start = new int[Side * Side + 1];
            Cursor = new int[Side * Side];
            Ids = new EntityId[entities];
            X = new float[entities];
            Y = new float[entities];
            VX = new float[entities];
            VY = new float[entities];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly int Column(float v) => Math.Clamp((int)(v * PerUnit), 0, Side - 1);
    }

    /// <summary>A crowd of <paramref name="entities"/> on a square map sized for <paramref name="density"/>, uniform
    /// positions, velocities in [-1, 1].</summary>
    public static World Create(int entities, Density density, int seed = 42)
    {
        float neighbors = density == Density.Crowd ? 24 : 2;
        float extent = MathF.Sqrt(entities * MathF.PI * Range2 / neighbors);
        var world = new World(new WorldOptions { InitialEntityCapacity = entities + 16 });
        world.AddSingleton<Map>() = new Map(extent);
        world.AddSingleton<RowGrid>().Cells.Reserve(entities, extent, Range);
        ref var lists = ref world.AddSingleton<ListGrid>();
        lists.Cells.Reserve(entities, extent, Range * Margin);
        lists.Ids = new EntityId[entities * 4];
        var rows = new ClusterRows();
        rows.Reserve(entities);
        world.AddSingleton<Clusters>().Rows = rows;

        var all = new Entity[entities];
        world.CreateEntities(all, new Position(), new Velocity(), new Push(), new Neighbors());
        var random = new Random(seed);
        foreach (var item in world.Query<Position, Velocity>())
        {
            item.Component1 = new Position(random.NextSingle() * extent, random.NextSingle() * extent);
            item.Component2 = new Velocity(random.NextSingle() * 2 - 1, random.NextSingle() * 2 - 1);
        }

        return world;
    }

    // #region grid
    /// <summary>The ids per cell by a counting sort over the table (and in (a2) the copies), as users write a grid.</summary>
    public static void Fill(World world, ref Cells cells, bool copy)
    {
        var start = cells.Start;
        Array.Clear(start);
        foreach (var item in world.Query<Position>())
            start[cells.Column(item.Component1.Y) * cells.Side + cells.Column(item.Component1.X) + 1]++;
        for (int c = 1; c < start.Length; c++) start[c] += start[c - 1];
        Array.Copy(start, cells.Cursor, cells.Cursor.Length);
        var cursor = cells.Cursor;
        foreach (var item in world.Query<Position, Velocity>())
        {
            ref readonly var p = ref item.Component1;
            int k = cursor[cells.Column(p.Y) * cells.Side + cells.Column(p.X)]++;
            cells.Ids[k] = item.Entity.Id;
            if (!copy) continue;
            cells.X[k] = p.X;
            cells.Y[k] = p.Y;
            cells.VX[k] = item.Component2.X;
            cells.VY[k] = item.Component2.Y;
        }
    }

    /// <summary>(a) One tick: the grid, then each entity's push from the ids of the 3 x 3 cells around it, each read
    /// through the lookups.</summary>
    public static void GridTick(World world)
    {
        Fill(world, ref world.GetSingleton<RowGrid>().Cells, false);
        Crowd.Separate(world);
    }

    /// <summary>(a2) One tick: the grid with copies, then each entity's push from the copies.</summary>
    public static void CopiedGridTick(World world)
    {
        Fill(world, ref world.GetSingleton<RowGrid>().Cells, true);
        Crowd.SeparateCopied(world);
    }

    [ForEach(SimdMode = SimdMode.Off)]
    public static void Separate(Entity self, in Position p, in Velocity v, ref Push push, in RowGrid grid,
        Lookup<Position> positions, Lookup<Velocity> velocities)
    {
        ref readonly var cells = ref grid.Cells;
        int cx = cells.Column(p.X), cy = cells.Column(p.Y), side = cells.Side;
        var start = cells.Start;
        var ids = cells.Ids;
        float px = 0, py = 0;
        for (int y = Math.Max(cy - 1, 0); y <= Math.Min(cy + 1, side - 1); y++)
        for (int x = Math.Max(cx - 1, 0); x <= Math.Min(cx + 1, side - 1); x++)
        {
            int c = y * side + x;
            for (int k = start[c]; k < start[c + 1]; k++)
            {
                var id = ids[k];
                if (id == self.Id) continue;
                ref readonly var q = ref positions.Read(id);
                float dx = p.X - q.X, dy = p.Y - q.Y, d2 = dx * dx + dy * dy;
                if (d2 >= Range2) continue;
                ref readonly var u = ref velocities.Read(id);
                float w = Range2 - d2;
                px += w * (dx + Drag * (u.X - v.X));
                py += w * (dy + Drag * (u.Y - v.Y));
            }
        }

        push = new Push(px, py);
    }

    [ForEach(SimdMode = SimdMode.Off)]
    public static void SeparateCopied(Entity self, in Position p, in Velocity v, ref Push push, in RowGrid grid)
    {
        ref readonly var cells = ref grid.Cells;
        int cx = cells.Column(p.X), cy = cells.Column(p.Y), side = cells.Side;
        var start = cells.Start;
        var ids = cells.Ids;
        float[] xs = cells.X, ys = cells.Y, vxs = cells.VX, vys = cells.VY;
        float px = 0, py = 0;
        for (int y = Math.Max(cy - 1, 0); y <= Math.Min(cy + 1, side - 1); y++)
        for (int x = Math.Max(cx - 1, 0); x <= Math.Min(cx + 1, side - 1); x++)
        {
            int c = y * side + x, end = start[c + 1];
            for (int k = start[c]; k < end; k++)
            {
                float dx = p.X - xs[k], dy = p.Y - ys[k], d2 = dx * dx + dy * dy;
                if (d2 >= Range2 || ids[k] == self.Id) continue;
                float w = Range2 - d2;
                px += w * (dx + Drag * (vxs[k] - v.X));
                py += w * (dy + Drag * (vys[k] - v.Y));
            }
        }

        push = new Push(px, py);
    }
    // #endregion grid

    // #region lists
    /// <summary>(b) The lists: a grid of cells of the list range, then each entity's ids within Range * Margin.</summary>
    public static void BuildLists(World world)
    {
        ref var lists = ref world.GetSingleton<ListGrid>();
        Fill(world, ref lists.Cells, false);
        lists.Count = 0;
        Crowd.Collect(world);
    }

    [ForEach(SimdMode = SimdMode.Off)]
    public static void Collect(Entity self, in Position p, ref Neighbors neighbors, ref ListGrid lists, Lookup<Position> positions)
    {
        ref readonly var cells = ref lists.Cells;
        int cx = cells.Column(p.X), cy = cells.Column(p.Y), side = cells.Side, first = lists.Count;
        const float listRange2 = Range * Margin * (Range * Margin);
        for (int y = Math.Max(cy - 1, 0); y <= Math.Min(cy + 1, side - 1); y++)
        for (int x = Math.Max(cx - 1, 0); x <= Math.Min(cx + 1, side - 1); x++)
        {
            int c = y * side + x;
            for (int k = cells.Start[c]; k < cells.Start[c + 1]; k++)
            {
                var id = cells.Ids[k];
                if (id == self.Id) continue;
                ref readonly var q = ref positions.Read(id);
                float dx = p.X - q.X, dy = p.Y - q.Y;
                if (dx * dx + dy * dy >= listRange2) continue;
                if (lists.Count == lists.Ids.Length) Array.Resize(ref lists.Ids, lists.Ids.Length * 2);   // setup only
                lists.Ids[lists.Count++] = id;
            }
        }

        neighbors = new Neighbors(first, lists.Count - first);
    }

    /// <summary>(b) One tick over the lists: each neighbor read through the lookups.</summary>
    [ForEach(SimdMode = SimdMode.Off)]
    public static void Interact(in Position p, in Velocity v, ref Push push, in Neighbors neighbors, in ListGrid lists,
        Lookup<Position> positions, Lookup<Velocity> velocities)
    {
        var ids = lists.Ids;
        float px = 0, py = 0;
        for (int k = neighbors.Start, end = neighbors.Start + neighbors.Count; k < end; k++)
        {
            var id = ids[k];
            ref readonly var q = ref positions.Read(id);
            float dx = p.X - q.X, dy = p.Y - q.Y, d2 = dx * dx + dy * dy;
            if (d2 >= Range2) continue;
            ref readonly var u = ref velocities.Read(id);
            float w = Range2 - d2;
            px += w * (dx + Drag * (u.X - v.X));
            py += w * (dy + Drag * (u.Y - v.Y));
        }

        push = new Push(px, py);
    }
    // #endregion lists

    // #region clusters
    /// <summary>(c) The sort: the table's positions and velocities copied out (the prototype's side arrays), ordered
    /// along the Morton curve, clusters and boxes, then the cluster pairs within the list range (cached until the next sort).</summary>
    public static void Sort(World world)
    {
        var rows = world.GetSingleton<Clusters>().Rows;
        int k = 0;
        foreach (var item in world.Query<Position, Velocity>())
        {
            rows.SourceX[k] = item.Component1.X;
            rows.SourceY[k] = item.Component1.Y;
            rows.SourceVX[k] = item.Component2.X;
            rows.SourceVY[k++] = item.Component2.Y;
        }

        rows.Sort(k, world.GetSingleton<Map>().Extent);
    }

    /// <summary>(c), user level: the current positions and velocities copied into their sorted slots (rows move between
    /// sorts in a real tick), the kernel, and the pushes copied back into the table. What a user can write today; the
    /// core version (L1) keeps the table itself sorted and pays none of the two copies.</summary>
    public static void ClusterTickWithCopies(World world)
    {
        var rows = world.GetSingleton<Clusters>().Rows;
        var b = rows.Clusters;
        int k = 0;
        foreach (var item in world.Query<Position, Velocity>())
        {
            int s = b.Slot[k++];
            b.X[s] = item.Component1.X;
            b.Y[s] = item.Component1.Y;
            rows.VX[s] = item.Component2.X;
            rows.VY[s] = item.Component2.Y;
        }

        rows.Interact();
        WriteBack(world);
    }

    /// <summary>(c) The pushes of the sorted rows copied into the table's Push column.</summary>
    public static void WriteBack(World world)
    {
        var rows = world.GetSingleton<Clusters>().Rows;
        var slot = rows.Clusters.Slot;
        int k = 0;
        foreach (var item in world.Query<Push>())
        {
            int s = slot[k++];
            item.Component1 = new Push(rows.PX[s], rows.PY[s]);
        }
    }
    // #endregion clusters

    /// <summary>The pushes in table order.</summary>
    public static Push[] Pushes(World world)
    {
        var all = new Push[world.Count<Push>()];
        int k = 0;
        foreach (var item in world.Query<Push>()) all[k++] = item.Component1;
        return all;
    }

    /// <summary>The expected pushes in table order, summed in double over the pairs the float test selects, with the sum
    /// of the terms' magnitudes (the error bound's scale). O(n^2 / cells): a check, not a benchmark.</summary>
    public static (double X, double Y, double Magnitude)[] Reference(World world)
    {
        int n = world.Count<Position>();
        var x = new float[n];
        var y = new float[n];
        var vx = new float[n];
        var vy = new float[n];
        int k = 0;
        foreach (var item in world.Query<Position, Velocity>())
        {
            x[k] = item.Component1.X;
            y[k] = item.Component1.Y;
            vx[k] = item.Component2.X;
            vy[k++] = item.Component2.Y;
        }

        var cells = new Cells();
        cells.Reserve(n, world.GetSingleton<Map>().Extent, Range);
        var bucket = new System.Collections.Generic.List<int>[cells.Side * cells.Side];
        for (int i = 0; i < n; i++) (bucket[cells.Column(y[i]) * cells.Side + cells.Column(x[i])] ??= new()).Add(i);
        var expected = new (double, double, double)[n];
        for (int i = 0; i < n; i++)
        {
            double px = 0, py = 0, magnitude = 0;
            int cx = cells.Column(x[i]), cy = cells.Column(y[i]);
            for (int gy = Math.Max(cy - 1, 0); gy <= Math.Min(cy + 1, cells.Side - 1); gy++)
            for (int gx = Math.Max(cx - 1, 0); gx <= Math.Min(cx + 1, cells.Side - 1); gx++)
            {
                var list = bucket[gy * cells.Side + gx];
                if (list == null) continue;
                foreach (int j in list)
                {
                    if (j == i) continue;
                    float dx = x[i] - x[j], dy = y[i] - y[j], d2 = dx * dx + dy * dy;
                    if (d2 >= Range2 || d2 == 0) continue; // two rows at one point (16M random floats repeat one): the methods skip them as themselves
                    float w = Range2 - d2, tx = w * (dx + Drag * (vx[j] - vx[i])), ty = w * (dy + Drag * (vy[j] - vy[i]));
                    px += tx;
                    py += ty;
                    magnitude += Math.Abs(tx) + Math.Abs(ty);
                }
            }

            expected[i] = (px, py, magnitude);
        }

        return expected;
    }

    /// <summary>The first row whose push is off the reference by more than 1e-5 * (1 + magnitude), or -1.</summary>
    public static int FirstMismatch(Push[] got, (double X, double Y, double Magnitude)[] expected)
    {
        for (int i = 0; i < got.Length; i++)
        {
            double tolerance = 1e-5 * (1 + expected[i].Magnitude);
            if (Math.Abs(got[i].X - expected[i].X) > tolerance || Math.Abs(got[i].Y - expected[i].Y) > tolerance) return i;
        }

        return -1;
    }
}

/// <summary>
/// (c) The rows of the cluster-pair kernel: positions in <see cref="Clusters"/> (sorted, padded), velocities and pushes
/// in the same order, and for each cluster the clusters within the list range (a CSR list, GROMACS's cluster-pair list).
/// <para>
/// What the prototype copies that the core version would not: the positions and velocities into these sorted side
/// arrays (the core permutes the table's columns in place at a re-sort and the kernel reads the columns), the pushes
/// out of <see cref="PX"/> / <see cref="PY"/> into the table (the core writes the column), and <see cref="SortedClusters.Order"/> /
/// <see cref="SortedClusters.Slot"/> (the core moves rows, so a row is its slot). The core would permute every column of
/// the table and the entity slots; the prototype moves only the two columns the kernel reads.
/// </para>
/// </summary>
public sealed class ClusterRows
{
    public readonly SortedClusters Clusters = new();
    public float[] VX, VY, PX, PY;
    public float[] SourceX, SourceY, SourceVX, SourceVY;
    public int[] PairStart;
    public int[] Pairs;

    public void Reserve(int capacity)
    {
        Clusters.Reserve(capacity);
        int padded = Clusters.X.Length;
        VX = new float[padded];
        VY = new float[padded];
        PX = new float[padded];
        PY = new float[padded];
        SourceX = new float[capacity];
        SourceY = new float[capacity];
        SourceVX = new float[capacity];
        SourceVY = new float[capacity];
        PairStart = new int[padded / SortedClusters.Lanes + 1];
        Pairs = new int[Math.Max(64, padded)];
    }

    /// <summary>Sort the <paramref name="count"/> source rows and list the cluster pairs. O(n log n) for the pair search.</summary>
    public void Sort(int count, float extent)
    {
        var b = Clusters;
        b.Sort(SourceX.AsSpan(0, count), SourceY.AsSpan(0, count), extent);
        for (int s = 0; s < count; s++)
        {
            VX[s] = SourceVX[b.Order[s]];
            VY[s] = SourceVY[b.Order[s]];
        }

        for (int s = count; s < b.ClusterCount * SortedClusters.Lanes; s++) VX[s] = VY[s] = 0;
        int at = 0;
        for (int cluster = 0; cluster < b.ClusterCount; cluster++)
        {
            PairStart[cluster] = at;
            if (Pairs.Length - at < b.ClusterCount) Array.Resize(ref Pairs, Math.Max(Pairs.Length * 2, at + b.ClusterCount)); // warm-up only
            at = b.ClustersNear(cluster, Crowd.Range * Crowd.Margin, Pairs, at);
        }

        PairStart[b.ClusterCount] = at;
    }

    /// <summary>
    /// The kernel: for each cluster of 8 rows (i in the lanes), its own cluster with the self pairs masked, then each cluster
    /// of its list with every j broadcast: 64 pairs per cluster pair, the cutoff a mask, the sums kept in the i lanes and
    /// stored once per cluster. 8 lanes where 256-bit vectors are hardware (x86 with AVX), else 4 lanes per half cluster
    /// (ARM64's NEON, x86 without AVX): the same sums in the same order per lane, so both write the same pushes.
    /// </summary>
    public void Interact()
    {
        if (Vector256.IsHardwareAccelerated) Interact8();
        else Interact4();
    }

    private void Interact8()
    {
        var b = Clusters;
        ref float x = ref MemoryMarshal.GetArrayDataReference(b.X);
        ref float y = ref MemoryMarshal.GetArrayDataReference(b.Y);
        ref float vx = ref MemoryMarshal.GetArrayDataReference(VX);
        ref float vy = ref MemoryMarshal.GetArrayDataReference(VY);
        ref float outX = ref MemoryMarshal.GetArrayDataReference(PX);
        ref float outY = ref MemoryMarshal.GetArrayDataReference(PY);
        var start = PairStart;
        var pairs = Pairs;
        var lane = Vector256.Create(0, 1, 2, 3, 4, 5, 6, 7);
        var all = Vector256<float>.AllBitsSet;
        for (int cluster = 0; cluster < b.ClusterCount; cluster++)
        {
            nuint i0 = (nuint)(cluster * SortedClusters.Lanes);
            var xi = Vector256.LoadUnsafe(ref x, i0);
            var yi = Vector256.LoadUnsafe(ref y, i0);
            var vxi = Vector256.LoadUnsafe(ref vx, i0);
            var vyi = Vector256.LoadUnsafe(ref vy, i0);
            Vector256<float> ax = default, ay = default;
            for (int j = 0; j < SortedClusters.Lanes; j++)
            {
                var other = ~Vector256.Equals(lane, Vector256.Create(j)).AsSingle();      // not the row itself
                nuint at = i0 + (nuint)j;
                Pair(xi, yi, vxi, vyi, Unsafe.Add(ref x, at), Unsafe.Add(ref y, at), Unsafe.Add(ref vx, at),
                    Unsafe.Add(ref vy, at), other, ref ax, ref ay);
            }

            for (int p = start[cluster], end = start[cluster + 1]; p < end; p++)
            {
                nuint j0 = (nuint)(pairs[p] * SortedClusters.Lanes);
                for (nuint j = j0; j < j0 + SortedClusters.Lanes; j++)
                    Pair(xi, yi, vxi, vyi, Unsafe.Add(ref x, j), Unsafe.Add(ref y, j), Unsafe.Add(ref vx, j),
                        Unsafe.Add(ref vy, j), all, ref ax, ref ay);
            }

            ax.StoreUnsafe(ref outX, i0);
            ay.StoreUnsafe(ref outY, i0);
        }
    }

    // The 4-wide kernel: each cluster as two halves of 4 i lanes, each against the 8 rows of its own cluster (self masked)
    // and of every cluster of its list.
    private void Interact4()
    {
        var b = Clusters;
        ref float x = ref MemoryMarshal.GetArrayDataReference(b.X);
        ref float y = ref MemoryMarshal.GetArrayDataReference(b.Y);
        ref float vx = ref MemoryMarshal.GetArrayDataReference(VX);
        ref float vy = ref MemoryMarshal.GetArrayDataReference(VY);
        ref float outX = ref MemoryMarshal.GetArrayDataReference(PX);
        ref float outY = ref MemoryMarshal.GetArrayDataReference(PY);
        var start = PairStart;
        var pairs = Pairs;
        var all = Vector128<float>.AllBitsSet;
        for (int cluster = 0; cluster < b.ClusterCount; cluster++)
        {
            nuint b0 = (nuint)(cluster * SortedClusters.Lanes);
            for (int half = 0; half < SortedClusters.Lanes; half += 4)
            {
                nuint i0 = b0 + (nuint)half;
                var lane = Vector128.Create(half, half + 1, half + 2, half + 3);
                var xi = Vector128.LoadUnsafe(ref x, i0);
                var yi = Vector128.LoadUnsafe(ref y, i0);
                var vxi = Vector128.LoadUnsafe(ref vx, i0);
                var vyi = Vector128.LoadUnsafe(ref vy, i0);
                Vector128<float> ax = default, ay = default;
                for (int j = 0; j < SortedClusters.Lanes; j++)
                {
                    var other = ~Vector128.Equals(lane, Vector128.Create(j)).AsSingle();      // not the row itself
                    nuint at = b0 + (nuint)j;
                    Pair(xi, yi, vxi, vyi, Unsafe.Add(ref x, at), Unsafe.Add(ref y, at), Unsafe.Add(ref vx, at),
                        Unsafe.Add(ref vy, at), other, ref ax, ref ay);
                }

                for (int p = start[cluster], end = start[cluster + 1]; p < end; p++)
                {
                    nuint j0 = (nuint)(pairs[p] * SortedClusters.Lanes);
                    for (nuint j = j0; j < j0 + SortedClusters.Lanes; j++)
                        Pair(xi, yi, vxi, vyi, Unsafe.Add(ref x, j), Unsafe.Add(ref y, j), Unsafe.Add(ref vx, j),
                            Unsafe.Add(ref vy, j), all, ref ax, ref ay);
                }

                ax.StoreUnsafe(ref outX, i0);
                ay.StoreUnsafe(ref outY, i0);
            }
        }
    }

    // One j against the 4 i lanes, as the 8-lane Pair.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Pair(Vector128<float> xi, Vector128<float> yi, Vector128<float> vxi, Vector128<float> vyi,
        float xj, float yj, float vxj, float vyj, Vector128<float> keep, ref Vector128<float> ax, ref Vector128<float> ay)
    {
        var dx = xi - Vector128.Create(xj);
        var dy = yi - Vector128.Create(yj);
        var d2 = dx * dx + dy * dy;
        var r2 = Vector128.Create(Crowd.Range2);
        var w = (r2 - d2) & Vector128.LessThan(d2, r2) & keep;
        var drag = Vector128.Create(Crowd.Drag);
        ax += w * (dx + drag * (Vector128.Create(vxj) - vxi));
        ay += w * (dy + drag * (Vector128.Create(vyj) - vyi));
    }

    // One j against the 8 i lanes: the term of Crowd.Separate, lane by lane, zero where out of range or masked.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Pair(Vector256<float> xi, Vector256<float> yi, Vector256<float> vxi, Vector256<float> vyi,
        float xj, float yj, float vxj, float vyj, Vector256<float> keep, ref Vector256<float> ax, ref Vector256<float> ay)
    {
        var dx = xi - Vector256.Create(xj);
        var dy = yi - Vector256.Create(yj);
        var d2 = dx * dx + dy * dy;
        var r2 = Vector256.Create(Crowd.Range2);
        var w = (r2 - d2) & Vector256.LessThan(d2, r2) & keep;
        var drag = Vector256.Create(Crowd.Drag);
        ax += w * (dx + drag * (Vector256.Create(vxj) - vxi));
        ay += w * (dy + drag * (Vector256.Create(vyj) - vyi));
    }
}
