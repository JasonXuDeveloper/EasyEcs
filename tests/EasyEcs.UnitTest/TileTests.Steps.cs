using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>
/// Stepping tiles (a <c>ref Tile</c> parameter): Jacobi steps into the tile's second buffer, swapped in after the pass,
/// the rows the step skips zeroed; [GaussSeidel] sweeps in place, color by color, until settled; tiles that cannot
/// change sleep. Checked bit for bit against the whole grid (heat, a blur per boundary, Life on a torus, a 3D heat, a
/// distance field in 2D and 3D, a 2D tile over a 3D grid), on one thread and on four.
/// </summary>
public partial class TileTests
{
    // ---------------- Jacobi ----------------

    public record struct PingPlate(TilePos Pos) : IComponent, ITile<TilePos>
    {
        readonly TilePos IIndexed<TilePos>.Key => Pos;
        static int ITileShape.Width => 16;
        static int ITileShape.Depth => 16;
        static TilePos ITile<TilePos>.Beside(TilePos p, TileFaces face) => Step(p, face);
    }

    /// <summary>The heat of <see cref="DiffuseSystem"/>, stepped into the tile's second buffer: no NextHeat, no commit.</summary>
    [EcsSystem]
    public ref struct PingHeatSystem
    {
        [ForEach(Parallel = true, ChunkSize = 8)]
        static void Diffuse(ref Tile<Heat, PingPlate> heat)
        {
            heat.ExchangeHalo();
            foreach (var row in heat.Rows(TileStencil.Layer))
            {
                var cells = heat.At(row);
                var into = MemoryMarshal.Cast<Heat, float>(heat.Write(row));
                for (int x = 0; x < 16; x += Vector128<float>.Count)
                {
                    var here = cells.Load128<float>(x);
                    var around = cells.Load128<float>(x - 1) + cells.Load128<float>(x + 1) + cells.Load128<float>(x, 0, -1)
                        + cells.Load128<float>(x, 0, 1);
                    (here + (around - here * 4f) * DiffuseSystem.Rate).CopyTo(into.Slice(x));
                }
            }
        }
    }

    /// <summary>The plates of a heat world, a few hot places; <paramref name="empty"/>: plates whose buffer is empty (sized by the pass).</summary>
    private static (World World, Dictionary<TilePos, Entity> Plates) PingHeatWorld(int threads, int size, TilePos[] holes, TilePos[] empty)
    {
        var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        var plates = new Dictionary<TilePos, Entity>();
        foreach (var place in Box(size, 1, size, holes))
        {
            var plate = world.CreateEntity(new PingPlate(place));
            if (Array.IndexOf(empty, place) < 0)
            {
                var heat = plate.GetOrAdd<DynamicBuffer<Heat>>().SetCount(256);
                if ((place.X + 2 * place.Z) % 5 == 0) heat[(place.X * 3 % 16) * 16 + place.Z * 7 % 16] = new Heat(1000f);
            }
            else plate.GetOrAdd<DynamicBuffer<Heat>>(); // no cells yet
            plates.Add(place, plate);
        }

        world.AddSystem<PingHeatSystem>();
        return (world, plates);
    }

    [TestCase(1)]
    [TestCase(4)]
    public void PingPongHeatIsTheHeatOfTheWholeGrid(int threads)
    {
        const int size = 9, cells = size * 16;
        var holes = new[] { new TilePos(4, 0, 4), new TilePos(0, 0, 8), new TilePos(7, 0, 2) };
        var empty = new[] { new TilePos(8, 0, 8), new TilePos(3, 0, 1) };
        var (world, plates) = PingHeatWorld(threads, size, holes, empty);
        using var _ = world;

        var grid = new float[cells, cells];
        foreach (var (place, plate) in plates)
            if (Array.IndexOf(empty, place) < 0)
                for (int i = 0; i < 256; i++) grid[place.X * 16 + i % 16, place.Z * 16 + i / 16] = plate.Read<DynamicBuffer<Heat>>()[i].T;
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
            foreach (var plate in plates.Values) Assert.That(plate.Read<DynamicBuffer<Heat>>().Count, Is.EqualTo(256), "a tile's cells, sized from its shape");
            Same(plates, 16, 16, grid, (Entity e, int i) => e.Read<DynamicBuffer<Heat>>()[i].T, $"step {step}");
        }
        Assert.That(DynamicBuffer<Heat>.MaxPooledLength, Is.GreaterThanOrEqualTo(256), "the pool keeps the lists of a tile's cells");

        float At(int x, int z) => x < 0 || z < 0 || x >= cells || z >= cells || !plates.ContainsKey(new TilePos(x / 16, 0, z / 16)) ? 0 : grid[x, z];
    }

    [TestCase(1)]
    [TestCase(4)]
    public void PingPongStepsLeaveSnapshotsAsTheyWere(int threads)
    {
        // A snapshot shares each tile's list until it is written: the swap of a shared list's array would change the
        // snapshot too, so a shared list goes to the buffer whole and the next step takes another.
        var (world, plates) = PingHeatWorld(threads, 6, [], []);
        var (twin, twinPlates) = PingHeatWorld(threads, 6, [], []);
        using var a = world;
        using var b = twin;
        for (int i = 0; i < 10; i++)
        {
            world.Update();
            twin.Update();
        }
        var snapshot = world.Capture();
        var captured = Cells(plates);
        for (int i = 0; i < 10; i++) world.Update();
        world.Restore(snapshot);
        Assert.That(Cells(plates), Is.EqualTo(captured), "the snapshot keeps the cells it captured");
        for (int i = 0; i < 10; i++)
        {
            world.Update();
            twin.Update();
        }
        Assert.That(Cells(plates), Is.EqualTo(Cells(twinPlates)), "the steps after a restore are the steps of a world never restored");

        static float[] Cells(Dictionary<TilePos, Entity> plates)
        {
            var all = new List<float>();
            foreach (var (_, plate) in plates.OrderBy(p => p.Key.X).ThenBy(p => p.Key.Z))
                foreach (var heat in plate.Read<DynamicBuffer<Heat>>()) all.Add(heat.T);
            return all.ToArray();
        }
    }

    /// <summary>The blur of <see cref="Blur{TTile}"/>, stepped into the tile's second buffer.</summary>
    private static void PingBlur<TTile>(ref Tile<Heat, TTile> heat) where TTile : struct, ITileShape
    {
        heat.ExchangeHalo();
        foreach (var row in heat.Rows(TileStencil.Layer))
        {
            var c = heat.At(row);
            var into = MemoryMarshal.Cast<Heat, float>(heat.Write(row));
            for (int x = 0; x < 16; x += 4)
            {
                var sum = c.Load128<float>(x - 1, 0, -1) + c.Load128<float>(x, 0, -1) + c.Load128<float>(x + 1, 0, -1)
                    + c.Load128<float>(x - 1) + c.Load128<float>(x) + c.Load128<float>(x + 1)
                    + c.Load128<float>(x - 1, 0, 1) + c.Load128<float>(x, 0, 1) + c.Load128<float>(x + 1, 0, 1);
                (sum * (1f / 9)).CopyTo(into.Slice(x));
            }
        }
    }

    [EcsSystem]
    public ref struct PingBlurFillSystem
    {
        [ForEach(Parallel = true, ChunkSize = 4)]
        static void Step(ref Tile<Heat, BlurFill> heat) => PingBlur(ref heat);
    }

    [EcsSystem]
    public ref struct PingBlurClampSystem
    {
        [ForEach(Parallel = true, ChunkSize = 4)]
        static void Step(ref Tile<Heat, BlurClamp> heat) => PingBlur(ref heat);
    }

    [EcsSystem]
    public ref struct PingBlurMirrorSystem
    {
        [ForEach(Parallel = true, ChunkSize = 4)]
        static void Step(ref Tile<Heat, BlurMirror> heat) => PingBlur(ref heat);
    }

    [EcsSystem]
    public ref struct PingBlurTorusSystem
    {
        [ForEach(Parallel = true, ChunkSize = 4)]
        static void Step(ref Tile<Heat, BlurTorus> heat) => PingBlur(ref heat);
    }

    [TestCase(TileBoundary.Border, 1)]
    [TestCase(TileBoundary.Border, 4)]
    [TestCase(TileBoundary.Clamp, 1)]
    [TestCase(TileBoundary.Clamp, 4)]
    [TestCase(TileBoundary.Mirror, 1)]
    [TestCase(TileBoundary.Mirror, 4)]
    [TestCase(null, 1)]
    [TestCase(null, 4)]
    public void APingPongBlurIsTheBlurOfTheWholeGrid(TileBoundary? boundary, int threads)
    {
        switch (boundary)
        {
            case TileBoundary.Border: BlurGrid<BlurFill, PingBlurFillSystem>(threads, p => new BlurFill(p), boundary, false, new TilePos(1, 0, 2), new TilePos(3, 0, 0)); break;
            case TileBoundary.Clamp: BlurGrid<BlurClamp, PingBlurClampSystem>(threads, p => new BlurClamp(p), boundary, false); break;
            case TileBoundary.Mirror: BlurGrid<BlurMirror, PingBlurMirrorSystem>(threads, p => new BlurMirror(p), boundary, false); break;
            default: BlurGrid<BlurTorus, PingBlurTorusSystem>(threads, p => new BlurTorus(p), null, false); break;
        }
    }

    public record struct PingLifePlate(TilePos Pos) : IComponent, ITile<TilePos>
    {
        readonly TilePos IIndexed<TilePos>.Key => Pos;
        static int ITileShape.Width => 16;
        static int ITileShape.Depth => 16;
        static TileNeighborhood ITileShape.Neighborhood => TileNeighborhood.All;
        static TilePos ITile<TilePos>.Beside(TilePos p, TileFaces face) => Wrap(p, face);
    }

    /// <summary>Life of <see cref="LifeSystem"/>, stepped into the tile's second buffer: the rows of dead cells beside dead cells are skipped, and must read as zeros.</summary>
    [EcsSystem]
    public ref struct PingLifeSystem
    {
        [ForEach(Parallel = true, ChunkSize = 2)]
        static void Step(ref Tile<Life, PingLifePlate> life)
        {
            life.ExchangeHalo();
            foreach (var row in life.Rows(TileStencil.Layer))
            {
                var c = life.At(row);
                var alive = c.Load128<byte>();
                var count = c.Load128<byte>(-1, 0, -1) + c.Load128<byte>(0, 0, -1) + c.Load128<byte>(1, 0, -1)
                    + c.Load128<byte>(-1) + c.Load128<byte>(1)
                    + c.Load128<byte>(-1, 0, 1) + c.Load128<byte>(0, 0, 1) + c.Load128<byte>(1, 0, 1);
                var born = Vector128.Equals(count, Vector128.Create((byte)3));
                var stays = Vector128.Equals(count, Vector128.Create((byte)2)) & alive;
                ((born | stays) & Vector128<byte>.One).CopyTo(MemoryMarshal.Cast<Life, byte>(life.Write(row)));
            }
        }
    }

    private static (World World, Dictionary<TilePos, Entity> Plates) PingLifeWorld(int threads)
    {
        var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        var random = new Random(11);
        var plates = new Dictionary<TilePos, Entity>();
        foreach (var place in Box(Torus, 1, Torus))
        {
            var plate = world.CreateEntity(new PingLifePlate(place));
            var cells = plate.GetOrAdd<DynamicBuffer<Life>>().SetCount(256);
            if (place.X != 2)
                for (int i = 0; i < 256; i++) cells[i] = new Life((byte)(random.Next(3) == 0 ? 1 : 0));
            plates.Add(place, plate);
        }

        world.AddSystem<PingLifeSystem>();
        return (world, plates);
    }

    [TestCase(1)]
    [TestCase(4)]
    public void PingPongLifeIsTheGameOfTheWholeTorus(int threads)
    {
        const int cells = Torus * 16;
        var (world, plates) = PingLifeWorld(threads);
        using var _ = world;
        var grid = Whole(plates, 16, 16, (Entity e, int i) => e.Read<DynamicBuffer<Life>>()[i].Alive, Torus);
        for (int step = 0; step < 80; step++)
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

    [Test]
    public void ACellWrittenByAnotherSystemWakesItsTileAndTheTilesAround()
    {
        // Life settles into still lifes and blinkers; a glider written later must still move across sleeping tiles.
        var (world, plates) = PingLifeWorld(1);
        using var _ = world;
        foreach (var plate in plates.Values) plate.GetOrAdd<DynamicBuffer<Life>>().AsSpan().Clear();
        world.Update();
        world.Update(); // every tile asleep: nothing lives
        var glider = new[] { (1, 0), (2, 1), (0, 2), (1, 2), (2, 2) };
        var start = plates[new TilePos(0, 0, 0)].GetOrAdd<DynamicBuffer<Life>>().AsSpan();
        foreach (var (x, z) in glider) start[(z + 6) * 16 + x + 6] = new Life(1);
        const int cells = Torus * 16;
        var grid = Whole(plates, 16, 16, (Entity e, int i) => e.Read<DynamicBuffer<Life>>()[i].Alive, Torus);
        for (int step = 0; step < 4 * 40; step++) // a glider crosses a plate in 64 generations
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
        Assert.That(plates.Values.Sum(p => p.Read<DynamicBuffer<Life>>().AsReadOnlySpan().ToArray().Count(c => c.Alive != 0)), Is.EqualTo(5), "the glider, still whole");
        Assert.That(plates[new TilePos(0, 0, 0)].Read<DynamicBuffer<Life>>().AsReadOnlySpan().ToArray().Count(c => c.Alive != 0), Is.LessThan(5), "it moved on");
    }

    // A sleeping pass walks only the tiles that woke: a blinker in one plate of a quiet torus steps that plate alone,
    // through a tile leaving the grid and another joining it.

    public record struct QuietPlate(TilePos Pos) : IComponent, ITile<TilePos>
    {
        readonly TilePos IIndexed<TilePos>.Key => Pos;
        static int ITileShape.Width => 16;
        static int ITileShape.Depth => 16;
        static TileNeighborhood ITileShape.Neighborhood => TileNeighborhood.All;
        static TilePos ITile<TilePos>.Beside(TilePos p, TileFaces face) => Wrap(p, face);
    }

    /// <summary>The steps <see cref="QuietLifeSystem"/> made: what the test reads, not state of the system.</summary>
    public static class QuietSteps
    {
        public static int Count;
    }

    [EcsSystem]
    public ref struct QuietLifeSystem
    {
        [ForEach]
        static void Step(ref Tile<Life, QuietPlate> life)
        {
            QuietSteps.Count++;
            life.ExchangeHalo();
            foreach (var row in life.Rows(TileStencil.Layer))
            {
                var c = life.At(row);
                var count = c.Load128<byte>(-1, 0, -1) + c.Load128<byte>(0, 0, -1) + c.Load128<byte>(1, 0, -1)
                    + c.Load128<byte>(-1) + c.Load128<byte>(1)
                    + c.Load128<byte>(-1, 0, 1) + c.Load128<byte>(0, 0, 1) + c.Load128<byte>(1, 0, 1);
                var born = Vector128.Equals(count, Vector128.Create((byte)3));
                var stays = Vector128.Equals(count, Vector128.Create((byte)2)) & c.Load128<byte>();
                ((born | stays) & Vector128<byte>.One).CopyTo(MemoryMarshal.Cast<Life, byte>(life.Write(row)));
            }
        }
    }

    [Test]
    public void ASleepingPassStepsOnlyTheTilesThatWoke()
    {
        using var world = new World();
        var plates = new Dictionary<TilePos, Entity>();
        foreach (var place in Box(Torus, 1, Torus))
        {
            var plate = world.CreateEntity(new QuietPlate(place));
            plate.GetOrAdd<DynamicBuffer<Life>>().SetCount(256);
            plates.Add(place, plate);
        }
        var blinking = plates[new TilePos(1, 0, 1)];
        for (int x = 7; x <= 9; x++) blinking.GetOrAdd<DynamicBuffer<Life>>().AsSpan()[8 * 16 + x] = new Life(1);
        world.AddSystem<QuietLifeSystem>();
        for (int i = 0; i < 4; i++) world.Update(); // every plate steps once, then only the blinker's is awake

        void Steady(string when)
        {
            for (int i = 0; i < 6; i++)
            {
                int before = QuietSteps.Count;
                world.Update();
                Assert.That(QuietSteps.Count - before, Is.EqualTo(1), $"{when}, update {i}: one plate changes, one steps");
                var cells = blinking.Read<DynamicBuffer<Life>>().AsReadOnlySpan();
                bool across = cells[8 * 16 + 7].Alive == 1; // the blinker turns a quarter each step
                for (int z = 0; z < 16; z++)
                for (int x = 0; x < 16; x++)
                {
                    bool alive = across ? z == 8 && x is >= 7 and <= 9 : x == 8 && z is >= 7 and <= 9;
                    Assert.That(cells[z * 16 + x].Alive, Is.EqualTo(alive ? 1 : 0), $"{when}: cell ({x}, {z})");
                }
            }
            foreach (var (place, plate) in plates)
                if (plate != blinking && place != new TilePos(3, 0, 3))
                    Assert.That(plate.Read<DynamicBuffer<Life>>().AsReadOnlySpan().ToArray().Any(c => c.Alive != 0), Is.False, $"{when}: the plate at {place} stays empty");
        }

        Steady("settled");
        var gone = plates[new TilePos(3, 0, 3)];
        plates.Remove(new TilePos(3, 0, 3));
        gone.Destroy();
        world.Update(); // the plates around it wake, step once and sleep
        world.Update();
        Steady("a plate left");
        var block = world.CreateEntity(new QuietPlate(new TilePos(3, 0, 3)));
        var still = block.GetOrAdd<DynamicBuffer<Life>>().SetCount(256);
        foreach (int at in new[] { 7 * 16 + 7, 7 * 16 + 8, 8 * 16 + 7, 8 * 16 + 8 }) still[at] = new Life(1); // a still life
        plates.Add(new TilePos(3, 0, 3), block);
        world.Update(); // it joins and steps with the plates around it, unchanged
        world.Update();
        Steady("a plate joined");
        Assert.That(block.Read<DynamicBuffer<Life>>().AsReadOnlySpan().ToArray().Count(c => c.Alive != 0), Is.EqualTo(4), "the block stays");
    }

    // A field that moves everywhere, then settles: the pass steps every tile unseen while nearly all change, and the
    // tiles sleep again once it looks and finds them settled.

    public record struct Flood(int Level);

    public record struct FloodPlate(TilePos Pos) : IComponent, ITile<TilePos>
    {
        readonly TilePos IIndexed<TilePos>.Key => Pos;
        static int ITileShape.Width => 16;
        static int ITileShape.Depth => 16;
        static TilePos ITile<TilePos>.Beside(TilePos p, TileFaces face) => Wrap(p, face);
    }

    [EcsSystem]
    public ref struct FloodSystem
    {
        [ForEach(Parallel = true, ChunkSize = 2)]
        static void Step(ref Tile<Flood, FloodPlate> flood)
        {
            flood.ExchangeHalo();
            foreach (var row in flood.Rows(TileStencil.Layer))
            {
                var c = flood.At(row);
                var into = flood.Write(row);
                for (int x = 0; x < 16; x++)
                    into[x] = new Flood(Math.Max(Math.Max(Math.Max(c[x, 0, 0].Level, c[x - 1, 0, 0].Level), Math.Max(c[x + 1, 0, 0].Level, c[x, 0, -1].Level)), c[x, 0, 1].Level));
            }
        }
    }

    [TestCase(1)]
    [TestCase(4)]
    public void AFieldMovingEverywhereStepsAwakeThenSleepsWhenSettled(int threads)
    {
        const int cells = Torus * 16;
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        var random = new Random(5);
        var plates = new Dictionary<TilePos, Entity>();
        foreach (var place in Box(Torus, 1, Torus))
        {
            var plate = world.CreateEntity(new FloodPlate(place));
            var levels = plate.GetOrAdd<DynamicBuffer<Flood>>().SetCount(256);
            for (int i = 0; i < 256; i++) levels[i] = new Flood(random.Next(1_000_000));
            plates.Add(place, plate);
        }
        world.AddSystem<FloodSystem>();
        var grid = Whole(plates, 16, 16, (Entity e, int i) => e.Read<DynamicBuffer<Flood>>()[i].Level, Torus);
        int stamped = 0;
        for (int step = 0; step < 120; step++)
        {
            ulong before = world.ChangeTick;
            world.Update();
            stamped = world.Count<DynamicBuffer<Flood>>(QueryFilter.Create().Changed<DynamicBuffer<Flood>>(before + 1));
            var next = new int[cells, cells];
            for (int z = 0; z < cells; z++)
            for (int x = 0; x < cells; x++)
                next[x, z] = Math.Max(Math.Max(Math.Max(grid[x, z], grid[(x + cells - 1) % cells, z]), Math.Max(grid[(x + 1) % cells, z], grid[x, (z + cells - 1) % cells])), grid[x, (z + 1) % cells]);
            grid = next;
            Same(plates, 16, 16, grid, (Entity e, int i) => e.Read<DynamicBuffer<Flood>>()[i].Level, $"step {step}");
            if (step == 0) Assert.That(stamped, Is.EqualTo(Torus * Torus), "every tile moves");
        }
        Assert.That(stamped, Is.Zero, "settled: no tile steps, none is stamped");
    }

    // A 3D heat: cubes of 8 x 8 x 4 cells, neighbors on all six faces, a seven-point stencil.

    public record struct HeatCube(TilePos Pos) : IComponent, ITile<TilePos>
    {
        readonly TilePos IIndexed<TilePos>.Key => Pos;
        static int ITileShape.Width => 8;
        static int ITileShape.Depth => 8;
        static int ITileShape.Height => 4;
        static TileFaces ITileShape.Faces => TileFaces.All;
        static TilePos ITile<TilePos>.Beside(TilePos p, TileFaces face) => Step(p, face);
    }

    [EcsSystem]
    public ref struct PingCubeHeatSystem
    {
        public const float Rate = 0.1f;

        [ForEach(Parallel = true, ChunkSize = 2)]
        static void Diffuse(ref Tile<Heat, HeatCube> heat)
        {
            heat.ExchangeHalo();
            foreach (var row in heat.Rows(TileStencil.All))
            {
                var c = heat.At(row);
                var into = MemoryMarshal.Cast<Heat, float>(heat.Write(row));
                for (int x = 0; x < 8; x += 4)
                {
                    var here = c.Load128<float>(x);
                    var around = c.Load128<float>(x - 1) + c.Load128<float>(x + 1) + c.Load128<float>(x, 0, -1) + c.Load128<float>(x, 0, 1)
                        + c.Load128<float>(x, -1, 0) + c.Load128<float>(x, 1, 0);
                    (here + (around - here * 6f) * Rate).CopyTo(into.Slice(x));
                }
            }
        }
    }

    [TestCase(1)]
    [TestCase(4)]
    public void PingPongHeatInCubesIsTheHeatOfTheWholeVolume(int threads)
    {
        const int n = 3, w = 8, d = 8, h = 4;
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        var cubes = new Dictionary<TilePos, Entity>();
        var random = new Random(5);
        foreach (var place in Box(n, n, n, new TilePos(1, 1, 1), new TilePos(2, 0, 1)))
        {
            var cube = world.CreateEntity(new HeatCube(place));
            var heat = cube.GetOrAdd<DynamicBuffer<Heat>>().SetCount(w * d * h);
            for (int i = 0; i < 3; i++) heat[random.Next(heat.Length)] = new Heat(random.Next(100, 1000));
            cubes.Add(place, cube);
        }
        world.AddSystem<PingCubeHeatSystem>();

        var grid = new float[n * w, n * h, n * d];
        Each((x, y, z, cube, i) => grid[x, y, z] = cube.Read<DynamicBuffer<Heat>>()[i].T);
        for (int step = 0; step < 30; step++)
        {
            world.Update();
            var next = new float[n * w, n * h, n * d];
            Each((x, y, z, _, _) =>
            {
                float here = grid[x, y, z];
                float around = At(x - 1, y, z) + At(x + 1, y, z) + At(x, y, z - 1) + At(x, y, z + 1) + At(x, y - 1, z) + At(x, y + 1, z);
                next[x, y, z] = here + (around - here * 6f) * PingCubeHeatSystem.Rate;
            });
            grid = next;
            Each((x, y, z, cube, i) =>
            {
                if (cube.Read<DynamicBuffer<Heat>>()[i].T != grid[x, y, z])
                    Assert.Fail($"step {step}, cell ({x}, {y}, {z}): {cube.Read<DynamicBuffer<Heat>>()[i].T}, expected {grid[x, y, z]}");
            });
        }

        float At(int x, int y, int z) => x < 0 || y < 0 || z < 0 || x >= n * w || y >= n * h || z >= n * d
            || !cubes.ContainsKey(new TilePos(x / w, y / h, z / d)) ? 0 : grid[x, y, z];

        void Each(Action<int, int, int, Entity, int> cell)
        {
            foreach (var (place, cube) in cubes)
                for (int y = 0; y < h; y++)
                for (int z = 0; z < d; z++)
                for (int x = 0; x < w; x++)
                    cell(place.X * w + x, place.Y * h + y, place.Z * d + z, cube, (y * d + z) * w + x);
        }
    }

    // A 2D tile over a 3D grid: the ground's heat (Columns<Chunk>) beside the chunk's rocks (a 3D tile), one key.

    public record struct RockChunk(TilePos Pos) : IComponent, ITile<TilePos>
    {
        readonly TilePos IIndexed<TilePos>.Key => Pos;
        static int ITileShape.Width => 8;
        static int ITileShape.Depth => 8;
        static int ITileShape.Height => 4;
        static TilePos ITile<TilePos>.Beside(TilePos p, TileFaces face) => Step(p, face);
    }

    public record struct Rock(byte Solid);

    /// <summary>Heat spreads over the ground (a 2D tile of the chunk) except where the chunk's top layer is rock (it stays cold).</summary>
    [EcsSystem]
    public ref struct GroundHeatSystem
    {
        [ForEach(Parallel = true, ChunkSize = 2)]
        static void Diffuse(ref Tile<Heat, Columns<RockChunk>> heat, Tile<Rock, RockChunk> rocks)
        {
            heat.ExchangeHalo();
            rocks.ExchangeHalo(3, 3, mask: false);
            foreach (var row in heat.Rows(TileStencil.Layer))
            {
                var c = heat.At(row);
                var into = heat.Write(row);
                for (int x = 0; x < 8; x++)
                {
                    float around = c[x - 1, 0, 0].T + c[x + 1, 0, 0].T + c[x, 0, -1].T + c[x, 0, 1].T;
                    into[x] = new Heat(rocks[x, 3, row.Z].Solid != 0 ? 0 : c[x, 0, 0].T + (around - 4 * c[x, 0, 0].T) * 0.2f);
                }
            }
        }
    }

    [TestCase(1)]
    [TestCase(4)]
    public void AGroundTileOverAChunkGridReadsTheChunksRocks(int threads)
    {
        const int n = 3, cells = n * 8;
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        var chunks = new Dictionary<TilePos, Entity>();
        foreach (var place in Box(n, 1, n, new TilePos(2, 0, 2)))
        {
            var chunk = world.CreateEntity(new RockChunk(place));
            var heat = chunk.GetOrAdd<DynamicBuffer<Heat>>().SetCount(64);
            heat[(place.X * 5 + place.Z * 3) % 64] = new Heat(500);
            var rocks = chunk.GetOrAdd<DynamicBuffer<Rock>>().SetCount(256);
            for (int i = 0; i < 8; i++) rocks[3 * 64 + i * 9 % 64] = new Rock(1); // the top layer, some columns
            chunks.Add(place, chunk);
        }
        world.AddSystem<GroundHeatSystem>();

        var grid = Whole(chunks, 8, 8, (Entity e, int i) => e.Read<DynamicBuffer<Heat>>()[i].T, n);
        for (int step = 0; step < 30; step++)
        {
            if (step == 15) // a rock placed later wakes its chunk
                chunks[new TilePos(1, 0, 1)].GetOrAdd<DynamicBuffer<Rock>>().AsSpan()[3 * 64 + 4 * 8 + 4] = new Rock(1);
            world.Update();
            var next = new float[cells, cells];
            for (int z = 0; z < cells; z++)
            for (int x = 0; x < cells; x++)
            {
                if (!chunks.TryGetValue(new TilePos(x / 8, 0, z / 8), out var chunk)) continue;
                float around = At(x - 1, z) + At(x + 1, z) + At(x, z - 1) + At(x, z + 1);
                bool solid = chunk.Read<DynamicBuffer<Rock>>()[3 * 64 + z % 8 * 8 + x % 8].Solid != 0;
                next[x, z] = solid ? 0 : grid[x, z] + (around - 4 * grid[x, z]) * 0.2f;
            }

            grid = next;
            Same(chunks, 8, 8, grid, (Entity e, int i) => e.Read<DynamicBuffer<Heat>>()[i].T, $"step {step}");
        }

        float At(int x, int z) => x < 0 || z < 0 || x >= cells || z >= cells || !chunks.ContainsKey(new TilePos(x / 8, 0, z / 8)) ? 0 : grid[x, z];
    }

    // ---------------- Gauss-Seidel ----------------

    public record struct DistPlate(TilePos Pos) : IComponent, ITile<TilePos>
    {
        readonly TilePos IIndexed<TilePos>.Key => Pos;
        static int ITileShape.Width => 16;
        static int ITileShape.Depth => 16;
        static TilePos ITile<TilePos>.Beside(TilePos p, TileFaces face) => Step(p, face);
    }

    /// <summary>A distance: 0 unreached, 1 a source, n the steps to the nearest source plus one.</summary>
    public record struct Dist(int V);

    /// <summary>The nearer of a distance and a neighbor's one step further (0: none).</summary>
    private static int Nearer(int here, int neighbor) => neighbor == 0 ? here : here == 0 ? neighbor + 1 : Math.Min(here, neighbor + 1);

    /// <summary>A distance field relaxed in place: d = min(d, neighbor + 1), sweep after sweep until it settles.</summary>
    [EcsSystem]
    public ref struct SpreadSystem
    {
        [ForEach(Parallel = true, ChunkSize = 2), GaussSeidel]
        static void Spread(ref Tile<Dist, DistPlate> dist)
        {
            dist.ExchangeHalo();
            foreach (var row in dist.Rows(TileStencil.Layer))
            {
                var c = dist.At(row);
                var into = dist.Write(row);
                for (int x = 0; x < 16; x++)
                {
                    int here = c[x, 0, 0].V;
                    if (here == 1) continue;
                    into[x] = new Dist(Nearer(Nearer(Nearer(Nearer(here, c[x - 1, 0, 0].V), c[x + 1, 0, 0].V), c[x, 0, -1].V), c[x, 0, 1].V));
                }
            }
        }
    }

    /// <summary>The distances of a grid of cells from its sources (1), through the cells <paramref name="open"/> holds: a search by layers.</summary>
    private static int[] Distances(int count, Func<int, int> source, Func<int, IEnumerable<int>> around)
    {
        var dist = new int[count];
        var queue = new Queue<int>();
        for (int i = 0; i < count; i++)
            if (source(i) == 1) { dist[i] = 1; queue.Enqueue(i); }
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            foreach (int j in around(i))
                if (dist[j] == 0) { dist[j] = dist[i] + 1; queue.Enqueue(j); }
        }
        return dist;
    }

    private static (World World, Dictionary<TilePos, Entity> Plates) DistWorld(int threads, int size, TilePos[] holes, int seed)
    {
        var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        var random = new Random(seed);
        var plates = new Dictionary<TilePos, Entity>();
        foreach (var place in Box(size, 1, size, holes))
        {
            var plate = world.CreateEntity(new DistPlate(place));
            var cells = plate.GetOrAdd<DynamicBuffer<Dist>>().SetCount(256);
            if (random.Next(4) == 0) cells[random.Next(256)] = new Dist(1);
            plates.Add(place, plate);
        }
        world.AddSystem<SpreadSystem>();
        return (world, plates);
    }

    /// <summary>The settled field of a 2D grid of plates: what the pass must reach.</summary>
    private static int[,] SettledField(Dictionary<TilePos, Entity> plates, int size)
    {
        int cells = size * 16;
        var sources = Whole(plates, 16, 16, (Entity e, int i) => e.Read<DynamicBuffer<Dist>>()[i].V == 1 ? 1 : 0, size);
        bool Open(int x, int z) => x >= 0 && z >= 0 && x < cells && z < cells && plates.ContainsKey(new TilePos(x / 16, 0, z / 16));
        var flat = Distances(cells * cells, i => sources[i % cells, i / cells], i =>
        {
            int x = i % cells, z = i / cells;
            var next = new List<int>();
            foreach (var (dx, dz) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
                if (Open(x + dx, z + dz)) next.Add((z + dz) * cells + x + dx);
            return next;
        });
        var field = new int[cells, cells];
        for (int i = 0; i < flat.Length; i++) field[i % cells, i / cells] = Open(i % cells, i / cells) ? flat[i] : 0;
        return field;
    }

    [Test]
    public void GaussSeidelSettlesTheDistanceFieldTheSameOnAnyNumberOfThreads()
    {
        const int size = 6;
        var holes = new[] { new TilePos(2, 0, 2), new TilePos(3, 0, 2), new TilePos(0, 0, 4) };
        var (one, onePlates) = DistWorld(1, size, holes, 9);
        var (four, fourPlates) = DistWorld(4, size, holes, 9);
        using var a = one;
        using var b = four;
        var settled = SettledField(onePlates, size);
        int passes = 0;
        for (; passes < 40; passes++)
        {
            one.Update();
            four.Update();
            var field = Whole(onePlates, 16, 16, (Entity e, int i) => e.Read<DynamicBuffer<Dist>>()[i].V, size);
            Same(fourPlates, 16, 16, field, (Entity e, int i) => e.Read<DynamicBuffer<Dist>>()[i].V, $"pass {passes}, four threads against one");
            if (Equal(field, settled)) break;
        }
        Assert.That(passes, Is.LessThan(size * 2), "a pass carries a field across a tile of each color, not a cell");
        Same(onePlates, 16, 16, settled, (Entity e, int i) => e.Read<DynamicBuffer<Dist>>()[i].V, "settled");

        // A source written later, a tile destroyed, a tile created: each wakes the tiles it changes, the field settles again.
        onePlates[new TilePos(5, 0, 5)].GetOrAdd<DynamicBuffer<Dist>>().AsSpan()[255] = new Dist(1);
        Settle(one, onePlates, "a new source");
        var gone = onePlates[new TilePos(1, 0, 1)];
        onePlates.Remove(new TilePos(1, 0, 1));
        gone.Destroy();
        foreach (var plate in onePlates.Values) // distances only shrink: start the field again from its sources
        {
            var cells = plate.GetOrAdd<DynamicBuffer<Dist>>().AsSpan();
            for (int i = 0; i < cells.Length; i++) if (cells[i].V != 1) cells[i] = default;
        }
        Settle(one, onePlates, "a tile destroyed");
        var made = one.CreateEntity(new DistPlate(new TilePos(2, 0, 2)));
        made.GetOrAdd<DynamicBuffer<Dist>>().SetCount(256)[17] = new Dist(1);
        onePlates.Add(new TilePos(2, 0, 2), made);
        Settle(one, onePlates, "a tile created");

        static void Settle(World world, Dictionary<TilePos, Entity> plates, string when)
        {
            for (int i = 0; i < 40; i++) world.Update();
            Same(plates, 16, 16, SettledField(plates, size), (Entity e, int i) => e.Read<DynamicBuffer<Dist>>()[i].V, when);
        }

        static bool Equal(int[,] x, int[,] y) => x.Cast<int>().SequenceEqual(y.Cast<int>());
    }

    // A 3D distance field over cubes with all six faces: two colors.

    public record struct DistCube(TilePos Pos) : IComponent, ITile<TilePos>
    {
        readonly TilePos IIndexed<TilePos>.Key => Pos;
        static int ITileShape.Width => 8;
        static int ITileShape.Depth => 8;
        static int ITileShape.Height => 4;
        static TileFaces ITileShape.Faces => TileFaces.All;
        static TilePos ITile<TilePos>.Beside(TilePos p, TileFaces face) => Step(p, face);
    }

    [EcsSystem]
    public ref struct SpreadCubeSystem
    {
        [ForEach(Parallel = true, ChunkSize = 2), GaussSeidel(MaxSweeps = 6)]
        static void Spread(ref Tile<Dist, DistCube> dist)
        {
            dist.ExchangeHalo();
            foreach (var row in dist.Rows(TileStencil.All))
            {
                var c = dist.At(row);
                var into = dist.Write(row);
                for (int x = 0; x < 8; x++)
                {
                    int here = c[x, 0, 0].V;
                    if (here == 1) continue;
                    here = Nearer(Nearer(Nearer(here, c[x - 1, 0, 0].V), c[x + 1, 0, 0].V), c[x, 0, -1].V);
                    into[x] = new Dist(Nearer(Nearer(Nearer(here, c[x, 0, 1].V), c[x, -1, 0].V), c[x, 1, 0].V));
                }
            }
        }
    }

    [TestCase(1)]
    [TestCase(4)]
    public void GaussSeidelSettlesADistanceFieldInCubes(int threads)
    {
        const int n = 3, w = 8, d = 8, h = 4;
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        var cubes = new Dictionary<TilePos, Entity>();
        foreach (var place in Box(n, n, n, new TilePos(1, 1, 1)))
        {
            var cube = world.CreateEntity(new DistCube(place));
            var cells = cube.GetOrAdd<DynamicBuffer<Dist>>().SetCount(w * d * h);
            if (place == new TilePos(0, 0, 0)) cells[0] = new Dist(1);
            if (place == new TilePos(2, 2, 0)) cells[w * d * h - 1] = new Dist(1);
            cubes.Add(place, cube);
        }
        world.AddSystem<SpreadCubeSystem>();
        for (int i = 0; i < 60; i++) world.Update(); // MaxSweeps = 6: a tile settles over several passes

        int X = n * w, Y = n * h, Z = n * d;
        bool Open(int x, int y, int z) => x >= 0 && y >= 0 && z >= 0 && x < X && y < Y && z < Z && cubes.ContainsKey(new TilePos(x / w, y / h, z / d));
        int Index(int x, int y, int z) => (y * Z + z) * X + x;
        int Cell(int x, int y, int z) => cubes[new TilePos(x / w, y / h, z / d)].Read<DynamicBuffer<Dist>>()[(y % h * d + z % d) * w + x % w].V;
        var expected = Distances(X * Y * Z, i =>
        {
            int x = i % X, z = i / X % Z, y = i / X / Z;
            return Open(x, y, z) && Cell(x, y, z) == 1 ? 1 : 0;
        }, i =>
        {
            int x = i % X, z = i / X % Z, y = i / X / Z;
            var next = new List<int>();
            foreach (var (dx, dy, dz) in new[] { (-1, 0, 0), (1, 0, 0), (0, -1, 0), (0, 1, 0), (0, 0, -1), (0, 0, 1) })
                if (Open(x + dx, y + dy, z + dz)) next.Add(Index(x + dx, y + dy, z + dz));
            return next;
        });
        for (int y = 0; y < Y; y++)
        for (int z = 0; z < Z; z++)
        for (int x = 0; x < X; x++)
            if (Open(x, y, z) && Cell(x, y, z) != expected[Index(x, y, z)])
                Assert.Fail($"cell ({x}, {y}, {z}): {Cell(x, y, z)}, expected {expected[Index(x, y, z)]}");
    }

    // ---------------- misuse ----------------

    private const int OddTorus = 3;

    public record struct OddPlate(TilePos Pos) : IComponent, ITile<TilePos>
    {
        readonly TilePos IIndexed<TilePos>.Key => Pos;
        static int ITileShape.Width => 4;
        static int ITileShape.Depth => 4;
        static TilePos ITile<TilePos>.Beside(TilePos p, TileFaces face)
        {
            var next = Step(p, face);
            return new TilePos((next.X + OddTorus) % OddTorus, next.Y, (next.Z + OddTorus) % OddTorus);
        }
    }

    [EcsSystem]
    public ref struct SpreadOddSystem
    {
        [ForEach, GaussSeidel]
        static void Spread(ref Tile<Dist, OddPlate> dist)
        {
            dist.ExchangeHalo();
            foreach (var row in dist.Rows()) dist.Write(row);
        }
    }

    [Test]
    public void MisusedStepsThrow()
    {
        using var world = new World();
        foreach (var place in Box(OddTorus, 1, OddTorus))
            world.CreateEntity(new OddPlate(place)).GetOrAdd<DynamicBuffer<Dist>>().SetCount(16)[0] = new Dist(1);
        world.AddSystem<SpreadOddSystem>();
        var odd = Assert.Throws<InvalidOperationException>(() => world.Update());
        Assert.That(odd.Message, Does.Contain("neighbors of one color"));

        using var other = new World();
        var plates = Grid(other, Box(2, 1, 1), s => new PingPlate(s));
        foreach (var plate in plates) plate.GetOrAdd<DynamicBuffer<Heat>>().SetCount(256);
        Assert.Throws<InvalidOperationException>(() => Rows(plates[0], plates[1]), "a tile read before its halo is exchanged");
        var notStepping = Assert.Throws<InvalidOperationException>(() => Write(plates[0]));
        Assert.That(notStepping.Message, Does.Contain("by ref"));

        static void Rows(Entity e, Entity other)
        {
            var tile = TileOf<Heat, PingPlate>(e, out _);
            tile.ExchangeHalo();
            TileOf<Heat, PingPlate>(other, out _).ExchangeHalo(); // the texture holds another tile's now
            foreach (var _ in tile.Rows()) { }
        }

        static void Write(Entity e)
        {
            var tile = TileOf<Heat, PingPlate>(e, out _);
            tile.ExchangeHalo();
            tile.Write(new TileRow<PingPlate>(0, 0));
        }
    }

    [Test]
    public void AStepWritesItsCellsAndRunsAfterTheirReaders()
    {
        using var world = new World(new WorldOptions { ParallelSystems = true });
        world.AddSystem<ReadHeatSystem>();
        world.AddSystem<PingHeatSystem>(); // writes the heat: a stage of its own
        Assert.That(world.DescribeSchedule().Groups[0].Stages.Length, Is.EqualTo(2), world.DescribeSchedule().ToString());
    }

    [Test]
    public void StepsDoNotAllocate()
    {
        var (heat, _) = PingHeatWorld(4, 8, [], []);
        var (life, _) = PingLifeWorld(4);
        var (dist, plates) = DistWorld(4, 6, [], 3);
        using var a = heat;
        using var b = life;
        using var c = dist;
        dist.AddSystem<PulseSystem>();
        plates[new TilePos(3, 0, 3)].Set(new Pulse());
        var names = new[] { "heat", "life", "dist" };
        var worlds = new[] { heat, life, dist };
        for (int w = 0; w < worlds.Length; w++)
        {
            var world = worlds[w];
            for (int i = 0; i < 200; i++) world.Update(); // every thread gets its frame, every tile its second buffer
            Assert.That(Allocations.Of(() => { for (int i = 0; i < 100; i++) world.Update(); }), Is.Zero, names[w]);
        }
    }

    /// <summary>A tile's cells and grid first used after 256 component types: their bits live past the inline 256.</summary>
    public record struct FarHeat(float Value) : IComponent;

    public record struct FarPlate(TilePos Pos) : IComponent, ITile<TilePos>
    {
        readonly TilePos IIndexed<TilePos>.Key => Pos;
        static int ITileShape.Width => 16;
        static int ITileShape.Depth => 16;
        static TilePos ITile<TilePos>.Beside(TilePos p, TileFaces face) => Step(p, face);
    }

    [EcsSystem]
    public ref struct FarHeatSystem
    {
        [ForEach]
        static void Diffuse(ref Tile<FarHeat, FarPlate> heat)
        {
            heat.ExchangeHalo();
            foreach (var row in heat.Rows(TileStencil.Layer))
            {
                var cells = heat.At(row);
                var into = heat.Write(row);
                for (int x = 0; x < 16; x++)
                    into[x] = new FarHeat((cells[x - 1, 0, 0].Value + cells[x + 1, 0, 0].Value + cells[x, 0, -1].Value + cells[x, 0, 1].Value) * 0.25f);
            }
        }
    }

    [Test]
    public void StepsOfComponentsPastBit255DoNotAllocate()
    {
        // The pass builds its Changed filters once: a filter built per pass interns its bits past 255, which allocates.
        using (var wide = new World()) Wide.AddAll(wide.CreateEntity());
        using var world = new World();
        foreach (var place in Box(4, 1, 4, []))
        {
            var heat = world.CreateEntity(new FarPlate(place)).GetOrAdd<DynamicBuffer<FarHeat>>().SetCount(256);
            heat[place.X * 16 + place.Z] = new FarHeat(1000f);
        }
        world.AddSystem<FarHeatSystem>();
        Assert.That(ComponentType<FarHeat>.Bit, Is.GreaterThan(255));
        for (int i = 0; i < 200; i++) world.Update();
        Assert.That(Allocations.Of(() => { for (int i = 0; i < 100; i++) world.Update(); }), Is.Zero);
    }

    /// <summary>A source that moves along its tile every update: the field around it keeps changing.</summary>
    public record struct Pulse(int At) : IComponent;

    [EcsSystem]
    public ref struct PulseSystem
    {
        [ForEach]
        static void Move(ref Pulse pulse, Mut<DynamicBuffer<Dist>> dist)
        {
            pulse.At = (pulse.At + 37) % 256;
            dist.Write().AsSpan()[pulse.At] = new Dist(1);
        }
    }
}
