using System;
using EasyEcs.Aot.Library;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>
/// Methods calling constructors and helpers of another assembly (EasyEcs.Aot.Library, compiled against its reference
/// assembly), each required to get a vector loop: what that assembly's generator recorded is inlined (Recorded.cs).
/// <see cref="ForeignRulesByRow"/> runs the same bodies one row at a time; the two worlds must end alike, bit for bit.
/// </summary>
[EcsSystem]
public ref struct ForeignRules
{
    [ForEach(SimdMode = SimdMode.Require)]
    static void Drift(ref Ember s) => s = new Ember(s.X + 1, s.Y * 0.5f);

    [ForEach(SimdMode = SimdMode.Require)]
    static void Glow(ref Furnace h, ref Ember s)
    {
        s.X = Physics.Scaled(h.Degrees);
        h.Warm(0.25f);
    }

    [ForEach(SimdMode = SimdMode.Require)]
    static void Chill(ref Ember s) => s.Y = s.Y.Cool(0.5f);
}

/// <summary><see cref="ForeignRules"/> one row at a time.</summary>
[EcsSystem]
public ref struct ForeignRulesByRow
{
    [ForEach(SimdMode = SimdMode.Off)]
    static void Drift(ref Ember s) => s = new Ember(s.X + 1, s.Y * 0.5f);

    [ForEach(SimdMode = SimdMode.Off)]
    static void Glow(ref Furnace h, ref Ember s)
    {
        s.X = Physics.Scaled(h.Degrees);
        h.Warm(0.25f);
    }

    [ForEach(SimdMode = SimdMode.Off)]
    static void Chill(ref Ember s) => s.Y = s.Y.Cool(0.5f);
}

public class ForeignBodyTests
{
    private static World Make<TSystem>(int count, int seed, Entity[] entities) where TSystem : struct, allows ref struct
    {
        var world = new World();
        var random = new Random(seed);
        for (int i = 0; i < count; i++)
            entities[i] = world.CreateEntity(new Ember(random.Next(-50, 50) * 0.25f, random.Next(-50, 50) * 0.75f), new Furnace(random.Next(-10, 10) * 0.125f));
        world.AddSystem<TSystem>();
        return world;
    }

    [Test]
    public void EveryMethodGetsAVectorLoop()
    {
        if (!System.Numerics.Vector.IsHardwareAccelerated) Assert.Ignore("no vector hardware: the scalar job runs");
        using var world = new World();
        world.AddSystem<ForeignRules>();
        foreach (var loop in world.DescribeSchedule().Groups[0].Stages[0].Systems[0].Loops)
            Assert.That(loop, Does.Contain(": vector"), loop);
    }

    [TestCase(1, 1)]
    [TestCase(7, 2)]
    [TestCase(64, 3)]
    [TestCase(1000 + 13, 4)]
    public void ForeignBodiesInlinedComputeWhatTheirCallsCompute(int count, int seed)
    {
        var a = new Entity[count];
        var b = new Entity[count];
        using var vector = Make<ForeignRules>(count, seed, a);
        using var scalar = Make<ForeignRulesByRow>(count, seed, b);
        for (int round = 0; round < 5; round++)
        {
            vector.Update();
            scalar.Update();
            for (int i = 0; i < count; i++)
            {
                string at = $"round {round}, entity {i}";
                var (p, q) = (a[i].Read<Ember>(), b[i].Read<Ember>());
                Assert.That(BitConverter.SingleToInt32Bits(p.X), Is.EqualTo(BitConverter.SingleToInt32Bits(q.X)), at);
                Assert.That(BitConverter.SingleToInt32Bits(p.Y), Is.EqualTo(BitConverter.SingleToInt32Bits(q.Y)), at);
                Assert.That(BitConverter.SingleToInt32Bits(a[i].Read<Furnace>().Degrees), Is.EqualTo(BitConverter.SingleToInt32Bits(b[i].Read<Furnace>().Degrees)), at);
            }
        }
    }

    [Test]
    public void ForeignBodiesInlinedAllocateNothing()
    {
        var entities = new Entity[1000];
        using var world = Make<ForeignRules>(entities.Length, 5, entities);
        Allocations.WarmUp(() => world.Update(), 10);
        Assert.That(Allocations.Of(() => world.Update()), Is.Zero);
    }
}
