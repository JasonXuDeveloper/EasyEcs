using System;
using System.Linq;
using System.Numerics;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary><see cref="EntityRandom"/>: random numbers as a pure function of (seed, entity, tick, stream).</summary>
public class EntityRandomTests
{
    public record struct Roll(int Value, float Fraction) : IComponent;

    [EcsSystem]
    public ref struct ParallelRolls
    {
        [ForEach(Parallel = true, ChunkSize = 64)]
        static void Roll(ref Roll roll, Entity entity, in EntityRandom random) =>
            roll = new Roll(random.Range(entity.Id, 0, 1, 7), random.Float(entity.Id, 1));
    }

    [EcsSystem]
    public ref struct SerialRolls
    {
        [ForEach]
        static void Roll(ref Roll roll, Entity entity, in EntityRandom random) =>
            roll = new Roll(random.Range(entity.Id, 0, 1, 7), random.Float(entity.Id, 1));
    }

    private static Roll[] Rolls<TSystem>(int threads, ulong seed, ulong tick) where TSystem : struct, allows ref struct
    {
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        var entities = new Entity[20_000];
        world.CreateEntities(entities, new Roll());
        world.AddSingleton<EntityRandom>() = new EntityRandom(seed) { Tick = tick - 1 };  // the update adds one
        world.AddSystem<TSystem>();
        world.Update();
        return entities.Select(e => e.Roll).ToArray();
    }

    [Test]
    public void TheSameInputsGiveTheSameNumbersForAnyThreadCount()
    {
        var serial = Rolls<SerialRolls>(1, 42, 7);
        foreach (int threads in new[] { 1, 2, 4, 8 })
            for (int run = 0; run < 3; run++)
                Assert.That(Rolls<ParallelRolls>(threads, 42, 7), Is.EqualTo(serial), $"{threads} threads");
        Assert.That(Rolls<SerialRolls>(1, 43, 7), Is.Not.EqualTo(serial), "another seed");
        Assert.That(Rolls<SerialRolls>(1, 42, 8), Is.Not.EqualTo(serial), "another tick");
    }

    public struct Other : ISystemGroup;

    [EcsSystem, UpdateInGroup<Other>]
    public ref struct OtherRolls
    {
        [ForEach]
        static void Roll(ref Roll roll, Entity entity, in EntityRandom random) => roll = new Roll(random.Range(entity.Id, 0, 1, 1000), 0);
    }

    [Test]
    public void EveryUpdateDrawsNewNumbers()
    {
        using var world = new World();
        world.SetEntityRandom(Seed: 5);
        world.AddSystem<SerialRolls>();
        world.AddSystem<OtherRolls>();
        Assert.That(world.EntityRandom.Tick, Is.Zero, "no update yet");
        world.Update();
        Assert.That(world.EntityRandom.Tick, Is.EqualTo(1), "the update's systems draw for tick 1");
        world.Update<Other>();
        world.Update<Other>();
        Assert.That(world.EntityRandom.Tick, Is.EqualTo(1), "a group's update draws with the last Update()'s tick");
        world.Update();
        Assert.That(world.EntityRandom.Tick, Is.EqualTo(2));

        var entity = world.CreateEntity(new Roll());
        world.Update<Other>();
        Assert.That(entity.Roll.Value, Is.EqualTo(EntityRandom.Range(5, entity.Id, 2, 0, 1, 1000)));
        var seen = new System.Collections.Generic.HashSet<int>();
        for (int i = 0; i < 20; i++)
        {
            world.Update();
            world.Update<Other>();
            seen.Add(entity.Roll.Value);
        }

        Assert.That(seen.Count, Is.GreaterThan(10), "each Update() draws new numbers");
    }

    [Test]
    public void TheTickIsSavedAndRestoredWithTheSingleton()
    {
        using var world = new World();
        world.SetEntityRandom(Seed: 5);
        for (int i = 0; i < 3; i++) world.Update();
        var snapshot = world.Capture();
        world.Update();
        world.Restore(snapshot);
        Assert.That(world.EntityRandom, Is.EqualTo(new EntityRandom(5) { Tick = 3 }));

        var save = new System.Buffers.ArrayBufferWriter<byte>();
        world.Save(save);
        using var copy = new World();
        copy.Load(save.WrittenSpan);
        Assert.That(copy.EntityRandom.Tick, Is.EqualTo(3));
        copy.Update();
        Assert.That(copy.EntityRandom.Tick, Is.EqualTo(4), "the loaded world continues the sequence");
    }

    [Test]
    public void TheBitsAreAStableFunction()
    {
        // Pinned values: a change to the hash would change every saved replay, so it must be deliberate.
        Assert.That(EntityRandom.Bits(0, new EntityId(0, 1), 0, 0), Is.EqualTo(0x8B39DB00F83BAEE6UL));
        Assert.That(EntityRandom.Bits(1234, new EntityId(5, 2), 99, 3), Is.EqualTo(0x5BCD4D16E1C83828UL));
        var random = new EntityRandom(1234) { Tick = 99 };
        Assert.That(random.Bits(new EntityId(5, 2), 3), Is.EqualTo(EntityRandom.Bits(1234, new EntityId(5, 2), 99, 3)));
        Assert.That(random.Float(new EntityId(5, 2), 3), Is.EqualTo(EntityRandom.Float(1234, new EntityId(5, 2), 99, 3)));
    }

    [Test]
    public void EachInputChangesAboutHalfTheBits()
    {
        // Avalanche: flipping any one bit of any input flips each output bit with probability close to 1/2.
        const int samples = 2000;
        var flips = new double[4];
        for (int n = 0; n < samples; n++)
        {
            ulong seed = (ulong)n * 0x9E3779B97F4A7C15, tick = (ulong)n;
            var id = new EntityId(n, 1 + n % 3);
            uint stream = (uint)(n % 5);
            ulong bits = EntityRandom.Bits(seed, id, tick, stream);
            int bit = n % 32;
            flips[0] += BitOperations.PopCount(bits ^ EntityRandom.Bits(seed ^ (1UL << bit), id, tick, stream));
            flips[1] += BitOperations.PopCount(bits ^ EntityRandom.Bits(seed, new EntityId(id.Index ^ (1 << bit % 31), id.Version), tick, stream));
            flips[2] += BitOperations.PopCount(bits ^ EntityRandom.Bits(seed, id, tick ^ (1UL << bit), stream));
            flips[3] += BitOperations.PopCount(bits ^ EntityRandom.Bits(seed, id, tick, stream ^ (1u << bit)));
        }

        foreach (double total in flips) Assert.That(total / samples, Is.InRange(31.0, 33.0));
    }

    [Test]
    public void SeedsDoNotJustSwapTheNumbersOfEntities()
    {
        // With seed ^ id, seeds 1 and 2 would give entity 3 the numbers of entity 0.
        var a = Enumerable.Range(0, 1000).Select(i => EntityRandom.Bits(1, new EntityId(i, 1), 0, 0)).ToHashSet();
        int shared = Enumerable.Range(0, 1000).Count(i => a.Contains(EntityRandom.Bits(2, new EntityId(i, 1), 0, 0)));
        Assert.That(shared, Is.Zero);
    }

    [Test]
    public void RangesAndChancesAreUniform()
    {
        var counts = new int[6];
        int hits = 0;
        float least = 1, most = 0;
        const int samples = 600_000;
        for (int i = 0; i < samples; i++)
        {
            var id = new EntityId(i, 1);
            counts[EntityRandom.Range(7, id, 3, 0, 10, 16) - 10]++;
            if (EntityRandom.Chance(7, id, 3, 1, 0.25f)) hits++;
            float f = EntityRandom.Float(7, id, 3, 2);
            least = MathF.Min(least, f);
            most = MathF.Max(most, f);
        }

        foreach (int count in counts) Assert.That(count, Is.InRange(samples / 6 * 0.98, samples / 6 * 1.02));
        Assert.That(hits, Is.InRange(samples / 4 * 0.98, samples / 4 * 1.02));
        Assert.That(least, Is.InRange(0f, 0.001f));
        Assert.That(most, Is.InRange(0.999f, 1f).And.LessThan(1f));
        Assert.That(EntityRandom.Range(7, default, 0, 0, 5, 5), Is.EqualTo(5));
        Assert.That(EntityRandom.Range(7, default, 0, 0, int.MinValue, int.MaxValue), Is.InRange(int.MinValue, int.MaxValue - 1));
        Assert.That(EntityRandom.Range(7, default, 0, 0, -1f, 1f), Is.InRange(-1f, 1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => EntityRandom.Range(7, default, 0, 0, 2, 1));
    }

    public record struct Fraction(float Value) : IComponent;
    public record struct Die(int Value) : IComponent;
    public record struct Hit(int Value) : IComponent;
    public record struct Spread(float Value) : IComponent;
    public record struct Noise(ulong Value) : IComponent;
    public struct Lacks : IComponent;

    /// <summary>The draws a vector loop takes, written for one row (<see cref="DrawsAsLanes"/>).</summary>
    [EcsSystem]
    public ref struct LaneDraws
    {
        [ForEach]
        static void Floats(ref Fraction f, ref Spread s, Entity entity, in EntityRandom random)
        {
            f.Value = random.Float(entity.Id, 0);
            s.Value = random.Range(entity.Id, 1, -s.Value, s.Value + 1);
        }

        [ForEach]
        static void Ints(ref Die d, ref Hit h, Entity entity, in EntityRandom random)
        {
            d.Value = random.Range(entity.Id, 2, -3, 1000);
            h.Value = random.Chance(entity.Id, 3, 0.3f) ? 1 : 0;
        }

        [ForEach, Without<Lacks>]
        static void Static(ref Fraction f, Entity entity, in EntityRandom random) =>
            f.Value += EntityRandom.Float(random.Seed ^ 99, entity.Id, random.Tick + 5, 4);

        [ForEach]
        static void Bits(ref Noise n, Entity entity, in EntityRandom random) => n.Value = random.Bits(entity.Id, 5);
    }

    /// <summary><see cref="LaneDraws"/> row by row.</summary>
    [EcsSystem]
    public ref struct RowDraws
    {
        [ForEach(SimdMode = SimdMode.Off)]
        static void Floats(ref Fraction f, ref Spread s, Entity entity, in EntityRandom random)
        {
            f.Value = random.Float(entity.Id, 0);
            s.Value = random.Range(entity.Id, 1, -s.Value, s.Value + 1);
        }

        [ForEach(SimdMode = SimdMode.Off)]
        static void Ints(ref Die d, ref Hit h, Entity entity, in EntityRandom random)
        {
            d.Value = random.Range(entity.Id, 2, -3, 1000);
            h.Value = random.Chance(entity.Id, 3, 0.3f) ? 1 : 0;
        }

        [ForEach(SimdMode = SimdMode.Off), Without<Lacks>]
        static void Static(ref Fraction f, Entity entity, in EntityRandom random) =>
            f.Value += EntityRandom.Float(random.Seed ^ 99, entity.Id, random.Tick + 5, 4);

        [ForEach(SimdMode = SimdMode.Off)]
        static void Bits(ref Noise n, Entity entity, in EntityRandom random) => n.Value = random.Bits(entity.Id, 5);
    }

    /// <summary>A parallel draw with its row twin: chunks of masked words, ids of every version.</summary>
    [EcsSystem]
    public ref struct ParallelLaneDraws
    {
        [ForEach(Parallel = true, ChunkSize = 64), Without<Lacks>]
        static void Roll(ref Die d, Entity entity, in EntityRandom random) => d.Value = random.Range(entity.Id, 6, 0, 6);
    }

    [EcsSystem]
    public ref struct ParallelRowDraws
    {
        [ForEach(Parallel = true, ChunkSize = 64, SimdMode = SimdMode.Off), Without<Lacks>]
        static void Roll(ref Die d, Entity entity, in EntityRandom random) => d.Value = random.Range(entity.Id, 6, 0, 6);
    }

    private static (float, float, int, int, ulong)[] Draws<TSystem>(int count, int threads) where TSystem : struct, allows ref struct
    {
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        var entities = new Entity[count];
        world.CreateEntities(entities, new Fraction(), new Spread(2.5f), new Die(), new Hit(), new Noise());
        // Versions above 1 (destroyed and made again), holes of a filter (masked words), rows of other tables.
        for (int i = 0; i < count; i += 3) entities[i].Destroy();
        for (int i = 0; i < count; i += 3) entities[i] = world.CreateEntity(new Fraction(), new Spread(i % 7), new Die(), new Hit(), new Noise());
        for (int i = 1; i < count; i += 5) entities[i].Set(new Lacks());
        world.SetEntityRandom(Seed: 0xC0FFEE);
        world.AddSystem<TSystem>();
        world.Update();
        world.Update();
        return entities.Select(e => (e.Get<Fraction>().Value, e.Get<Spread>().Value, e.Get<Die>().Value, e.Get<Hit>().Value, e.Get<Noise>().Value)).ToArray();
    }

    [TestCase(1)]
    [TestCase(15)]
    [TestCase(37)]
    [TestCase(1000)]
    public void DrawsAsLanes(int count)
    {
        Assert.That(Draws<LaneDraws>(count, 1), Is.EqualTo(Draws<RowDraws>(count, 1)));
        foreach (int threads in new[] { 1, 4 })
            Assert.That(Draws<ParallelLaneDraws>(count, threads), Is.EqualTo(Draws<ParallelRowDraws>(count, 1)), $"{threads} threads");
    }

    [Test]
    public void DrawsGetVectorLoops()
    {
        using var world = new World();
        world.AddSystem<LaneDraws>();
        world.AddSystem<ParallelLaneDraws>();
        foreach (var system in world.DescribeSchedule().Groups[0].Stages.SelectMany(s => s.Systems))
            foreach (var loop in system.Loops)
                Assert.That(loop, Does.Contain(": vector"), loop);
    }

    [Test]
    public void DrawsAsLanesDoNotAllocate()
    {
        using var world = new World();
        var entities = new Entity[1000];
        world.CreateEntities(entities, new Fraction(), new Spread(1), new Die(), new Hit(), new Noise());
        world.SetEntityRandom(Seed: 3);
        world.AddSystem<LaneDraws>();
        Allocations.WarmUp(() => world.Update(), 20);
        Assert.That(Allocations.Of(() => world.Update()), Is.Zero);
    }

    [Test]
    public void DrawingDoesNotAllocate()
    {
        long before = Allocations.Baseline();
        float sum = 0;
        var random = new EntityRandom(1) { Tick = 2 };
        for (int i = 0; i < 1000; i++) sum += random.Float(new EntityId(i, 1), 0) + random.Range(new EntityId(i, 1), 1, 0, 10);
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
        Assert.That(sum, Is.GreaterThan(0));
    }
}
