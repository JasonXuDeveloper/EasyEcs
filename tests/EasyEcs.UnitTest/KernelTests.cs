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
public struct Spot2 : IComponent { public float X, Y; }
public struct Drift2 : IComponent { public float X, Y; }
public struct Spot3 : IComponent { public float X, Y, Z; }
public struct Drift3 : IComponent { public float X, Y, Z; }
public record struct Color4(float R, float G, float B, float A) : IComponent;
public struct Far3 : IComponent { public double X, Y, Z; }
public struct Pace : IComponent { public double Value; }
public struct Cell2 : IComponent { public long X, Y; }
public struct Hash4 : IComponent { public uint A, B, C, D; }

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

    private static readonly float[] Special = [float.NaN, -0f, 0f, 1f, -1f, 2f, 3f, float.PositiveInfinity, float.NegativeInfinity, 1e-30f, float.MaxValue];

    private static float NextFloat(Random random, int i) => i % 5 == 0 ? Special[random.Next(Special.Length)] : (float)(random.NextDouble() * 20 - 10);

    private static double NextDouble(Random random, int i) =>
        i % 7 == 0 ? new[] { double.NaN, -0d, 1e11, -1e11, double.NegativeInfinity }[random.Next(5)] : random.NextDouble() * 200 - 100;

    private static bool Same<T>(in T a, in T b) where T : struct =>
        MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in a)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in b)));

    /// <summary>Components of <paramref name="count"/> entities in two tables, the generated loop, and the method itself per row.</summary>
    private static void Check<T1, T2>(int count, Func<Random, int, T1> make1, Func<Random, int, T2> make2,
        Action<World> generated, ScalarRow<T1, T2> scalar)
        where T1 : struct, IComponent where T2 : struct, IComponent
    {
        using var world = new World();
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
            Assert.That(Same(entities[i].Get<T1>(), a[i]), $"{typeof(T1).Name} of row {i} of {count}: {entities[i].Get<T1>()} instead of {a[i]}");
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
        }

        void Frame()
        {
            Kernels.Step(world);
            Kernels.Guard(world, 2f);
            Kernels.Shift(world);
            FieldKernels.Drift(world);
            FieldKernels.Seek(world, 4f);
            FieldKernels.Travel(world);
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
