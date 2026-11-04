using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>
/// Cell methods (<see cref="Cell{TCell, TTile}"/>, the shader form): a blur per boundary, a halo of 2 on a torus and
/// the Game of Life in bytes, each against the whole grid computed at once (bit for bit against the tile methods it
/// replaces), on one thread and on four; the cell's place (X, Y, Z) in a 3D tile; the rows the walk skips filled with
/// what the method computes from zeros; a pass bounded to the layers a component gives (ITileLayers) against the
/// whole one; the method as written (SimdMode.Off) against its vector form; an offset past the halo throwing; the
/// schedule's lines and 0 B.
/// </summary>
public partial class TileTests
{
    [EcsSystem]
    public ref struct CellBlurFillSystem
    {
        [ForEach(Parallel = true, ChunkSize = 4)]
        static void Step(Cell<Heat, BlurFill> w, out NextHeat next) =>
            next = new((w[-1, 0, -1].T + w[0, 0, -1].T + w[1, 0, -1].T
            + w[-1, 0, 0].T + w.Value.T + w[1, 0, 0].T
            + w[-1, 0, 1].T + w[0, 0, 1].T + w[1, 0, 1].T) * (1f / 9));
    }

    [EcsSystem]
    public ref struct CellBlurClampSystem
    {
        [ForEach(Parallel = true, ChunkSize = 4)]
        static void Step(Cell<Heat, BlurClamp> w, out NextHeat next) =>
            next = new((w[-1, 0, -1].T + w[0, 0, -1].T + w[1, 0, -1].T
            + w[-1, 0, 0].T + w.Value.T + w[1, 0, 0].T
            + w[-1, 0, 1].T + w[0, 0, 1].T + w[1, 0, 1].T) * (1f / 9));
    }

    [EcsSystem]
    public ref struct CellBlurMirrorSystem
    {
        [ForEach(Parallel = true, ChunkSize = 4)]
        static void Step(Cell<Heat, BlurMirror> w, out NextHeat next) =>
            next = new((w[-1, 0, -1].T + w[0, 0, -1].T + w[1, 0, -1].T
            + w[-1, 0, 0].T + w.Value.T + w[1, 0, 0].T
            + w[-1, 0, 1].T + w[0, 0, 1].T + w[1, 0, 1].T) * (1f / 9));
    }

    [EcsSystem]
    public ref struct CellBlurTorusSystem
    {
        [ForEach(Parallel = true, ChunkSize = 4)]
        static void Step(Cell<Heat, BlurTorus> w, out NextHeat next) =>
            next = new((w[-1, 0, -1].T + w[0, 0, -1].T + w[1, 0, -1].T
            + w[-1, 0, 0].T + w.Value.T + w[1, 0, 0].T
            + w[-1, 0, 1].T + w[0, 0, 1].T + w[1, 0, 1].T) * (1f / 9));
    }

    [TestCase(TileBoundary.Border, 1)]
    [TestCase(TileBoundary.Border, 4)]
    [TestCase(TileBoundary.Clamp, 1)]
    [TestCase(TileBoundary.Clamp, 4)]
    [TestCase(TileBoundary.Mirror, 1)]
    [TestCase(TileBoundary.Mirror, 4)]
    [TestCase(null, 1)]
    [TestCase(null, 4)]
    public void ABlurOfCellsIsTheBlurOfTheWholeGrid(TileBoundary? boundary, int threads)
    {
        switch (boundary)
        {
            case TileBoundary.Border: BlurGrid<BlurFill, CellBlurFillSystem>(threads, p => new BlurFill(p), boundary, true, new TilePos(1, 0, 2), new TilePos(3, 0, 0)); break;
            case TileBoundary.Clamp: BlurGrid<BlurClamp, CellBlurClampSystem>(threads, p => new BlurClamp(p), boundary, true); break;
            case TileBoundary.Mirror: BlurGrid<BlurMirror, CellBlurMirrorSystem>(threads, p => new BlurMirror(p), boundary, true); break;
            default: BlurGrid<BlurTorus, CellBlurTorusSystem>(threads, p => new BlurTorus(p), null, true); break;
        }
    }

    /// <summary>A torus of plates of 16 x 16 with a halo of 2.</summary>
    public record struct WaveTorus(TilePos Pos) : IComponent, ITile<TilePos>
    {
        readonly TilePos IIndexed<TilePos>.Key => Pos;
        static int ITileShape.Width => 16;
        static int ITileShape.Depth => 16;
        static int ITileShape.Halo => 2;
        static TileNeighborhood ITileShape.Neighborhood => TileNeighborhood.All;
        static TilePos ITile<TilePos>.Beside(TilePos p, TileFaces face) => Wrap(p, face);
    }

    /// <summary>A fourth-order Laplacian step: the cells one and two away on both axes.</summary>
    [EcsSystem]
    public ref struct CellWaveSystem
    {
        public const float Rate = 0.05f;

        [ForEach(Parallel = true, ChunkSize = 2)]
        static void Step(Cell<Heat, WaveTorus> w, out NextHeat next)
        {
            float here = w.Value.T;
            float near = w[-1, 0, 0].T + w[1, 0, 0].T + w[0, 0, -1].T + w[0, 0, 1].T;
            float far = w[-2, 0, 0].T + w[2, 0, 0].T + w[0, 0, -2].T + w[0, 0, 2].T;
            next = new(here + (16f * near - far - 60f * here) * (Rate / 12f));
        }
    }

    /// <summary>The same step with its out cell a Heat, the cell's next value: the pass steps the tile (no NextHeat, no commit).</summary>
    [EcsSystem]
    public ref struct CellWaveStepSystem
    {
        [ForEach(Parallel = true, ChunkSize = 2)]
        static void Step(Cell<Heat, WaveTorus> w, out Heat next)
        {
            float here = w.Value.T;
            float near = w[-1, 0, 0].T + w[1, 0, 0].T + w[0, 0, -1].T + w[0, 0, 1].T;
            float far = w[-2, 0, 0].T + w[2, 0, 0].T + w[0, 0, -2].T + w[0, 0, 2].T;
            next = new(here + (16f * near - far - 60f * here) * (CellWaveSystem.Rate / 12f));
        }
    }

    [TestCase(1, false)]
    [TestCase(4, false)]
    [TestCase(1, true)]
    [TestCase(4, true)]
    public void AHaloOfTwoIsTheWholeTorus(int threads, bool stepped)
    {
        const int cells = Torus * 16;
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        var random = new Random(5);
        var plates = new Dictionary<TilePos, Entity>();
        foreach (var place in Box(Torus, 1, Torus))
        {
            var plate = world.CreateEntity(new WaveTorus(place));
            var heat = plate.GetOrAdd<DynamicBuffer<Heat>>().SetCount(256);
            if (!stepped) plate.GetOrAdd<DynamicBuffer<NextHeat>>().SetCount(256);
            if (place.X != 1) // a quiet column of plates, for the walk to skip
                for (int i = 0; i < 8; i++) heat[random.Next(256)] = new Heat(random.Next(1, 1000));
            plates.Add(place, plate);
        }
        if (stepped) world.AddSystem<CellWaveStepSystem>();
        else
        {
            world.AddSystem<CellWaveSystem>();
            world.AddSystem<CommitHeatSystem>();
        }

        var grid = Whole(plates, 16, 16, (Entity e, int i) => e.Read<DynamicBuffer<Heat>>()[i].T, Torus);
        for (int step = 0; step < 20; step++)
        {
            world.Update();
            var next = new float[cells, cells];
            for (int z = 0; z < cells; z++)
            for (int x = 0; x < cells; x++)
            {
                float here = grid[x, z];
                float near = At(x - 1, z) + At(x + 1, z) + At(x, z - 1) + At(x, z + 1);
                float far = At(x - 2, z) + At(x + 2, z) + At(x, z - 2) + At(x, z + 2);
                next[x, z] = here + (16f * near - far - 60f * here) * (CellWaveSystem.Rate / 12f);
            }

            grid = next;
            Same(plates, 16, 16, grid, (Entity e, int i) => e.Read<DynamicBuffer<Heat>>()[i].T, $"step {step}");
        }

        float At(int x, int z) => grid[(x + cells) % cells, (z + cells) % cells];
    }

    /// <summary>The Game of Life as one cell: the vector loop's lanes are bytes, the count an int.</summary>
    [EcsSystem]
    public ref struct CellLifeSystem
    {
        [ForEach(Parallel = true, ChunkSize = 2)]
        static void Step(Cell<Life, LifePlate> c, out NextLife next)
        {
            int count = c[-1, 0, -1].Alive + c[0, 0, -1].Alive + c[1, 0, -1].Alive
                + c[-1, 0, 0].Alive + c[1, 0, 0].Alive
                + c[-1, 0, 1].Alive + c[0, 0, 1].Alive + c[1, 0, 1].Alive;
            next = new((byte)(count == 3 || count == 2 && c.Value.Alive == 1 ? 1 : 0));
        }
    }

    [TestCase(1)]
    [TestCase(4)]
    public void TheGameOfLifeOfCellsIsTheGameOfTheTileMethod(int threads)
    {
        var (tiles, tilePlates) = LifeWorld(threads);
        var (cells, cellPlates) = LifeWorld(threads);
        using var _ = tiles;
        using var __ = cells;
        cells.RemoveSystem<LifeSystem>();
        cells.RemoveSystem<CommitLifeSystem>();
        cells.AddSystem<CellLifeSystem>();
        cells.AddSystem<CommitLifeSystem>();
        for (int step = 0; step < 40; step++)
        {
            tiles.Update();
            cells.Update();
            foreach (var (place, plate) in tilePlates)
                Assert.That(cellPlates[place].Read<DynamicBuffer<Life>>().AsReadOnlySpan().SequenceEqual(plate.Read<DynamicBuffer<Life>>().AsReadOnlySpan()),
                    $"generation {step}, plate {place}");
        }
    }

    /// <summary>The same rule as written, cell by cell.</summary>
    [EcsSystem]
    public ref struct CellLifeByCellSystem
    {
        [ForEach(Parallel = true, ChunkSize = 2, SimdMode = SimdMode.Off)]
        static void Step(Cell<Life, LifePlate> c, out NextLife next)
        {
            int count = c[-1, 0, -1].Alive + c[0, 0, -1].Alive + c[1, 0, -1].Alive
                + c[-1, 0, 0].Alive + c[1, 0, 0].Alive
                + c[-1, 0, 1].Alive + c[0, 0, 1].Alive + c[1, 0, 1].Alive;
            next = new((byte)(count == 3 || count == 2 && c.Value.Alive == 1 ? 1 : 0));
        }
    }

    /// <summary>
    /// Cells of 0, 1 or 2 (every neighborhood of three values turns up in a torus of 64 x 64 random cells): counts up to 16,
    /// past what 2 and 3 compare against, the saturated byte lanes against the rule as written.
    /// </summary>
    [TestCase(1)]
    [TestCase(4)]
    public void TheVectorLifeIsTheLifeAsWrittenForEveryNeighborhood(int threads)
    {
        using var vector = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        using var scalar = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        var random = new Random(13);
        var plates = new List<(Entity Vector, Entity Scalar)>();
        foreach (var place in Box(Torus, 1, Torus))
        {
            var a = vector.CreateEntity(new LifePlate(place));
            var b = scalar.CreateEntity(new LifePlate(place));
            var cells = a.GetOrAdd<DynamicBuffer<Life>>().SetCount(256);
            for (int i = 0; i < 256; i++) cells[i] = new Life((byte)random.Next(3));
            cells.CopyTo(b.GetOrAdd<DynamicBuffer<Life>>().SetCount(256));
            a.GetOrAdd<DynamicBuffer<NextLife>>().SetCount(256);
            b.GetOrAdd<DynamicBuffer<NextLife>>().SetCount(256);
            plates.Add((a, b));
        }
        vector.AddSystem<CellLifeSystem>();
        scalar.AddSystem<CellLifeByCellSystem>();
        vector.Update();
        scalar.Update();
        foreach (var (a, b) in plates)
            Assert.That(a.Read<DynamicBuffer<NextLife>>().AsReadOnlySpan().SequenceEqual(b.Read<DynamicBuffer<NextLife>>().AsReadOnlySpan()), Is.True);
    }

    /// <summary>A seven-point stencil over cubes: the cell, its neighbors along the three axes, and the layers above and below.</summary>
    [EcsSystem]
    public ref struct CellSevenPointSystem
    {
        [ForEach(Parallel = true, ChunkSize = 2)]
        static void Step(Cell<Heat, PlaceColumn> w, out NextHeat next) =>
            next = new(w.Value.T * 0.4f + (w[-1, 0, 0].T + w[1, 0, 0].T + w[0, -1, 0].T + w[0, 1, 0].T + w[0, 0, -1].T + w[0, 0, 1].T) * 0.1f);
    }

    [TestCase(1)]
    [TestCase(4)]
    public void ASevenPointStencilIsTheStencilOfTheWholeGrid(int threads)
    {
        const int w = 8, d = 8, h = 6;
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        var random = new Random(17);
        var columns = new Dictionary<TilePos, Entity>();
        foreach (var place in Box(3, 2, 2, new TilePos(1, 1, 0)))
        {
            var column = world.CreateEntity(new PlaceColumn(place));
            var heat = column.GetOrAdd<DynamicBuffer<Heat>>().SetCount(w * d * h);
            column.GetOrAdd<DynamicBuffer<NextHeat>>().SetCount(w * d * h);
            if (place.X != 2) // a quiet column of tiles: rows the walk skips
                for (int i = 0; i < 40; i++) heat[random.Next(heat.Length)] = new Heat(random.Next(1, 1000));
            columns.Add(place, column);
        }
        world.AddSystem<CellSevenPointSystem>();
        world.Update();

        foreach (var (place, column) in columns)
        {
            var next = column.Read<DynamicBuffer<NextHeat>>().AsReadOnlySpan();
            for (int y = 0; y < h; y++)
            for (int z = 0; z < d; z++)
            for (int x = 0; x < w; x++)
            {
                int gx = place.X * w + x, gy = place.Y * h + y, gz = place.Z * d + z;
                float expected = At(gx, gy, gz) * 0.4f
                    + (At(gx - 1, gy, gz) + At(gx + 1, gy, gz) + At(gx, gy - 1, gz) + At(gx, gy + 1, gz) + At(gx, gy, gz - 1) + At(gx, gy, gz + 1)) * 0.1f;
                Assert.That(next[(y * d + z) * w + x].T, Is.EqualTo(expected), $"column {place}, cell ({x}, {y}, {z})");
            }
        }

        // The cell at (x, y, z) of the whole grid: zero outside it and in the missing column.
        float At(int x, int y, int z)
        {
            if (x < 0 || y < 0 || z < 0 || !columns.TryGetValue(new TilePos(x / w, y / h, z / d), out var column)) return 0;
            return column.Read<DynamicBuffer<Heat>>()[(y % h * d + z % d) * w + x % w].T;
        }
    }

    /// <summary>The layers of a column a cell pass covers (ITileLayers): where its heat and its neighbors' reach.</summary>
    public record struct HeatLayers(int From, int To) : IComponent, ITileLayers;

    /// <summary>The seven-point stencil bounded to each column's layers.</summary>
    [EcsSystem]
    public ref struct CellLayersSystem
    {
        [ForEach(Parallel = true, ChunkSize = 2)]
        static void Step(Cell<Heat, PlaceColumn> w, in HeatLayers layers, out NextHeat next) =>
            next = new(w.Value.T * 0.4f + (w[-1, 0, 0].T + w[1, 0, 0].T + w[0, -1, 0].T + w[0, 1, 0].T + w[0, 0, -1].T + w[0, 0, 1].T) * 0.1f);
    }

    // Columns of 4 x 2 x 2 tiles, heat in layers 2 and 3 of the columns at x = 0: those and the columns beside them
    // (x = 1) take layers 1 to 4, a dry column whose neighbors are dry takes none (x = 2: From above To), and x = 3 takes
    // layers far outside the tile (clamped: every layer). Each step's cells equal the unbounded pass's bit for bit, every
    // cell written (the buffers start at a value no step gives), on one thread and four, and the pass allocates nothing.
    private static (World Bounded, World Whole, List<(Entity Bounded, Entity Whole)> Columns) LayeredColumns(int threads)
    {
        const int w = 8, d = 8, h = 6;
        var bounded = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        var whole = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        var random = new Random(29);
        var columns = new List<(Entity, Entity)>();
        foreach (var place in Box(4, 2, 2))
        {
            var a = bounded.CreateEntity(new PlaceColumn(place), place.X switch
            {
                0 or 1 => new HeatLayers(1, 4),
                2 => new HeatLayers(3, 2),
                _ => new HeatLayers(-5, 50),
            });
            var b = whole.CreateEntity(new PlaceColumn(place));
            var heat = a.GetOrAdd<DynamicBuffer<Heat>>().SetCount(w * d * h);
            if (place.X == 0)
                for (int i = 0; i < 60; i++) heat[(2 + random.Next(2)) * w * d + random.Next(w * d)] = new Heat(random.Next(1, 1000));
            heat.CopyTo(b.GetOrAdd<DynamicBuffer<Heat>>().SetCount(w * d * h));
            a.GetOrAdd<DynamicBuffer<NextHeat>>().SetCount(w * d * h).Fill(new NextHeat(-7));
            b.GetOrAdd<DynamicBuffer<NextHeat>>().SetCount(w * d * h);
            columns.Add((a, b));
        }
        bounded.AddSystem<CellLayersSystem>();
        whole.AddSystem<CellSevenPointSystem>();
        return (bounded, whole, columns);
    }

    [TestCase(1)]
    [TestCase(4)]
    public void LayersBoundThePassToWhereTheCellsAre(int threads)
    {
        var (bounded, whole, columns) = LayeredColumns(threads);
        using (bounded)
        using (whole)
        {
            bounded.Update();
            whole.Update();
            foreach (var (a, b) in columns)
                Assert.That(MemoryMarshal.AsBytes(a.Read<DynamicBuffer<NextHeat>>().AsReadOnlySpan()).SequenceEqual(MemoryMarshal.AsBytes(b.Read<DynamicBuffer<NextHeat>>().AsReadOnlySpan())), Is.True,
                    $"column {a.Read<PlaceColumn>().Pos}");
            Assert.That(bounded.DescribeSchedule().ToString(), Does.Contain("layers of 'layers'"));
        }
    }

    [Test]
    public void LayersAllocateNothing()
    {
        var (bounded, whole, _) = LayeredColumns(4);
        using (bounded)
        using (whole)
        {
            Allocations.WarmUp(bounded.Update, 50);
            Assert.That(Allocations.Of(() => { for (int i = 0; i < 10; i++) bounded.Update(); }), Is.Zero);
        }
    }

    /// <summary>A 3D tile of 8 x 8 x 6: what a cell reads of its place and its layers.</summary>
    public record struct PlaceColumn(TilePos Pos) : IComponent, ITile<TilePos>
    {
        readonly TilePos IIndexed<TilePos>.Key => Pos;
        static int ITileShape.Width => 8;
        static int ITileShape.Depth => 8;
        static int ITileShape.Height => 6;
        static TileFaces ITileShape.Faces => TileFaces.All;
        static TileNeighborhood ITileShape.Neighborhood => TileNeighborhood.All;
        static TilePos ITile<TilePos>.Beside(TilePos p, TileFaces face) => Step(p, face);
    }

    /// <summary>Reads the cell's place: every row runs, the quiet ones too.</summary>
    [EcsSystem]
    public ref struct CellPlaceSystem
    {
        [ForEach]
        static void Step(Cell<Heat, PlaceColumn> w, out NextHeat next, in PlaceColumn column) =>
            next = new(w.X + 10 * w.Z + 100 * w.Y + 1000 * column.Pos.X + w[0, 1, 0].T + w[0, -1, -1].T * 0.5f);
    }

    /// <summary>The same body as written, cell by cell.</summary>
    [EcsSystem]
    public ref struct CellPlaceByCellSystem
    {
        [ForEach(SimdMode = SimdMode.Off)]
        static void Step(Cell<Heat, PlaceColumn> w, out NextHeat next, in PlaceColumn column) =>
            next = new(w.X + 10 * w.Z + 100 * w.Y + 1000 * column.Pos.X + w[0, 1, 0].T + w[0, -1, -1].T * 0.5f);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ACellReadsItsPlaceAndItsLayers(bool byCell)
    {
        using var world = new World();
        var columns = new Dictionary<TilePos, Entity>();
        foreach (var place in Box(2, 2, 1))
        {
            var column = world.CreateEntity(new PlaceColumn(place));
            var heat = column.GetOrAdd<DynamicBuffer<Heat>>().SetCount(8 * 8 * 6);
            column.GetOrAdd<DynamicBuffer<NextHeat>>().SetCount(8 * 8 * 6);
            for (int i = 0; i < heat.Length; i++) heat[i] = new Heat(place.X * 7 + place.Y * 3 + i % 11);
            columns.Add(place, column);
        }
        if (byCell) world.AddSystem<CellPlaceByCellSystem>();
        else world.AddSystem<CellPlaceSystem>();
        world.Update();

        foreach (var (place, column) in columns)
        {
            ref readonly var next = ref column.Read<DynamicBuffer<NextHeat>>();
            for (int y = 0; y < 6; y++)
            for (int z = 0; z < 8; z++)
            for (int x = 0; x < 8; x++)
            {
                float expected = x + 10 * z + 100 * y + 1000 * place.X + Heat(place, x, y + 1, z) + Heat(place, x, y - 1, z - 1) * 0.5f;
                Assert.That(next[(y * 8 + z) * 8 + x].T, Is.EqualTo(expected), $"column {place}, cell ({x}, {y}, {z})");
            }
        }

        // The cell (x, y, z) of the whole grid of columns stacked two high: zero past its edges.
        float Heat(TilePos at, int x, int y, int z)
        {
            int gy = at.Y * 6 + y;
            if (z < 0 || gy < 0 || gy >= 12) return 0;
            return columns[new TilePos(at.X, gy / 6, 0)].Read<DynamicBuffer<Heat>>()[(gy % 6 * 8 + z) * 8 + x].T;
        }
    }

    /// <summary>A glow of one on every cell: a row the walk skips (zeros around) still gets its one.</summary>
    [EcsSystem]
    public ref struct CellGlowSystem
    {
        [ForEach]
        static void Step(Cell<Heat, HeatPlate> w, out NextHeat next) => next = new(w.Value.T * 0.5f + w[0, 0, 1].T * 0.25f + 1f);
    }

    [Test]
    public void TheRowsTheWalkSkipsGetWhatTheMethodComputesFromZeros()
    {
        using var world = new World();
        var plate = world.CreateEntity(new HeatPlate(new TilePos(0, 0, 0)));
        var heat = plate.GetOrAdd<DynamicBuffer<Heat>>().SetCount(256);
        var stale = plate.GetOrAdd<DynamicBuffer<NextHeat>>().SetCount(256);
        stale.Fill(new NextHeat(-5)); // what the last step left: every cell is written
        heat[5 * 16 + 3] = new Heat(8); // row 5 is warm, row 4 reads it
        world.AddSystem<CellGlowSystem>();
        world.Update();

        ref readonly var next = ref plate.Read<DynamicBuffer<NextHeat>>();
        for (int i = 0; i < 256; i++)
        {
            float expected = i == 5 * 16 + 3 ? 5f : i == 4 * 16 + 3 ? 3f : 1f;
            Assert.That(next[i].T, Is.EqualTo(expected), $"cell {i}");
        }
    }

    /// <summary>The glow stepping the tile: its blank value is not zero, so the rows the walk skips are written too.</summary>
    [EcsSystem]
    public ref struct CellGlowStepSystem
    {
        [ForEach]
        static void Step(Cell<Heat, HeatPlate> w, out Heat next) => next = new(w.Value.T * 0.5f + w[0, 0, 1].T * 0.25f + 1f);
    }

    /// <summary>A cooling stepping the tile: its blank value is zero, which the rows the step does not write get.</summary>
    [EcsSystem]
    public ref struct CellCoolStepSystem
    {
        [ForEach]
        static void Step(Cell<Heat, HeatPlate> w, out Heat next) => next = new(w.Value.T * 0.5f + w[0, 0, 1].T * 0.25f);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AStepOfCellsWritesTheTileAndItsSkippedRows(bool glow)
    {
        using var world = new World();
        var plate = world.CreateEntity(new HeatPlate(new TilePos(0, 0, 0)));
        var heat = plate.GetOrAdd<DynamicBuffer<Heat>>().SetCount(256);
        heat[5 * 16 + 3] = new Heat(8); // row 5 is warm, row 4 reads it
        heat[9 * 16 + 7] = new Heat(4);
        if (glow) world.AddSystem<CellGlowStepSystem>();
        else world.AddSystem<CellCoolStepSystem>();
        float add = glow ? 1f : 0f;
        var expected = new float[256];
        for (int i = 0; i < 256; i++) expected[i] = add;
        expected[5 * 16 + 3] = 4f + add;
        expected[4 * 16 + 3] = 2f + add;
        expected[9 * 16 + 7] = 2f + add;
        expected[8 * 16 + 7] = 1f + add;
        world.Update();
        for (int i = 0; i < 256; i++)
            Assert.That(plate.Read<DynamicBuffer<Heat>>()[i].T, Is.EqualTo(expected[i]), $"cell {i}");

        // A second step from the first's cells: the buffers swapped back hold what the first step left in them.
        world.Update();
        for (int i = 0; i < 256; i++)
        {
            float below = i / 16 < 15 ? expected[i + 16] : 0f;
            Assert.That(plate.Read<DynamicBuffer<Heat>>()[i].T, Is.EqualTo(expected[i] * 0.5f + below * 0.25f + add), $"cell {i}, step 2");
        }

        var schedule = world.DescribeSchedule().ToString();
        Assert.That(schedule, Does.Contain("steps the tile of 'w'"));
        long bytes = Allocations.Of(() => world.Update());
        Assert.That(bytes, Is.Zero);
    }

    public record struct Count(int N);
    public record struct Share(int N);

    /// <summary>Divides by the cell: on zeros it would throw.</summary>
    [EcsSystem]
    public ref struct CellShareSystem
    {
        [ForEach]
        static void Step(Cell<Count, HeatPlate> c, out Share share) => share = new(1000 / c.Value.N);
    }

    [Test]
    public void ATileWithoutQuietRowsNeverRunsTheMethodOnZeros()
    {
        using var world = new World();
        var plate = world.CreateEntity(new HeatPlate(new TilePos(0, 0, 0)));
        var counts = plate.GetOrAdd<DynamicBuffer<Count>>().SetCount(256);
        for (int i = 0; i < 256; i++) counts[i] = new Count(i + 1);
        plate.GetOrAdd<DynamicBuffer<Share>>();
        world.AddSystem<CellShareSystem>();
        world.Update();
        var shares = plate.Read<DynamicBuffer<Share>>().AsReadOnlySpan();
        for (int i = 0; i < 256; i++) Assert.That(shares[i].N, Is.EqualTo(1000 / (i + 1)));
    }

    /// <summary>A plate of 4 x 4: its rows of ints are 16 bytes, narrower than Vector&lt;T&gt; on AVX2, so they run Vector128.</summary>
    public record struct NarrowPlate(TilePos Pos) : IComponent, ITile<TilePos>
    {
        readonly TilePos IIndexed<TilePos>.Key => Pos;
        static int ITileShape.Width => 4;
        static int ITileShape.Depth => 4;
        static TilePos ITile<TilePos>.Beside(TilePos p, TileFaces face) => Wrap(p, face);
    }

    public record struct Unsigned(uint N);
    public record struct Curve(float F);

    // One field each: a struct of several fields stores through Interleaved, which has no Vector128 form.

    /// <summary>Signed integer division in rows of 16 bytes.</summary>
    [EcsSystem]
    public ref struct CellNarrowSignedSystem
    {
        [ForEach]
        static void Step(Cell<Count, NarrowPlate> c, out Share q) => q = new(1000003 / c.Value.N);
    }

    /// <summary>Unsigned integer division in rows of 16 bytes.</summary>
    [EcsSystem]
    public ref struct CellNarrowUnsignedSystem
    {
        [ForEach]
        static void Step(Cell<Count, NarrowPlate> c, out Unsigned q) => q = new(4000000000u / (uint)c.Value.N);
    }

    /// <summary>LaneMath's Sinh (FloatMode.Fast) in rows of 16 bytes.</summary>
    [EcsSystem]
    public ref struct CellNarrowCurveSystem
    {
        [ForEach(FloatMode = FloatMode.Fast)]
        static void Step(Cell<Count, NarrowPlate> c, out Curve q) => q = new(MathF.Sinh(c.Value.N * 0.01f));
    }

    [Test]
    public void RowsOf16BytesDivideInVector128()
    {
        using var world = new World();
        var plate = world.CreateEntity(new NarrowPlate(new TilePos(0, 0, 0)));
        var counts = plate.GetOrAdd<DynamicBuffer<Count>>().SetCount(16);
        int[] ns = [1, 2, 3, 7, -1, -7, 1000003, -1000003, int.MaxValue, 13, 100, -100, 65536, 99991, -5, 1 << 20];
        for (int i = 0; i < 16; i++) counts[i] = new Count(ns[i]);
        plate.GetOrAdd<DynamicBuffer<Share>>();
        plate.GetOrAdd<DynamicBuffer<Unsigned>>();
        plate.GetOrAdd<DynamicBuffer<Curve>>();
        world.AddSystem<CellNarrowSignedSystem>();
        world.AddSystem<CellNarrowUnsignedSystem>();
        world.AddSystem<CellNarrowCurveSystem>();
        world.Update();
        var signed = plate.Read<DynamicBuffer<Share>>().AsReadOnlySpan();
        var unsigned = plate.Read<DynamicBuffer<Unsigned>>().AsReadOnlySpan();
        var curve = plate.Read<DynamicBuffer<Curve>>().AsReadOnlySpan();
        for (int i = 0; i < 16; i++)
        {
            int n = ns[i];
            Assert.That(signed[i].N, Is.EqualTo(1000003 / n), $"cell {i}");
            Assert.That(unsigned[i].N, Is.EqualTo(4000000000u / (uint)n), $"cell {i}");
            Assert.That(curve[i].F, Is.EqualTo(MathF.Sinh(n * 0.01f)).Within(2).Ulps, $"cell {i}");
        }
        string schedule = world.DescribeSchedule().ToString();
        if (System.Numerics.Vector.IsHardwareAccelerated && System.Numerics.Vector<int>.Count > 4)
            Assert.That(schedule.Split("Vector<int> or Vector128 per row").Length - 1, Is.EqualTo(3), schedule); // all three: LaneMath has Vector128 forms
    }

    /// <summary>Two inputs of one plate (the second only sampled) and two out cells.</summary>
    [EcsSystem]
    public ref struct CellPairSystem
    {
        [ForEach]
        static void Step(Cell<Heat, HeatPlate> w, Cell<Count, HeatPlate> n, out NextHeat next, out Share share)
        {
            next = new(w.Value.T + w[1, 0, 0].T * n[0, 0, 1].N);
            share = new(n.Value.N * 2 + n[-1, 0, 0].N - (int)w[0, 0, -1].T);
        }
    }

    /// <summary>The same method cell by cell.</summary>
    [EcsSystem]
    public ref struct CellPairByCellSystem
    {
        [ForEach(SimdMode = SimdMode.Off)]
        static void Step(Cell<Heat, HeatPlate> w, Cell<Count, HeatPlate> n, out NextHeat next, out Share share)
        {
            next = new(w.Value.T + w[1, 0, 0].T * n[0, 0, 1].N);
            share = new(n.Value.N * 2 + n[-1, 0, 0].N - (int)w[0, 0, -1].T);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void TwoCellsAndTwoOutCellsAreTheArithmeticOfTheWholePlate(bool byCell)
    {
        using var world = new World();
        var plate = world.CreateEntity(new HeatPlate(new TilePos(0, 0, 0)));
        var heat = plate.GetOrAdd<DynamicBuffer<Heat>>().SetCount(256);
        var counts = plate.GetOrAdd<DynamicBuffer<Count>>().SetCount(256);
        var random = new Random(5);
        for (int i = 0; i < 256; i++)
        {
            // Rows 6 to 9 cold and empty in both: no row may be skipped by the heat alone, the counts are sampled.
            int row = i / 16;
            heat[i] = new Heat(row is >= 6 and <= 9 ? 0 : random.Next(-50, 50));
            counts[i] = new Count(row is >= 6 and <= 8 ? 0 : random.Next(-9, 9));
        }
        plate.GetOrAdd<DynamicBuffer<NextHeat>>();
        plate.GetOrAdd<DynamicBuffer<Share>>();
        if (byCell) world.AddSystem<CellPairByCellSystem>();
        else world.AddSystem<CellPairSystem>();
        world.Update();
        var heats = heat.ToArray();
        var ns = counts.ToArray();

        float H(int x, int z) => x is >= 0 and < 16 && z is >= 0 and < 16 ? heats[z * 16 + x].T : 0;
        int N(int x, int z) => x is >= 0 and < 16 && z is >= 0 and < 16 ? ns[z * 16 + x].N : 0;
        var next = plate.Read<DynamicBuffer<NextHeat>>().AsReadOnlySpan();
        var share = plate.Read<DynamicBuffer<Share>>().AsReadOnlySpan();
        for (int z = 0; z < 16; z++)
        for (int x = 0; x < 16; x++)
        {
            Assert.That(next[z * 16 + x].T, Is.EqualTo(H(x, z) + H(x + 1, z) * N(x, z + 1)), $"next ({x}, {z})");
            Assert.That(share[z * 16 + x].N, Is.EqualTo(N(x, z) * 2 + N(x - 1, z) - (int)H(x, z - 1)), $"share ({x}, {z})");
        }
        string schedule = world.DescribeSchedule().ToString();
        Assert.That(schedule, Does.Contain(byCell ? "cells of 'w', 'n': scalar (SimdMode.Off; every row: several cells)" : "cells of 'w', 'n': vector ("), schedule);
    }

    public record struct Wet(byte L);
    public record struct Stone(byte R);
    public record struct NextWet(byte L);

    /// <summary>Water spreading over sampled rock: on a dry stencil it is 0 whatever the rock (proved), so dry rows are skipped.</summary>
    [EcsSystem]
    public ref struct CellSpreadSystem
    {
        [ForEach]
        static void Step(Cell<Wet, HeatPlate> w, Cell<Stone, HeatPlate> g, out NextWet next)
        {
            int side = Math.Max(Math.Max(w[-1, 0, 0].L, w[1, 0, 0].L), Math.Max(w[0, 0, -1].L, w[0, 0, 1].L));
            next = new((byte)(g.Value.R != 0 ? 0 : Math.Max(side - 1, (int)w.Value.L)));
        }
    }

    /// <summary>A constant term: a dry stencil gives 7 (proved), and reads its place.</summary>
    [EcsSystem]
    public ref struct CellSevenSystem
    {
        [ForEach]
        static void Step(Cell<Wet, HeatPlate> w, Cell<Stone, HeatPlate> g, out NextWet next) =>
            next = new((byte)(w.Value.L != 0 ? w.X + g.Value.R : 7));
    }

    /// <summary>What the rock adds on a dry stencil too: not proved, every row runs.</summary>
    [EcsSystem]
    public ref struct CellSumSystem
    {
        [ForEach]
        static void Step(Cell<Wet, HeatPlate> w, Cell<Stone, HeatPlate> g, out NextWet next) => next = new((byte)(w.Value.L + g.Value.R));
    }

    [EcsSystem]
    public ref struct CellSpreadByCellSystem
    {
        [ForEach(SimdMode = SimdMode.Off)]
        static void Step(Cell<Wet, HeatPlate> w, Cell<Stone, HeatPlate> g, out NextWet next)
        {
            int side = Math.Max(Math.Max(w[-1, 0, 0].L, w[1, 0, 0].L), Math.Max(w[0, 0, -1].L, w[0, 0, 1].L));
            next = new((byte)(g.Value.R != 0 ? 0 : Math.Max(side - 1, (int)w.Value.L)));
        }
    }

    private static Func<int, int, byte> Spread(byte[] wet, byte[] rock)
    {
        int W(int x, int z) => x is >= 0 and < 16 && z is >= 0 and < 16 ? wet[z * 16 + x] : 0;
        return (x, z) =>
        {
            int side = Math.Max(Math.Max(W(x - 1, z), W(x + 1, z)), Math.Max(W(x, z - 1), W(x, z + 1)));
            return (byte)(rock[z * 16 + x] != 0 ? 0 : Math.Max(side - 1, W(x, z)));
        };
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public void BlankRowsOfTheDriverAreProvedAndSkipped(int form)
    {
        using var world = new World();
        var plate = world.CreateEntity(new HeatPlate(new TilePos(0, 0, 0)));
        var wet = new byte[256];
        var rock = new byte[256];
        var random = new Random(9);
        for (int i = 0; i < 256; i++)
        {
            rock[i] = (byte)(random.Next(3) == 0 ? 1 : 0);  // rock everywhere: no row of it is blank
            if (i / 16 is 7 or 8) wet[i] = (byte)random.Next(0, 8); // water on rows 7 and 8 only
        }
        var w = plate.GetOrAdd<DynamicBuffer<Wet>>().SetCount(256);
        var g = plate.GetOrAdd<DynamicBuffer<Stone>>().SetCount(256);
        for (int i = 0; i < 256; i++) (w[i], g[i]) = (new Wet(wet[i]), new Stone(rock[i]));
        plate.GetOrAdd<DynamicBuffer<NextWet>>().SetCount(256).Fill(new NextWet(99)); // what the last step left: every cell is written
        switch (form)
        {
            case 0: world.AddSystem<CellSpreadSystem>(); break;
            case 1: world.AddSystem<CellSpreadByCellSystem>(); break;
            case 2: world.AddSystem<CellSevenSystem>(); break;
            default: world.AddSystem<CellSumSystem>(); break;
        }
        world.Update();

        var next = plate.Read<DynamicBuffer<NextWet>>().AsReadOnlySpan();
        var spread = Spread(wet, rock);
        for (int z = 0; z < 16; z++)
        for (int x = 0; x < 16; x++)
        {
            int i = z * 16 + x;
            int expected = form switch { 2 => wet[i] != 0 ? (byte)(x + rock[i]) : 7, 3 => (byte)(wet[i] + rock[i]), _ => spread(x, z) };
            Assert.That(next[i].L, Is.EqualTo(expected), $"cell ({x}, {z})");
        }
        string schedule = world.DescribeSchedule().ToString();
        string walk = form switch { 1 => "every row: several cells", 3 => "every row: several cells", _ => "rows of 'w': blank rows proved constant" };
        Assert.That(schedule, Does.Contain(walk), schedule);
    }

    public record struct Warm(float T);

    /// <summary>0 times a sampled float is NaN where the sample is: not proved.</summary>
    [EcsSystem]
    public ref struct CellNaNSystem
    {
        [ForEach]
        static void Step(Cell<Heat, HeatPlate> w, Cell<Warm, HeatPlate> g, out NextHeat next) => next = new(w.Value.T * g.Value.T);
    }

    /// <summary>A sampled choice between two constants: not proved.</summary>
    [EcsSystem]
    public ref struct CellChoiceSystem
    {
        [ForEach]
        static void Step(Cell<Wet, HeatPlate> w, Cell<Stone, HeatPlate> g, out NextWet next) => next = new((byte)(g.Value.R != 0 ? 1 : 2 + w.Value.L));
    }

    /// <summary>A sampled choice whose arms are both 0 on a dry cell: proved.</summary>
    [EcsSystem]
    public ref struct CellAgreeSystem
    {
        [ForEach]
        static void Step(Cell<Wet, HeatPlate> w, Cell<Stone, HeatPlate> g, out NextWet next) => next = new((byte)(g.Value.R != 0 ? w.Value.L : w.Value.L * g.Value.R));
    }

    [Test]
    public void OnlyWhatTheDriverDecidesIsProved()
    {
        using var world = new World();
        world.AddSystem<CellNaNSystem>();
        world.AddSystem<CellChoiceSystem>();
        world.AddSystem<CellAgreeSystem>();
        var plate = world.CreateEntity(new HeatPlate(new TilePos(0, 0, 0)));
        plate.GetOrAdd<DynamicBuffer<Heat>>().SetCount(256);
        plate.GetOrAdd<DynamicBuffer<Warm>>().SetCount(256).Fill(new Warm(float.NaN));
        plate.GetOrAdd<DynamicBuffer<Wet>>().SetCount(256);
        plate.GetOrAdd<DynamicBuffer<Stone>>().SetCount(256)[3] = new Stone(1);
        plate.GetOrAdd<DynamicBuffer<NextHeat>>();
        plate.GetOrAdd<DynamicBuffer<NextWet>>();
        world.Update();
        string schedule = world.DescribeSchedule().ToString();
        string[] lines = schedule.Split('\n');
        string Line(string system) => lines[Array.FindIndex(lines, l => l.Contains(system + ",")) + 1];
        Assert.That(Line("CellNaNSystem"), Does.Contain("every row: several cells"), schedule);
        Assert.That(Line("CellChoiceSystem"), Does.Contain("every row: several cells"), schedule);
        Assert.That(Line("CellAgreeSystem"), Does.Contain("blank rows proved constant"), schedule);
        // Every row ran where nothing was proved: a skipped row would hold 0 * 0 from the blank cells, not NaN.
        Assert.That(float.IsNaN(plate.Read<DynamicBuffer<NextHeat>>()[100].T));
    }

    /// <summary>X in byte lanes: compared and added to, and a result per tile of two-byte fields summed from it.</summary>
    [EcsSystem]
    public ref struct CellEdgeSystem
    {
        [ForEach]
        static void Step(Cell<Life, LifePlate> c, out NextLife next, ref Reduce<EdgeTally> tally)
        {
            int edge = c.X == 0 || c.X == 15 ? 1 : 0;
            next = new((byte)(c.Value.Alive + edge * 10 + (c.X > 7 ? c.X : 0)));
            tally.Value.Edges += c.Value.Alive != 0 && edge != 0 ? (ushort)1 : (ushort)0;
            tally.Value.Right = Math.Max(tally.Value.Right, c.Value.Alive != 0 ? (short)c.X : short.MinValue);
        }
    }

    [EcsSystem]
    public ref struct CellEdgeByCellSystem
    {
        [ForEach(SimdMode = SimdMode.Off)]
        static void Step(Cell<Life, LifePlate> c, out NextLife next, ref Reduce<EdgeTally> tally)
        {
            int edge = c.X == 0 || c.X == 15 ? 1 : 0;
            next = new((byte)(c.Value.Alive + edge * 10 + (c.X > 7 ? c.X : 0)));
            tally.Value.Edges += c.Value.Alive != 0 && edge != 0 ? (ushort)1 : (ushort)0;
            tally.Value.Right = Math.Max(tally.Value.Right, c.Value.Alive != 0 ? (short)c.X : short.MinValue);
        }
    }

    /// <summary>A plate of 300 x 2: its X passes a byte, so the row runs cell by cell (the vector form holds X up to 255).</summary>
    public record struct BytePastPlate(TilePos Pos) : IComponent, ITile<TilePos>
    {
        readonly TilePos IIndexed<TilePos>.Key => Pos;
        static int ITileShape.Width => 300;
        static int ITileShape.Depth => 2;
        static TilePos ITile<TilePos>.Beside(TilePos p, TileFaces face) => Wrap(p, face);
    }

    [EcsSystem]
    public ref struct CellWideEdgeSystem
    {
        [ForEach]
        static void Step(Cell<Life, BytePastPlate> c, out NextLife next) => next = new((byte)(c.Value.Alive + (c.X > 250 ? 1 : 0)));
    }

    [EcsSystem]
    public ref struct CellWideEdgeByCellSystem
    {
        [ForEach(SimdMode = SimdMode.Off)]
        static void Step(Cell<Life, BytePastPlate> c, out NextLife next) => next = new((byte)(c.Value.Alive + (c.X > 250 ? 1 : 0)));
    }

    [TestCase(1)]
    [TestCase(4)]
    public void XInByteLanesIsXCellByCell(int threads)
    {
        using var vector = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        using var scalar = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        var random = new Random(23);
        var plates = new List<(Entity Vector, Entity Scalar)>();
        foreach (var place in Box(Torus, 1, Torus))
        {
            var a = vector.CreateEntity(new LifePlate(place), EdgeTally.Identity);
            var b = scalar.CreateEntity(new LifePlate(place), EdgeTally.Identity);
            var cells = a.GetOrAdd<DynamicBuffer<Life>>().SetCount(256);
            if (place.X != 0) // the plates of column 0 are blank: their result is the identity (each row runs: the method reads X)
                for (int i = 0; i < 256; i++) cells[i] = new Life((byte)(random.Next(4) == 0 ? random.Next(1, 3) : 0));
            cells.CopyTo(b.GetOrAdd<DynamicBuffer<Life>>().SetCount(256));
            a.GetOrAdd<DynamicBuffer<NextLife>>();
            b.GetOrAdd<DynamicBuffer<NextLife>>();
            plates.Add((a, b));
        }
        foreach (var place in Box(2, 1, 1))
        {
            var a = vector.CreateEntity(new BytePastPlate(place));
            var b = scalar.CreateEntity(new BytePastPlate(place));
            var cells = a.GetOrAdd<DynamicBuffer<Life>>().SetCount(600);
            for (int i = 0; i < 600; i++) cells[i] = new Life((byte)random.Next(3));
            cells.CopyTo(b.GetOrAdd<DynamicBuffer<Life>>().SetCount(600));
            a.GetOrAdd<DynamicBuffer<NextLife>>();
            b.GetOrAdd<DynamicBuffer<NextLife>>();
            plates.Add((a, b));
        }
        vector.AddSystem<CellEdgeSystem>();
        vector.AddSystem<CellWideEdgeSystem>();
        scalar.AddSystem<CellEdgeByCellSystem>();
        scalar.AddSystem<CellWideEdgeByCellSystem>();
        vector.Update();
        scalar.Update();
        foreach (var (a, b) in plates)
        {
            Assert.That(a.Read<DynamicBuffer<NextLife>>().AsReadOnlySpan().SequenceEqual(b.Read<DynamicBuffer<NextLife>>().AsReadOnlySpan()), Is.True);
            if (a.Has<EdgeTally>()) Assert.That(a.Read<EdgeTally>(), Is.EqualTo(b.Read<EdgeTally>()));
        }
        var schedule = vector.DescribeSchedule().ToString();
        Assert.That(schedule, Does.Contain("cells of 'c': vector ("));
        Assert.That(schedule, Does.Not.Contain("cells of 'c': scalar"));
    }

    /// <summary>A result per tile: the cells that hold something, and the first and last row that does.</summary>
    [EcsSystem]
    public ref struct CellTallySystem
    {
        [ForEach]
        static void Step(Cell<Count, HeatPlate> c, out Share next, ref Reduce<CountTally> tally)
        {
            int n = c.Value.N;
            next = new(n * 2);
            tally.Value.Held += n != 0 ? 1 : 0;
            tally.Value.First = Math.Min(tally.Value.First, n != 0 ? c.Z : int.MaxValue);
            tally.Value.Last = Math.Max(tally.Value.Last, n != 0 ? c.Z : int.MinValue);
        }
    }

    [EcsSystem]
    public ref struct CellTallyByCellSystem
    {
        [ForEach(SimdMode = SimdMode.Off)]
        static void Step(Cell<Count, HeatPlate> c, out Share next, ref Reduce<CountTally> tally)
        {
            int n = c.Value.N;
            next = new(n * 2);
            tally.Value.Held += n != 0 ? 1 : 0;
            tally.Value.First = Math.Min(tally.Value.First, n != 0 ? c.Z : int.MaxValue);
            tally.Value.Last = Math.Max(tally.Value.Last, n != 0 ? c.Z : int.MinValue);
        }
    }

    [TestCase(false, 1)]
    [TestCase(true, 1)]
    [TestCase(false, 4)]
    public void AResultPerTileIsTheTallyOfItsCells(bool byCell, int threads)
    {
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        var random = new Random(11);
        var plates = new List<(Entity Plate, int[] Counts)>();
        for (int p = 0; p < 6; p++)
        {
            var plate = world.CreateEntity(new HeatPlate(new TilePos(p * 3, 0, 0)), new CountTally { Held = 77, First = -5, Last = 99 });
            var counts = new int[256];
            if (p > 0) // plate 0 holds nothing: its result is the identity
                for (int k = 0; k < 6 * p; k++) counts[random.Next(256)] = random.Next(1, 9);
            var buffer = plate.GetOrAdd<DynamicBuffer<Count>>().SetCount(256);
            for (int i = 0; i < 256; i++) buffer[i] = new Count(counts[i]);
            plate.GetOrAdd<DynamicBuffer<Share>>();
            plates.Add((plate, counts));
        }
        if (byCell) world.AddSystem<CellTallyByCellSystem>();
        else world.AddSystem<CellTallySystem>();
        world.Update();

        foreach (var (plate, counts) in plates)
        {
            int held = 0, first = int.MaxValue, last = int.MinValue;
            for (int i = 0; i < 256; i++)
                if (counts[i] != 0) (held, first, last) = (held + 1, Math.Min(first, i / 16), Math.Max(last, i / 16));
            var tally = plate.Read<CountTally>();
            Assert.That((tally.Held, tally.First, tally.Last), Is.EqualTo((held, first, last)));
            var share = plate.Read<DynamicBuffer<Share>>().AsReadOnlySpan();
            for (int i = 0; i < 256; i++) Assert.That(share[i].N, Is.EqualTo(counts[i] * 2), $"cell {i}");
        }
        if (!byCell)
            Assert.That(world.DescribeSchedule().ToString(), Does.Contain("cells of 'c': vector (").And.Contain("rows of 'c': blank rows proved constant"),
                world.DescribeSchedule().ToString());
    }

    /// <summary>Reads two cells away with a halo of one.</summary>
    [EcsSystem]
    public ref struct CellPastTheHaloSystem
    {
        [ForEach]
        static void Step(Cell<Heat, HeatPlate> w, out NextHeat next) => next = new(w[2, 0, 0].T);
    }

    [Test]
    public void AnOffsetPastTheHaloThrows()
    {
        using var world = new World();
        var plate = world.CreateEntity(new HeatPlate(new TilePos(0, 0, 0)));
        plate.GetOrAdd<DynamicBuffer<Heat>>().SetCount(256);
        plate.GetOrAdd<DynamicBuffer<NextHeat>>().SetCount(256);
        world.AddSystem<CellPastTheHaloSystem>();
        Assert.Catch(() => world.Update());
    }

    [Test]
    public void TheScheduleSaysHowTheCellsRun()
    {
        using var world = new World();
        world.AddSystem<CellBlurTorusSystem>();
        world.AddSystem<CellLifeSystem>();
        world.AddSystem<CellPlaceSystem>();
        world.AddSystem<CellPlaceByCellSystem>();
        string schedule = world.DescribeSchedule().ToString();
        Assert.That(schedule, Does.Contain("cells of 'w': vector (").And.Contain("cells of 'c': vector (")
            .And.Contain("every row: it reads its place").And.Contain("cells of 'w': scalar (SimdMode.Off; every row: it reads its place)"), schedule);
    }

    /// <summary>Plates of 32 floats a row, a halo of 2: four vectors of 128 bits, two of 256, one of 512 per row... and a second.</summary>
    public record struct PlaneWide(TilePos Pos) : IComponent, ITile<TilePos>
    {
        readonly TilePos IIndexed<TilePos>.Key => Pos;
        static int ITileShape.Width => 32;
        static int ITileShape.Depth => 8;
        static int ITileShape.Halo => 2;
        static TileNeighborhood ITileShape.Neighborhood => TileNeighborhood.All;
        static TilePos ITile<TilePos>.Beside(TilePos p, TileFaces face) => Step(p, face);
    }

    /// <summary>Plates of 64 bytes a row, a halo of 2: shifts by one and two bytes at every width.</summary>
    public record struct PlaneBytes(TilePos Pos) : IComponent, ITile<TilePos>
    {
        readonly TilePos IIndexed<TilePos>.Key => Pos;
        static int ITileShape.Width => 64;
        static int ITileShape.Depth => 4;
        static int ITileShape.Halo => 2;
        static TileNeighborhood ITileShape.Neighborhood => TileNeighborhood.All;
        static TilePos ITile<TilePos>.Beside(TilePos p, TileFaces face) => Step(p, face);
    }

    /// <summary>Plates of 12 floats a row (48 bytes, not a whole number of 256-bit vectors): the halo copied, as before.</summary>
    public record struct PlaneOdd(TilePos Pos) : IComponent, ITile<TilePos>
    {
        readonly TilePos IIndexed<TilePos>.Key => Pos;
        static int ITileShape.Width => 12;
        static int ITileShape.Depth => 4;
        static TileNeighborhood ITileShape.Neighborhood => TileNeighborhood.All;
        static TilePos ITile<TilePos>.Beside(TilePos p, TileFaces face) => Step(p, face);
    }

    [EcsSystem]
    public ref struct CellWideSystem
    {
        [ForEach(Parallel = true, ChunkSize = 2)]
        static void Step(Cell<Heat, PlaneWide> w, out Heat next) =>
            next = new(w.Value.T * 0.5f + (w[-2, 0, 0].T + w[2, 0, 0].T) * 0.1f + (w[-1, 0, -1].T + w[1, 0, 1].T) * 0.05f
                + w[0, 0, -2].T * 0.01f + w[1, 0, 2].T * 0.02f + w[-2, 0, 1].T * 0.03f);
    }

    [EcsSystem]
    public ref struct CellWideByCellSystem
    {
        [ForEach(Parallel = true, ChunkSize = 2, SimdMode = SimdMode.Off)]
        static void Step(Cell<Heat, PlaneWide> w, out Heat next) =>
            next = new(w.Value.T * 0.5f + (w[-2, 0, 0].T + w[2, 0, 0].T) * 0.1f + (w[-1, 0, -1].T + w[1, 0, 1].T) * 0.05f
                + w[0, 0, -2].T * 0.01f + w[1, 0, 2].T * 0.02f + w[-2, 0, 1].T * 0.03f);
    }

    // Cross stencils (shifts along x on the cell's own row only): the forms that read the halo in place.
    [EcsSystem]
    public ref struct CellWideCrossSystem
    {
        [ForEach(Parallel = true, ChunkSize = 2)]
        static void Step(Cell<Heat, PlaneWide> w, out Heat next) =>
            next = new(w.Value.T * 0.5f + (w[-2, 0, 0].T + w[2, 0, 0].T) * 0.1f + (w[-1, 0, 0].T + w[1, 0, 0].T) * 0.05f
                + w[0, 0, -2].T * 0.01f + w[0, 0, 2].T * 0.02f + w[0, 0, -1].T * 0.03f);
    }

    [EcsSystem]
    public ref struct CellWideCrossByCellSystem
    {
        [ForEach(Parallel = true, ChunkSize = 2, SimdMode = SimdMode.Off)]
        static void Step(Cell<Heat, PlaneWide> w, out Heat next) =>
            next = new(w.Value.T * 0.5f + (w[-2, 0, 0].T + w[2, 0, 0].T) * 0.1f + (w[-1, 0, 0].T + w[1, 0, 0].T) * 0.05f
                + w[0, 0, -2].T * 0.01f + w[0, 0, 2].T * 0.02f + w[0, 0, -1].T * 0.03f);
    }

    [EcsSystem]
    public ref struct CellByteCrossSystem
    {
        [ForEach(Parallel = true, ChunkSize = 2)]
        static void Step(Cell<Life, PlaneBytes> c, out NextLife next) =>
            next = new((byte)(c[-2, 0, 0].Alive + c[1, 0, 0].Alive + c[0, 0, 1].Alive + c[0, 0, -2].Alive + c.Value.Alive));
    }

    [EcsSystem]
    public ref struct CellByteCrossByCellSystem
    {
        [ForEach(Parallel = true, ChunkSize = 2, SimdMode = SimdMode.Off)]
        static void Step(Cell<Life, PlaneBytes> c, out NextLife next) =>
            next = new((byte)(c[-2, 0, 0].Alive + c[1, 0, 0].Alive + c[0, 0, 1].Alive + c[0, 0, -2].Alive + c.Value.Alive));
    }

    [EcsSystem]
    public ref struct CellByteSystem
    {
        [ForEach(Parallel = true, ChunkSize = 2)]
        static void Step(Cell<Life, PlaneBytes> c, out NextLife next) =>
            next = new((byte)(c[-2, 0, 0].Alive + c[1, 0, -1].Alive + c[2, 0, 1].Alive + c[-1, 0, 2].Alive + c.Value.Alive));
    }

    [EcsSystem]
    public ref struct CellByteByCellSystem
    {
        [ForEach(Parallel = true, ChunkSize = 2, SimdMode = SimdMode.Off)]
        static void Step(Cell<Life, PlaneBytes> c, out NextLife next) =>
            next = new((byte)(c[-2, 0, 0].Alive + c[1, 0, -1].Alive + c[2, 0, 1].Alive + c[-1, 0, 2].Alive + c.Value.Alive));
    }

    [EcsSystem]
    public ref struct CellOddSystem
    {
        [ForEach(Parallel = true, ChunkSize = 2)]
        static void Step(Cell<Heat, PlaneOdd> w, out NextHeat next) =>
            next = new((w[-1, 0, -1].T + w[1, 0, 1].T + w[-1, 0, 0].T + w.Value.T + w[1, 0, -1].T) * 0.2f);
    }

    [EcsSystem]
    public ref struct CellOddByCellSystem
    {
        [ForEach(Parallel = true, ChunkSize = 2, SimdMode = SimdMode.Off)]
        static void Step(Cell<Heat, PlaneOdd> w, out NextHeat next) =>
            next = new((w[-1, 0, -1].T + w[1, 0, 1].T + w[-1, 0, 0].T + w.Value.T + w[1, 0, -1].T) * 0.2f);
    }

    /// <summary>
    /// The halo read in place (TilePlane, CellRow's shifts: cross stencils) against the method as written over the copied
    /// halo, bit for bit: rows of several vectors at every width (32 floats stepping their tile), shifts by one and two
    /// bytes (64 bytes); box stencils of the same shapes and a row the shape does not let it read in place (12 floats),
    /// which copy the halo; on a grid with holes and quiet rows, 1 and 4 threads. The schedule says which read in place.
    /// </summary>
    [TestCase(1)]
    [TestCase(4)]
    public void TheHaloReadInPlaceIsTheHaloCopied(int threads)
    {
        var holes = new[] { new TilePos(1, 0, 1), new TilePos(2, 0, 0) };
        Twins<PlaneWide, Heat, CellWideCrossSystem, CellWideCrossByCellSystem>(threads, p => new PlaneWide(p), 32 * 8, holes, 6,
            static (r, i) => i % 3 == 0 ? new Heat(r.Next(1, 1000)) : default, null, true);
        Twins<PlaneBytes, Life, CellByteCrossSystem, CellByteCrossByCellSystem>(threads, p => new PlaneBytes(p), 64 * 4, holes, 1,
            static (r, i) => new Life((byte)r.Next(4)), static e => e.GetOrAdd<DynamicBuffer<NextLife>>().SetCount(64 * 4), true);
        Twins<PlaneWide, Heat, CellWideSystem, CellWideByCellSystem>(threads, p => new PlaneWide(p), 32 * 8, holes, 6,
            static (r, i) => i % 3 == 0 ? new Heat(r.Next(1, 1000)) : default, null, false);
        Twins<PlaneBytes, Life, CellByteSystem, CellByteByCellSystem>(threads, p => new PlaneBytes(p), 64 * 4, holes, 1,
            static (r, i) => new Life((byte)r.Next(4)), static e => e.GetOrAdd<DynamicBuffer<NextLife>>().SetCount(64 * 4), false);
        Twins<PlaneOdd, Heat, CellOddSystem, CellOddByCellSystem>(threads, p => new PlaneOdd(p), 12 * 4, holes, 1,
            static (r, i) => new Heat(r.Next(-1000, 1000) * 0.5f), static e => e.GetOrAdd<DynamicBuffer<NextHeat>>().SetCount(12 * 4), false);

        static void Twins<TTile, TCell, TVector, TScalar>(int threads, Func<TilePos, TTile> place, int cells, TilePos[] holes, int steps,
            Func<Random, int, TCell> fill, Action<Entity> outBuffer, bool inPlace)
            where TTile : struct, IComponent where TCell : unmanaged where TVector : struct, allows ref struct where TScalar : struct, allows ref struct
        {
            using var vector = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
            using var scalar = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
            var random = new Random(17);
            var plates = new List<(Entity Vector, Entity Scalar)>();
            foreach (var p in Box(3, 1, 3, holes))
            {
                var a = vector.CreateEntity(place(p));
                var b = scalar.CreateEntity(place(p));
                var c = a.GetOrAdd<DynamicBuffer<TCell>>().SetCount(cells);
                if (p.X != 0) // a quiet column of plates
                    for (int i = 0; i < cells; i++)
                        if (i / 8 % 2 == 0) c[i] = fill(random, i); // quiet rows too
                c.CopyTo(b.GetOrAdd<DynamicBuffer<TCell>>().SetCount(cells));
                outBuffer?.Invoke(a);
                outBuffer?.Invoke(b);
                plates.Add((a, b));
            }
            vector.AddSystem<TVector>();
            scalar.AddSystem<TScalar>();
            Assert.That(vector.DescribeSchedule().ToString().Contains("read in place"), Is.EqualTo(inPlace), typeof(TVector).Name);
            for (int step = 0; step < steps; step++)
            {
                vector.Update();
                scalar.Update();
                foreach (var (a, b) in plates)
                {
                    Assert.That(System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.Read<DynamicBuffer<TCell>>().AsReadOnlySpan())
                        .SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(b.Read<DynamicBuffer<TCell>>().AsReadOnlySpan())), $"{typeof(TTile).Name} cells, step {step}");
                    if (outBuffer == null) continue;
                    Assert.That(SameOut(a, b), $"{typeof(TTile).Name} out cells, step {step}");
                }
            }
        }

        static bool SameOut(Entity a, Entity b) => a.Has<DynamicBuffer<NextLife>>()
            ? a.Read<DynamicBuffer<NextLife>>().AsReadOnlySpan().SequenceEqual(b.Read<DynamicBuffer<NextLife>>().AsReadOnlySpan())
            : a.Read<DynamicBuffer<NextHeat>>().AsReadOnlySpan().SequenceEqual(b.Read<DynamicBuffer<NextHeat>>().AsReadOnlySpan());
    }

    /// <summary>
    /// CellRow's loads of a row's lanes from i + dx against the lanes read one at a time from the row and the rows beside
    /// it: bytes, shorts, floats and doubles, every dx a vector reaches, every vector of a row of three, each width
    /// (where a width is not accelerated, or its shift instruction is missing, the shift through memory).
    /// </summary>
    [Test]
    public void CellRowLoadsAreTheLanesAcrossTheRowsEnds()
    {
        Check<byte>();
        Check<ushort>();
        Check<float>();
        Check<double>();

        static void Check<T>() where T : unmanaged, System.Numerics.INumber<T>
        {
            foreach (int form in new[] { 16, 32, 64, 0 }) // 0: Vector<T>
            {
                int bytes = form == 0 ? System.Numerics.Vector<byte>.Count : form;
                int lanes = bytes / System.Runtime.CompilerServices.Unsafe.SizeOf<T>(), width = 3 * lanes;
                var row = new T[width];
                var west = new T[width];
                var east = new T[width];
                for (int j = 0; j < width; j++)
                {
                    row[j] = T.CreateTruncating(1 + j);
                    west[j] = T.CreateTruncating(101 + j);
                    east[j] = T.CreateTruncating(201 - j);
                }
                for (int i = 0; i < width; i += lanes)
                for (int dx = -lanes; dx <= lanes; dx++)
                {
                    var got = new T[lanes];
#pragma warning disable CA1857 // every dx through one loop: the shift's immediate is then not a constant, which the JIT handles with a slower form
                    switch (form)
                    {
                        case 16: CellRow.Load128(ref row[0], ref west[0], ref east[0], i, dx, width).CopyTo(got); break;
                        case 32: CellRow.Load256(ref row[0], ref west[0], ref east[0], i, dx, width).CopyTo(got); break;
                        case 64: CellRow.Load512(ref row[0], ref west[0], ref east[0], i, dx, width).CopyTo(got); break;
                        default: CellRow.LoadVector(ref row[0], ref west[0], ref east[0], i, dx, width).CopyTo(got); break;
                    }
#pragma warning restore CA1857
                    for (int j = 0; j < lanes; j++)
                    {
                        int x = i + dx + j;
                        T want = x < 0 ? west[width + x] : x >= width ? east[x - width] : row[x];
                        Assert.That(got[j], Is.EqualTo(want), $"{typeof(T).Name}, {bytes} bytes, i {i}, dx {dx}, lane {j}");
                    }
                }
            }
        }
    }

    [Test]
    public void CellsAllocateNothing()
    {
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4 });
        foreach (var place in Box(Torus, 1, Torus))
        {
            var plate = world.CreateEntity(new BlurTorus(place));
            plate.GetOrAdd<DynamicBuffer<Heat>>().SetCount(256)[place.X * 16 + place.Z] = new Heat(100);
            plate.GetOrAdd<DynamicBuffer<NextHeat>>().SetCount(256);
        }
        world.AddSystem<CellBlurTorusSystem>();
        world.AddSystem<CommitHeatSystem>();
        Allocations.WarmUp(world.Update, 50);
        Assert.That(Allocations.Of(() => { for (int i = 0; i < 10; i++) world.Update(); }), Is.Zero);
    }
}

/// <summary>A cell method's result per tile: what its cells hold, and the first and last row that holds something.</summary>
public partial struct CountTally : IComponent
{
    [Sum] public int Held;
    [Min] public int First;
    [Max] public int Last;
}

/// <summary>A cell method's result per tile of two-byte fields, summed in byte lanes: the live cells on an edge, the rightmost one.</summary>
public partial struct EdgeTally : IComponent
{
    [Sum] public ushort Edges;
    [Max] public short Right;
}
