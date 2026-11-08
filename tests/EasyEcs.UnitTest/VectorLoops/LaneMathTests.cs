using System;
using System.Linq;
using System.Numerics;
using System.Runtime.Intrinsics;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

// The lowerings of math a vector loop computes exactly: one input lane, four results.
public record struct Real : IComponent { public float Value; }
public record struct Real4 : IComponent { public float A, B, C, D; }
public record struct Whole : IComponent { public int Value; }
public record struct Whole4 : IComponent { public int A, B, C, D; }
public record struct Divisor : IComponent { public int Value; }
public record struct Natural : IComponent { public uint Value; }
public record struct Natural4 : IComponent { public uint A, B, C, D; }
public record struct Long1 : IComponent { public long Value; }
public record struct Long4 : IComponent { public long A, B, C, D; }
public record struct Real8 : IComponent { public double Value; }
public record struct Real8x4 : IComponent { public double A, B, C, D; }

/// <summary>
/// Methods using the operations a vector loop lowers to other exact operations (integer division by a constant as
/// shifts or through doubles, a float divided by a power of two as a product, Pow by 0 and 1, the throwing integer
/// Abs / CopySign / Sign) and, under FloatMode.Fast, to .NET's vector approximations.
/// </summary>
public static class LoweredKernels
{
    // Math with no vector form called once per lane (LaneMath.PerLane): each lane the scalar function's bits.
    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Angles(ref Real4 o, in Real x)
    {
        float a = x.Value * 0.5f - 0.25f, b = x.Value * x.Value - a, c = MathF.Abs(a - b) * 0.125f + MathF.Min(a, b);
        o.A = MathF.Atan2(a, b) + c * c - MathF.Max(a, c);
        o.B = MathF.Asin(MathF.Min(MathF.Max(a, -1), 1)) * b - c * 0.5f + a * b;
        o.C = MathF.Sin(a) * c + MathF.Cbrt(b) - a * a * 0.5f + b * c;
        o.D = MathF.Pow(MathF.Abs(a) + 0.5f, b) - c * a + MathF.Max(b, c) * 0.25f - a;
    }

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void DoubleAngles(ref Real8x4 o, in Real8 x)
    {
        double a = x.Value * 0.5 - 0.25, b = x.Value * x.Value - a, c = Math.Abs(a - b) * 0.125 + Math.Min(a, b);
        o.A = Math.Atan2(a, b) + c * c - Math.Max(a, c) + a * b - b * c + a * c;
        o.B = Math.Tanh(a) * b - c * 0.5 + a * b - Math.Min(a, c) * 0.25 + b * b;
        o.C = Math.Exp(Math.Min(a, 50)) * c - a * a * 0.5 + b * c - Math.Abs(b) * 0.125 + c;
        o.D = Math.Pow(Math.Abs(a) + 0.5, Math.Min(b, 8)) - c * a + Math.Max(b, c) * 0.25 - a * b + c;
    }

    // The call alone: below the gate, so Require opens it (the bits are the same either way).
    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Bearing(ref Real4 o, in Real x) => o.A = MathF.Atan2(x.Value, 1.5f);

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Halve(ref Real4 o, in Real x)
    {
        o.A = x.Value / 2f;
        o.B = x.Value / -0.25f;
        o.C = MathF.Pow(x.Value, 1);
        o.D = MathF.Pow(x.Value, 0);
    }

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Scale(ref Real4 o, in Real x)
    {
        o.A = x.Value / 1.7014118E+38f;  // 2^127: its reciprocal is 2^-127, a subnormal float
        o.B = x.Value / 5.877472E-39f;   // 2^-127: a subnormal divisor, its reciprocal 2^127
        o.C = x.Value / 1024f;
        o.D = x.Value / 3f;              // not a power of two: a division
    }

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Shifts(ref Whole4 o, in Whole x)
    {
        o.A = x.Value / 8;
        o.B = x.Value % 8;
        o.C = x.Value / -16;
        o.D = x.Value % int.MinValue;
    }

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Constants(ref Whole4 o, in Whole x)
    {
        o.A = x.Value / 7;
        o.B = x.Value % 7;
        o.C = x.Value / -1000003;
        o.D = x.Value / int.MinValue;
    }

    // Each throws for int.MinValue: such a lane sends its vector's rows to the method.
    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Throwing(ref Whole4 o, in Whole x)
    {
        o.A = Math.Abs(x.Value);
        o.B = x.Value / -1;
        o.C = x.Value % -1;
        o.D = Math.Sign(x.Value);
    }

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Naturals(ref Natural4 o, in Natural x)
    {
        o.A = x.Value / 16u;
        o.B = x.Value % 16u;
        o.C = x.Value / 7u;
        o.D = x.Value % 10u;
    }

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Sign(ref Whole o, in Real x) => o.Value = MathF.Sign(x.Value);

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Divide(ref Whole4 o, in Whole x, in Divisor y)
    {
        o.A = x.Value / y.Value;
        o.B = x.Value % y.Value;
        o.C = int.CopySign(x.Value, y.Value);
        o.D = y.Value != 0 && x.Value / y.Value > 3 ? 1 : 0; // the division runs only where the divisor is not 0
    }

    [ForEach(Parallel = true, SimdMode = SimdMode.Require, ChunkSize = 256)]
    internal static void DivideParallel(ref Whole4 o, in Whole x, in Divisor y)
    {
        o.A += x.Value / y.Value;
        o.B += x.Value % y.Value;
    }

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Longs(ref Long4 o, in Long1 x)
    {
        o.A = x.Value / 8L;
        o.B = x.Value % 8L;
        o.C = x.Value / long.MinValue;
        o.D = Math.Abs(x.Value);
    }

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Doubles(ref Real8x4 o, in Real8 x)
    {
        o.A = x.Value / 2;
        o.B = x.Value / -0.0009765625; // -2^-10
        o.C = Math.Pow(x.Value, 1);
        o.D = Math.CopySign(Math.Pow(x.Value, 0), x.Value);
    }

    [ForEach(SimdMode = SimdMode.Require, FloatMode = FloatMode.Fast)]
    internal static void Powers(ref Real4 o, in Real x)
    {
        o.A = MathF.Pow(x.Value, 2);
        o.B = MathF.Pow(x.Value, 0.5f);
        o.C = MathF.Pow(x.Value, -1);
        o.D = x.Value / 3f;
    }

    [ForEach(SimdMode = SimdMode.Require, FloatMode = FloatMode.Fast)]
    internal static void Curves(ref Real4 o, in Real x)
    {
        o.A = MathF.Tan(x.Value);
        o.B = MathF.Log10(x.Value);
        o.C = MathF.Pow(x.Value, 3);
        o.D = MathF.Pow(x.Value, -2);
    }

    [ForEach(SimdMode = SimdMode.Require, FloatMode = FloatMode.Fast)]
    internal static void Hyperbolic(ref Real4 o, in Real x)
    {
        o.A = MathF.Sinh(x.Value);
        o.B = MathF.Cosh(x.Value);
        o.C = MathF.Tanh(x.Value);
        o.D = MathF.Pow(x.Value, 4);
    }

    // Any other exponent of floats: e^(y ln x) in doubles; a base that is not a finite positive number, or an exponent
    // that is not finite, sends its vector's rows to the method.
    [ForEach(SimdMode = SimdMode.Require, FloatMode = FloatMode.Fast)]
    internal static void Exponents(ref Real4 o, in Real x)
    {
        o.A = MathF.Pow(x.Value, 1.5f);
        o.B = MathF.Pow(x.Value, -0.75f);
        o.C = MathF.Pow(1.0001f, x.Value);
        o.D = MathF.Pow(x.Value, x.Value);
    }
}

/// <summary>
/// The lowerings against the scalar operations: every 32-bit input of the one-input forms (Exhaustive, explicit: about
/// 8 minutes in Release on 4 cores) or a sample of them with every edge (by default), random operands and edges for two inputs,
/// the rows of a lane that throws, parallel runs, allocations.
/// </summary>
public class LaneMathTests
{
    private const int Rows = 1 << 20;

    private static readonly uint[] Edges =
    [
        0, 1, 2, 3, 7, 8, 15, 16, 17, 0x7FFFFFFF, 0x80000000, 0x80000001, 0xFFFFFFFF, 0xFFFFFFFE, 0xFFFFFFF9, 0xFFFFFFF8,
        0x7F800000, 0xFF800000, 0x7FC00000, 0xFFC00000, 0x7F800001, 0x7FA00000, 0x00000001, 0x80000001, 0x007FFFFF,
        0x00800000, 0x80800000, 0x3F800000, 0xBF800000, 0x7F7FFFFF, 0xFF7FFFFF, 0x00400000, 0x34000000, 0x39800000,
        0x41200000, 0x42C80000, 0xC2B40000, 0x3FC90FDB,
    ];

    /// <summary>The <paramref name="i"/>th input: the edges first, then every <paramref name="stride"/>th 32-bit pattern.</summary>
    private static uint Input(long i, uint stride) => i < Edges.Length ? Edges[i] : unchecked((uint)((i - Edges.Length) * stride));

    private delegate TOut Scalar<TIn, TOut>(TIn input);

    private delegate bool Same<T>(in T got, in T want);

    /// <summary>
    /// Runs <paramref name="generated"/> over every input of <paramref name="count"/> (in worlds of 2^20 rows, one per
    /// core) and compares each row with <paramref name="scalar"/>.
    /// </summary>
    private static void Sweep<TIn, TOut>(long count, uint stride, Func<uint, TIn> make, Action<World> generated, Scalar<TIn, TOut> scalar, Same<TOut> same, Func<TIn, string> show)
        where TIn : struct, IComponent where TOut : struct, IComponent
    {
        long chunks = (count + Rows - 1) / Rows, wrong = 0;
        string first = null;
        int workers = Math.Min(Environment.ProcessorCount, 4);
        Parallel.For(0, workers, w =>
        {
            using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 1 });
            var inputs = new TIn[Rows];
            var entities = new Entity[Rows];
            world.Reserve<TIn, TOut>(Rows);
            for (int i = 0; i < Rows; i++) entities[i] = world.CreateEntity(default(TIn), default(TOut));
            for (long chunk = w; chunk < chunks; chunk += workers)
            {
                int n = (int)Math.Min(Rows, count - chunk * Rows);
                var fill = new Fill<TIn, TOut> { Make = make, Start = chunk * Rows, Count = n, Stride = stride, Inputs = inputs };
                world.ForEachBatch<TIn, TOut, Fill<TIn, TOut>>(ref fill);
                generated(world);
                var check = new Check<TIn, TOut> { Scalar = scalar, Same = same, Count = n, Inputs = inputs };
                world.ForEachBatch<TIn, TOut, Check<TIn, TOut>>(ref check);
                if (check.Wrong > 0)
                {
                    Interlocked.Add(ref wrong, check.Wrong);
                    Interlocked.CompareExchange(ref first, $"{show(inputs[check.First])}: {check.Got} instead of {check.Want}", null);
                }
            }
        });
        Assert.That(wrong, Is.Zero, $"{wrong} rows differ, first {first}");
    }

    /// <summary>Writes the inputs of one chunk (rows past its count repeat 0) and zeroes the results.</summary>
    private struct Fill<TIn, TOut> : IForEachBatch<TIn, TOut> where TIn : struct, IComponent where TOut : struct, IComponent
    {
        public Func<uint, TIn> Make;
        public long Start;
        public int Count;
        public uint Stride;
        public TIn[] Inputs;
        private int _row;

        public void Execute(EntitySpan entities, Span<TIn> input, Span<TOut> output)
        {
            for (int i = 0; i < input.Length; i++, _row++)
            {
                input[i] = Inputs[_row] = Make(_row < Count ? Input(Start + _row, Stride) : 0);
                output[i] = default;
            }
        }

        static int IForEachBatch<TIn, TOut>.WriteMask => 3;
    }

    private struct Check<TIn, TOut> : IForEachBatch<TIn, TOut> where TIn : struct, IComponent where TOut : struct, IComponent
    {
        public Scalar<TIn, TOut> Scalar;
        public Same<TOut> Same;
        public int Count;
        public TIn[] Inputs;
        public long Wrong;
        public int First;
        public TOut Got, Want;
        private int _row;

        public void Execute(EntitySpan entities, Span<TIn> input, Span<TOut> output)
        {
            for (int i = 0; i < input.Length && _row < Count; i++, _row++)
            {
                var want = Scalar(Inputs[_row]);
                if (Same(output[i], want)) continue;
                if (Wrong++ == 0) (First, Got, Want) = (_row, output[i], want);
            }
        }

        static int IForEachBatch<TIn, TOut>.WriteMask => 0;
    }

    private static bool Bits(float a, float b) => BitConverter.SingleToUInt32Bits(a) == BitConverter.SingleToUInt32Bits(b);
    private static bool Bits(double a, double b) => BitConverter.DoubleToUInt64Bits(a) == BitConverter.DoubleToUInt64Bits(b);

    private static long Samples(uint stride) => stride == 1 ? 1L << 32 : (1L << 32) / stride + Edges.Length;

    private static readonly object[] Strides =
    [
        new TestCaseData(1u).SetCategory("Exhaustive").Explicit("every 32-bit input: about 8 minutes for the four sweeps in Release on 4 cores (0.5 to 4 each), far longer in Debug"),
        new TestCaseData(4099u),
    ];

    /// <summary>x / 2^k as x * 2^-k, Pow(x, 1) as x and Pow(x, 0) as 1: the scalar bits for every float, NaNs and subnormals included.</summary>
    [TestCaseSource(nameof(Strides))]
    public void FloatsDividedByPowersOfTwoAreTheScalarBits(uint stride)
    {
        static Real4 Halve(Real x) { var o = new Real4(); LoweredKernels.Halve(ref o, in x); return o; }
        static Real4 Scale(Real x) { var o = new Real4(); LoweredKernels.Scale(ref o, in x); return o; }
        static bool Same(in Real4 a, in Real4 b) => Bits(a.A, b.A) && Bits(a.B, b.B) && Bits(a.C, b.C) && Bits(a.D, b.D);
        Sweep<Real, Real4>(Samples(stride), stride, u => new Real { Value = BitConverter.UInt32BitsToSingle(u) }, LoweredKernels.Halve, Halve, Same, x => $"{BitConverter.SingleToUInt32Bits(x.Value):X8}");
        Sweep<Real, Real4>(Samples(stride), stride, u => new Real { Value = BitConverter.UInt32BitsToSingle(u) }, LoweredKernels.Scale, Scale, Same, x => $"{BitConverter.SingleToUInt32Bits(x.Value):X8}");
    }

    /// <summary>Integer division and '%' by constants (shifts for powers of two, doubles otherwise): the scalar results for every int and uint.</summary>
    [TestCaseSource(nameof(Strides))]
    public void IntegersDividedByConstantsAreTheScalarResults(uint stride)
    {
        static Whole4 Shifts(Whole x) { var o = new Whole4(); LoweredKernels.Shifts(ref o, in x); return o; }
        static Whole4 Constants(Whole x) { var o = new Whole4(); LoweredKernels.Constants(ref o, in x); return o; }
        static Natural4 Naturals(Natural x) { var o = new Natural4(); LoweredKernels.Naturals(ref o, in x); return o; }
        static bool Same(in Whole4 a, in Whole4 b) => a == b;
        static bool SameNatural(in Natural4 a, in Natural4 b) => a == b;
        Sweep<Whole, Whole4>(Samples(stride), stride, u => new Whole { Value = (int)u }, LoweredKernels.Shifts, Shifts, Same, x => x.Value.ToString());
        Sweep<Whole, Whole4>(Samples(stride), stride, u => new Whole { Value = (int)u }, LoweredKernels.Constants, Constants, Same, x => x.Value.ToString());
        Sweep<Natural, Natural4>(Samples(stride), stride, u => new Natural { Value = u }, LoweredKernels.Naturals, Naturals, SameNatural, x => x.Value.ToString());
    }

    /// <summary>
    /// Math.Abs, / -1, % -1 and Math.Sign of every int but int.MinValue, and MathF.Sign of every float but NaN: the scalar
    /// results (those inputs throw: <see cref="LanesThatThrowRunTheMethod"/>).
    /// </summary>
    [TestCaseSource(nameof(Strides))]
    public void ThrowingOperationsAreTheScalarResultsElsewhere(uint stride)
    {
        static Whole4 Throwing(Whole x) { var o = new Whole4(); LoweredKernels.Throwing(ref o, in x); return o; }
        static Whole Sign(Real x) { var o = new Whole(); LoweredKernels.Sign(ref o, in x); return o; }
        static bool Same(in Whole4 a, in Whole4 b) => a == b;
        static bool SameSign(in Whole a, in Whole b) => a == b;
        Sweep<Whole, Whole4>(Samples(stride), stride, u => new Whole { Value = u == 0x80000000 ? 0 : (int)u }, LoweredKernels.Throwing, Throwing, Same, x => x.Value.ToString());
        Sweep<Real, Whole>(Samples(stride), stride, u => new Real { Value = float.IsNaN(BitConverter.UInt32BitsToSingle(u)) ? 0 : BitConverter.UInt32BitsToSingle(u) },
            LoweredKernels.Sign, Sign, SameSign, x => $"{BitConverter.SingleToUInt32Bits(x.Value):X8}");
    }

    private static readonly int[] Specials = [0, 1, -1, 2, -2, 3, 7, -7, 8, -8, int.MinValue, int.MinValue + 1, int.MaxValue, int.MaxValue - 1, 1 << 30, -(1 << 30), 46341, -46341];

    private static int NextInt(Random random) => random.Next(4) switch
    {
        0 => Specials[random.Next(Specials.Length)],
        1 => random.Next(-100, 100),
        _ => random.Next(int.MinValue, int.MaxValue),
    };

    /// <summary>
    /// x / y, x % y, int.CopySign(x, y) and a division behind <c>y != 0 &amp;&amp;</c> with a divisor per row (0, -1 and
    /// int.MinValue among them): the scalar results, the rows of lanes that throw run by the method.
    /// </summary>
    [Test]
    public void DivisorsPerRowAreTheScalarResults()
    {
        const int count = 100_003;
        using var world = new World();
        var random = new Random(7);
        var xs = new Whole[count];
        var ys = new Divisor[count];
        var entities = new Entity[count];
        world.Reserve<Whole4, Whole, Divisor>(count);
        for (int i = 0; i < count; i++)
        {
            xs[i] = new Whole { Value = NextInt(random) };
            ys[i] = new Divisor { Value = NextInt(random) };
            // no row of a lane that would throw: 0 and MinValue / -1 only in the guarded D
            if (ys[i].Value == 0 || xs[i].Value == int.MinValue && ys[i].Value == -1 || xs[i].Value == int.MinValue && ys[i].Value >= 0)
                xs[i].Value = 5;
            if (ys[i].Value == 0) ys[i].Value = 3;
            entities[i] = world.CreateEntity(new Whole4(), xs[i], ys[i]);
        }
        LoweredKernels.Divide(world);
        for (int i = 0; i < count; i++)
        {
            var want = new Whole4();
            LoweredKernels.Divide(ref want, in xs[i], in ys[i]);
            Assert.That(entities[i].Get<Whole4>(), Is.EqualTo(want), $"{xs[i].Value} and {ys[i].Value}");
        }

        // A divisor of 0 behind the guard: those lanes do not divide, and the vector loop keeps them (no throw).
        for (int i = 0; i < count; i += 3) entities[i].Set(new Divisor { Value = 0 });
        Assert.Throws<DivideByZeroException>(() => LoweredKernels.Divide(world)); // x / 0 in A throws as the method does
    }

    /// <summary>A lane that throws sends its vector's rows to the method, which throws as written; the other rows keep the vector loop.</summary>
    [Test]
    public void LanesThatThrowRunTheMethod()
    {
        using var world = new World();
        using var signs = new World();
        var entities = new Entity[1000];
        var reals = new Entity[1000];
        for (int i = 0; i < entities.Length; i++)
        {
            entities[i] = world.CreateEntity(new Whole4(), new Whole { Value = i - 500 });
            reals[i] = signs.CreateEntity(new Whole(), new Real { Value = i - 500.5f });
        }
        LoweredKernels.Throwing(world);
        LoweredKernels.Sign(signs);
        Assert.That((entities[0].Get<Whole4>(), reals[0].Get<Whole>().Value, reals[999].Get<Whole>().Value),
            Is.EqualTo((new Whole4 { A = 500, B = 500, C = 0, D = -1 }, -1, 1)));
        entities[700].Set(new Whole { Value = int.MinValue });
        Assert.Throws<OverflowException>(() => LoweredKernels.Throwing(world));
        reals[300].Set(new Real { Value = float.NaN });
        Assert.Throws<ArithmeticException>(() => LoweredKernels.Sign(signs));
    }

    /// <summary>
    /// A lane that throws among a run's last rows (every length from 1 to 40, the throwing row last): the method runs
    /// those rows and throws as written, and every row before it is the method's.
    /// </summary>
    [Test]
    public void ALaneThatThrowsInTheLastRowsRunsTheMethod([Range(1, 40)] int count)
    {
        using var world = new World();
        var entities = new Entity[count];
        world.Reserve<Whole4, Whole>(count);
        for (int i = 0; i < count; i++) entities[i] = world.CreateEntity(new Whole4(), new Whole { Value = i == count - 1 ? int.MinValue : i * 7919 - 100_000 });
        Assert.Throws<OverflowException>(() => LoweredKernels.Throwing(world));
        for (int i = 0; i < count - 1; i++)
        {
            var want = new Whole4();
            LoweredKernels.Throwing(ref want, new Whole { Value = i * 7919 - 100_000 });
            Assert.That(entities[i].Get<Whole4>(), Is.EqualTo(want), $"row {i} of {count}");
        }
        Assert.That(entities[count - 1].Get<Whole4>(), Is.EqualTo(new Whole4()), "the row that throws");
    }

    /// <summary>long lanes: division by powers of two and Math.Abs, the scalar results for random values and edges.</summary>
    [Test]
    public void LongsAreTheScalarResults()
    {
        const int count = 50_001;
        using var world = new World();
        var random = new Random(3);
        var xs = new Long1[count];
        var entities = new Entity[count];
        long[] specials = [0, 1, -1, 7, -7, 8, -8, 9, -9, long.MinValue + 1, long.MaxValue, long.MaxValue - 7, 1L << 62, -(1L << 62)];
        world.Reserve<Long4, Long1>(count);
        for (int i = 0; i < count; i++)
        {
            xs[i] = new Long1 { Value = random.Next(3) == 0 ? specials[random.Next(specials.Length)] : random.NextInt64(long.MinValue, long.MaxValue) };
            if (xs[i].Value == long.MinValue) xs[i].Value = 0;
            entities[i] = world.CreateEntity(new Long4(), xs[i]);
        }
        LoweredKernels.Longs(world);
        for (int i = 0; i < count; i++)
        {
            var want = new Long4();
            LoweredKernels.Longs(ref want, in xs[i]);
            Assert.That(entities[i].Get<Long4>(), Is.EqualTo(want), $"{xs[i].Value}");
        }
    }

    /// <summary>double lanes: x / 2^k, Pow(x, 1) and Pow(x, 0), the scalar bits for random bit patterns and edges.</summary>
    [Test]
    public void DoublesAreTheScalarBits()
    {
        const int count = 200_003;
        using var world = new World();
        var random = new Random(5);
        var xs = new Real8[count];
        var entities = new Entity[count];
        ulong[] specials = [0, 0x8000000000000000, 1, 0x7FF0000000000000, 0xFFF0000000000000, 0x7FF8000000000000, 0x7FF0000000000001,
            0xFFF8000000000001, 0x000FFFFFFFFFFFFF, 0x0010000000000000, 0x7FEFFFFFFFFFFFFF, 0x3FF0000000000000];
        world.Reserve<Real8x4, Real8>(count);
        for (int i = 0; i < count; i++)
        {
            ulong bits = random.Next(4) == 0 ? specials[random.Next(specials.Length)] : (ulong)random.NextInt64() ^ ((ulong)random.Next(2) << 63);
            xs[i] = new Real8 { Value = BitConverter.UInt64BitsToDouble(bits) };
            entities[i] = world.CreateEntity(new Real8x4(), xs[i]);
        }
        LoweredKernels.Doubles(world);
        for (int i = 0; i < count; i++)
        {
            var want = new Real8x4();
            LoweredKernels.Doubles(ref want, in xs[i]);
            var got = entities[i].Get<Real8x4>();
            Assert.That(Bits(got.A, want.A) && Bits(got.B, want.B) && Bits(got.C, want.C) && Bits(got.D, want.D), $"{BitConverter.DoubleToUInt64Bits(xs[i].Value):X16}: {got} instead of {want}");
        }
    }

    /// <summary>
    /// Math called once per lane inside the vector loop (Atan2, Asin, Sin without FloatMode.Fast, Cbrt, Pow by a value;
    /// Tanh, Exp, Atan2 and Pow of doubles): the scalar bits for random bit patterns and edges, and the call alone under
    /// SimdMode.Require.
    /// </summary>
    [Test]
    public void MathCalledPerLaneIsTheScalarBits()
    {
        const int count = 100_003;
        using var world = new World();
        var random = new Random(9);
        var xs = new Real[count];
        var ds = new Real8[count];
        var entities = new Entity[count];
        uint[] specials = [0, 0x80000000, 1, 0x7F800000, 0xFF800000, 0x7FC00000, 0x3F800000, 0xBF800000, 0x00800000, 0x7F7FFFFF];
        world.Reserve<Real4, Real, Real8x4, Real8>(count);
        for (int i = 0; i < count; i++)
        {
            uint bits = random.Next(4) == 0 ? specials[random.Next(specials.Length)] : random.Next(3) == 0 ? (uint)random.Next() : BitConverter.SingleToUInt32Bits((float)(random.NextDouble() * 8 - 4));
            xs[i] = new Real { Value = BitConverter.UInt32BitsToSingle(bits) };
            ds[i] = new Real8 { Value = random.Next(4) == 0 ? BitConverter.UInt32BitsToSingle(bits) : random.NextDouble() * 8 - 4 };
            entities[i] = world.CreateEntity(new Real4(), xs[i], new Real8x4(), ds[i]);
        }
        LoweredKernels.Angles(world);
        LoweredKernels.DoubleAngles(world);
        for (int i = 0; i < count; i++)
        {
            var want = new Real4();
            LoweredKernels.Angles(ref want, in xs[i]);
            var got = entities[i].Get<Real4>();
            Assert.That(Bits(got.A, want.A) && Bits(got.B, want.B) && Bits(got.C, want.C) && Bits(got.D, want.D), $"{BitConverter.SingleToUInt32Bits(xs[i].Value):X8}: {got} instead of {want}");
            var wanted = new Real8x4();
            LoweredKernels.DoubleAngles(ref wanted, in ds[i]);
            var gotten = entities[i].Get<Real8x4>();
            Assert.That(Bits(gotten.A, wanted.A) && Bits(gotten.B, wanted.B) && Bits(gotten.C, wanted.C) && Bits(gotten.D, wanted.D), $"{ds[i].Value}: {gotten} instead of {wanted}");
        }
        LoweredKernels.Bearing(world);
        for (int i = 0; i < count; i++)
            Assert.That(Bits(entities[i].Get<Real4>().A, MathF.Atan2(xs[i].Value, 1.5f)), $"{xs[i].Value}");
    }

    /// <summary>The per-lane bodies get their vector loops (the gate opens them); the call alone stays as written.</summary>
    [Test]
    public void ThePerLaneGateOpensHeavyBodiesOnly()
    {
        using var world = new World();
        world.AddSystem<PerLaneLoops>();
        Assert.That(world.DescribeSchedule().Groups[0].Stages.SelectMany(s => s.Systems).SelectMany(s => s.Loops).Select(l => l.Split(' ')[0] + (l.Contains(": vector") ? " vector" : " scalar")),
            Is.EquivalentTo(new[] { "Angles: vector", "Alone: scalar" }));
    }

    /// <summary>A parallel loop with a divisor per row at 1 and 4 threads: the same results as the method row by row.</summary>
    [TestCase(1)]
    [TestCase(4)]
    public void ParallelDivisionsMatchTheMethod(int threads)
    {
        const int count = 70_001;
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        var random = new Random(11);
        var xs = new Whole[count];
        var ys = new Divisor[count];
        var outs = new Whole4[count];
        var entities = new Entity[count];
        world.Reserve<Whole4, Whole, Divisor>(count);
        for (int i = 0; i < count; i++)
        {
            xs[i] = new Whole { Value = NextInt(random) | 1 };
            ys[i] = new Divisor { Value = NextInt(random) is var y && (y == 0 || y == -1) ? 9 : y };
            entities[i] = world.CreateEntity(new Whole4(), xs[i], ys[i]);
        }
        for (int round = 0; round < 3; round++)
        {
            LoweredKernels.DivideParallel(world);
            for (int i = 0; i < count; i++) LoweredKernels.DivideParallel(ref outs[i], in xs[i], in ys[i]);
        }
        for (int i = 0; i < count; i++) Assert.That(entities[i].Get<Whole4>(), Is.EqualTo(outs[i]), $"row {i} at {threads} threads");
    }

    /// <summary>The lowered loops allocate nothing once warm.</summary>
    [Test]
    public void LoweredLoopsDoNotAllocate()
    {
        using var world = new World();
        for (int i = 0; i < 1000; i++) world.CreateEntity(new Whole4(), new Whole { Value = i * 37 - 999 }, new Divisor { Value = i % 13 + 1 }, new Real { Value = i * 0.37f }, new Real4());
        void Step()
        {
            LoweredKernels.Divide(world);
            LoweredKernels.Constants(world);
            LoweredKernels.Halve(world);
            LoweredKernels.Hyperbolic(world);
        }
        Allocations.WarmUp(Step, 3);
        Assert.That(Allocations.Of(Step), Is.Zero);
    }

    private static long Ulps(float a, float b)
    {
        if (float.IsNaN(a) || float.IsNaN(b)) return float.IsNaN(a) && float.IsNaN(b) ? 0 : long.MaxValue;
        if (a == b) return 0;
        // Bits count ulps across the whole line of one sign, infinity included: float.MaxValue is 1 ulp from it, where a
        // product of three overflows that the scalar Pow still rounds down.
        if (Math.Sign(a) != Math.Sign(b) && a != 0 && b != 0) return long.MaxValue;
        return Math.Abs(BitConverter.SingleToInt32Bits(a) - (long)BitConverter.SingleToInt32Bits(b));
    }

    /// <summary>
    /// The FloatMode.Fast forms (Pow by 2, 3, 4, 0.5, -1, -2 and any other exponent; division by any constant; Tan, Log10, Sinh, Cosh, Tanh)
    /// within <paramref name="ulps"/> of the scalar functions over a sample of every float (zeros, infinities, NaN where
    /// the scalar function has them).
    /// </summary>
    [TestCaseSource(nameof(Strides))]
    public void FastFormsAreWithinUlpsOfTheScalarFunctions(uint stride)
    {
        static Real4 Powers(Real x) { var o = new Real4(); LoweredKernels.Powers(ref o, in x); return o; }
        static Real4 Curves(Real x) { var o = new Real4(); LoweredKernels.Curves(ref o, in x); return o; }
        static Real4 Hyperbolic(Real x) { var o = new Real4(); LoweredKernels.Hyperbolic(ref o, in x); return o; }
        static Real4 Exponents(Real x) { var o = new Real4(); LoweredKernels.Exponents(ref o, in x); return o; }
        static bool Near(in Real4 a, in Real4 b) => Ulps(a.A, b.A) <= 2 && Ulps(a.B, b.B) <= 2 && Ulps(a.C, b.C) <= 2 && Ulps(a.D, b.D) <= 2;
        static bool Tan(in Real4 a, in Real4 b) =>
            (Ulps(a.A, b.A) <= 4 || MathF.Abs(a.A - b.A) <= 1e-7f) && Ulps(a.B, b.B) <= 2 && Ulps(a.C, b.C) <= 2 && Ulps(a.D, b.D) <= 2;
        Func<uint, Real> real = u => new Real { Value = BitConverter.UInt32BitsToSingle(u) };
        Func<Real, string> show = x => $"{BitConverter.SingleToUInt32Bits(x.Value):X8}";
        Sweep<Real, Real4>(Samples(stride), stride, real, LoweredKernels.Powers, Powers, Near, show);
        Sweep<Real, Real4>(Samples(stride), stride, real, LoweredKernels.Curves, Curves, Tan, show);
        Sweep<Real, Real4>(Samples(stride), stride, real, LoweredKernels.Hyperbolic, Hyperbolic, Near, show);
        Sweep<Real, Real4>(Samples(stride), stride, real, LoweredKernels.Exponents, Exponents, Near, show);
    }

    /// <summary>
    /// The Vector128 forms (the rows of a cell method narrower than Vector&lt;T&gt;): the quotients exact against the
    /// scalar division over the edges and random operands of both signs (AVX's path, and the path through longs under
    /// DOTNET_EnableAVX=0), the float functions within 2 ulps of the scalar ones.
    /// </summary>
    [Test]
    public void TheVector128FormsAreTheScalarOperations()
    {
        var random = new Random(128);
        uint[] edges = [1, 2, 3, 7, 0x7FFFFFFF, 0x80000000, 0x80000001, 0xFFFFFFFF, 0xFFFFFFFE, 0xFFFFFFF9, 65536, 1000003];
        Span<uint> x = stackalloc uint[4], y = stackalloc uint[4];
        for (int round = 0; round < 200000; round++)
        {
            for (int k = 0; k < 4; k++)
            {
                x[k] = round < edges.Length * edges.Length / 4 * 4 ? edges[(round * 4 + k) / edges.Length % edges.Length] : (uint)random.NextInt64();
                y[k] = round < edges.Length * edges.Length / 4 * 4 ? edges[(round * 4 + k) % edges.Length] : (uint)random.Next(1, 1 << random.Next(1, 31)) * (random.Next(2) == 0 ? 1u : uint.MaxValue);
                if (y[k] == 0) y[k] = 1;
            }
            var xv = Vector128.Create<uint>(x);
            var yv = Vector128.Create<uint>(y);
            var unsigned = LaneMath.Quotient(xv, yv);
            var signed = LaneMath.Quotient(xv.AsInt32(), yv.AsInt32());
            for (int k = 0; k < 4; k++)
            {
                Assert.That(unsigned[k], Is.EqualTo(x[k] / y[k]), $"{x[k]} / {y[k]}");
                int a = (int)x[k], b = (int)y[k];
                if (a == int.MinValue && b == -1) continue; // the caller replaces this divisor
                Assert.That(signed[k], Is.EqualTo(a / b), $"{a} / {b}");
            }
        }
        for (int i = 0; i < 100000; i++)
        {
            uint bits = i < Edges.Length ? Edges[i] : (uint)random.NextInt64();
            float f = BitConverter.UInt32BitsToSingle(bits), small = f % 80f;
            var v = Vector128.Create(small);
            Assert.That(Ulps(LaneMath.Sinh(v)[0], MathF.Sinh(small)), Is.LessThanOrEqualTo(2), $"Sinh {small}");
            Assert.That(Ulps(LaneMath.Cosh(v)[0], MathF.Cosh(small)), Is.LessThanOrEqualTo(2), $"Cosh {small}");
            Assert.That(Ulps(LaneMath.Tanh(v)[0], MathF.Tanh(small)), Is.LessThanOrEqualTo(2), $"Tanh {small}");
            Assert.That(Ulps(LaneMath.Log10(Vector128.Create(f))[0], MathF.Log10(f)), Is.LessThanOrEqualTo(2), $"Log10 {f}");
            float b = MathF.Abs(f), e = (float)(random.NextDouble() * 40 - 20);
            if (b > 0 && float.IsFinite(b))
                Assert.That(Ulps(LaneMath.Pow(Vector128.Create(b), Vector128.Create(e))[0], MathF.Pow(b, e)), Is.LessThanOrEqualTo(1), $"Pow {b} {e}");
        }
    }
}

/// <summary>A heavy per-lane body and the call alone, in a system: the schedule names the loops they get.</summary>
[EcsSystem]
public ref struct PerLaneLoops
{
    [ForEach]
    static void Angles(ref Real4 o, in Real x)
    {
        float a = x.Value * 0.5f - 0.25f, b = x.Value * x.Value - a, c = MathF.Abs(a - b) * 0.125f + MathF.Min(a, b);
        o.A = MathF.Atan2(a, b) + c * c - MathF.Max(a, c) + a * b - b * c;
    }

#pragma warning disable EECS055 // the call alone stays as written: what this checks
    [ForEach]
    static void Alone(ref Real4 o, in Real x) => o.B = MathF.Atan2(x.Value, 1.5f);
#pragma warning restore EECS055
}
