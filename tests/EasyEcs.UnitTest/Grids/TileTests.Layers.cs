using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>
/// A ground cell reading a layer of the tile it is the ground of (<see cref="Layer{TCell, TTile, TLayer}"/>): heat spreading
/// over the ground of rock chunks except where the chunk's top layer is solid, bit for bit against the walk by hand
/// (<see cref="GroundHeatSystem"/>) and the method as written (SimdMode.Off), on one thread and four, through a rock
/// placed later (its chunk wakes), quiet chunks (rows the walk skips) and solid cells in the other layers (never read);
/// a layer of ints beside float cells as a vector loop; the exchange copying that layer alone; 0 B.
/// </summary>
public partial class TileTests
{
    /// <summary>The top layer of a <see cref="RockChunk"/> (4 layers).</summary>
    public struct TopLayer : ILayer
    {
        public static int Index => 3;
    }

    /// <summary><see cref="GroundHeatSystem"/> as a ground cell reading the top layer of its chunk.</summary>
    [EcsSystem]
    public ref struct CellGroundHeatSystem
    {
        [ForEach(Parallel = true, ChunkSize = 2)]
        static void Diffuse(Cell<Heat, Ground<RockChunk>> heat, Layer<Rock, RockChunk, TopLayer> rock, out Heat next) =>
            next = new(rock.Value.Solid != 0 ? 0 : heat.Value.T + (heat[-1, 0, 0].T + heat[1, 0, 0].T + heat[0, 0, -1].T + heat[0, 0, 1].T - 4 * heat.Value.T) * 0.2f);
    }

    /// <summary><see cref="CellGroundHeatSystem"/> as written, cell by cell.</summary>
    [EcsSystem]
    public ref struct CellGroundHeatByCellSystem
    {
        [ForEach(Parallel = true, ChunkSize = 2, SimdMode = SimdMode.Off)]
        static void Diffuse(Cell<Heat, Ground<RockChunk>> heat, Layer<Rock, RockChunk, TopLayer> rock, out Heat next) =>
            next = new(rock.Value.Solid != 0 ? 0 : heat.Value.T + (heat[-1, 0, 0].T + heat[1, 0, 0].T + heat[0, 0, -1].T + heat[0, 0, 1].T - 4 * heat.Value.T) * 0.2f);
    }

    /// <summary>A chunk's ore: 4-byte cells, lanes of the heat's size.</summary>
    public record struct Ore(int Solid);

    /// <summary>Heat spreading over the ground except under ore in the top layer, slowed by the ore west and south of it.</summary>
    [EcsSystem]
    public ref struct CellGroundOreSystem
    {
        [ForEach(Parallel = true, ChunkSize = 2)]
        static void Diffuse(Cell<Heat, Ground<RockChunk>> heat, Layer<Ore, RockChunk, TopLayer> ore, out Heat next) =>
            next = new(ore.Value.Solid != 0 ? 0 : (heat.Value.T + (heat[-1, 0, 0].T + heat[1, 0, 0].T + heat[0, 0, -1].T + heat[0, 0, 1].T - 4 * heat.Value.T) * 0.2f)
                * (1 - (ore[-1, 0, 0].Solid + ore[0, 0, 1].Solid) * 0.25f));
    }

    /// <summary><see cref="CellGroundOreSystem"/> as written, cell by cell.</summary>
    [EcsSystem]
    public ref struct CellGroundOreByCellSystem
    {
        [ForEach(Parallel = true, ChunkSize = 2, SimdMode = SimdMode.Off)]
        static void Diffuse(Cell<Heat, Ground<RockChunk>> heat, Layer<Ore, RockChunk, TopLayer> ore, out Heat next) =>
            next = new(ore.Value.Solid != 0 ? 0 : (heat.Value.T + (heat[-1, 0, 0].T + heat[1, 0, 0].T + heat[0, 0, -1].T + heat[0, 0, 1].T - 4 * heat.Value.T) * 0.2f)
                * (1 - (ore[-1, 0, 0].Solid + ore[0, 0, 1].Solid) * 0.25f));
    }

    /// <summary><see cref="CellGroundOreSystem"/> by hand: the ground stepped by ref, the chunk's ore read at layer 3.</summary>
    [EcsSystem]
    public ref struct HandGroundOreSystem
    {
        [ForEach(Parallel = true, ChunkSize = 2)]
        static void Diffuse(ref Tile<Heat, Ground<RockChunk>> heat, Tile<Ore, RockChunk> ores)
        {
            heat.ExchangeHalo();
            ores.ExchangeHalo(3, 3, mask: false);
            foreach (var row in heat.Rows(TileStencil.Layer))
            {
                var c = heat.At(row);
                var into = heat.Write(row);
                for (int x = 0; x < 8; x++)
                {
                    float around = c[x - 1, 0, 0].T + c[x + 1, 0, 0].T + c[x, 0, -1].T + c[x, 0, 1].T;
                    into[x] = new Heat(ores[x, 3, row.Z].Solid != 0 ? 0 : (c[x, 0, 0].T + (around - 4 * c[x, 0, 0].T) * 0.2f)
                        * (1 - (ores[x - 1, 3, row.Z].Solid + ores[x, 3, row.Z + 1].Solid) * 0.25f));
                }
            }
        }
    }

    /// <summary>
    /// A 3 x 3 square of rock chunks (with Rock or Ore cells) with a hole in its middle and a quiet column (rows the walk
    /// skips), heat on some ground cells, solid cells in the top layer and in layer 2 (which no method reads).
    /// </summary>
    private static Dictionary<TilePos, Entity> LayerChunks<TSolid>(World world, Func<int, TSolid> solid) where TSolid : unmanaged
    {
        var chunks = new Dictionary<TilePos, Entity>();
        var random = new Random(31);
        foreach (var place in Box(3, 1, 3, new TilePos(1, 0, 1)))
        {
            var chunk = world.CreateEntity(new RockChunk(place));
            var heat = chunk.GetOrAdd<DynamicBuffer<Heat>>().SetCount(64);
            if (place.X != 2)
                for (int i = 0; i < 12; i++) heat[random.Next(64)] = new Heat(random.Next(1, 1000));
            var cells = chunk.GetOrAdd<DynamicBuffer<TSolid>>().SetCount(256);
            for (int i = 0; i < 8; i++) cells[3 * 64 + random.Next(64)] = solid(1); // the top layer
            for (int i = 0; i < 20; i++) cells[2 * 64 + random.Next(64)] = solid(1); // under it: never read
            chunks.Add(place, chunk);
        }
        return chunks;
    }

    /// <summary>The three worlds stepped 30 times, bit for bit each step, a solid cell placed in chunk (0, 0, 1) at step 15.</summary>
    private static void SameLayerSteps<TCells, TByCell, THand, TSolid>(int threads, Func<int, TSolid> solid, string form)
        where TCells : struct, allows ref struct where TByCell : struct, allows ref struct where THand : struct, allows ref struct where TSolid : unmanaged
    {
        using var cells = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        using var byCell = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        using var hand = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        var (a, b, c) = (LayerChunks(cells, solid), LayerChunks(byCell, solid), LayerChunks(hand, solid));
        cells.AddSystem<TCells>();
        byCell.AddSystem<TByCell>();
        hand.AddSystem<THand>();
        Assert.That(cells.DescribeSchedule().ToString(), Does.Contain(form).And.Contain("steps the tile of 'heat'"));
        Assert.That(byCell.DescribeSchedule().ToString(), Does.Contain("cells of 'heat', ").And.Contain(": scalar (SimdMode.Off"));
        for (int step = 0; step < 30; step++)
        {
            if (step == 15) // a solid cell placed later wakes its chunk
                foreach (var chunks in new[] { a, b, c })
                    chunks[new TilePos(0, 0, 1)].GetOrAdd<DynamicBuffer<TSolid>>().AsSpan()[3 * 64 + 4 * 8 + 4] = solid(1);
            cells.Update();
            byCell.Update();
            hand.Update();
            foreach (var (place, chunk) in a)
            {
                var got = chunk.Read<DynamicBuffer<Heat>>().AsReadOnlySpan();
                Assert.That(got.SequenceEqual(b[place].Read<DynamicBuffer<Heat>>().AsReadOnlySpan()), Is.True, $"step {step}, chunk {place}: as written");
                Assert.That(got.SequenceEqual(c[place].Read<DynamicBuffer<Heat>>().AsReadOnlySpan()), Is.True, $"step {step}, chunk {place}: by hand");
            }
        }
    }

    // Byte rocks beside float heat: lanes four times as wide, so the rows run the method as written (Kernel.Pairs).
    [TestCase(1)]
    [TestCase(4)]
    public void AGroundCellReadingItsChunksTopLayerStepsAsTheWalkByHand(int threads) =>
        SameLayerSteps<CellGroundHeatSystem, CellGroundHeatByCellSystem, GroundHeatSystem, Rock>(threads, s => new Rock((byte)s), "cells of 'heat', 'rock': ");

    [TestCase(1)]
    [TestCase(4)]
    public void AGroundCellReadingALayerOfIntsIsAVectorLoop(int threads) =>
        SameLayerSteps<CellGroundOreSystem, CellGroundOreByCellSystem, HandGroundOreSystem, Ore>(threads, s => new Ore(s), "cells of 'heat', 'ore': vector (");

    [Test]
    public void GroundCellsReadingALayerAllocateNothing()
    {
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4 });
        LayerChunks(world, s => new Ore(s));
        world.AddSystem<CellGroundOreSystem>();
        Assert.That(Allocations.Steady(world.Update, 50, 10), Is.Zero);
    }

    /// <summary>What a tile's texture holds of layers 2 and 3 after an exchange: a cell of each, and one of layer 3's ring.</summary>
    public record struct LayerProbe(int Under, int At, int Ring) : IComponent;

    /// <summary>Layer 3 exchanged with the layers around it (2 and the one over the tile), as ExchangeHalo(3, 3) does.</summary>
    [EcsSystem]
    public ref struct ExchangeAroundLayerSystem
    {
        [ForEach]
        static void Fill(Tile<Ore, RockChunk> ores, ref LayerProbe probe)
        {
            ores.ExchangeHalo(3, 3, mask: false);
            probe = new LayerProbe(ores[0, 2, 0].Solid, ores[0, 3, 0].Solid, ores[-1, 3, 0].Solid);
        }
    }

    /// <summary>Layer 3 alone exchanged, as a Layer's pass does: layer 2 is left as the texture held it.</summary>
    [EcsSystem]
    public ref struct ExchangeTopLayerSystem
    {
        [ForEach]
        static void Fill(Tile<Ore, RockChunk> ores, ref LayerProbe probe)
        {
            ores.ExchangeLayer(TopLayer.Index);
            probe = new LayerProbe(ores[0, 2, 0].Solid, ores[0, 3, 0].Solid, ores[-1, 3, 0].Solid);
        }
    }

    // Two chunks side by side, every ore cell 1: layer 3 exchanged with the layers around it on one thread fills the
    // texture; then every cell 2 and layer 3 alone exchanged into the same place: its cells and its ring (the west
    // chunk's) are 2, layer 2 still the 1 copied before.
    [Test]
    public void ALayersExchangeCopiesThatLayerAlone()
    {
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 1 });
        var west = world.CreateEntity(new RockChunk(new TilePos(0, 0, 0)));
        var east = world.CreateEntity(new RockChunk(new TilePos(1, 0, 0)), new LayerProbe());
        west.GetOrAdd<DynamicBuffer<Ore>>().SetCount(256).Fill(new Ore(1));
        east.GetOrAdd<DynamicBuffer<Ore>>().SetCount(256).Fill(new Ore(1));
        var around = world.AddSystem<ExchangeAroundLayerSystem>();
        var top = world.AddSystem<ExchangeTopLayerSystem>();
        top.Enabled = false;
        world.Update();
        Assert.That(east.Read<LayerProbe>(), Is.EqualTo(new LayerProbe(1, 1, 1)));
        west.Get<DynamicBuffer<Ore>>().AsSpan().Fill(new Ore(2));
        east.Get<DynamicBuffer<Ore>>().AsSpan().Fill(new Ore(2));
        around.Enabled = false;
        top.Enabled = true;
        world.Update();
        Assert.That(east.Read<LayerProbe>(), Is.EqualTo(new LayerProbe(1, 2, 2)));
    }
}
