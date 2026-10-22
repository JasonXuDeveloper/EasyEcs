using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public struct Heat : IComponent { public float Value; }
public struct Speed : IComponent { public float Value; }
public struct Mass : IComponent { public float Value; }
public record struct Bits(int Value) : IComponent;     // a positional record struct: its lane is an auto-property
public record struct Mask(int Value) : IComponent;
public record struct Gravity(float Pull) : ISingletonComponent;

// Components of several fields of one lane type: the vector loop splits them into one vector per field (Interleaved).
public record struct Spot2 : IComponent { public float X, Y; }
public record struct Drift2 : IComponent { public float X, Y; }
public record struct Spot3 : IComponent { public float X, Y, Z; }
public record struct Drift3 : IComponent { public float X, Y, Z; }
public record struct Color4(float R, float G, float B, float A) : IComponent;
public record struct Far3 : IComponent { public double X, Y, Z; }
public record struct Pace : IComponent { public double Value; }
public record struct Cell2 : IComponent { public long X, Y; }
public record struct Hash4 : IComponent { public uint A, B, C, D; }

/// <summary>Kernels written for one row; the generator runs them as Vector&lt;T&gt; loops (Kernel.cs).</summary>
public static class Kernels
{
    public const float Limit = 10f;
    public static readonly float Drag = 0.25f;

    [ForEach]
    internal static void Step(ref Heat heat, ref Speed speed, in Mass mass, in Gravity gravity)
    {
        float force = heat.Value * 0.5f - mass.Value * gravity.Pull;
        speed.Value = MathF.Max(MathF.Min(speed.Value + force - Drag, Limit), -Limit);
        if (heat.Value > 100f) heat.Value = 100f;
        else heat.Value += speed.Value * 0.125f;
    }

    [ForEach]
    internal static void Guard(ref Heat heat, in Mass mass, float scale)
    {
        if (mass.Value <= 0 || mass.Value != mass.Value) return; // NaN: a return clears its lanes
        heat.Value = mass.Value > 1 ? MathF.Sqrt(mass.Value) * scale : heat.Value / mass.Value;
        heat.Value -= 1;
    }

    [ForEach]
    internal static void Shift(ref Bits bits, in Mask mask)
    {
        var mixed = (bits.Value << 3) ^ (bits.Value >> 2) ^ ~mask.Value;
        bits.Value = mixed > 0 && (mask.Value & 1) == 0 ? mixed * 7 - 3 : Math.Max(mixed, mask.Value);
        bits.Value++;
    }
}

// Lanes of several types of one size: int <-> float, uint <-> float, long <-> double, ulong <-> double.
public record struct Level : IComponent { public float Value; }
public record struct Rank : IComponent { public int Value; }
public record struct Code32 : IComponent { public uint Value; }
public record struct Depth : IComponent { public double Value; }
public record struct Stamp : IComponent { public long Value; }
public record struct Serial : IComponent { public ulong Value; }
public record struct Bounds(float Min, float Max) : ISingletonComponent;

/// <summary>Kernels of Math.Clamp, Lerp, the MathF / float forms, conversions, patterns and masks.</summary>
public static class MoreKernels
{
    [ForEach]
    internal static void Clamp(ref Level level, in Rank tally, in Bounds bounds)
    {
        level.Value = Math.Clamp(level.Value, bounds.Min, bounds.Max); // uniform bounds: checked per vector
        if (tally.Value > 0) level.Value = Math.Clamp(level.Value, -1f, 1f);
    }

    [ForEach]
    internal static void ClampLanes(ref Spot2 spot, in Level level) => spot.X = Math.Clamp(spot.X, level.Value, spot.Y); // bounds per lane

    [ForEach]
    internal static void ClampInts(ref Rank tally, in Code32 code) => tally.Value = Math.Clamp(tally.Value, -100, (int)(code.Value & 255));

    [ForEach]
    internal static void Blend(ref Level level, in Rank tally)
    {
        float t = tally.Value * 0.001f;                  // int to float
        float a = level.Value;
        level.Value = float.Lerp(a, 10f, 0.25f) + (a + (5f - a) * t);
    }

    [ForEach]
    internal static void Rounding(ref Spot3 spot)
    {
        spot.X = MathF.Round(spot.X) + MathF.Truncate(spot.Y);
        spot.Y = MathF.CopySign(spot.Y, spot.Z) - float.Max(spot.X, spot.Z);
        spot.Z = float.Min(MathF.Abs(spot.Z), float.Sqrt(spot.Y)) + float.Floor(spot.X) + MathF.Ceiling(spot.Z);
    }

    [ForEach]
    internal static void Convert(ref Level level, ref Rank tally, ref Code32 code)
    {
        int whole = (int)level.Value;                    // saturates, NaN to 0
        uint positive = (uint)level.Value;               // saturates at 0 and uint.MaxValue
        float back = code.Value;                         // uint to float, rounded
        tally.Value = whole + (int)code.Value;           // uint to int: the same bits
        code.Value = positive ^ (uint)tally.Value;
        level.Value = back * 0.5f + tally.Value;
    }

    [ForEach]
    internal static void ConvertWide(ref Depth depth, ref Stamp stamp, ref Serial serial)
    {
        long whole = (long)depth.Value;
        ulong positive = (ulong)depth.Value;
        stamp.Value = whole - (long)serial.Value;
        serial.Value = positive + (ulong)stamp.Value;
        depth.Value = stamp.Value * 0.25 + (double)serial.Value;
    }

    [ForEach]
    internal static void Masks(ref Level level, ref Rank tally)
    {
        bool hot = level.Value > 5;
        bool cold = tally.Value < 0;
        if (hot == cold) tally.Value++;
        if (!(hot || cold) && level.Value is not > 1) level.Value = -level.Value;
        if (level.Value is > 0 and < 10 or float.NaN or -0f) tally.Value += 2;
        else if (tally.Value is 3 or < -50) level.Value = 7;
        if (float.IsNaN(level.Value)) level.Value = 0;
        bool both = hot && !cold;
        level.Value = both != (tally.Value >= 100) ? level.Value + 1 : level.Value;
    }
}

/// <summary>Kernels over components of several fields.</summary>
public static class FieldKernels
{
    public const float Dt = 1f / 30;

    [ForEach]
    internal static void Drift(ref Spot2 p, in Drift2 v)
    {
        p.X += v.X * Dt;
        p.Y = v.X < 0 ? -p.Y : p.Y + v.Y; // a field written under a mask, another read in it
    }

    [ForEach]
    internal static void Seek(ref Spot3 p, ref Drift3 v, float speed)
    {
        float dx = 1 - p.X, dy = 2 - p.Y, dz = 3 - p.Z;
        float length = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        if (length <= 0) return;                               // 0 and NaN rows keep their values
        float scale = speed / MathF.Max(length, 1e-6f);
        v.X = dx * scale;
        v.Y = dy * scale;
        v.Z = dz * scale;
        p.X += v.X * Dt;
        p.Y += v.Y * Dt;
        p.Z += v.Z * Dt;
    }

    [ForEach]
    internal static void FadeLanes(ref Color4 c)
    {
        c.A = MathF.Max(c.A - 0.25f, 0);
        if (c.A == 0) c.R = c.G = c.B = 0;
    }

    [ForEach]
    internal static void Travel(ref Far3 p, in Pace pace)
    {
        p.X = Math.FusedMultiplyAdd(p.X, pace.Value, 1.5);
        p.Y = Math.Floor(p.Y * pace.Value);
        p.Z = Math.Abs(p.Z) > 1e10 ? double.NaN : p.Z - pace.Value;
    }

    [ForEach]
    internal static void Step(ref Cell2 c)
    {
        c.X = c.X * 6364136223846793005L + 1442695040888963407L; // wraps
        c.Y ^= c.X >> 7;
    }

    [ForEach]
    internal static void Mix(ref Hash4 h)
    {
        h.A += h.B;
        h.D ^= h.A;
        h.D = (h.D << 16) | (h.D >> 16);
        h.C += h.D;
        h.B ^= h.C;
    }
}

public class KernelTests
{
    /// <summary>The lengths that cut a vector short or leave a tail, at both vector widths, and a long run.</summary>
    private static readonly int[] Lengths = [0, 1, Vector<float>.Count - 1, Vector<float>.Count, Vector<float>.Count + 1,
        Vector512<float>.Count - 1, Vector512<float>.Count + 1, Vector<double>.Count + 1, 1000];

    private static readonly float[] Special = [float.NaN, -0f, 0f, 1f, -1f, 2f, 3f, 0.5f, 2.5f, -2.5f, float.PositiveInfinity, float.NegativeInfinity, 1e-30f, float.MaxValue,
        2147483648f, -2147483904f, 4294967296f, 3e9f, -3e9f, float.Epsilon];

    private static float NextFloat(Random random, int i) => i % 5 == 0 ? Special[random.Next(Special.Length)] : (float)(random.NextDouble() * 20 - 10);

    private static double NextDouble(Random random, int i) =>
        i % 7 == 0 ? new[] { double.NaN, -0d, 1e11, -1e11, double.NegativeInfinity, 9.3e18, 1.9e19, -9.3e18, 2.5 }[random.Next(9)] : random.NextDouble() * 200 - 100;

    private static Level NextLevel(Random r, int i) => new() { Value = NextFloat(r, i) };
    private static Rank NextTally(Random r, int i) => new() { Value = i % 4 == 0 ? new[] { int.MinValue, int.MaxValue, 0, 3, -51 }[r.Next(5)] : r.Next(-200, 200) };
    private static Code32 NextCode(Random r, int i) => new() { Value = i % 4 == 1 ? new[] { uint.MaxValue, 0u, 16777217u, 2147483648u }[r.Next(4)] : (uint)r.Next() };

    private static string Bits<T>(in T a) where T : struct => System.Convert.ToHexString(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in a)));

    /// <summary>
    /// The same bits, except that any NaN equals any NaN: which NaN an operation of two NaN operands returns is not
    /// defined in .NET (x86 returns the first operand, and the JIT may swap the operands of + and *), so the method
    /// itself can return either; every value that is not NaN must match bit for bit.
    /// </summary>
    private static bool Same<T>(in T a, in T b) where T : struct
    {
        if (MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in a)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in b)))) return true;
        foreach (var field in typeof(T).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
        {
            object x = field.GetValue(a)!, y = field.GetValue(b)!;
            bool same = (x, y) switch
            {
                (float f, float g) => float.IsNaN(f) ? float.IsNaN(g) : BitConverter.SingleToInt32Bits(f) == BitConverter.SingleToInt32Bits(g),
                (double f, double g) => double.IsNaN(f) ? double.IsNaN(g) : BitConverter.DoubleToInt64Bits(f) == BitConverter.DoubleToInt64Bits(g),
                _ => x.Equals(y),
            };
            if (!same) return false;
        }
        return true;
    }

    /// <summary>Components of <paramref name="count"/> entities in two tables, the generated loop, and the method itself per row.</summary>
    private static void Check<T1, T2>(int count, Func<Random, int, T1> make1, Func<Random, int, T2> make2,
        Action<World> generated, ScalarRow<T1, T2> scalar)
        where T1 : struct, IComponent where T2 : struct, IComponent
    {
        using var world = new World();
        world.Reserve<T1, T2>(count);
        var random = new Random(count);
        var a = new T1[count];
        var b = new T2[count];
        var entities = new Entity[count];
        for (int i = 0; i < count; i++)
        {
            a[i] = make1(random, i);
            b[i] = make2(random, i);
            entities[i] = world.CreateEntity(a[i], b[i]);
            if (i % 3 == 0) entities[i].Set(new Frozen());
        }

        for (int round = 0; round < 3; round++)
        {
            generated(world);
            for (int i = 0; i < count; i++) scalar(ref a[i], ref b[i]);
        }

        for (int i = 0; i < count; i++)
        {
            Assert.That(Same(entities[i].Get<T1>(), a[i]), $"{typeof(T1).Name} of row {i} of {count}: {Bits(entities[i].Get<T1>())} instead of {Bits(a[i])} ({b[i]})");
            Assert.That(Same(entities[i].Get<T2>(), b[i]), $"{typeof(T2).Name} of row {i} of {count}: {entities[i].Get<T2>()} instead of {b[i]}");
        }
    }

    private delegate void ScalarRow<T1, T2>(ref T1 a, ref T2 b);

    [TestCaseSource(nameof(Lengths))]
    public void ComponentsOfSeveralFieldsMatchTheMethodBitForBit(int count)
    {
        Check(count, (r, i) => new Spot2 { X = NextFloat(r, i), Y = NextFloat(r, i + 1) }, (r, i) => new Drift2 { X = NextFloat(r, i + 2), Y = NextFloat(r, i + 3) },
            FieldKernels.Drift, (ref Spot2 p, ref Drift2 v) => FieldKernels.Drift(ref p, in v));
        Check(count, (r, i) => new Spot3 { X = NextFloat(r, i), Y = NextFloat(r, i + 1), Z = NextFloat(r, i + 2) },
            (r, i) => new Drift3 { X = NextFloat(r, i + 3), Y = NextFloat(r, i + 4), Z = NextFloat(r, i + 5) },
            w => FieldKernels.Seek(w, 4f), (ref Spot3 p, ref Drift3 v) => FieldKernels.Seek(ref p, ref v, 4f));
        Check(count, (r, i) => new Color4(NextFloat(r, i), NextFloat(r, i + 1), NextFloat(r, i + 2), NextFloat(r, i + 3) % 1),
            (r, i) => new Heat { Value = NextFloat(r, i) }, FieldKernels.FadeLanes, (ref Color4 c, ref Heat _) => FieldKernels.FadeLanes(ref c));
        Check(count, (r, i) => new Far3 { X = NextDouble(r, i), Y = NextDouble(r, i + 1), Z = NextDouble(r, i + 2) },
            (r, i) => new Pace { Value = NextDouble(r, i + 3) }, FieldKernels.Travel, (ref Far3 p, ref Pace v) => FieldKernels.Travel(ref p, in v));
        Check(count, (r, i) => new Cell2 { X = r.NextInt64(long.MinValue, long.MaxValue), Y = r.NextInt64() }, (r, i) => new Heat(),
            FieldKernels.Step, (ref Cell2 c, ref Heat _) => FieldKernels.Step(ref c));
        Check(count, (r, i) => new Hash4 { A = (uint)r.Next(), B = uint.MaxValue - (uint)i, C = (uint)r.Next(), D = (uint)i }, (r, i) => new Heat(),
            FieldKernels.Mix, (ref Hash4 h, ref Heat _) => FieldKernels.Mix(ref h));
    }

    [TestCaseSource(nameof(Lengths))]
    public void ClampLerpMathConversionsAndMasksMatchTheMethodBitForBit(int count)
    {
        // bounds in order, bounds of NaN, bounds equal
        foreach (var bounds in new[] { new Bounds(-5, 5), new Bounds(float.NaN, 2), new Bounds(3, 3) })
            Check(count, NextLevel, NextTally, w => { w.SetBounds(bounds.Min, bounds.Max); MoreKernels.Clamp(w); },
                (ref Level l, ref Rank t) => MoreKernels.Clamp(ref l, in t, in bounds));
        Check(count, (r, i) => new Spot2 { X = NextFloat(r, i), Y = 100 + i }, (r, i) => new Level { Value = i % 9 == 0 ? float.NaN : -100 - i },
            MoreKernels.ClampLanes, (ref Spot2 s, ref Level l) => MoreKernels.ClampLanes(ref s, in l));
        Check(count, NextTally, NextCode, MoreKernels.ClampInts, (ref Rank t, ref Code32 c) => MoreKernels.ClampInts(ref t, in c));
        Check(count, NextLevel, NextTally, MoreKernels.Blend, (ref Level l, ref Rank t) => MoreKernels.Blend(ref l, in t));
        Check(count, (r, i) => new Spot3 { X = NextFloat(r, i), Y = NextFloat(r, i + 1), Z = NextFloat(r, i + 2) }, NextLevel,
            MoreKernels.Rounding, (ref Spot3 s, ref Level _) => MoreKernels.Rounding(ref s));
        Check3(count, NextLevel, NextTally, NextCode, MoreKernels.Convert, MoreKernels.Convert);
        Check3(count, (r, i) => new Depth { Value = NextDouble(r, i) }, (r, i) => new Stamp { Value = i % 3 == 0 ? long.MinValue + i : r.NextInt64() },
            (r, i) => new Serial { Value = i % 5 == 0 ? ulong.MaxValue : (ulong)r.NextInt64() }, MoreKernels.ConvertWide, MoreKernels.ConvertWide);
        Check(count, NextLevel, NextTally, MoreKernels.Masks, (ref Level l, ref Rank t) => MoreKernels.Masks(ref l, ref t));
    }

    /// <summary>Math.Clamp with a min above its max throws for the row that has it, after the rows before it, as written.</summary>
    [Test]
    public void AClampThatThrowsLeavesTheRowsTheMethodLeaves()
    {
        const int count = 100, bad = 37;
        using var vector = new World();
        var spots = new Spot2[count];
        var levels = new Level[count];
        for (int i = 0; i < count; i++)
        {
            spots[i] = new Spot2 { X = i, Y = i == bad ? -1000 : 1000 };
            levels[i] = new Level { Value = 10 };
            vector.CreateEntity(spots[i], levels[i]);
        }

        Assert.Throws<ArgumentException>(() => MoreKernels.ClampLanes(vector));
        Assert.Throws<ArgumentException>(() => { for (int i = 0; i < count; i++) MoreKernels.ClampLanes(ref spots[i], in levels[i]); });
        int row = 0;
        foreach (var item in vector.Query<Spot2>()) Assert.That(Same(item.Spot2, spots[row++]), $"row {row - 1}");
    }

    /// <summary>Which rows lose a component (removed in place: the row keeps its place, its presence bit clears).</summary>
    public static readonly (string Name, Func<int, bool> Hole)[] Holes =
    [
        ("every third", i => i % 3 == 0),
        ("random half", i => (uint)(i * 2654435761u) >> 31 == 1),
        ("every other word", i => (i >> 6) % 2 == 1),
        ("one per word", i => (i & 63) == 5),
        ("all but one per word", i => (i & 63) != 40),
        ("after row 70", i => i >= 70),
    ];

    private static readonly int[] HoleCounts = [1, 8, 17, 64, 65, 130, 1000];

    private static System.Collections.Generic.IEnumerable<TestCaseData> HoleCases() =>
        from hole in Holes from count in HoleCounts select new TestCaseData(hole.Name, count);

    /// <summary>
    /// Rows lacking a component (removed in place) are lanes the vector loop leaves out: a word of 64 rows with some
    /// passing runs as a masked vector loop, and every row ends as the method leaves it (the rows it does not visit as
    /// they were).
    /// </summary>
    [TestCaseSource(nameof(HoleCases))]
    public void RowsWithoutAComponentAreLanesTheLoopLeavesOut(string pattern, int count)
    {
        var hole = Holes.First(h => h.Name == pattern).Hole;
        CheckHoles(count, hole, (r, i) => new Spot3 { X = NextFloat(r, i), Y = NextFloat(r, i + 1), Z = NextFloat(r, i + 2) },
            (r, i) => new Drift3 { X = NextFloat(r, i + 3), Y = NextFloat(r, i + 4), Z = NextFloat(r, i + 5) },
            w => FieldKernels.Seek(w, 4f), (ref Spot3 p, ref Drift3 v) => FieldKernels.Seek(ref p, ref v, 4f));
        CheckHoles(count, hole, NextLevel, NextTally, MoreKernels.Masks, (ref Level l, ref Rank t) => MoreKernels.Masks(ref l, ref t));
        var bounds = new Bounds(-5, 5);
        CheckHoles(count, hole, NextLevel, NextTally, w => { w.SetBounds(bounds.Min, bounds.Max); MoreKernels.Clamp(w); },
            (ref Level l, ref Rank t) => MoreKernels.Clamp(ref l, in t, in bounds));
        CheckHoles(count, hole, (r, i) => new Far3 { X = NextDouble(r, i), Y = NextDouble(r, i + 1), Z = NextDouble(r, i + 2) },
            (r, i) => new Pace { Value = NextDouble(r, i + 3) }, FieldKernels.Travel, (ref Far3 p, ref Pace v) => FieldKernels.Travel(ref p, in v));
    }

    /// <summary>A Math.Clamp that throws in a masked word throws for the row that has it, after the passing rows before it.</summary>
    [Test]
    public void AClampThatThrowsInAMaskedWordLeavesTheRowsTheMethodLeaves()
    {
        const int count = 100;
        using var vector = new World();
        var spots = new Spot2[count];
        var levels = new Level[count];
        var entities = new Entity[count];
        for (int i = 0; i < count; i++)
        {
            // Rows 36 and 38 lack Level but hold bounds that would throw: only row 37 throws.
            spots[i] = new Spot2 { X = i, Y = i is >= 36 and <= 38 ? -1000 : 1000 };
            levels[i] = new Level { Value = 10 };
            entities[i] = vector.CreateEntity(spots[i], levels[i]);
        }

        bool Hole(int i) => i % 2 == 0;
        for (int i = 0; i < count; i++) if (Hole(i)) entities[i].Remove<Level>();
        Assert.Throws<ArgumentException>(() => MoreKernels.ClampLanes(vector));
        Assert.Throws<ArgumentException>(() => { for (int i = 0; i < count; i++) if (!Hole(i)) MoreKernels.ClampLanes(ref spots[i], in levels[i]); });
        for (int i = 0; i < count; i++) Assert.That(Same(entities[i].Get<Spot2>(), spots[i]), $"row {i}");
    }

    /// <summary>Rows destroyed during an outer pass (tombstones until it ends) are lanes the vector loop leaves out.</summary>
    [Test]
    public void RowsDestroyedDuringAPassAreLanesTheLoopLeavesOut()
    {
        const int count = 200;
        using var world = new World();
        var random = new Random(3);
        var levels = new Level[count];
        var ranks = new Rank[count];
        var entities = new Entity[count];
        for (int i = 0; i < count; i++)
        {
            (levels[i], ranks[i]) = (NextLevel(random, i), NextTally(random, i));
            entities[i] = world.CreateEntity(levels[i], ranks[i]);
            if (i % 5 == 0) entities[i].Remove<Rank>(); // a test of presence bits: the masked path
        }

        world.CreateEntity(new Heat());
        foreach (var _ in world.Query<Heat>())
        {
            for (int i = 0; i < count; i += 7) entities[i].Destroy();
            MoreKernels.Masks(world);
        }

        for (int i = 0; i < count; i++)
        {
            if (i % 7 == 0) continue;
            if (i % 5 != 0) MoreKernels.Masks(ref levels[i], ref ranks[i]);
            Assert.That(Same(entities[i].Get<Level>(), levels[i]), $"Level of row {i}: {Bits(entities[i].Get<Level>())} instead of {Bits(levels[i])}");
        }
    }

    /// <summary>
    /// <see cref="Check{T1, T2}"/> over one table whose rows in <paramref name="hole"/> lack one of the two components
    /// (the first for odd rows, the second for even ones): the method runs on the rows that have both.
    /// </summary>
    private static void CheckHoles<T1, T2>(int count, Func<int, bool> hole, Func<Random, int, T1> make1, Func<Random, int, T2> make2,
        Action<World> generated, ScalarRow<T1, T2> scalar)
        where T1 : struct, IComponent where T2 : struct, IComponent
    {
        using var world = new World();
        world.Reserve<T1, T2>(count);
        var random = new Random(count);
        var a = new T1[count];
        var b = new T2[count];
        var entities = new Entity[count];
        for (int i = 0; i < count; i++)
        {
            a[i] = make1(random, i);
            b[i] = make2(random, i);
            entities[i] = world.CreateEntity(a[i], b[i]);
        }

        for (int i = 0; i < count; i++)
            if (hole(i))
            {
                if (i % 2 == 1) entities[i].Remove<T1>();
                else entities[i].Remove<T2>();
            }

        for (int round = 0; round < 3; round++)
        {
            generated(world);
            for (int i = 0; i < count; i++) if (!hole(i)) scalar(ref a[i], ref b[i]);
        }

        for (int i = 0; i < count; i++)
        {
            if (entities[i].Has<T1>()) Assert.That(Same(entities[i].Get<T1>(), a[i]), $"{typeof(T1).Name} of row {i} of {count}: {Bits(entities[i].Get<T1>())} instead of {Bits(a[i])}");
            if (entities[i].Has<T2>()) Assert.That(Same(entities[i].Get<T2>(), b[i]), $"{typeof(T2).Name} of row {i} of {count}: {Bits(entities[i].Get<T2>())} instead of {Bits(b[i])}");
        }
    }

    private delegate void ScalarRow3<T1, T2, T3>(ref T1 a, ref T2 b, ref T3 c);

    private static void Check3<T1, T2, T3>(int count, Func<Random, int, T1> make1, Func<Random, int, T2> make2, Func<Random, int, T3> make3,
        Action<World> generated, ScalarRow3<T1, T2, T3> scalar)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
    {
        using var world = new World();
        var random = new Random(count);
        var a = new T1[count];
        var b = new T2[count];
        var c = new T3[count];
        var entities = new Entity[count];
        world.Reserve<T1, T2, T3>(count);
        for (int i = 0; i < count; i++)
        {
            (a[i], b[i], c[i]) = (make1(random, i), make2(random, i), make3(random, i));
            entities[i] = world.CreateEntity(a[i], b[i], c[i]);
            if (i % 3 == 0) entities[i].Set(new Frozen());
        }

        for (int round = 0; round < 3; round++)
        {
            generated(world);
            for (int i = 0; i < count; i++) scalar(ref a[i], ref b[i], ref c[i]);
        }

        for (int i = 0; i < count; i++)
        {
            Assert.That(Same(entities[i].Get<T1>(), a[i]), $"{typeof(T1).Name} of row {i} of {count}: {entities[i].Get<T1>()} instead of {a[i]} ({b[i]})");
            Assert.That(Same(entities[i].Get<T2>(), b[i]), $"{typeof(T2).Name} of row {i} of {count}: {entities[i].Get<T2>()} instead of {b[i]}");
            Assert.That(Same(entities[i].Get<T3>(), c[i]), $"{typeof(T3).Name} of row {i} of {count}: {entities[i].Get<T3>()} instead of {c[i]}");
        }
    }

    /// <summary>Interleaved.LoadN splits exactly the rows of one vector into fields; StoreN writes exactly those rows back.</summary>
    [Test]
    public void InterleavedLoadsAndStoresMoveExactlyTheRowsOfOneVector()
    {
        Assume.That(Interleaved.IsAccelerated, "no AVX2 or AVX-512");
        RoundTrip<float>(); RoundTrip<int>(); RoundTrip<uint>();
        RoundTrip<double>(); RoundTrip<long>(); RoundTrip<ulong>();
    }

    private static void RoundTrip<T>() where T : unmanaged, INumber<T>
    {
        foreach (int fields in new[] { 2, 3, 4 })
        foreach (bool wide in new[] { false, true })
        {
            if (wide && !Interleaved.IsAccelerated512) continue;
            int lanes = wide ? Vector512<T>.Count : Vector<T>.Count, rows = lanes * 3, at = lanes + 1; // an unaligned row
            var source = new T[rows * fields];
            for (int i = 0; i < source.Length; i++) source[i] = T.CreateTruncating(i + 1);
            var target = new T[source.Length]; // zeros: rows outside the vector must stay 0
            var split = new T[fields][];
            ref T column = ref source[0], into = ref target[0];
            if (wide) Wide(fields, ref column, ref into, (nuint)at, split);
            else Narrow(fields, ref column, ref into, (nuint)at, split);
            for (int f = 0; f < fields; f++)
            for (int l = 0; l < lanes; l++)
                Assert.That(split[f][l], Is.EqualTo(source[(at + l) * fields + f]), $"{typeof(T).Name} {fields} fields {(wide ? 512 : Vector<byte>.Count * 8)}: field {f} lane {l}");
            for (int i = 0; i < target.Length; i++)
                Assert.That(target[i], Is.EqualTo(i >= at * fields && i < (at + lanes) * fields ? source[i] : T.Zero), $"{typeof(T).Name} {fields} fields: element {i}");
        }
    }

    private static void Narrow<T>(int fields, ref T column, ref T into, nuint row, T[][] split) where T : unmanaged
    {
        switch (fields)
        {
            case 2:
                Interleaved.Load2(ref column, row, out Vector<T> a, out Vector<T> b);
                Interleaved.Store2(a, b, ref into, row);
                (split[0], split[1]) = (Lanes(a), Lanes(b));
                break;
            case 3:
                Interleaved.Load3(ref column, row, out Vector<T> c, out Vector<T> d, out Vector<T> e);
                Interleaved.Store3(c, d, e, ref into, row);
                (split[0], split[1], split[2]) = (Lanes(c), Lanes(d), Lanes(e));
                break;
            default:
                Interleaved.Load4(ref column, row, out Vector<T> f, out Vector<T> g, out Vector<T> h, out Vector<T> k);
                Interleaved.Store4(f, g, h, k, ref into, row);
                (split[0], split[1], split[2], split[3]) = (Lanes(f), Lanes(g), Lanes(h), Lanes(k));
                break;
        }
    }

    private static void Wide<T>(int fields, ref T column, ref T into, nuint row, T[][] split) where T : unmanaged
    {
        switch (fields)
        {
            case 2:
                Interleaved.Load2(ref column, row, out Vector512<T> a, out Vector512<T> b);
                Interleaved.Store2(a, b, ref into, row);
                (split[0], split[1]) = (Lanes(a), Lanes(b));
                break;
            case 3:
                Interleaved.Load3(ref column, row, out Vector512<T> c, out Vector512<T> d, out Vector512<T> e);
                Interleaved.Store3(c, d, e, ref into, row);
                (split[0], split[1], split[2]) = (Lanes(c), Lanes(d), Lanes(e));
                break;
            default:
                Interleaved.Load4(ref column, row, out Vector512<T> f, out Vector512<T> g, out Vector512<T> h, out Vector512<T> k);
                Interleaved.Store4(f, g, h, k, ref into, row);
                (split[0], split[1], split[2], split[3]) = (Lanes(f), Lanes(g), Lanes(h), Lanes(k));
                break;
        }
    }

    private static T[] Lanes<T>(Vector<T> v) where T : unmanaged
    {
        var lanes = new T[Vector<T>.Count];
        v.CopyTo(lanes);
        return lanes;
    }

    private static T[] Lanes<T>(Vector512<T> v) where T : unmanaged
    {
        var lanes = new T[Vector512<T>.Count];
        v.CopyTo(lanes);
        return lanes;
    }

    [Test]
    public void VectorLoopsDoNotAllocate()
    {
        using var world = new World();
        world.SetGravity(0.5f);
        for (int i = 0; i < 1000; i++)
        {
            world.CreateEntity(new Heat { Value = i }, new Speed(), new Mass { Value = 1 }, new Bits(i), new Mask(i));
            world.CreateEntity(new Spot3 { X = i }, new Drift3(), new Spot2(), new Drift2(), new Far3(), new Pace { Value = 2 });
            var e = world.CreateEntity(new Level { Value = i }, new Rank { Value = i - 500 }, new Code32 { Value = (uint)i }, new Spot2 { Y = float.PositiveInfinity }); // no Clamp throws
            if (i % 3 == 0) e.Remove<Rank>(); // rows to test: masked words
        }
        world.SetBounds(-10, 10);

        void Frame()
        {
            Kernels.Step(world);
            Kernels.Guard(world, 2f);
            Kernels.Shift(world);
            FieldKernels.Drift(world);
            FieldKernels.Seek(world, 4f);
            FieldKernels.Travel(world);
            MoreKernels.Clamp(world);
            MoreKernels.ClampLanes(world);
            MoreKernels.Blend(world);
            MoreKernels.Convert(world);
            MoreKernels.Masks(world);
        }

        Frame(); // warm-up: the query cache, the static index tables of Interleaved
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10; i++) Frame();
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    // Lane counts that leave a tail, and two tables (the Frozen tag) so a pass sees several runs.
    [TestCase(1)]
    [TestCase(7)]
    [TestCase(1003)]
    public void VectorLoopsMatchTheMethodBitForBit(int count)
    {
        float[] special = [float.NaN, -0f, 0f, 1f, -1f, 100f, 101f, float.PositiveInfinity, float.NegativeInfinity, 1e-30f];
        var random = new Random(count);
        float Next(int i) => i % 5 == 0 ? special[random.Next(special.Length)] : (float)(random.NextDouble() * 300 - 100);

        using var world = new World();
        world.SetGravity(0.75f);
        var heat = new Heat[count];
        var speed = new Speed[count];
        var mass = new Mass[count];
        var bits = new Bits[count];
        var masks = new Mask[count];
        var entities = new Entity[count];
        for (int i = 0; i < count; i++)
        {
            heat[i].Value = Next(i);
            speed[i].Value = Next(i + 1);
            mass[i].Value = Next(i + 2);
            bits[i] = new Bits(random.Next(int.MinValue, int.MaxValue));
            masks[i] = new Mask(random.Next(int.MinValue, int.MaxValue));
            entities[i] = world.CreateEntity(heat[i], speed[i], mass[i], bits[i], masks[i]);
            if (i % 3 == 0) entities[i].Set(new Frozen());
        }

        var gravity = world.Gravity;
        for (int round = 0; round < 3; round++)
        {
            Kernels.Step(world);
            Kernels.Guard(world, 1.5f);
            Kernels.Shift(world);
            for (int i = 0; i < count; i++)
            {
                Kernels.Step(ref heat[i], ref speed[i], in mass[i], in gravity);
                Kernels.Guard(ref heat[i], in mass[i], 1.5f);
                Kernels.Shift(ref bits[i], in masks[i]);
            }
        }

        for (int i = 0; i < count; i++)
        {
            Assert.That(BitConverter.SingleToInt32Bits(entities[i].Heat.Value), Is.EqualTo(BitConverter.SingleToInt32Bits(heat[i].Value)), $"heat {i}");
            Assert.That(BitConverter.SingleToInt32Bits(entities[i].Speed.Value), Is.EqualTo(BitConverter.SingleToInt32Bits(speed[i].Value)), $"speed {i}");
            Assert.That(entities[i].Bits, Is.EqualTo(bits[i]), $"bits {i}");
        }
    }
}
