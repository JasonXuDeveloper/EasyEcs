using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>
/// The halos a pass taking a tile by value keeps (TileHalos): the Game of Life on a torus of plates where few plates
/// change, so most plates take their kept halo back, against the whole torus computed at once, on one thread and on
/// four; cells written from outside the pass, a plate's cells removed and given back, a plate destroyed and another
/// created in its place, all seen by the plates around them; the kept halos actually read; 0 B once warm.
/// </summary>
public partial class TileTests
{
    public record struct KeptPlate(TilePos Pos) : IComponent, ITile<TilePos>
    {
        readonly TilePos IIndexed<TilePos>.Key => Pos;
        static int ITileShape.Width => 16;
        static int ITileShape.Depth => 16;
        static TileNeighborhood ITileShape.Neighborhood => TileNeighborhood.All;
        static TilePos ITile<TilePos>.Beside(TilePos p, TileFaces face) => Wrap(p, face);
    }

    /// <summary>Life by value, as <see cref="LifeSystem"/>, over plates that mostly do not change.</summary>
    [EcsSystem]
    public ref struct KeptLifeSystem
    {
        [ForEach(Parallel = true, ChunkSize = 2)]
        static void Step(Tile<Life, KeptPlate> life, Mut<DynamicBuffer<NextLife>> next)
        {
            life.ExchangeHalo();
            var into = MemoryMarshal.Cast<NextLife, byte>(next.Write().AsSpan());
            into.Clear(); // the rows the walk skips are empty
            for (int z = 0; z < 16; z++)
            for (int x = 0; x < 16; x++)
            {
                int count = 0;
                for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                    if ((dx | dz) != 0) count += life[x + dx, 0, z + dz].Alive;
                into[z * 16 + x] = (byte)(count == 3 || count == 2 && life[x, 0, z].Alive == 1 ? 1 : 0);
            }
        }
    }

    /// <summary>The step copied into the cells only where it changed them: a still plate's cells are never written.</summary>
    [EcsSystem]
    public ref struct CommitChangedLifeSystem
    {
        [ForEach]
        static void Commit(in DynamicBuffer<NextLife> next, Mut<DynamicBuffer<Life>> life)
        {
            var cells = MemoryMarshal.Cast<Life, NextLife>(life.Value.AsReadOnlySpan());
            if (!cells.SequenceEqual(next.AsReadOnlySpan())) next.AsReadOnlySpan().CopyTo(MemoryMarshal.Cast<Life, NextLife>(life.Write().AsSpan()));
        }
    }

    /// <summary>A torus of quiet plates: blinkers in two of them, the others empty.</summary>
    private static (World World, Dictionary<TilePos, Entity> Plates) KeptWorld(int threads)
    {
        var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        var plates = new Dictionary<TilePos, Entity>();
        foreach (var place in Box(Torus, 1, Torus)) plates.Add(place, KeptPlateAt(world, place, place == new TilePos(1, 0, 1) || place == new TilePos(3, 0, 2)));
        world.AddSystem<KeptLifeSystem>();
        world.AddSystem<CommitChangedLifeSystem>();
        return (world, plates);
    }

    /// <summary>A plate, a blinker across its west edge when <paramref name="blinker"/> (its halo reads the plate west).</summary>
    private static Entity KeptPlateAt(World world, TilePos place, bool blinker)
    {
        var plate = world.CreateEntity(new KeptPlate(place));
        var cells = plate.GetOrAdd<DynamicBuffer<Life>>().SetCount(256);
        plate.GetOrAdd<DynamicBuffer<NextLife>>().SetCount(256);
        if (blinker)
            for (int z = 7; z <= 9; z++) cells[z * 16] = new Life(1);
        return plate;
    }

    /// <summary>The whole torus a step later: the cells of a plate that is gone or has no cells are dead and stay dead.</summary>
    private static byte[,] LifeStep(byte[,] grid, ISet<TilePos> dead)
    {
        const int cells = Torus * 16;
        var next = new byte[cells, cells];
        for (int z = 0; z < cells; z++)
        for (int x = 0; x < cells; x++)
        {
            if (dead.Contains(new TilePos(x / 16, 0, z / 16))) continue;
            int count = 0;
            for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
                if ((dx | dz) != 0) count += grid[(x + dx + cells) % cells, (z + dz + cells) % cells];
            next[x, z] = (byte)(count == 3 || count == 2 && grid[x, z] == 1 ? 1 : 0);
        }
        return next;
    }

    private static TileHaloCache<Life, KeptPlate> KeptHalos(World world) =>
        world.TileState<TileHaloCache<Life, KeptPlate>>();

    [TestCase(1)]
    [TestCase(4)]
    public void KeptHalosGiveTheGameOfTheWholeTorusWhateverChangesTheGrid(int threads)
    {
        var (world, plates) = KeptWorld(threads);
        using var _ = world;
        var dead = new HashSet<TilePos>();
        var grid = Whole(plates, 16, 16, (Entity e, int i) => e.Read<DynamicBuffer<Life>>()[i].Alive, Torus);
        var quiet = new TilePos(0, 0, 3);
        int kept = 0;
        for (int step = 0; step < 40; step++)
        {
            switch (step)
            {
                case 6: // cells written by another system: a blinker on the plate's north edge, read by the plate north of it
                    var cells = plates[quiet].GetOrAdd<DynamicBuffer<Life>>().SetCount(256);
                    for (int x = 4; x <= 6; x++) cells[x] = new Life(1);
                    for (int x = 4; x <= 6; x++) grid[quiet.X * 16 + x, quiet.Z * 16] = 1;
                    break;
                case 14: // the plate loses its cells: the plates around read dead cells there
                    plates[quiet].Remove<DynamicBuffer<Life>>();
                    dead.Add(quiet);
                    for (int z = 0; z < 16; z++)
                    for (int x = 0; x < 16; x++) grid[quiet.X * 16 + x, quiet.Z * 16 + z] = 0;
                    break;
                case 20: // ...and gets cells back: a blinker on its east edge
                    var back = plates[quiet].GetOrAdd<DynamicBuffer<Life>>().SetCount(256);
                    for (int z = 3; z <= 5; z++) back[z * 16 + 15] = new Life(1);
                    for (int z = 3; z <= 5; z++) grid[quiet.X * 16 + 15, quiet.Z * 16 + z] = 1;
                    dead.Remove(quiet);
                    break;
                case 28: // a plate destroyed and another made in its place, with a blinker across its west edge
                    var place = new TilePos(2, 0, 0);
                    plates[place].Destroy();
                    plates[place] = KeptPlateAt(world, place, true);
                    for (int z = 0; z < 16; z++)
                    for (int x = 0; x < 16; x++) grid[place.X * 16 + x, place.Z * 16 + z] = (byte)(x == 0 && z is >= 7 and <= 9 ? 1 : 0);
                    break;
            }
            world.Update();
            grid = LifeStep(grid, dead);
            foreach (var (place, plate) in plates)
            {
                if (dead.Contains(place)) continue;
                var cells = plate.Read<DynamicBuffer<Life>>().AsReadOnlySpan();
                for (int z = 0; z < 16; z++)
                for (int x = 0; x < 16; x++)
                    if (cells[z * 16 + x].Alive != grid[place.X * 16 + x, place.Z * 16 + z])
                        Assert.Fail($"step {step}, plate {place}, cell ({x}, {z}): {cells[z * 16 + x].Alive}, expected {grid[place.X * 16 + x, place.Z * 16 + z]}");
            }
            kept = Math.Max(kept, KeptHalos(world).Kept);
        }

        // Most plates are still: their halos were kept, and read (a kept halo that was wrong would have failed above).
        Assert.That(kept, Is.GreaterThanOrEqualTo(Torus * Torus / 2));
    }

    [TestCase(1)]
    [TestCase(4)]
    public void KeptHalosAllocateNothingOnceWarm(int threads)
    {
        var (world, _) = KeptWorld(threads);
        using var __ = world;
        Allocations.WarmUp(world.Update, 200);
        Assert.That(Allocations.Of(() => { for (int i = 0; i < 100; i++) world.Update(); }), Is.Zero);
        Assert.That(KeptHalos(world).Kept, Is.GreaterThanOrEqualTo(Torus * Torus / 2));
    }
}
