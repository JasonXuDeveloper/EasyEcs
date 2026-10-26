using System;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public struct Mettle : IComponent
{
    public float Value;

    /// <summary>A component property with a body: read in a condition.</summary>
    public readonly bool Low => Value < 10;

    /// <summary>A component method that writes its struct and returns early.</summary>
    public void Drain(float by)
    {
        if (by <= 0) return;
        Value -= by;
        if (Value < 0) Value = 0;
    }
}

public struct Guard : IComponent { public float Value; }
public struct Scars : IComponent { public int Count; }
public struct Downed : IComponent;

/// <summary>The helpers the methods of <see cref="HelperRules"/> call, as a user writes them (Kernel.Inlining.cs).</summary>
public static class Combat
{
    /// <summary>Static, an expression body.</summary>
    public static float Mitigated(float hit, float guard) => hit * 100f / (100f + guard);

    /// <summary>Locals and several returns.</summary>
    public static float Damage(float hit, float guard)
    {
        if (guard >= hit) return 0;
        float left = hit - guard;
        if (left > 50) return 50;
        return left;
    }

    /// <summary>An out parameter and a bool result.</summary>
    public static bool TrySplit(float v, out float half)
    {
        if (v <= 0)
        {
            half = 0;
            return false;
        }

        half = v * 0.5f;
        return true;
    }

    /// <summary>A ref parameter, the caller's field.</summary>
    public static void Heal(ref float v, float by) => v += by;

    /// <summary>A helper that calls a helper.</summary>
    public static float Outer(float x) => Inner(x) + 1;

    private static float Inner(float x) => x * 2;

    /// <summary>An extension method.</summary>
    public static float Clamp01(this float v) => v < 0 ? 0 : v > 1 ? 1 : v;

    /// <summary>A structural change of the caller's entity, through the generated member.</summary>
    public static void Wound(Entity e, ref Scars s)
    {
        s.Count++;
        if (s.Count > 3) e.SetDowned();
    }
}

/// <summary>C# 14 extension members over a component.</summary>
public static class MettleExtensions
{
    extension(ref Mettle v)
    {
        public bool Spent => v.Value <= 0;
        public void Restore(float by) { v.Value += by; }
    }
}

/// <summary>
/// Methods that call their own helpers, each required to get a vector loop: the helpers are inlined into it
/// (Kernel.Inlining.cs). <see cref="HelperRulesByRow"/> runs the same bodies one row at a time; the two worlds must end
/// alike, bit for bit.
/// </summary>
[EcsSystem]
public ref struct HelperRules
{
    [ForEach(SimdMode = SimdMode.Require)]
    static void Hit(ref Mettle v, in Guard g) => v.Value -= Combat.Damage(Combat.Mitigated(30, g.Value), 5);

    [ForEach(SimdMode = SimdMode.Require)]
    static void Rest(ref Mettle v)
    {
        if (v.Low) v.Restore(Combat.Outer(1.5f));
    }

    [ForEach(SimdMode = SimdMode.Require)]
    static void Fade(ref Mettle v, in Guard g)
    {
        if (Combat.TrySplit(g.Value - 40, out float half)) v.Drain(half);
    }

    [ForEach(SimdMode = SimdMode.Require)]
    static void Mend(ref Mettle v, in Guard g)
    {
        Combat.Heal(ref v.Value, g.Value.Clamp01());
        if (v.Spent) v.Value = 1;
    }

    [ForEach(SimdMode = SimdMode.Require)]
    static void Thicken(ref Guard g)
    {
        g.Value = Grow(Grow(g.Value));
        static float Grow(float x) => x < 200 ? x + 3 : x;
    }

    [ForEach(SimdMode = SimdMode.Require), Without<Downed>]
    static void Scar(Entity e, ref Scars s, in Mettle v)
    {
        if (v.Value < 20) Combat.Wound(e, ref s);
    }
}

/// <summary><see cref="HelperRules"/> one row at a time.</summary>
[EcsSystem]
public ref struct HelperRulesByRow
{
    [ForEach(SimdMode = SimdMode.Off)]
    static void Hit(ref Mettle v, in Guard g) => v.Value -= Combat.Damage(Combat.Mitigated(30, g.Value), 5);

    [ForEach(SimdMode = SimdMode.Off)]
    static void Rest(ref Mettle v)
    {
        if (v.Low) v.Restore(Combat.Outer(1.5f));
    }

    [ForEach(SimdMode = SimdMode.Off)]
    static void Fade(ref Mettle v, in Guard g)
    {
        if (Combat.TrySplit(g.Value - 40, out float half)) v.Drain(half);
    }

    [ForEach(SimdMode = SimdMode.Off)]
    static void Mend(ref Mettle v, in Guard g)
    {
        Combat.Heal(ref v.Value, g.Value.Clamp01());
        if (v.Spent) v.Value = 1;
    }

    [ForEach(SimdMode = SimdMode.Off)]
    static void Thicken(ref Guard g)
    {
        g.Value = Grow(Grow(g.Value));
        static float Grow(float x) => x < 200 ? x + 3 : x;
    }

    [ForEach(SimdMode = SimdMode.Off), Without<Downed>]
    static void Scar(Entity e, ref Scars s, in Mettle v)
    {
        if (v.Value < 20) Combat.Wound(e, ref s);
    }
}

public class HelperLoopTests
{
    private static World Make<TSystem>(int count, int seed, Entity[] entities) where TSystem : struct, allows ref struct
    {
        var world = new World();
        world.Reserve<Mettle, Guard, Scars>(count);
        var random = new Random(seed);
        for (int i = 0; i < count; i++)
            entities[i] = world.CreateEntity(new Mettle { Value = random.Next(-20, 120) * 0.75f }, new Guard { Value = random.Next(-5, 90) * 0.5f },
                new Scars { Count = random.Next(0, 3) });
        world.AddSystem<TSystem>();
        return world;
    }

    [Test]
    public void EveryMethodGetsAVectorLoop()
    {
        if (!System.Numerics.Vector.IsHardwareAccelerated) Assert.Ignore("no vector hardware: the scalar job runs");
        using var world = new World();
        world.AddSystem<HelperRules>();
        foreach (var loop in world.DescribeSchedule().Groups[0].Stages[0].Systems[0].Loops)
            Assert.That(loop, Does.Contain(": vector"), loop);
    }

    /// <summary>
    /// The vector world and the one run row by row over the same random rows, six updates with new values each: every
    /// entity ends alike, its floats bit for bit, its tags the same.
    /// </summary>
    [TestCase(1, 1)]
    [TestCase(7, 2)]
    [TestCase(64, 3)]
    [TestCase(65, 4)]
    [TestCase(1000 + 13, 5)]
    public void HelpersInlinedComputeWhatTheirCallsCompute(int count, int seed)
    {
        var a = new Entity[count];
        var b = new Entity[count];
        using var vector = Make<HelperRules>(count, seed, a);
        using var scalar = Make<HelperRulesByRow>(count, seed, b);
        var random = new Random(seed * 31);
        for (int round = 0; round < 6; round++)
        {
            for (int i = 0; i < count; i++)
            {
                float value = random.Next(-20, 120) * 0.75f;
                a[i].Set(new Mettle { Value = value });
                b[i].Set(new Mettle { Value = value });
            }

            vector.Update();
            scalar.Update();
            for (int i = 0; i < count; i++)
            {
                string at = $"round {round}, entity {i}";
                Assert.That(BitConverter.SingleToInt32Bits(a[i].Read<Mettle>().Value), Is.EqualTo(BitConverter.SingleToInt32Bits(b[i].Read<Mettle>().Value)), at);
                Assert.That(BitConverter.SingleToInt32Bits(a[i].Read<Guard>().Value), Is.EqualTo(BitConverter.SingleToInt32Bits(b[i].Read<Guard>().Value)), at);
                Assert.That(a[i].Read<Scars>().Count, Is.EqualTo(b[i].Read<Scars>().Count), at);
                Assert.That(a[i].Has<Downed>(), Is.EqualTo(b[i].Has<Downed>()), at);
            }
        }
    }
}
