using System;
using System.Linq;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public record struct Rise(float Value) : IComponent;
public record struct Lift(float Value) : IComponent;
public record struct Tock(int Value) : IComponent;
public record struct Tick(int Value) : IComponent;
public record struct Glide2(float X, float Y) : IComponent;
public record struct Push2(float X, float Y) : IComponent;
public record struct Glide3(float X, float Y, float Z) : IComponent;
public record struct Push3(float X, float Y, float Z) : IComponent;
public record struct Sag(float Value) : ISingletonComponent;
public record struct Gait(int Value) : ISingletonComponent;

/// <summary>Flat jobs (SystemGenerator.LaneStepOf): each one walk over Core's lane kernel, uniforms broadcast before its loop.</summary>
[EcsSystem]
public ref struct LaneKernelSystem
{
    [ForEach] static void Climb(ref Rise r, in Lift l) => r.Value += l.Value * 0.25f - 1f;
    [ForEach] static void Ratio(ref Rise r, in Lift l) => r.Value = -(r.Value / (l.Value + 2f));
    [ForEach] static void Count(ref Tock t, in Tick k) => t.Value = (t.Value * 3 + k.Value ^ 5) & ~k.Value | 6;
    [ForEach] static void Slide(ref Glide2 p, in Push2 v) => p = new Glide2(p.X + v.X * 0.5f, p.Y + v.Y * 0.5f);
    [ForEach] static void Drift(ref Glide3 p, in Push3 v) { p.X -= v.X; p.Y -= v.Y; p.Z -= v.Z; }
    [ForEach] static void Fall(ref Rise r, in Lift l, in Sag g) => r.Value += (l.Value - g.Value) * g.Value + 0.5f;
    [ForEach] static void Pace(ref Tock t, in Tick k, in Gait s) => t.Value = t.Value * s.Value + (k.Value ^ s.Value);
    [ForEach] static void Sink(ref Glide2 p, in Push2 v, in Sag g) => p = new Glide2(p.X - v.X * g.Value, p.Y - v.Y * g.Value);
}

/// <summary>The same methods row by row (SimdMode.Off).</summary>
[EcsSystem]
public ref struct LaneKernelRows
{
    [ForEach(SimdMode = SimdMode.Off)] static void Climb(ref Rise r, in Lift l) => r.Value += l.Value * 0.25f - 1f;
    [ForEach(SimdMode = SimdMode.Off)] static void Ratio(ref Rise r, in Lift l) => r.Value = -(r.Value / (l.Value + 2f));
    [ForEach(SimdMode = SimdMode.Off)] static void Count(ref Tock t, in Tick k) => t.Value = (t.Value * 3 + k.Value ^ 5) & ~k.Value | 6;
    [ForEach(SimdMode = SimdMode.Off)] static void Slide(ref Glide2 p, in Push2 v) => p = new Glide2(p.X + v.X * 0.5f, p.Y + v.Y * 0.5f);
    [ForEach(SimdMode = SimdMode.Off)] static void Drift(ref Glide3 p, in Push3 v) { p.X -= v.X; p.Y -= v.Y; p.Z -= v.Z; }
    [ForEach(SimdMode = SimdMode.Off)] static void Fall(ref Rise r, in Lift l, in Sag g) => r.Value += (l.Value - g.Value) * g.Value + 0.5f;
    [ForEach(SimdMode = SimdMode.Off)] static void Pace(ref Tock t, in Tick k, in Gait s) => t.Value = t.Value * s.Value + (k.Value ^ s.Value);
    [ForEach(SimdMode = SimdMode.Off)] static void Sink(ref Glide2 p, in Push2 v, in Sag g) => p = new Glide2(p.X - v.X * g.Value, p.Y - v.Y * g.Value);
}

/// <summary>
/// The lane kernel's flat jobs against the methods row by row, bit for bit: every length to past a 512-bit vector's
/// tails, long runs, NaN (any NaN as one) and the zeros, and masked words (rows whose read component is disabled); a singleton's value
/// among the arithmetic (a uniform of the run, float and int, changed between updates). The CI runs the suite
/// in the default mode; this class also under DOTNET_PreferredVectorBitWidth=256, DOTNET_EnableAVX=0 and
/// DOTNET_EnableHWIntrinsic=0 (the kernel's chain from 256, 128 and no width).
/// </summary>
public class LaneKernelTests
{
    private static readonly int[] Lengths = [.. Enumerable.Range(0, 70), 255, 1000, 1021];

    [Test]
    public void FlatJobsTakeTheLaneKernel()
    {
        using var world = new World();
        world.AddSystem<LaneKernelSystem>();
        var loops = world.DescribeSchedule().Groups[0].Stages[0].Systems[0].Loops;
        Assert.That(loops, Has.Length.EqualTo(8).And.All.Contain("vector (lane kernel: Vector512, Vector256, Vector128)"));
        Assert.That(loops.Count(l => l.Contains(", flat,")), Is.EqualTo(3)); // Slide, Drift and Sink: lanes of whole rows
    }

    [TestCaseSource(nameof(Lengths))]
    public void FlatJobsMatchTheMethodBitForBit(int count)
    {
        foreach (bool holes in new[] { false, true })
        {
            using World vector = Make(count, holes), scalar = Make(count, holes);
            vector.AddSystem<LaneKernelSystem>();
            scalar.AddSystem<LaneKernelRows>();
            for (int round = 0; round < 3; round++)
            {
                // a uniform read once per run, as the method reads it per row: a new value each update, NaN and infinity
                foreach (var world in new[] { vector, scalar })
                {
                    world.GetOrAddSingleton<Sag>() = new Sag(round switch { 0 => 0.75f, 1 => float.NaN, _ => float.PositiveInfinity });
                    world.GetOrAddSingleton<Gait>() = new Gait(3 - round * 7);
                }
                vector.Update();
                scalar.Update();
            }
            Assert.That(Columns(vector), Is.EqualTo(Columns(scalar)), $"{count} rows{(holes ? ", masked words" : "")}");
        }
    }

    private static World Make(int count, bool holes)
    {
        var world = new World();
        var random = new Random(count);
        world.Reserve<Rise, Lift, Tock, Tick, Glide2, Push2, Glide3, Push3>(count);
        for (int i = 0; i < count; i++)
        {
            float F(int k) => KernelTests.NextFloat(random, i + k);
            var e = world.CreateEntity(new Rise(F(0)), new Lift(F(1)), new Tock(random.Next()), new Tick(random.Next(-100, 100)),
                new Glide2(F(2), F(3)), new Push2(F(4), F(5)), new Glide3(F(6), F(7), F(8)), new Push3(F(9), F(10), F(11)));
            // a disabled component the methods read leaves its row out: the table's words of rows are masked
            if (holes && i % 3 == 1)
            {
                e.Disable<Lift>();
                e.Disable<Tick>();
                e.Disable<Push2>();
                e.Disable<Push3>();
            }
        }
        return world;
    }

    // The bits of each value, every NaN as one: which NaN an operation of two NaN operands returns follows the operand
    // order the JIT picks for + and *, which differs between compiles of the same method (KernelTests.Same).
    private static int Bits(float value) => float.IsNaN(value) ? int.MinValue : BitConverter.SingleToInt32Bits(value);

    private static string Columns(World world)
    {
        var text = new System.Text.StringBuilder();
        foreach (var e in world.Entities)
        {
            text.Append(Bits(e.Read<Rise>().Value)).Append(',').Append(e.Read<Tock>().Value).Append(',');
            var (g2, g3) = (e.Read<Glide2>(), e.Read<Glide3>());
            foreach (float f in new[] { g2.X, g2.Y, g3.X, g3.Y, g3.Z }) text.Append(Bits(f)).Append(',');
            text.Append(';');
        }
        return text.ToString();
    }
}
