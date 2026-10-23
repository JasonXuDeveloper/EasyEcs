using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>
/// <see cref="Tile{TCell, TTile}"/>: a tile's cells padded with its neighbors' (faces, edges and corners, halos of one to
/// five cells, missing and empty neighbors, the boundaries, layers under and over it), the active rows, the samples,
/// its scheduling as a read, and stencils over grids of tiles (heat, a blur per boundary, the Game of Life on a torus, a
/// fourth-order Laplacian, a 27-point sum, a 1D smoothing) checked against the whole grid computed at once, on one thread
/// and on four.
/// </summary>
public class TileTests
{
    public record struct TilePos(int X, int Y, int Z);

    private static TilePos Step(TilePos p, TileFaces face) => face switch
    {
        TileFaces.North => p with { Z = p.Z - 1 },
        TileFaces.South => p with { Z = p.Z + 1 },
        TileFaces.West => p with { X = p.X - 1 },
        TileFaces.East => p with { X = p.X + 1 },
        TileFaces.Below => p with { Y = p.Y - 1 },
        _ => p with { Y = p.Y + 1 },
    };

    /// <summary>The tiles of a torus of <see cref="Torus"/> x <see cref="Torus"/> tiles: east of the last is the first.</summary>
    private const int Torus = 4;

    private static TilePos Wrap(TilePos p, TileFaces face)
    {
        var next = Step(p, face);
        return new TilePos((next.X + Torus) % Torus, next.Y, (next.Z + Torus) % Torus);
    }

    // ---------------- shapes ----------------

    /// <summary>A cube of 4 x 3 x 2 cells with a neighbor on each of its six faces.</summary>
    public record struct Cube(TilePos Pos) : IComponent, ITile<TilePos>
    {
        readonly TilePos IIndexed<TilePos>.Key => Pos;
        static int ITileShape.Width => 4;
        static int ITileShape.Depth => 3;
        static int ITileShape.Height => 2;
        static TileFaces ITileShape.Faces => TileFaces.All;
        static TilePos ITile<TilePos>.Beside(TilePos p, TileFaces face) => Step(p, face);
    }

    /// <summary>The cube with its 26 neighbors (Moore).</summary>
    public record struct MooreCube(TilePos Pos) : IComponent, ITile<TilePos>
    {
        readonly TilePos IIndexed<TilePos>.Key => Pos;
        static int ITileShape.Width => 4;
        static int ITileShape.Depth => 3;
        static int ITileShape.Height => 2;
        static TileFaces ITileShape.Faces => TileFaces.All;
        static TileNeighborhood ITileShape.Neighborhood => TileNeighborhood.All;
        static TilePos ITile<TilePos>.Beside(TilePos p, TileFaces face) => Step(p, face);
    }

    /// <summary>The cube with its 18 neighbors across faces and edges, a halo of 2 (its height), clamped.</summary>
    public record struct EdgeCube(TilePos Pos) : IComponent, ITile<TilePos>
    {
        readonly TilePos IIndexed<TilePos>.Key => Pos;
        static int ITileShape.Width => 4;
        static int ITileShape.Depth => 3;
        static int ITileShape.Height => 2;
        static int ITileShape.Halo => 2;
        static TileFaces ITileShape.Faces => TileFaces.All;
        static TileNeighborhood ITileShape.Neighborhood => TileNeighborhood.FacesAndEdges;
        static TileBoundary ITileShape.Boundary => TileBoundary.Clamp;
        static TilePos ITile<TilePos>.Beside(TilePos p, TileFaces face) => Step(p, face);
    }

    /// <summary>The cube with its 26 neighbors, a halo of 2, mirrored.</summary>
    public record struct MirrorCube(TilePos Pos) : IComponent, ITile<TilePos>
    {
        readonly TilePos IIndexed<TilePos>.Key => Pos;
        static int ITileShape.Width => 4;
        static int ITileShape.Depth => 3;
        static int ITileShape.Height => 2;
        static int ITileShape.Halo => 2;
        static TileFaces ITileShape.Faces => TileFaces.All;
        static TileNeighborhood ITileShape.Neighborhood => TileNeighborhood.All;
        static TileBoundary ITileShape.Boundary => TileBoundary.Mirror;
        static TilePos ITile<TilePos>.Beside(TilePos p, TileFaces face) => Step(p, face);
    }

    /// <summary>A column of 16 x 16 x 4 cells, neighbors on its sides only (a voxel world's chunk).</summary>
    public record struct Column(TilePos Pos) : IComponent, ITile<TilePos>
    {
        readonly TilePos IIndexed<TilePos>.Key => Pos;
        static int ITileShape.Width => 16;
        static int ITileShape.Depth => 16;
        static int ITileShape.Height => 4;
        static TilePos ITile<TilePos>.Beside(TilePos p, TileFaces face) => Step(p, face);
    }

    /// <summary>A column of 8 x 8 x 6 cells with a halo of 2, its corners and its neighbors below and above.</summary>
    public record struct DeepColumn(TilePos Pos) : IComponent, ITile<TilePos>
    {
        readonly TilePos IIndexed<TilePos>.Key => Pos;
        static int ITileShape.Width => 8;
        static int ITileShape.Depth => 8;
        static int ITileShape.Height => 6;
        static int ITileShape.Halo => 2;
        static TileFaces ITileShape.Faces => TileFaces.All;
        static TileNeighborhood ITileShape.Neighborhood => TileNeighborhood.All;
        static TilePos ITile<TilePos>.Beside(TilePos p, TileFaces face) => Step(p, face);
    }

    /// <summary>A 2D plate of 5 x 7 cells.</summary>
    public record struct Plate(TilePos Pos) : IComponent, ITile<TilePos>
    {
        readonly TilePos IIndexed<TilePos>.Key => Pos;
        static int ITileShape.Width => 5;
        static int ITileShape.Depth => 7;
        static TilePos ITile<TilePos>.Beside(TilePos p, TileFaces face) => Step(p, face);
    }

    /// <summary>The plate with a halo of 2 and its 8 neighbors.</summary>
    public record struct WidePlate(TilePos Pos) : IComponent, ITile<TilePos>
    {
        readonly TilePos IIndexed<TilePos>.Key => Pos;
        static int ITileShape.Width => 5;
        static int ITileShape.Depth => 7;
        static int ITileShape.Halo => 2;
        static TileNeighborhood ITileShape.Neighborhood => TileNeighborhood.All;
        static TilePos ITile<TilePos>.Beside(TilePos p, TileFaces face) => Step(p, face);
    }

    /// <summary>The plate with a halo of 3, its 8 neighbors, clamped.</summary>
    public record struct ClampPlate(TilePos Pos) : IComponent, ITile<TilePos>
    {
        readonly TilePos IIndexed<TilePos>.Key => Pos;
        static int ITileShape.Width => 5;
        static int ITileShape.Depth => 7;
        static int ITileShape.Halo => 3;
        static TileNeighborhood ITileShape.Neighborhood => TileNeighborhood.All;
        static TileBoundary ITileShape.Boundary => TileBoundary.Clamp;
        static TilePos ITile<TilePos>.Beside(TilePos p, TileFaces face) => Step(p, face);
    }

    /// <summary>The plate with the widest halo it can have, its width (5), mirrored.</summary>
    public record struct LimitPlate(TilePos Pos) : IComponent, ITile<TilePos>
    {
        readonly TilePos IIndexed<TilePos>.Key => Pos;
        static int ITileShape.Width => 5;
        static int ITileShape.Depth => 7;
        static int ITileShape.Halo => 5;
        static TileNeighborhood ITileShape.Neighborhood => TileNeighborhood.All;
        static TileBoundary ITileShape.Boundary => TileBoundary.Mirror;
        static TilePos ITile<TilePos>.Beside(TilePos p, TileFaces face) => Step(p, face);
    }

    /// <summary>A halo one cell wider than the plate: it would reach past the tile beside (EECS072; Pad throws too).</summary>
#pragma warning disable EECS072
    public record struct OverPlate(TilePos Pos) : IComponent, ITile<TilePos>
    {
        readonly TilePos IIndexed<TilePos>.Key => Pos;
        static int ITileShape.Width => 5;
        static int ITileShape.Depth => 7;
        static int ITileShape.Halo => 6;
        static TilePos ITile<TilePos>.Beside(TilePos p, TileFaces face) => Step(p, face);
    }
#pragma warning restore EECS072

    /// <summary>A 1D tile: one row of 8 cells, neighbors west and east, a halo of 2.</summary>
    public record struct Line(TilePos Pos) : IComponent, ITile<TilePos>
    {
        readonly TilePos IIndexed<TilePos>.Key => Pos;
        static int ITileShape.Width => 8;
        static int ITileShape.Depth => 1;
        static int ITileShape.Halo => 2;
        static TileFaces ITileShape.Faces => TileFaces.West | TileFaces.East;
        static TilePos ITile<TilePos>.Beside(TilePos p, TileFaces face) => Step(p, face);
    }

    public record struct Cell(int V);
    public record struct NextCell(int V);
    public record struct Voxel(byte V);

    // ---------------- the halo ----------------

    /// <summary>A cell's value from its place in the whole grid: positive, different for every cell.</summary>
    private static int Value(int x, int y, int z) => (x + 50) * 10_000 + (y + 50) * 100 + z + 50;

    private static Entity[] Grid<TTile>(World world, IEnumerable<TilePos> places, Func<TilePos, TTile> tile, ICollection<TilePos> empty = null)
        where TTile : struct, IComponent, ITileShape
    {
        int w = TTile.Width, d = TTile.Depth, h = TTile.Height;
        var all = new List<Entity>();
        foreach (var place in places)
        {
            var entity = world.CreateEntity(tile(place));
            ref var buffer = ref entity.GetOrAdd<DynamicBuffer<Cell>>();
            if (empty?.Contains(place) != true)
            {
                var cells = buffer.SetCount(w * d * h);
                for (int y = 0; y < h; y++)
                for (int z = 0; z < d; z++)
                for (int x = 0; x < w; x++)
                    cells[(y * d + z) * w + x] = new Cell(Value(place.X * w + x, place.Y * h + y, place.Z * d + z));
            }
            all.Add(entity);
        }

        return all.ToArray();
    }

    /// <summary>
    /// What texel (x, y, z) of a tile at <paramref name="at"/> holds, from the definition: the cell at that place of the
    /// whole grid when its tile is one the shape reads (its faces' axes, its neighborhood) and has cells; where the tile
    /// across an axis is missing, the clamped or mirrored coordinate of the tile's own along that axis; else the fill.
    /// </summary>
    private static int Expected<TTile>(ISet<TilePos> grid, TilePos at, int x, int y, int z, int fill) where TTile : ITileShape
    {
        int w = TTile.Width, d = TTile.Depth, h = TTile.Height;
        int ox = Off(x, w), oy = Off(y, h), oz = Off(z, d);
        int sx = x - ox * w, sy = y - oy * h, sz = z - oz * d;
        if (TTile.Boundary != TileBoundary.Fill)
        {
            if (ox != 0 && !Has(ox, 0, 0)) (ox, sx) = (0, Remap(x, w));
            if (oz != 0 && !Has(0, 0, oz)) (oz, sz) = (0, Remap(z, d));
            if (oy != 0 && !Has(0, oy, 0)) (oy, sy) = (0, Remap(y, h));
        }
        if ((ox | oy | oz) != 0 && !Has(ox, oy, oz)) return fill;
        return Value((at.X + ox) * w + sx, (at.Y + oy) * h + sy, (at.Z + oz) * d + sz);

        static int Off(int c, int n) => c < 0 ? -1 : c >= n ? 1 : 0;
        static int Remap(int c, int n) => TTile.Boundary == TileBoundary.Clamp ? Math.Clamp(c, 0, n - 1) : c < 0 ? -1 - c : 2 * n - 1 - c;

        bool Has(int dx, int dy, int dz)
        {
            var faces = TTile.Faces;
            if (h == 1) faces &= TileFaces.Sides;
            if (Math.Abs(dx) + Math.Abs(dy) + Math.Abs(dz) > (int)TTile.Neighborhood + 1) return false;
            if (dx != 0 && (faces & (dx < 0 ? TileFaces.West : TileFaces.East)) == 0) return false;
            if (dz != 0 && (faces & (dz < 0 ? TileFaces.North : TileFaces.South)) == 0) return false;
            if (dy != 0 && (faces & (dy < 0 ? TileFaces.Below : TileFaces.Above)) == 0) return false;
            return grid.Contains(new TilePos(at.X + dx, at.Y + dy, at.Z + dz));
        }
    }

    private static Tile<TCell, TTile> TileOf<TCell, TTile>(Entity entity, [System.Diagnostics.CodeAnalysis.UnscopedRef] out TileNeighbors around)
        where TCell : unmanaged where TTile : struct, IComponent, ITile<TilePos>
    {
        TileNeighbors.Of<TTile, TilePos>(LookupExtensions.Prepared<TTile, TilePos>(entity.World), entity, out around);
        return new Tile<TCell, TTile>(entity, in around);
    }

    private static IEnumerable<TilePos> Box(int nx, int ny, int nz, params TilePos[] holes)
    {
        for (int x = 0; x < nx; x++)
        for (int y = 0; y < ny; y++)
        for (int z = 0; z < nz; z++)
            if (Array.IndexOf(holes, new TilePos(x, y, z)) < 0)
                yield return new TilePos(x, y, z);
    }

    /// <summary>Loads every tile of a grid, layers <paramref name="from"/> to <paramref name="to"/>, and checks every texel against <see cref="Expected{TTile}"/>.</summary>
    private static int CheckHalo<TTile>(ISet<TilePos> places, ICollection<TilePos> empty, Func<TilePos, TTile> make, int fill, int from = 0, int to = -1)
        where TTile : struct, IComponent, ITile<TilePos>
    {
        using var world = new World();
        var tiles = Grid(world, places, make, empty);
        var full = new HashSet<TilePos>(places.Where(p => !empty.Contains(p)));
        int w = TTile.Width, d = TTile.Depth, r = TTile.Halo, hy = TTile.Height > 1 ? r : 0;
        if (to < 0) to = TTile.Height - 1;
        int texels = 0;
        foreach (var tile in tiles)
        {
            var at = tile.Read<TTile>().Key;
            if (empty.Contains(at)) continue;
            foreach (bool mask in new[] { true, false }) // the texture is the same with or without the row masks
            {
                var padded = TileOf<Cell, TTile>(tile, out var around).Pad(from, to, new Cell(fill), mask);
                Assert.That((padded.From, padded.To), Is.EqualTo((from, to)));
                for (int y = from - hy; y <= to + hy; y++)
                for (int z = -r; z < d + r; z++)
                for (int x = -r; x < w + r; x++)
                {
                    int expected = Expected<TTile>(full, at, x, y, z, fill);
                    if (padded[x, y, z].V != expected)
                        Assert.Fail($"{typeof(TTile).Name} {at} layers {from}-{to} (mask {mask}), texel ({x}, {y}, {z}): {padded[x, y, z].V}, expected {expected}");
                    if (mask) texels++;
                }
            }
        }

        return texels;
    }

    private static readonly TilePos[] CubeHoles = [new(1, 0, 1), new(2, 1, 1), new(1, 1, 0), new(0, 0, 0), new(2, 2, 2)];
    private static readonly TilePos[] CubeEmpty = [new(0, 1, 0), new(2, 0, 2)];

    [TestCase(0)]
    [TestCase(-7)]
    public void ACubesHaloComesFromItsSixNeighbors(int fill)
    {
        var places = new HashSet<TilePos>(Box(3, 3, 3, CubeHoles));
        Assert.That(CheckHalo(places, CubeEmpty, s => new Cube(s), fill), Is.EqualTo((places.Count - CubeEmpty.Length) * 6 * 5 * 4));
    }

    [TestCase(0)]
    [TestCase(-7)]
    public void AMooreCubesHaloComesFromItsTwentySixNeighbors(int fill)
    {
        var places = new HashSet<TilePos>(Box(3, 3, 3, CubeHoles));
        Assert.That(CheckHalo(places, CubeEmpty, s => new MooreCube(s), fill), Is.Positive);
        using var world = new World();
        var cubes = Grid(world, places, s => new MooreCube(s));
        var middle = cubes.Single(c => c.Read<MooreCube>().Pos == new TilePos(1, 1, 1));
        var tile = TileOf<Cell, MooreCube>(middle, out var around);
        int found = 0;
        for (int dy = -1; dy <= 1; dy++)
        for (int dz = -1; dz <= 1; dz++)
        for (int dx = -1; dx <= 1; dx++)
        {
            if ((dx | dy | dz) == 0) continue;
            var expected = cubes.FirstOrDefault(c => c.Read<MooreCube>().Pos == new TilePos(1 + dx, 1 + dy, 1 + dz));
            Assert.That(tile.Beside(dx, dy, dz), Is.EqualTo(expected.Id), $"({dx}, {dy}, {dz})");
            found += expected.Id == default ? 0 : 1;
        }
        Assert.That(found, Is.EqualTo(26 - CubeHoles.Count(h => Math.Abs(h.X - 1) <= 1 && Math.Abs(h.Y - 1) <= 1 && Math.Abs(h.Z - 1) <= 1)));
        Assert.That(tile.Beside(TileFaces.Above), Is.EqualTo(tile.Beside(0, 1, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => TileOf<Cell, MooreCube>(middle, out var a).Beside(2, 0, 0));
    }

    [Test]
    public void EdgesAndCornersFollowTheNeighborhoodAndTheBoundary()
    {
        // Clamped, and mirrored, over a full grid (every axis a boundary somewhere) and one with holes.
        var full = new HashSet<TilePos>(Box(3, 3, 3));
        var holes = new HashSet<TilePos>(Box(3, 3, 3, CubeHoles));
        foreach (var places in new[] { full, holes })
        {
            Assert.That(CheckHalo(places, CubeEmpty, s => new EdgeCube(s), -3), Is.Positive);
            Assert.That(CheckHalo(places, CubeEmpty, s => new MirrorCube(s), -3), Is.Positive);
            Assert.That(CheckHalo(places, [], s => new MirrorCube(s), 0, 1, 1), Is.Positive);
        }
    }

    [Test]
    public void AColumnPadsTheLayersAskedWithTheFillUnderAndOverTheWorld()
    {
        var places = new HashSet<TilePos>(Box(2, 1, 2, new TilePos(1, 0, 1)));
        foreach (var (from, to) in new[] { (0, 3), (1, 2), (2, 2), (0, 0), (3, 3) })
            CheckHalo(places, [], s => new Column(s), -1, from, to);
        var deep = new HashSet<TilePos>(Box(3, 3, 3, CubeHoles));
        foreach (var (from, to) in new[] { (0, 5), (2, 3), (0, 0), (5, 5) })
            CheckHalo(deep, CubeEmpty, s => new DeepColumn(s), 9, from, to);
    }

    [Test]
    public void PlatesHaveOneLayerAndHalosOfOneToFiveCells()
    {
        var places = new HashSet<TilePos>(Box(3, 1, 3, new TilePos(1, 0, 0)));
        var empty = new[] { new TilePos(2, 0, 2) };
        CheckHalo(places, empty, s => new Plate(s), 0);
        CheckHalo(places, empty, s => new WidePlate(s), 4);
        CheckHalo(places, empty, s => new ClampPlate(s), 4);
        CheckHalo(places, empty, s => new LimitPlate(s), 4);
        CheckHalo(new HashSet<TilePos>(Box(3, 1, 3)), [], s => new LimitPlate(s), 4);
        CheckHalo(new HashSet<TilePos>(Box(4, 1, 1, new TilePos(2, 0, 0))), [new TilePos(3, 0, 0)], s => new Line(s), 6);

        using var world = new World();
        var plates = Grid(world, Box(3, 1, 2), s => new Plate(s));
        var tile = TileOf<Cell, Plate>(plates[0], out var first);
        Assert.That(tile.Beside(TileFaces.West), Is.EqualTo(default(EntityId)));
        Assert.That(tile.Beside(TileFaces.East), Is.EqualTo(plates[2].Id)); // Box orders x, then z: (1, 0, 0)
        Assert.That(tile.Beside(TileFaces.Above), Is.EqualTo(default(EntityId)));
        Assert.That(tile.Beside(1, 0, 1), Is.EqualTo(default(EntityId)), "no corners in a neighborhood of faces");
        Assert.That(TileOf<Cell, Plate>(plates[0], out first).Cells.Length, Is.EqualTo(35));
    }

    [Test]
    public void MisuseThrows()
    {
        using var world = new World();
        var plates = Grid(world, Box(2, 1, 1), s => new Plate(s));
        var columns = Grid(world, Box(1, 1, 1), s => new Column(s));
        var wide = Grid(world, Box(1, 1, 1), s => new WidePlate(s));
        var over = Grid(world, Box(1, 1, 1), s => new OverPlate(s));
        Assert.Throws<ArgumentOutOfRangeException>(() => Pad<Column>(columns[0], 2, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Pad<Column>(columns[0], 0, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => Pad<Plate>(plates[0], 0, 1));
        var shape = Assert.Throws<InvalidOperationException>(() => Pad<OverPlate>(over[0], 0, 0));
        Assert.That(shape.Message, Does.Contain("halo of 6"));
        Assert.Throws<InvalidOperationException>(() => NoMask(plates[0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = Sample<Plate>(plates[0], -2, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = Sample<Plate>(plates[0], 0, 1, 0));   // a 2D tile has no layer above
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = Sample<Plate>(plates[0], 0, 0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = Sample<WidePlate>(wide[0], -3, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = Sample<WidePlate>(wide[0], 0, 0, -3));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = Load128<WidePlate>(wide[0], 4));     // 4 ints from 4: past the halo
        Assert.That(Load128<WidePlate>(wide[0], 3), Is.Zero);                                    // 3 to 6, the last two the halo
        Assert.That(Sample<WidePlate>(wide[0], -2, 0, -2), Is.Zero);
        Assert.That(Sample<Plate>(plates[0], 5, 0, 0), Is.EqualTo(Value(5, 0, 0)));              // the cell after the row: the east neighbor's
        Assert.That(Sample<Plate>(plates[0], -1, 0, -1), Is.Zero);                               // a corner: the fill
        plates[1].GetOrAdd<DynamicBuffer<Cell>>().SetCount(3);                                   // a neighbor of 3 cells
        Assert.Throws<InvalidOperationException>(() => Pad<Plate>(plates[0], 0, 0));
        plates[1].GetOrAdd<DynamicBuffer<Cell>>().Clear();                                       // no cells: no neighbor
        Assert.DoesNotThrow(() => Pad<Plate>(plates[0], 0, 0));

        static void Pad<TTile>(Entity e, int from, int to) where TTile : struct, IComponent, ITile<TilePos> =>
            TileOf<Cell, TTile>(e, out _).Pad(from, to);

        static void NoMask(Entity e) => TileOf<Cell, Plate>(e, out _).Pad(0, 0, mask: false).Rows();

        static int Sample<TTile>(Entity e, int dx, int dy, int dz) where TTile : struct, IComponent, ITile<TilePos> =>
            TileOf<Cell, TTile>(e, out _).Pad().At(new TileRow<TTile>(0, 0))[dx, dy, dz].V;

        static int Load128<TTile>(Entity e, int dx) where TTile : struct, IComponent, ITile<TilePos> =>
            TileOf<Cell, TTile>(e, out _).Pad().At(new TileRow<TTile>(0, 0)).Load128<int>(dx).GetElement(3);
    }

    // ---------------- the active rows ----------------

    [TestCase(TileReads.Self)]
    [TestCase(TileReads.Layer)]
    [TestCase(TileReads.Layer | TileReads.Above)]
    [TestCase(TileReads.Cross)]
    [TestCase(TileReads.All)]
    [TestCase(TileReads.NorthSouth | TileReads.Below)]
    [TestCase(TileReads.Diagonals)]
    public void RowsVisitsTheRowsWhoseStencilReadsACellThatIsNotZero(TileReads reads)
    {
        // A halo of 1: exactly the rows whose stencil reads a cell that is not zero.
        VisitedRows<Column>(reads, Box(2, 1, 2, new TilePos(1, 0, 0)), 40, exact: true, [(0, 3), (1, 2), (3, 3)]);
        // A halo of 2: those rows at least (a halo row north or south that is not zero marks the 2 rows beside it).
        VisitedRows<DeepColumn>(reads, Box(2, 2, 2, new TilePos(1, 0, 0)), 6, exact: false, [(0, 5), (2, 3), (5, 5)]);
    }

    private static void VisitedRows<TTile>(TileReads reads, IEnumerable<TilePos> places, int wet, bool exact, (int From, int To)[] ranges)
        where TTile : struct, IComponent, ITile<TilePos>
    {
        using var world = new World();
        var random = new Random(7);
        int w = TTile.Width, d = TTile.Depth, h = TTile.Height, r = TTile.Halo, hy = h > 1 ? r : 0;
        var tiles = new List<Entity>();
        foreach (var place in places)
        {
            var make = (Func<TilePos, TTile>)(p => (TTile)Activator.CreateInstance(typeof(TTile), p)!);
            var tile = world.CreateEntity(make(place));
            var cells = tile.GetOrAdd<DynamicBuffer<Voxel>>().SetCount(w * d * h);
            for (int i = 0; i < wet; i++) cells[random.Next(cells.Length)] = new Voxel((byte)random.Next(1, 256));
            tiles.Add(tile);
        }

        foreach (var tile in tiles)
        foreach (var (from, to) in ranges)
        {
            var padded = TileOf<Voxel, TTile>(tile, out var around).Pad(from, to);
            var visited = new HashSet<(int, int)>();
            var order = new List<(int, int)>();
            foreach (var row in padded.Rows(reads))
            {
                visited.Add((row.Y, row.Z));
                order.Add((row.Y, row.Z));
                Assert.That(row.Index, Is.EqualTo((row.Y * d + row.Z) * w));
            }

            for (int i = 1; i < order.Count; i++)
                Assert.That(order[i].CompareTo(order[i - 1]), Is.Positive, "layer by layer, row by row");
            for (int y = from; y <= to; y++)
            for (int z = 0; z < d; z++)
            {
                bool needed = (reads & TileReads.Self) != 0 && Active(padded, y, z);
                for (int k = 1; k <= r; k++)
                {
                    needed |= (reads & TileReads.NorthSouth) != 0 && (Active(padded, y, z - k) || Active(padded, y, z + k));
                    if (k > hy) continue;
                    needed |= (reads & TileReads.Above) != 0 && Active(padded, y + k, z);
                    needed |= (reads & TileReads.Below) != 0 && Active(padded, y - k, z);
                    for (int j = 1; j <= r; j++)
                        needed |= (reads & TileReads.Diagonals) != 0 && (Active(padded, y + k, z - j) || Active(padded, y + k, z + j)
                            || Active(padded, y - k, z - j) || Active(padded, y - k, z + j));
                }

                bool seen = visited.Contains((y, z));
                if (exact) Assert.That(seen, Is.EqualTo(needed), $"layers {from}-{to}, row ({y}, {z}) reading {reads}");
                else if (needed) Assert.That(seen, Is.True, $"layers {from}-{to}, row ({y}, {z}) reading {reads}");
            }
        }

        // A row of the texture (a row of the tile or a halo row) holds a cell that is not zero, its halo cells counting.
        static bool Active(PaddedTile<Voxel, TTile> padded, int y, int z)
        {
            int r = TTile.Halo;
            for (int x = -r; x < TTile.Width + r; x++)
                if (padded[x, y, z].V != 0) return true;
            return false;
        }
    }

    // ---------------- stencils over grids ----------------

    /// <summary>A 2D plate of 16 x 16 temperatures (the heat diffusion).</summary>
    public record struct HeatPlate(TilePos Pos) : IComponent, ITile<TilePos>
    {
        readonly TilePos IIndexed<TilePos>.Key => Pos;
        static int ITileShape.Width => 16;
        static int ITileShape.Depth => 16;
        static TilePos ITile<TilePos>.Beside(TilePos p, TileFaces face) => Step(p, face);
    }

    public record struct Heat(float T);
    public record struct NextHeat(float T);

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
            var plate = heat.Pad();
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

        var grid = Whole(plates, 16, 16, (Entity e, int i) => e.Read<DynamicBuffer<Heat>>()[i].T, size);
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
            Same(plates, 16, 16, grid, (Entity e, int i) => e.Read<DynamicBuffer<Heat>>()[i].T, $"step {step}");
        }

        float At(int x, int z) => x < 0 || z < 0 || x >= cells || z >= cells || !plates.ContainsKey(new TilePos(x / 16, 0, z / 16)) ? 0 : grid[x, z];
    }

    /// <summary>The whole grid of a 2D grid of tiles, from each tile's cells.</summary>
    private static T[,] Whole<T>(Dictionary<TilePos, Entity> tiles, int w, int d, Func<Entity, int, T> cell, int size)
    {
        var grid = new T[size * w, size * d];
        foreach (var (place, tile) in tiles)
            for (int z = 0; z < d; z++)
            for (int x = 0; x < w; x++)
                grid[place.X * w + x, place.Z * d + z] = cell(tile, z * w + x);
        return grid;
    }

    /// <summary>Every tile's cells equal the whole grid's.</summary>
    private static void Same<T>(Dictionary<TilePos, Entity> tiles, int w, int d, T[,] grid, Func<Entity, int, T> cell, string when)
    {
        foreach (var (place, tile) in tiles)
            for (int z = 0; z < d; z++)
            for (int x = 0; x < w; x++)
                if (!EqualityComparer<T>.Default.Equals(cell(tile, z * w + x), grid[place.X * w + x, place.Z * d + z]))
                    Assert.Fail($"{when}, tile {place}, cell ({x}, {z}): {cell(tile, z * w + x)}, expected {grid[place.X * w + x, place.Z * d + z]}");
    }

    // A 3 x 3 box blur on plates of 16 x 16, one shape per boundary, and a torus (Beside wraps: no boundary at all).

    public record struct BlurFill(TilePos Pos) : IComponent, ITile<TilePos>
    {
        readonly TilePos IIndexed<TilePos>.Key => Pos;
        static int ITileShape.Width => 16;
        static int ITileShape.Depth => 16;
        static TileNeighborhood ITileShape.Neighborhood => TileNeighborhood.All;
        static TilePos ITile<TilePos>.Beside(TilePos p, TileFaces face) => Step(p, face);
    }

    public record struct BlurClamp(TilePos Pos) : IComponent, ITile<TilePos>
    {
        readonly TilePos IIndexed<TilePos>.Key => Pos;
        static int ITileShape.Width => 16;
        static int ITileShape.Depth => 16;
        static TileNeighborhood ITileShape.Neighborhood => TileNeighborhood.All;
        static TileBoundary ITileShape.Boundary => TileBoundary.Clamp;
        static TilePos ITile<TilePos>.Beside(TilePos p, TileFaces face) => Step(p, face);
    }

    public record struct BlurMirror(TilePos Pos) : IComponent, ITile<TilePos>
    {
        readonly TilePos IIndexed<TilePos>.Key => Pos;
        static int ITileShape.Width => 16;
        static int ITileShape.Depth => 16;
        static TileNeighborhood ITileShape.Neighborhood => TileNeighborhood.All;
        static TileBoundary ITileShape.Boundary => TileBoundary.Mirror;
        static TilePos ITile<TilePos>.Beside(TilePos p, TileFaces face) => Step(p, face);
    }

    public record struct BlurTorus(TilePos Pos) : IComponent, ITile<TilePos>
    {
        readonly TilePos IIndexed<TilePos>.Key => Pos;
        static int ITileShape.Width => 16;
        static int ITileShape.Depth => 16;
        static TileNeighborhood ITileShape.Neighborhood => TileNeighborhood.All;
        static TilePos ITile<TilePos>.Beside(TilePos p, TileFaces face) => Wrap(p, face);
    }

    /// <summary>The blur of one tile: the nine cells around each, summed row by row, a row of 16 in four vectors.</summary>
    private static void Blur<TTile>(Tile<Heat, TTile> heat, Mut<DynamicBuffer<NextHeat>> next) where TTile : struct, IComponent, ITileShape
    {
        var plate = heat.Pad();
        var into = MemoryMarshal.Cast<NextHeat, float>(next.Write().AsSpan());
        foreach (var row in plate.Rows(TileReads.Layer))
        {
            var c = plate.At(row);
            for (int x = 0; x < 16; x += 4)
            {
                var sum = c.Load128<float>(x - 1, 0, -1) + c.Load128<float>(x, 0, -1) + c.Load128<float>(x + 1, 0, -1)
                    + c.Load128<float>(x - 1) + c.Load128<float>(x) + c.Load128<float>(x + 1)
                    + c.Load128<float>(x - 1, 0, 1) + c.Load128<float>(x, 0, 1) + c.Load128<float>(x + 1, 0, 1);
                (sum * (1f / 9)).CopyTo(into.Slice(row.Index + x));
            }
        }
    }

    public class BlurFillSystem : EcsSystem
    {
        [ForEach(Parallel = true, ChunkRows = 4)]
        static void Step(Tile<Heat, BlurFill> heat, Mut<DynamicBuffer<NextHeat>> next) => Blur(heat, next);
    }

    public class BlurClampSystem : EcsSystem
    {
        [ForEach(Parallel = true, ChunkRows = 4)]
        static void Step(Tile<Heat, BlurClamp> heat, Mut<DynamicBuffer<NextHeat>> next) => Blur(heat, next);
    }

    public class BlurMirrorSystem : EcsSystem
    {
        [ForEach(Parallel = true, ChunkRows = 4)]
        static void Step(Tile<Heat, BlurMirror> heat, Mut<DynamicBuffer<NextHeat>> next) => Blur(heat, next);
    }

    public class BlurTorusSystem : EcsSystem
    {
        [ForEach(Parallel = true, ChunkRows = 4)]
        static void Step(Tile<Heat, BlurTorus> heat, Mut<DynamicBuffer<NextHeat>> next) => Blur(heat, next);
    }

    [TestCase(TileBoundary.Fill, 1)]
    [TestCase(TileBoundary.Fill, 4)]
    [TestCase(TileBoundary.Clamp, 1)]
    [TestCase(TileBoundary.Clamp, 4)]
    [TestCase(TileBoundary.Mirror, 1)]
    [TestCase(TileBoundary.Mirror, 4)]
    [TestCase(null, 1)]
    [TestCase(null, 4)]
    public void ABlurOnTilesIsTheBlurOfTheWholeGrid(TileBoundary? boundary, int threads)
    {
        switch (boundary)
        {
            case TileBoundary.Fill: BlurGrid<BlurFill, BlurFillSystem>(threads, p => new BlurFill(p), boundary, new TilePos(1, 0, 2), new TilePos(3, 0, 0)); break;
            case TileBoundary.Clamp: BlurGrid<BlurClamp, BlurClampSystem>(threads, p => new BlurClamp(p), boundary); break;
            case TileBoundary.Mirror: BlurGrid<BlurMirror, BlurMirrorSystem>(threads, p => new BlurMirror(p), boundary); break;
            default: BlurGrid<BlurTorus, BlurTorusSystem>(threads, p => new BlurTorus(p), null); break;
        }
    }

    /// <summary>
    /// Blurs a grid of 4 x 4 plates (holes missing from it: the fill) and checks every update against the whole grid
    /// blurred with the boundary.
    /// </summary>
    private static void BlurGrid<TTile, TSystem>(int threads, Func<TilePos, TTile> make, TileBoundary? boundary, params TilePos[] holes)
        where TTile : struct, IComponent, ITile<TilePos> where TSystem : EcsSystem, new()
    {
        const int size = Torus, cells = size * 16;
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        var random = new Random(3);
        var plates = new Dictionary<TilePos, Entity>();
        foreach (var place in Box(size, 1, size, holes))
        {
            var plate = world.CreateEntity(make(place));
            var heat = plate.GetOrAdd<DynamicBuffer<Heat>>().SetCount(256);
            plate.GetOrAdd<DynamicBuffer<NextHeat>>().SetCount(256);
            for (int i = 0; i < 6; i++) heat[random.Next(256)] = new Heat(random.Next(1, 1000));
            plates.Add(place, plate);
        }
        // A hot cell on each edge and corner of the grid, so the boundary decides what flows back.
        plates[new TilePos(0, 0, 0)].GetOrAdd<DynamicBuffer<Heat>>().AsSpan()[0] = new Heat(5000);
        plates[new TilePos(size - 1, 0, size - 1)].GetOrAdd<DynamicBuffer<Heat>>().AsSpan()[255] = new Heat(5000);
        world.AddSystem<TSystem>();
        world.AddSystem<CommitHeatSystem>();

        var grid = Whole(plates, 16, 16, (Entity e, int i) => e.Read<DynamicBuffer<Heat>>()[i].T, size);
        for (int step = 0; step < 25; step++)
        {
            world.Update();
            var next = new float[cells, cells];
            for (int z = 0; z < cells; z++)
            for (int x = 0; x < cells; x++)
            {
                if (!plates.ContainsKey(new TilePos(x / 16, 0, z / 16))) continue;
                float sum = At(x - 1, z - 1) + At(x, z - 1) + At(x + 1, z - 1)
                    + At(x - 1, z) + At(x, z) + At(x + 1, z)
                    + At(x - 1, z + 1) + At(x, z + 1) + At(x + 1, z + 1);
                next[x, z] = sum * (1f / 9);
            }

            grid = next;
            Same(plates, 16, 16, grid, (Entity e, int i) => e.Read<DynamicBuffer<Heat>>()[i].T, $"{boundary?.ToString() ?? "torus"} step {step}");
        }

        float At(int x, int z) => boundary switch
        {
            null => grid[(x + cells) % cells, (z + cells) % cells],
            TileBoundary.Fill => x < 0 || z < 0 || x >= cells || z >= cells || !plates.ContainsKey(new TilePos(x / 16, 0, z / 16)) ? 0 : grid[x, z],
            TileBoundary.Clamp => grid[Math.Clamp(x, 0, cells - 1), Math.Clamp(z, 0, cells - 1)],
            _ => grid[Mirror(x), Mirror(z)],
        };

        static int Mirror(int c) => c < 0 ? -1 - c : c >= cells ? 2 * cells - 1 - c : c;
    }

    // The Game of Life on a torus of 4 x 4 plates of 16 x 16 cells: nine points, every lane a byte.

    public record struct LifePlate(TilePos Pos) : IComponent, ITile<TilePos>
    {
        readonly TilePos IIndexed<TilePos>.Key => Pos;
        static int ITileShape.Width => 16;
        static int ITileShape.Depth => 16;
        static TileNeighborhood ITileShape.Neighborhood => TileNeighborhood.All;
        static TilePos ITile<TilePos>.Beside(TilePos p, TileFaces face) => Wrap(p, face);
    }

    public record struct Life(byte Alive);
    public record struct NextLife(byte Alive);

    /// <summary>A cell lives on with two or three live neighbors, and is born with three; a row of 16 in one vector.</summary>
    public class LifeSystem : EcsSystem
    {
        [ForEach(Parallel = true, ChunkRows = 2)]
        static void Step(Tile<Life, LifePlate> life, Mut<DynamicBuffer<NextLife>> next)
        {
            var plate = life.Pad();
            var into = MemoryMarshal.Cast<NextLife, byte>(next.Write().AsSpan());
            foreach (var row in plate.Rows(TileReads.Layer))
            {
                var c = plate.At(row);
                var alive = c.Load128<byte>();
                var count = c.Load128<byte>(-1, 0, -1) + c.Load128<byte>(0, 0, -1) + c.Load128<byte>(1, 0, -1)
                    + c.Load128<byte>(-1) + c.Load128<byte>(1)
                    + c.Load128<byte>(-1, 0, 1) + c.Load128<byte>(0, 0, 1) + c.Load128<byte>(1, 0, 1);
                var born = Vector128.Equals(count, Vector128.Create((byte)3));
                var stays = Vector128.Equals(count, Vector128.Create((byte)2)) & alive;
                ((born | stays) & Vector128<byte>.One).CopyTo(into.Slice(row.Index));
            }
        }
    }

    public class CommitLifeSystem : EcsSystem
    {
        [ForEach]
        static void Commit(in DynamicBuffer<NextLife> next, Mut<DynamicBuffer<Life>> life) =>
            next.AsReadOnlySpan().CopyTo(MemoryMarshal.Cast<Life, NextLife>(life.Write().AsSpan()));
    }

    private static (World World, Dictionary<TilePos, Entity> Plates) LifeWorld(int threads)
    {
        var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        var random = new Random(11);
        var plates = new Dictionary<TilePos, Entity>();
        foreach (var place in Box(Torus, 1, Torus))
        {
            var plate = world.CreateEntity(new LifePlate(place));
            var cells = plate.GetOrAdd<DynamicBuffer<Life>>().SetCount(256);
            plate.GetOrAdd<DynamicBuffer<NextLife>>().SetCount(256);
            if (place.X != 2) // a quiet column of plates, for the stencil to skip
                for (int i = 0; i < 256; i++) cells[i] = new Life((byte)(random.Next(3) == 0 ? 1 : 0));
            plates.Add(place, plate);
        }

        world.AddSystem<LifeSystem>();
        world.AddSystem<CommitLifeSystem>();
        return (world, plates);
    }

    [TestCase(1)]
    [TestCase(4)]
    public void TheGameOfLifeOnTilesIsTheGameOfTheWholeTorus(int threads)
    {
        const int cells = Torus * 16;
        var (world, plates) = LifeWorld(threads);
        using var _ = world;
        var grid = Whole(plates, 16, 16, (Entity e, int i) => e.Read<DynamicBuffer<Life>>()[i].Alive, Torus);
        for (int step = 0; step < 40; step++)
        {
            world.Update();
            var next = new byte[cells, cells];
            for (int z = 0; z < cells; z++)
            for (int x = 0; x < cells; x++)
            {
                int count = 0;
                for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                    if ((dx | dz) != 0) count += grid[(x + dx + cells) % cells, (z + dz + cells) % cells];
                next[x, z] = (byte)(count == 3 || count == 2 && grid[x, z] == 1 ? 1 : 0);
            }

            grid = next;
            Same(plates, 16, 16, grid, (Entity e, int i) => e.Read<DynamicBuffer<Life>>()[i].Alive, $"generation {step}");
        }
    }

    // Active tiles: a step that changed nothing lets its tile sleep, and the edges that changed wake the tiles beside.

    public record struct SparsePlate(TilePos Pos) : IComponent, ITile<TilePos>
    {
        readonly TilePos IIndexed<TilePos>.Key => Pos;
        static int ITileShape.Width => 16;
        static int ITileShape.Depth => 16;
        static TilePos ITile<TilePos>.Beside(TilePos p, TileFaces face) => Step(p, face);
    }

    /// <summary>Tags a tile whose step may change it.</summary>
    public struct Awake : IComponent;

    /// <summary>What the tile's last step changed: whether anything, and the faces whose edge.</summary>
    public record struct HeatChange(TileFaces Edges, bool Changed) : IComponent;

    public class SparseDiffuseSystem : EcsSystem
    {
        [ForEach(Parallel = true, ChunkRows = 8), With<Awake>]
        static void Diffuse(Tile<Heat, SparsePlate> heat, Mut<DynamicBuffer<NextHeat>> next, ref HeatChange change)
        {
            var plate = heat.Pad();
            var step = next.Write().AsSpan();
            var into = MemoryMarshal.Cast<NextHeat, float>(step);
            foreach (var row in plate.Rows(TileReads.Layer))
            {
                var cells = plate.At(row);
                for (int x = 0; x < 16; x += Vector128<float>.Count)
                {
                    var here = cells.Load128<float>(x);
                    var around = cells.Load128<float>(x - 1) + cells.Load128<float>(x + 1) + cells.Load128<float>(x, 0, -1)
                        + cells.Load128<float>(x, 0, 1);
                    (here + (around - here * 4f) * DiffuseSystem.Rate).CopyTo(into.Slice(row.Index + x));
                }
            }

            var edges = heat.ChangedEdges(MemoryMarshal.Cast<NextHeat, Heat>(step), out bool changed);
            change = new HeatChange(edges, changed);
        }
    }

    public class CommitAwakeSystem : EcsSystem
    {
        [ForEach, With<Awake>]
        static void Commit(in DynamicBuffer<NextHeat> next, Mut<DynamicBuffer<Heat>> heat) =>
            next.AsReadOnlySpan().CopyTo(MemoryMarshal.Cast<Heat, NextHeat>(heat.Write().AsSpan()));
    }

    public class SleepSystem : EcsSystem
    {
        [ForEach, With<Awake>]
        static void Sleep(Entity plate, in HeatChange change)
        {
            if (!change.Changed) plate.RemoveAwake();
        }
    }

    public class WakeSystem : EcsSystem
    {
        [ForEach, With<Awake>]
        void Wake(in HeatChange change, Tile<Heat, SparsePlate> heat)
        {
            foreach (var id in heat.Facing(change.Edges)) World.GetEntity(id).SetAwake();
        }
    }

    [TestCase(1)]
    [TestCase(4)]
    public void ActiveTilesStepOnlyWhereHeatMovesAndGetTheHeatOfEveryTile(int threads)
    {
        const int size = 9;
        var (always, plates) = HeatWorld(threads, size);
        using var _ = always;
        using var sparse = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        var tiles = new Dictionary<TilePos, Entity>();
        foreach (var (place, plate) in plates)
        {
            var tile = sparse.CreateEntity(new SparsePlate(place), new HeatChange(), new Awake());
            plate.Read<DynamicBuffer<Heat>>().AsReadOnlySpan().CopyTo(tile.GetOrAdd<DynamicBuffer<Heat>>().SetCount(256));
            plate.Read<DynamicBuffer<NextHeat>>().AsReadOnlySpan().CopyTo(tile.GetOrAdd<DynamicBuffer<NextHeat>>().SetCount(256));
            tiles.Add(place, tile);
        }
        // Cold everywhere but two plates: the rest sleeps until the heat reaches it.
        foreach (var (place, tile) in tiles)
            if (place != new TilePos(0, 0, 0) && place != new TilePos(5, 0, 5))
            {
                tile.GetOrAdd<DynamicBuffer<Heat>>().AsSpan().Clear();
                plates[place].GetOrAdd<DynamicBuffer<Heat>>().AsSpan().Clear();
                plates[place].GetOrAdd<DynamicBuffer<NextHeat>>().AsSpan().Clear();
                tile.GetOrAdd<DynamicBuffer<NextHeat>>().AsSpan().Clear();
            }
        sparse.AddSystem<SparseDiffuseSystem>();
        sparse.AddSystem<CommitAwakeSystem>();
        sparse.AddSystem<SleepSystem>();
        sparse.AddSystem<WakeSystem>();

        int fewest = int.MaxValue;
        for (int step = 0; step < 40; step++)
        {
            always.Update();
            sparse.Update();
            int awake = sparse.Count<Awake>(QueryFilter.Create());
            fewest = Math.Min(fewest, awake);
            foreach (var (place, plate) in plates)
                Assert.That(tiles[place].Read<DynamicBuffer<Heat>>().AsReadOnlySpan().SequenceEqual(plate.Read<DynamicBuffer<Heat>>().AsReadOnlySpan()),
                    $"step {step}, plate {place}");
        }

        Assert.That(fewest, Is.LessThan(size * size / 4), "most plates sleep");
    }

    // A fourth-order Laplacian: five points along each axis, two cells, rows or layers away (a halo of 2).

    public record struct WavePlate(TilePos Pos) : IComponent, ITile<TilePos>
    {
        readonly TilePos IIndexed<TilePos>.Key => Pos;
        static int ITileShape.Width => 16;
        static int ITileShape.Depth => 16;
        static int ITileShape.Halo => 2;
        static TilePos ITile<TilePos>.Beside(TilePos p, TileFaces face) => Step(p, face);
    }

    public class WaveSystem : EcsSystem
    {
        public const float Rate = 0.05f;

        [ForEach(Parallel = true, ChunkRows = 4)]
        static void Step(Tile<Heat, WavePlate> heat, Mut<DynamicBuffer<NextHeat>> next)
        {
            var plate = heat.Pad();
            var into = MemoryMarshal.Cast<NextHeat, float>(next.Write().AsSpan());
            foreach (var row in plate.Rows(TileReads.Layer))
            {
                var c = plate.At(row);
                for (int x = 0; x < 16; x += 4)
                {
                    var here = c.Load128<float>(x);
                    var alongX = -c.Load128<float>(x - 2) + 16f * c.Load128<float>(x - 1) - 30f * here + 16f * c.Load128<float>(x + 1) - c.Load128<float>(x + 2);
                    var alongZ = -c.Load128<float>(x, 0, -2) + 16f * c.Load128<float>(x, 0, -1) - 30f * here + 16f * c.Load128<float>(x, 0, 1) - c.Load128<float>(x, 0, 2);
                    (here + (alongX + alongZ) * (1f / 12) * Rate).CopyTo(into.Slice(row.Index + x));
                }
            }
        }
    }

    [TestCase(1)]
    [TestCase(4)]
    public void AFourthOrderLaplacianReadsAHaloOfTwo(int threads)
    {
        const int size = 5, cells = size * 16;
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        var plates = new Dictionary<TilePos, Entity>();
        foreach (var place in Box(size, 1, size, new TilePos(2, 0, 2)))
        {
            var plate = world.CreateEntity(new WavePlate(place));
            var heat = plate.GetOrAdd<DynamicBuffer<Heat>>().SetCount(256);
            plate.GetOrAdd<DynamicBuffer<NextHeat>>().SetCount(256);
            heat[(place.X * 5 + place.Z * 3) % 256] = new Heat(800f + place.X);
            heat[255 - place.Z] = new Heat(300f);
            plates.Add(place, plate);
        }
        world.AddSystem<WaveSystem>();
        world.AddSystem<CommitHeatSystem>();

        var grid = Whole(plates, 16, 16, (Entity e, int i) => e.Read<DynamicBuffer<Heat>>()[i].T, size);
        for (int step = 0; step < 30; step++)
        {
            world.Update();
            var next = new float[cells, cells];
            for (int z = 0; z < cells; z++)
            for (int x = 0; x < cells; x++)
            {
                if (!plates.ContainsKey(new TilePos(x / 16, 0, z / 16))) continue;
                float here = grid[x, z];
                float alongX = -At(x - 2, z) + 16f * At(x - 1, z) - 30f * here + 16f * At(x + 1, z) - At(x + 2, z);
                float alongZ = -At(x, z - 2) + 16f * At(x, z - 1) - 30f * here + 16f * At(x, z + 1) - At(x, z + 2);
                next[x, z] = here + (alongX + alongZ) * (1f / 12) * WaveSystem.Rate;
            }

            grid = next;
            Same(plates, 16, 16, grid, (Entity e, int i) => e.Read<DynamicBuffer<Heat>>()[i].T, $"step {step}");
        }

        float At(int x, int z) => x < 0 || z < 0 || x >= cells || z >= cells || !plates.ContainsKey(new TilePos(x / 16, 0, z / 16)) ? 0 : grid[x, z];
    }

    // A 27-point sum over cubes of 8 x 8 x 8: every tile around, faces, edges and corners.

    public record struct Box8(TilePos Pos) : IComponent, ITile<TilePos>
    {
        readonly TilePos IIndexed<TilePos>.Key => Pos;
        static int ITileShape.Width => 8;
        static int ITileShape.Depth => 8;
        static int ITileShape.Height => 8;
        static TileFaces ITileShape.Faces => TileFaces.All;
        static TileNeighborhood ITileShape.Neighborhood => TileNeighborhood.All;
        static TilePos ITile<TilePos>.Beside(TilePos p, TileFaces face) => Step(p, face);
    }

    public class SumSystem : EcsSystem
    {
        [ForEach(Parallel = true, ChunkRows = 2)]
        static void Step(Tile<Cell, Box8> cells, Mut<DynamicBuffer<NextCell>> next)
        {
            var box = cells.Pad();
            var into = MemoryMarshal.Cast<NextCell, int>(next.Write().AsSpan());
            foreach (var row in box.Rows())
            {
                var c = box.At(row);
                for (int x = 0; x < 8; x += 4)
                {
                    var sum = Vector128<int>.Zero;
                    for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                        sum += c.Load128<int>(x - 1, dy, dz) + c.Load128<int>(x, dy, dz) + c.Load128<int>(x + 1, dy, dz);
                    (sum & Vector128.Create(0xFFFF)).CopyTo(into.Slice(row.Index + x));
                }
            }
        }
    }

    public class CommitCellSystem : EcsSystem
    {
        [ForEach]
        static void Commit(in DynamicBuffer<NextCell> next, Mut<DynamicBuffer<Cell>> cells) =>
            next.AsReadOnlySpan().CopyTo(MemoryMarshal.Cast<Cell, NextCell>(cells.Write().AsSpan()));
    }

    [TestCase(1)]
    [TestCase(4)]
    public void ATwentySevenPointStencilReadsEveryTileAround(int threads)
    {
        const int size = 3, cells = size * 8;
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        var holes = new[] { new TilePos(1, 1, 1), new TilePos(0, 2, 0) };
        var boxes = new Dictionary<TilePos, Entity>();
        var random = new Random(5);
        foreach (var place in Box(size, size, size, holes))
        {
            var box = world.CreateEntity(new Box8(place));
            var buffer = box.GetOrAdd<DynamicBuffer<Cell>>().SetCount(512);
            box.GetOrAdd<DynamicBuffer<NextCell>>().SetCount(512);
            for (int i = 0; i < 4; i++) buffer[random.Next(512)] = new Cell(random.Next(1, 100));
            boxes.Add(place, box);
        }
        world.AddSystem<SumSystem>();
        world.AddSystem<CommitCellSystem>();

        var grid = new int[cells, cells, cells];
        foreach (var (p, box) in boxes) Copy(box, p, grid, true);
        for (int step = 0; step < 6; step++)
        {
            world.Update();
            var next = new int[cells, cells, cells];
            for (int y = 0; y < cells; y++)
            for (int z = 0; z < cells; z++)
            for (int x = 0; x < cells; x++)
            {
                if (!boxes.ContainsKey(new TilePos(x / 8, y / 8, z / 8))) continue;
                int sum = 0;
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                    sum += At(x + dx, y + dy, z + dz);
                next[x, y, z] = sum & 0xFFFF;
            }

            grid = next;
            foreach (var (p, box) in boxes)
            {
                var actual = new int[cells, cells, cells];
                Copy(box, p, actual, true);
                for (int y = 0; y < 8; y++)
                for (int z = 0; z < 8; z++)
                for (int x = 0; x < 8; x++)
                    Assert.That(actual[p.X * 8 + x, p.Y * 8 + y, p.Z * 8 + z], Is.EqualTo(grid[p.X * 8 + x, p.Y * 8 + y, p.Z * 8 + z]),
                        $"step {step}, box {p}, cell ({x}, {y}, {z})");
            }
        }

        int At(int x, int y, int z) =>
            x < 0 || y < 0 || z < 0 || x >= cells || y >= cells || z >= cells || !boxes.ContainsKey(new TilePos(x / 8, y / 8, z / 8)) ? 0 : grid[x, y, z];

        static void Copy(Entity box, TilePos p, int[,,] into, bool _)
        {
            var span = box.Read<DynamicBuffer<Cell>>().AsReadOnlySpan();
            for (int y = 0; y < 8; y++)
            for (int z = 0; z < 8; z++)
            for (int x = 0; x < 8; x++)
                into[p.X * 8 + x, p.Y * 8 + y, p.Z * 8 + z] = span[(y * 8 + z) * 8 + x].V;
        }
    }

    // A 1D smoothing over rows of 8 cells: five points along x (a halo of 2), no rows north or south.

    public record struct Strip(TilePos Pos) : IComponent, ITile<TilePos>
    {
        readonly TilePos IIndexed<TilePos>.Key => Pos;
        static int ITileShape.Width => 8;
        static int ITileShape.Depth => 1;
        static int ITileShape.Halo => 2;
        static TileFaces ITileShape.Faces => TileFaces.West | TileFaces.East;
        static TilePos ITile<TilePos>.Beside(TilePos p, TileFaces face) => Step(p, face);
    }

    public class SmoothSystem : EcsSystem
    {
        [ForEach(Parallel = true, ChunkRows = 2)]
        static void Step(Tile<Heat, Strip> heat, Mut<DynamicBuffer<NextHeat>> next)
        {
            var strip = heat.Pad();
            var into = MemoryMarshal.Cast<NextHeat, float>(next.Write().AsSpan());
            foreach (var row in strip.Rows(TileReads.Self))
            {
                var c = strip.At(row);
                for (int x = 0; x < 8; x += 4)
                    ((c.Load128<float>(x - 2) + c.Load128<float>(x - 1) + c.Load128<float>(x) + c.Load128<float>(x + 1) + c.Load128<float>(x + 2)) * 0.2f)
                        .CopyTo(into.Slice(row.Index + x));
            }
        }
    }

    [TestCase(1)]
    [TestCase(4)]
    public void AOneDimensionalTileSmoothsTheWholeLine(int threads)
    {
        const int size = 12, cells = size * 8;
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        var strips = new Dictionary<TilePos, Entity>();
        foreach (var place in Box(size, 1, 1, new TilePos(6, 0, 0)))
        {
            var strip = world.CreateEntity(new Strip(place));
            var heat = strip.GetOrAdd<DynamicBuffer<Heat>>().SetCount(8);
            strip.GetOrAdd<DynamicBuffer<NextHeat>>().SetCount(8);
            if (place.X % 3 == 0) heat[place.X % 8] = new Heat(100f * place.X + 1);
            strips.Add(place, strip);
        }
        world.AddSystem<SmoothSystem>();
        world.AddSystem<CommitHeatSystem>();

        var line = new float[cells];
        foreach (var (p, strip) in strips)
            for (int x = 0; x < 8; x++) line[p.X * 8 + x] = strip.Read<DynamicBuffer<Heat>>()[x].T;
        for (int step = 0; step < 20; step++)
        {
            world.Update();
            var next = new float[cells];
            for (int x = 0; x < cells; x++)
                if (strips.ContainsKey(new TilePos(x / 8, 0, 0)))
                    next[x] = (At(x - 2) + At(x - 1) + At(x) + At(x + 1) + At(x + 2)) * 0.2f;
            line = next;
            foreach (var (p, strip) in strips)
                for (int x = 0; x < 8; x++)
                    Assert.That(strip.Read<DynamicBuffer<Heat>>()[x].T, Is.EqualTo(line[p.X * 8 + x]), $"step {step}, strip {p}, cell {x}");
        }

        float At(int x) => x < 0 || x >= cells || !strips.ContainsKey(new TilePos(x / 8, 0, 0)) ? 0 : line[x];
    }

    // ---------------- scheduling and allocation ----------------

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
    public void StencilsOnTilesDoNotAllocate()
    {
        var (heat, _) = HeatWorld(4, 8);
        var (life, _) = LifeWorld(4);
        using var a = heat;
        using var b = life;
        foreach (var world in new[] { heat, life })
        {
            for (int i = 0; i < 30; i++) world.Update(); // every thread gets its texture
            Assert.That(Allocations.Least(() => { for (int i = 0; i < 100; i++) world.Update(); }), Is.Zero);
        }
    }
}
