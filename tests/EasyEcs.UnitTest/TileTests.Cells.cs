using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>
/// Cell methods (<see cref="Cell{TCell, TTile}"/>, the shader form): a blur per boundary, a halo of 2 on a torus and
/// the Game of Life in bytes, each against the whole grid computed at once (bit for bit against the tile methods it
/// replaces), on one thread and on four; the cell's place (X, Y, Z) in a 3D tile; the rows the walk skips filled with
/// what the method computes from zeros; the method as written (SimdMode.Off) against its vector form; an offset past
/// the halo throwing; the schedule's lines and 0 B.
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

    [TestCase(1)]
    [TestCase(4)]
    public void AHaloOfTwoIsTheWholeTorus(int threads)
    {
        const int cells = Torus * 16;
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        var random = new Random(5);
        var plates = new Dictionary<TilePos, Entity>();
        foreach (var place in Box(Torus, 1, Torus))
        {
            var plate = world.CreateEntity(new WaveTorus(place));
            var heat = plate.GetOrAdd<DynamicBuffer<Heat>>().SetCount(256);
            plate.GetOrAdd<DynamicBuffer<NextHeat>>().SetCount(256);
            if (place.X != 1) // a quiet column of plates, for the walk to skip
                for (int i = 0; i < 8; i++) heat[random.Next(256)] = new Heat(random.Next(1, 1000));
            plates.Add(place, plate);
        }
        world.AddSystem<CellWaveSystem>();
        world.AddSystem<CommitHeatSystem>();

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
            .And.Contain("every row: it reads its place").And.Contain("cells of 'w': scalar (SimdMode.Off)"), schedule);
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
