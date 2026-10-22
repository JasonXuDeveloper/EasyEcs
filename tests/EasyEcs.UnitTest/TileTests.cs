using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>
/// <see cref="Tile{TCell, TTile}"/>: a tile's cells padded with its neighbours' (faces, missing neighbours, corners,
/// layers under and over it), the active rows, the samples, its scheduling as a read, and a stencil over a grid of
/// tiles (heat diffusion) checked against the whole grid computed at once, on one thread and on four.
/// </summary>
public class TileTests
{
    public record struct TilePos(int X, int Y, int Z);

    /// <summary>A cube of 4 x 3 x 2 cells with a neighbour on each of its six faces.</summary>
    public record struct Cube(TilePos Pos) : IComponent, ITile<TilePos>
    {
        readonly TilePos IIndexed<TilePos>.Key => Pos;
        static int ITileShape.Width => 4;
        static int ITileShape.Depth => 3;
        static int ITileShape.Height => 2;
        static TileFaces ITileShape.Faces => TileFaces.All;

        static TilePos ITile<TilePos>.Beside(TilePos p, TileFaces face) => face switch
        {
            TileFaces.North => p with { Z = p.Z - 1 },
            TileFaces.South => p with { Z = p.Z + 1 },
            TileFaces.West => p with { X = p.X - 1 },
            TileFaces.East => p with { X = p.X + 1 },
            TileFaces.Below => p with { Y = p.Y - 1 },
            _ => p with { Y = p.Y + 1 },
        };
    }

    /// <summary>A column of 16 x 16 x 4 cells, neighbours on its sides only (a voxel world's chunk).</summary>
    public record struct Column(TilePos Pos) : IComponent, ITile<TilePos>
    {
        readonly TilePos IIndexed<TilePos>.Key => Pos;
        static int ITileShape.Width => 16;
        static int ITileShape.Depth => 16;
        static int ITileShape.Height => 4;
        static TilePos ITile<TilePos>.Beside(TilePos p, TileFaces face) => Side(p, face);
    }

    /// <summary>A 2D plate of 5 x 7 cells.</summary>
    public record struct Plate(TilePos Pos) : IComponent, ITile<TilePos>
    {
        readonly TilePos IIndexed<TilePos>.Key => Pos;
        static int ITileShape.Width => 5;
        static int ITileShape.Depth => 7;
        static TilePos ITile<TilePos>.Beside(TilePos p, TileFaces face) => Side(p, face);
    }

    /// <summary>A 2D plate of 16 x 16 temperatures (the heat diffusion).</summary>
    public record struct HeatPlate(TilePos Pos) : IComponent, ITile<TilePos>
    {
        readonly TilePos IIndexed<TilePos>.Key => Pos;
        static int ITileShape.Width => 16;
        static int ITileShape.Depth => 16;
        static TilePos ITile<TilePos>.Beside(TilePos p, TileFaces face) => Side(p, face);
    }

    private static TilePos Side(TilePos p, TileFaces face) => face switch
    {
        TileFaces.North => p with { Z = p.Z - 1 },
        TileFaces.South => p with { Z = p.Z + 1 },
        TileFaces.West => p with { X = p.X - 1 },
        _ => p with { X = p.X + 1 },
    };

    public record struct Cell(int V);
    public record struct Voxel(byte V);
    public record struct Heat(float T);
    public record struct NextHeat(float T);

    // ---------------- the halo ----------------

    /// <summary>A cell's value from its place in the whole grid: positive, different for every cell.</summary>
    private static int Value(int x, int y, int z) => (x + 50) * 10_000 + (y + 50) * 100 + z + 50;

    private static Entity[] Grid<TTile>(World world, IEnumerable<TilePos> places, Func<TilePos, TTile> tile, int w, int d, int h)
        where TTile : struct, IComponent
    {
        var all = new List<Entity>();
        foreach (var place in places)
        {
            var entity = world.CreateEntity(tile(place));
            var cells = entity.GetOrAdd<DynamicBuffer<Cell>>().SetCount(w * d * h);
            for (int y = 0; y < h; y++)
            for (int z = 0; z < d; z++)
            for (int x = 0; x < w; x++)
                cells[(y * d + z) * w + x] = new Cell(Value(place.X * w + x, place.Y * h + y, place.Z * d + z));
            all.Add(entity);
        }

        return all.ToArray();
    }

    /// <summary>
    /// What texel (x, y, z) of a tile at <paramref name="at"/> holds: its own cell, the cell across one face when the
    /// grid has that tile (and the shape a neighbour there), the fill beyond two faces (corners) or a missing neighbour.
    /// </summary>
    private static int Expected(HashSet<TilePos> grid, TilePos at, int x, int y, int z, int w, int d, int h, bool vertical, int fill)
    {
        int outside = (x < 0 || x >= w ? 1 : 0) + (z < 0 || z >= d ? 1 : 0) + (y < 0 || y >= h ? 1 : 0);
        if (outside > 1) return fill;
        if (outside == 1 && (y < 0 || y >= h) && !vertical) return fill;
        var owner = new TilePos(at.X + (x < 0 ? -1 : x >= w ? 1 : 0), at.Y + (y < 0 ? -1 : y >= h ? 1 : 0), at.Z + (z < 0 ? -1 : z >= d ? 1 : 0));
        return grid.Contains(owner) ? Value(at.X * w + x, at.Y * h + y, at.Z * d + z) : fill;
    }

    private static Tile<Cell, TTile> TileOf<TTile>(Entity entity, ref TileNeighbours around) where TTile : struct, IComponent, ITile<TilePos>
    {
        around = TileNeighbours.Of<TTile, TilePos>(LookupExtensions.Prepared<TTile, TilePos>(entity.World), entity);
        return new Tile<Cell, TTile>(entity, in around);
    }

    private static IEnumerable<TilePos> Box(int nx, int ny, int nz, params TilePos[] holes)
    {
        for (int x = 0; x < nx; x++)
        for (int y = 0; y < ny; y++)
        for (int z = 0; z < nz; z++)
            if (Array.IndexOf(holes, new TilePos(x, y, z)) < 0)
                yield return new TilePos(x, y, z);
    }

    [TestCase(0)]
    [TestCase(-7)]
    public void ACubesHaloComesFromItsSixNeighbours(int fill)
    {
        using var world = new World();
        var places = new HashSet<TilePos>(Box(3, 3, 3, new TilePos(1, 0, 1), new TilePos(2, 1, 1), new TilePos(1, 1, 0)));
        var cubes = Grid(world, places, s => new Cube(s), 4, 3, 2);
        int checkedTexels = 0;
        foreach (var cube in cubes)
        {
            var around = default(TileNeighbours);
            var padded = TileOf<Cube>(cube, ref around).Load(0, 1, new Cell(fill));
            var at = cube.Read<Cube>().Pos;
            for (int y = -1; y <= 2; y++)
            for (int z = -1; z <= 3; z++)
            for (int x = -1; x <= 4; x++)
            {
                int expected = Expected(places, at, x, y, z, 4, 3, 2, true, fill);
                if (padded[x, y, z].V != expected) Assert.Fail($"cube {at}, texel ({x}, {y}, {z}): {padded[x, y, z].V}, expected {expected}");
                checkedTexels++;
            }
        }

        Assert.That(checkedTexels, Is.EqualTo(cubes.Length * 6 * 5 * 4));
    }

    [Test]
    public void AColumnPadsTheLayersAskedWithTheFillUnderAndOverTheWorld()
    {
        using var world = new World();
        var places = new HashSet<TilePos>(Box(2, 1, 2, new TilePos(1, 0, 1)));
        var columns = Grid(world, places, s => new Column(s), 16, 16, 4);
        foreach (var column in columns)
        {
            var at = column.Read<Column>().Pos;
            foreach (var (from, to) in new[] { (0, 3), (1, 2), (2, 2), (0, 0), (3, 3) })
            {
                var around = default(TileNeighbours);
                var padded = TileOf<Column>(column, ref around).Load(from, to, new Cell(-1));
                Assert.That((padded.From, padded.To), Is.EqualTo((from, to)));
                for (int y = from - 1; y <= to + 1; y++)
                for (int z = -1; z <= 16; z++)
                for (int x = -1; x <= 16; x++)
                {
                    // Layers -1 and 4 are outside the column: no neighbour below or above (Faces = Sides).
                    int expected = Expected(places, at, x, y, z, 16, 16, 4, false, -1);
                    if (padded[x, y, z].V != expected) Assert.Fail($"{at} layers {from}-{to}, texel ({x}, {y}, {z}): {padded[x, y, z].V}, expected {expected}");
                }
            }
        }
    }

    [Test]
    public void APlateHasOneLayerAndNoLayersAroundIt()
    {
        using var world = new World();
        var places = new HashSet<TilePos>(Box(3, 1, 2));
        var plates = Grid(world, places, s => new Plate(s), 5, 7, 1);
        foreach (var plate in plates)
        {
            var at = plate.Read<Plate>().Pos;
            var around = default(TileNeighbours);
            var padded = TileOf<Plate>(plate, ref around).Load();
            for (int z = -1; z <= 7; z++)
            for (int x = -1; x <= 5; x++)
                Assert.That(padded[x, 0, z].V, Is.EqualTo(Expected(places, at, x, 0, z, 5, 7, 1, false, 0)), $"{at} ({x}, {z})");
        }

        var first = default(TileNeighbours);
        var tile = TileOf<Plate>(plates[0], ref first);
        Assert.That(tile.Beside(TileFaces.West), Is.EqualTo(default(EntityId)));
        Assert.That(tile.Beside(TileFaces.East), Is.EqualTo(plates[2].Id)); // Box orders x, then z: (1, 0, 0)
        Assert.That(tile.Beside(TileFaces.Above), Is.EqualTo(default(EntityId)));
        Assert.That(TileOf<Plate>(plates[0], ref first).Cells.Length, Is.EqualTo(35));
    }

    [Test]
    public void MisuseThrows()
    {
        using var world = new World();
        var plates = Grid(world, Box(2, 1, 1), s => new Plate(s), 5, 7, 1);
        var columns = Grid(world, Box(1, 1, 1), s => new Column(s), 16, 16, 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => Load<Column>(columns[0], 2, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Load<Column>(columns[0], 0, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => Load<Plate>(plates[0], 0, 1));
        Assert.Throws<InvalidOperationException>(() => NoMask(plates[0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = Sample(plates[0], -2, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = Sample(plates[0], 0, 1, 0));   // a 2D tile has no layer above
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = Sample(plates[0], 0, 0, 2));
        Assert.That(Sample(plates[0], 5, 0, 0), Is.EqualTo(Value(5, 0, 0)));             // the cell after the row: the east neighbour's
        Assert.That(Sample(plates[0], -1, 0, -1), Is.Zero);                              // a corner: the fill
        plates[1].GetOrAdd<DynamicBuffer<Cell>>().SetCount(3);                          // a neighbour of 3 cells
        Assert.Throws<InvalidOperationException>(() => Load<Plate>(plates[0], 0, 0));
        plates[1].GetOrAdd<DynamicBuffer<Cell>>().Clear();                              // no cells: no neighbour
        Assert.DoesNotThrow(() => Load<Plate>(plates[0], 0, 0));

        static void Load<TTile>(Entity e, int from, int to) where TTile : struct, IComponent, ITile<TilePos>
        {
            var around = default(TileNeighbours);
            TileOf<TTile>(e, ref around).Load(from, to);
        }

        static void NoMask(Entity e)
        {
            var around = default(TileNeighbours);
            TileOf<Plate>(e, ref around).Load(0, 0, mask: false).Rows();
        }

        static int Sample(Entity e, int dx, int dy, int dz)
        {
            var around = default(TileNeighbours);
            var padded = TileOf<Plate>(e, ref around).Load();
            return padded.At(new TileRow<Plate>(0, 0))[dx, dy, dz].V;
        }
    }

    // ---------------- the active rows ----------------

    [TestCase(TileReads.Self)]
    [TestCase(TileReads.Layer)]
    [TestCase(TileReads.Layer | TileReads.Above)]
    [TestCase(TileReads.All)]
    [TestCase(TileReads.Sides | TileReads.Below)]
    public void RowsVisitsTheRowsWhoseStencilReadsACellThatIsNotZero(TileReads reads)
    {
        using var world = new World();
        var random = new Random(7);
        var places = Box(2, 1, 2, new TilePos(1, 0, 0)).ToArray();
        var columns = new List<Entity>();
        foreach (var place in places)
        {
            var column = world.CreateEntity(new Column(place));
            var cells = column.GetOrAdd<DynamicBuffer<Voxel>>().SetCount(16 * 16 * 4);
            for (int i = 0; i < 40; i++) cells[random.Next(cells.Length)] = new Voxel((byte)random.Next(1, 256));
            columns.Add(column);
        }

        var index = LookupExtensions.Prepared<Column, TilePos>(world);
        foreach (var column in columns)
        foreach (var (from, to) in new[] { (0, 3), (1, 2), (3, 3) })
        {
            var around = TileNeighbours.Of<Column, TilePos>(index, column);
            var padded = new Tile<Voxel, Column>(column, in around).Load(from, to);
            var visited = new HashSet<(int, int)>();
            var order = new List<(int, int)>();
            foreach (var row in padded.Rows(reads))
            {
                visited.Add((row.Y, row.Z));
                order.Add((row.Y, row.Z));
                Assert.That(row.Index, Is.EqualTo((row.Y * 16 + row.Z) * 16));
            }

            for (int i = 1; i < order.Count; i++)
                Assert.That(order[i].CompareTo(order[i - 1]), Is.Positive, "layer by layer, row by row");
            for (int y = from; y <= to; y++)
            for (int z = 0; z < 16; z++)
            {
                bool expected = (reads & TileReads.Self) != 0 && Active(padded, y, z)
                    || (reads & TileReads.Sides) != 0 && (Active(padded, y, z - 1) || Active(padded, y, z + 1))
                    || (reads & TileReads.Above) != 0 && Active(padded, y + 1, z)
                    || (reads & TileReads.Below) != 0 && Active(padded, y - 1, z);
                Assert.That(visited.Contains((y, z)), Is.EqualTo(expected), $"layers {from}-{to}, row ({y}, {z}) reading {reads}");
            }
        }

        // A row of the tile holds a cell that is not zero, its halo cells at each end counting; a halo row, its cells.
        static bool Active(PaddedTile<Voxel, Column> padded, int y, int z)
        {
            bool inside = z is >= 0 and < 16;
            for (int x = inside ? -1 : 0; x <= (inside ? 16 : 15); x++)
                if (padded[x, y, z].V != 0) return true;
            return false;
        }
    }

    // ---------------- a stencil over a grid ----------------

    /// <summary>
    /// Heat spreading over plates of 16 x 16 cells, a five-point stencil four cells at a time: what a user of the tile
    /// writes. Plates missing from the grid are cold (the fill). Quiet rows (cold, beside cold) stay cold, so the
    /// stencil skips them, and the step's buffer already holds their zeros (it equals the heat after every commit).
    /// </summary>
    public class DiffuseSystem : EcsSystem
    {
        public const float Rate = 0.2f;

        [ForEach(Parallel = true, ChunkRows = 8)]
        static void Diffuse(Tile<Heat, HeatPlate> heat, Mut<DynamicBuffer<NextHeat>> next)
        {
            var plate = heat.Load();
            var into = MemoryMarshal.Cast<NextHeat, float>(next.Write().AsSpan());
            foreach (var row in plate.Rows(TileReads.Layer))
            {
                var cells = plate.At(row);
                for (int x = 0; x < 16; x += Vector128<float>.Count)
                {
                    var here = cells.Load128<float>(x);
                    var around = cells.Load128<float>(x - 1) + cells.Load128<float>(x + 1) + cells.Load128<float>(x, 0, -1)
                        + cells.Load128<float>(x, 0, 1);
                    (here + (around - here * 4f) * Rate).CopyTo(into.Slice(row.Index + x));
                }
            }
        }
    }

    /// <summary>Makes the step the heat, once every plate has stepped.</summary>
    public class CommitHeatSystem : EcsSystem
    {
        [ForEach]
        static void Commit(in DynamicBuffer<NextHeat> next, Mut<DynamicBuffer<Heat>> heat) =>
            next.AsReadOnlySpan().CopyTo(MemoryMarshal.Cast<Heat, NextHeat>(heat.Write().AsSpan()));
    }

    private static (World World, Dictionary<TilePos, Entity> Plates) HeatWorld(int threads, int size, params TilePos[] holes)
    {
        var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        var plates = new Dictionary<TilePos, Entity>();
        foreach (var place in Box(size, 1, size, holes))
        {
            var plate = world.CreateEntity(new HeatPlate(place));
            var heat = plate.GetOrAdd<DynamicBuffer<Heat>>().SetCount(256);
            plate.GetOrAdd<DynamicBuffer<NextHeat>>().SetCount(256);
            if ((place.X + 2 * place.Z) % 5 == 0) heat[(place.X * 3 % 16) * 16 + place.Z * 7 % 16] = new Heat(1000f); // a few hot places
            plates.Add(place, plate);
        }

        world.AddSystem<DiffuseSystem>();
        world.AddSystem<CommitHeatSystem>();
        return (world, plates);
    }

    [TestCase(1)]
    [TestCase(4)]
    public void HeatOnTilesIsTheHeatOfTheWholeGrid(int threads)
    {
        const int size = 9, cells = size * 16;
        var holes = new[] { new TilePos(4, 0, 4), new TilePos(0, 0, 8), new TilePos(7, 0, 2) };
        var (world, plates) = HeatWorld(threads, size, holes);
        using var _ = world;

        // The whole grid at once, the same arithmetic per cell, the holes cold.
        var grid = new float[cells, cells];
        foreach (var (place, plate) in plates)
        {
            var heat = plate.Read<DynamicBuffer<Heat>>().AsReadOnlySpan();
            for (int z = 0; z < 16; z++)
            for (int x = 0; x < 16; x++)
                grid[place.X * 16 + x, place.Z * 16 + z] = heat[z * 16 + x].T;
        }

        for (int step = 0; step < 60; step++)
        {
            world.Update();
            var next = new float[cells, cells];
            for (int z = 0; z < cells; z++)
            for (int x = 0; x < cells; x++)
            {
                if (!plates.ContainsKey(new TilePos(x / 16, 0, z / 16))) continue;
                float here = grid[x, z];
                float around = At(x - 1, z) + At(x + 1, z) + At(x, z - 1) + At(x, z + 1);
                next[x, z] = here + (around - here * 4f) * DiffuseSystem.Rate;
            }

            grid = next;
            foreach (var (place, plate) in plates)
            {
                var heat = plate.Read<DynamicBuffer<Heat>>().AsReadOnlySpan();
                for (int z = 0; z < 16; z++)
                for (int x = 0; x < 16; x++)
                    if (heat[z * 16 + x].T != grid[place.X * 16 + x, place.Z * 16 + z])
                        Assert.Fail($"step {step}, plate {place}, cell ({x}, {z}): {heat[z * 16 + x].T}, expected {grid[place.X * 16 + x, place.Z * 16 + z]}");
            }
        }

        float At(int x, int z) => x < 0 || z < 0 || x >= cells || z >= cells ? 0 : grid[x, z];
    }

    [Test]
    public void ATileIsScheduledAsAReadOfItsCellsAndItsGrid()
    {
        using (var world = new World())
        {
            world.AddSystem<DiffuseSystem>();
            world.AddSystem<CommitHeatSystem>(); // writes DynamicBuffer<Heat>: another stage
            Assert.That(world.DescribeSchedule().Groups[0].Stages.Length, Is.EqualTo(2), world.DescribeSchedule().ToString());
        }

        using (var world = new World())
        {
            world.AddSystem<DiffuseSystem>();
            world.AddSystem<ReadHeatSystem>(); // reads both: one stage
            Assert.That(world.DescribeSchedule().Groups[0].Stages.Length, Is.EqualTo(1), world.DescribeSchedule().ToString());
        }
    }

    public class ReadHeatSystem : EcsSystem
    {
        [ForEach]
        static void Read(in DynamicBuffer<Heat> heat, in HeatPlate plate, ref Health health) => health.Value = heat.Count + plate.Pos.X;
    }

    [Test]
    public void AStencilOnTilesDoesNotAllocate()
    {
        var (world, _) = HeatWorld(4, 8);
        using var __ = world;
        for (int i = 0; i < 30; i++) world.Update(); // every thread gets its texture
        long least = long.MaxValue;
        for (int window = 0; window < 3 && least != 0; window++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) world.Update();
            least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.That(least, Is.Zero);
    }
}
