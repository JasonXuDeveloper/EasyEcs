using System;
using System.Runtime.CompilerServices;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public struct Charge : IComponent { public float Value; }
public struct Grade : IComponent { public int Value; }
public struct Glow : IComponent { public byte Value; }
public record struct Mark(float X, float Y) : IComponent;

/// <summary>Helpers with switches, as a user writes them: inlined into the vector loop (Kernel.Inlining.cs).</summary>
public static class Grading
{
    /// <summary>A switch statement whose sections return, the result after it for the lanes no section took.</summary>
    public static float Weight(int grade)
    {
        switch (grade)
        {
            case 0:
                return 1;
            case < 5 and > 0:
                return 2;
        }

        return 3;
    }

    /// <summary>A switch expression as the body.</summary>
    public static float AtLeastOne(float v) => v switch { < 1 => 1, _ => v };
}

/// <summary>
/// Switches and patterns over lanes, each required to get a vector loop: every arm a mask, the first that matches
/// wins (Kernel.Switches.cs). <see cref="SwitchRulesByRow"/> runs the same bodies one row at a time; the two worlds
/// must end alike, bit for bit.
/// </summary>
[EcsSystem]
public ref struct SwitchRules
{
    // NaN takes no relational arm: it reaches its constant arm. '> 10' before '> 5': the first that matches wins.
    [ForEach(SimdMode = SimdMode.Require)]
    static void Clamp(ref Charge c, in Grade g) => c.Value = c.Value switch
    {
        < 0 => 0,
        float.NaN => -1,
        > 50 when g.Value > 3 => c.Value * 0.5f,
        > 10 => c.Value - 1,
        > 5 => 2,
        _ => c.Value + 1,
    };

    // The default section first: it takes the lanes no label matches, wherever it stands.
    [ForEach(SimdMode = SimdMode.Require)]
    static void Step(ref Charge c, in Grade g)
    {
        switch (g.Value)
        {
            default:
                c.Value += 1;
                break;
            case 0:
            case 1:
                c.Value = 0;
                break;
            case > 10 when c.Value > 5:
                c.Value -= 5;
                break;
        }
    }

    // Narrow lanes: a byte kernel's switch, an int value per arm.
    [ForEach(SimdMode = SimdMode.Require)]
    static void Tint(ref Glow g) => g.Value = g.Value switch
    {
        0 => 1,
        255 => 0,
        < 100 => (byte)(g.Value + 2),
        _ => (byte)(g.Value - 1),
    };

    // Positional and property patterns over a record struct: compares of its fields.
    [ForEach(SimdMode = SimdMode.Require)]
    static void Place(in Mark m, ref Charge c) => c.Value = m switch
    {
        (0, _) => 0,
        ( > 0, > 0) => m.X + m.Y,
        { X: < 0, Y: var y } when y > 2 => -y,
        _ => 2,
    };

    // A struct-valued switch: one vector per field and arm.
    [ForEach(SimdMode = SimdMode.Require)]
    static void Move(ref Mark m, in Grade g) => m = g.Value switch
    {
        0 => new Mark(0, 0),
        1 => m with { X = 1 },
        _ => new Mark(m.Y, m.X),
    };

    // A tuple of lanes, a type pattern over a value type, a var pattern and a helper with a switch statement.
    [ForEach(SimdMode = SimdMode.Require)]
    static void Weigh(ref Charge c, ref Grade g)
    {
        c.Value = (g.Value, c.Value) switch
        {
            (0, > 1) => 1,
            (_, < 0) or (< 0, _) => 3,
            _ => c.Value * Grading.Weight(g.Value),
        };
        if (g.Value is int and > 3 && c.Value * 2 is var twice && twice < 40) g.Value = (int)twice;
        c.Value = Grading.AtLeastOne(c.Value);
    }
}

/// <summary><see cref="SwitchRules"/> one row at a time.</summary>
[EcsSystem]
public ref struct SwitchRulesByRow
{
    [ForEach(SimdMode = SimdMode.Off)]
    static void Clamp(ref Charge c, in Grade g) => c.Value = c.Value switch
    {
        < 0 => 0,
        float.NaN => -1,
        > 50 when g.Value > 3 => c.Value * 0.5f,
        > 10 => c.Value - 1,
        > 5 => 2,
        _ => c.Value + 1,
    };

    [ForEach(SimdMode = SimdMode.Off)]
    static void Step(ref Charge c, in Grade g)
    {
        switch (g.Value)
        {
            default:
                c.Value += 1;
                break;
            case 0:
            case 1:
                c.Value = 0;
                break;
            case > 10 when c.Value > 5:
                c.Value -= 5;
                break;
        }
    }

    [ForEach(SimdMode = SimdMode.Off)]
    static void Tint(ref Glow g) => g.Value = g.Value switch
    {
        0 => 1,
        255 => 0,
        < 100 => (byte)(g.Value + 2),
        _ => (byte)(g.Value - 1),
    };

    [ForEach(SimdMode = SimdMode.Off)]
    static void Place(in Mark m, ref Charge c) => c.Value = m switch
    {
        (0, _) => 0,
        ( > 0, > 0) => m.X + m.Y,
        { X: < 0, Y: var y } when y > 2 => -y,
        _ => 2,
    };

    [ForEach(SimdMode = SimdMode.Off)]
    static void Move(ref Mark m, in Grade g) => m = g.Value switch
    {
        0 => new Mark(0, 0),
        1 => m with { X = 1 },
        _ => new Mark(m.Y, m.X),
    };

    [ForEach(SimdMode = SimdMode.Off)]
    static void Weigh(ref Charge c, ref Grade g)
    {
        c.Value = (g.Value, c.Value) switch
        {
            (0, > 1) => 1,
            (_, < 0) or (< 0, _) => 3,
            _ => c.Value * Grading.Weight(g.Value),
        };
        if (g.Value is int and > 3 && c.Value * 2 is var twice && twice < 40) g.Value = (int)twice;
        c.Value = Grading.AtLeastOne(c.Value);
    }
}

/// <summary>A switch in a parallel vector loop and its twin row by row; a switch that is not exhaustive.</summary>
public static class SwitchPasses
{
    [ForEach(Parallel = true, SimdMode = SimdMode.Require)]
    public static void ClampParallel(ref Charge c, in Grade g) => c.Value = c.Value switch { < 0 => 0, > 50 when g.Value > 3 => c.Value * 0.5f, > 10 => c.Value - 1, _ => c.Value + 1 };

    [ForEach(SimdMode = SimdMode.Off)]
    public static void ClampRows(ref Charge c, in Grade g) => c.Value = c.Value switch { < 0 => 0, > 50 when g.Value > 3 => c.Value * 0.5f, > 10 => c.Value - 1, _ => c.Value + 1 };

#pragma warning disable CS8509 // not exhaustive on purpose
    // A negative grade matches no arm: the lanes left go to the method, which throws as C# does.
    [ForEach(SimdMode = SimdMode.Require)]
    public static void Partial(ref Charge c, in Grade g) => c.Value = g.Value switch { 0 => 1, > 0 => 2 };

    [ForEach(SimdMode = SimdMode.Off)]
    public static void PartialRows(ref Charge c, in Grade g) => c.Value = g.Value switch { 0 => 1, > 0 => 2 };
#pragma warning restore CS8509
}

public class SwitchLoopTests
{
    private static float Value(Random random) => random.Next(40) switch
    {
        0 => float.NaN,
        1 => -0f,
        _ => random.Next(-20, 120) * 0.75f,
    };

    private static World Make(int count, int seed, Entity[] entities, int threads = 1)
    {
        var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        world.Reserve<Charge, Grade, Mark>(count);
        var random = new Random(seed);
        for (int i = 0; i < count; i++)
        {
            entities[i] = world.CreateEntity(new Charge { Value = Value(random) }, new Grade { Value = random.Next(-3, 16) },
                new Mark(random.Next(-2, 4), random.Next(-2, 6)));
            entities[i].Set(new Glow { Value = (byte)random.Next(256) });
        }

        return world;
    }

    private static World Make<TSystem>(int count, int seed, Entity[] entities, int threads = 1) where TSystem : struct, allows ref struct
    {
        var world = Make(count, seed, entities, threads);
        world.AddSystem<TSystem>();
        return world;
    }

    private static void Same(Entity a, Entity b, string at)
    {
        Assert.That(BitConverter.SingleToInt32Bits(a.Read<Charge>().Value), Is.EqualTo(BitConverter.SingleToInt32Bits(b.Read<Charge>().Value)), at);
        Assert.That(a.Read<Grade>().Value, Is.EqualTo(b.Read<Grade>().Value), at);
        Assert.That(a.Read<Glow>().Value, Is.EqualTo(b.Read<Glow>().Value), at);
        Assert.That(BitConverter.SingleToInt32Bits(a.Read<Mark>().X), Is.EqualTo(BitConverter.SingleToInt32Bits(b.Read<Mark>().X)), at);
        Assert.That(BitConverter.SingleToInt32Bits(a.Read<Mark>().Y), Is.EqualTo(BitConverter.SingleToInt32Bits(b.Read<Mark>().Y)), at);
    }

    [Test]
    public void EveryMethodGetsAVectorLoop()
    {
        if (!System.Numerics.Vector.IsHardwareAccelerated) Assert.Ignore("no vector hardware: the scalar job runs");
        using var world = new World();
        world.AddSystem<SwitchRules>();
        foreach (var loop in world.DescribeSchedule().Groups[0].Stages[0].Systems[0].Loops)
            Assert.That(loop, Does.Contain(": vector"), loop);
    }

    /// <summary>
    /// The vector world and the one run row by row over the same random rows (NaN and -0 among them), six updates with
    /// new values each: every entity ends alike, its floats bit for bit.
    /// </summary>
    [TestCase(1, 1)]
    [TestCase(7, 2)]
    [TestCase(64, 3)]
    [TestCase(65, 4)]
    [TestCase(1000 + 13, 5)]
    public void SwitchesComputeWhatTheirArmsCompute(int count, int seed)
    {
        var a = new Entity[count];
        var b = new Entity[count];
        using var vector = Make<SwitchRules>(count, seed, a);
        using var scalar = Make<SwitchRulesByRow>(count, seed, b);
        var random = new Random(seed * 31);
        for (int round = 0; round < 6; round++)
        {
            for (int i = 0; i < count; i++)
            {
                var charge = new Charge { Value = Value(random) };
                var grade = new Grade { Value = random.Next(-3, 16) };
                var glow = new Glow { Value = (byte)random.Next(256) };
                a[i].Set(charge);
                b[i].Set(charge);
                a[i].Set(grade);
                b[i].Set(grade);
                a[i].Set(glow);
                b[i].Set(glow);
            }

            vector.Update();
            scalar.Update();
            for (int i = 0; i < count; i++) Same(a[i], b[i], $"round {round}, entity {i}");
        }
    }

    [TestCase(1)]
    [TestCase(4)]
    public void AParallelSwitchIsBitForBitOnOneAndFourThreads(int threads)
    {
        const int count = 20_000;
        var a = new Entity[count];
        var b = new Entity[count];
        using var vector = Make(count, 9, a, threads);
        using var rows = Make(count, 9, b);
        for (int round = 0; round < 3; round++)
        {
            SwitchPasses.ClampParallel(vector);
            SwitchPasses.ClampRows(rows);
        }

        for (int i = 0; i < count; i++) Same(a[i], b[i], $"entity {i}");
    }

    /// <summary>A lane no arm matches: the vector's rows go to the method, which throws as the scalar one does.</summary>
    [Test]
    public void ASwitchThatIsNotExhaustiveThrowsAsCSharpDoes()
    {
        var a = new Entity[100];
        var b = new Entity[100];
        using var vector = Make(a.Length, 13, a);
        using var rows = Make(b.Length, 13, b);
        for (int i = 0; i < a.Length; i++)
        {
            a[i].Set(new Grade { Value = i % 7 });
            b[i].Set(new Grade { Value = i % 7 });
        }

        SwitchPasses.Partial(vector);
        SwitchPasses.PartialRows(rows);
        for (int i = 0; i < a.Length; i++) Same(a[i], b[i], $"entity {i}");

        a[70].Set(new Grade { Value = -1 });
        b[70].Set(new Grade { Value = -1 });
        Assert.Throws<SwitchExpressionException>(() => SwitchPasses.Partial(vector));
        Assert.Throws<SwitchExpressionException>(() => SwitchPasses.PartialRows(rows));
    }

    [Test]
    public void SwitchesAllocateNothing()
    {
        var entities = new Entity[4096 + 5];
        using var world = Make<SwitchRules>(entities.Length, 11, entities, 4);
        void Frame()
        {
            world.Update();
            SwitchPasses.ClampParallel(world);
        }

        Allocations.WarmUp(Frame, 50);
        Assert.That(Allocations.Of(() => { for (int i = 0; i < 10; i++) Frame(); }), Is.Zero);
    }
}
