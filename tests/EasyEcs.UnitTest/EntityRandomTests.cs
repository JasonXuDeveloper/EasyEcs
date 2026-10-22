using System;
using System.Linq;
using System.Numerics;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary><see cref="EntityRandom"/>: random numbers as a pure function of (seed, entity, tick, stream).</summary>
public class EntityRandomTests
{
    public record struct Roll(int Value, float Fraction) : IComponent;

    public class ParallelRolls : EcsSystem
    {
        [ForEach(Parallel = true, ChunkRows = 64)]
        static void Roll(ref Roll roll, Entity entity, in EntityRandom random) =>
            roll = new Roll(random.Range(entity.Id, 0, 1, 7), random.Float(entity.Id, 1));
    }

    public class SerialRolls : EcsSystem
    {
        [ForEach]
        static void Roll(ref Roll roll, Entity entity, in EntityRandom random) =>
            roll = new Roll(random.Range(entity.Id, 0, 1, 7), random.Float(entity.Id, 1));
    }

    private static Roll[] Rolls<TSystem>(int threads, ulong seed, ulong tick) where TSystem : EcsSystem, new()
    {
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        var entities = new Entity[20_000];
        world.CreateEntities(entities, new Roll());
        world.AddSingleton<EntityRandom>() = new EntityRandom(seed, tick);
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

    [Test]
    public void TheBitsAreAStableFunction()
    {
        // Pinned values: a change to the hash would change every saved replay, so it must be deliberate.
        Assert.That(EntityRandom.Bits(0, new EntityId(0, 1), 0, 0), Is.EqualTo(0x8B39DB00F83BAEE6UL));
        Assert.That(EntityRandom.Bits(1234, new EntityId(5, 2), 99, 3), Is.EqualTo(0x5BCD4D16E1C83828UL));
        var random = new EntityRandom(1234, 99);
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

    [Test]
    public void DrawingDoesNotAllocate()
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        float sum = 0;
        var random = new EntityRandom(1, 2);
        for (int i = 0; i < 1000; i++) sum += random.Float(new EntityId(i, 1), 0) + random.Range(new EntityId(i, 1), 1, 0, 10);
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
        Assert.That(sum, Is.GreaterThan(0));
    }
}
