using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>An element of a buffer of struct elements: each field one lane.</summary>
public record struct Ember2(float Heat, float Glow);

public struct Rate : IComponent { public float Value; }

/// <summary>What <see cref="ElementRules"/>' loops sum into variables of the method: sums, a Min, a Max, a count.</summary>
public struct Spread : IComponent { public int Sum; public long Wide; public float Low, High; public int Above; public float Total; }

/// <summary>
/// [ForEach] methods that run row by row and loop over their buffers' spans (Kernel.Loops.cs): the generator copies
/// each with its loops' vector forms. <see cref="ElementRulesByRow"/> runs the same bodies with SimdMode.Off, as
/// written; the two worlds must end alike, bit for bit.
/// </summary>
[EcsSystem]
public ref struct ElementRules
{
    [ForEach]
    static void Sums(in DynamicBuffer<float> heat, in DynamicBuffer<int> from, ref Spread spread)
    {
        int sum = 0;
        foreach (int x in from.AsReadOnlySpan()) sum += x;
        long wide = 7;
        var src = from.AsReadOnlySpan();
        for (int i = 0; i < src.Length; i++) wide -= src[i];
        float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
        int above = 0;
        foreach (float h in heat.AsReadOnlySpan())
        {
            lo = MathF.Min(lo, h);
            hi = Math.Max(h, hi);
            if (h > 1) above++;
        }
        spread = new Spread { Sum = sum, Wide = wide, Low = lo, High = hi, Above = above, Total = spread.Total };
    }

    [ForEach(FloatMode = FloatMode.Fast)]
    static void Total(in DynamicBuffer<float> heat, ref Spread spread)
    {
        float t = 0;
        foreach (float h in heat.AsReadOnlySpan()) t += h;
        spread.Total = t;
    }

    [ForEach]
    static void Cool(ref DynamicBuffer<float> heat, in Rate rate)
    {
        float k = rate.Value;
        var s = heat.AsSpan();
        for (int i = 0; i < s.Length; i++) s[i] = s[i] * k + 1f;
    }

    [ForEach]
    static void Floor(ref DynamicBuffer<float> heat, in Rate rate)
    {
        float k = rate.Value;
        foreach (ref float h in heat.AsSpan()) h = h > k ? h - k : 0f;
    }

    [ForEach]
    static void Copy(in DynamicBuffer<int> from, ref DynamicBuffer<long> to)
    {
        var src = from.AsReadOnlySpan();
        var dst = to.AsSpan();
        for (int i = 0; i < src.Length; i++) dst[i] = src[i] * 3L + 1;
    }

    [ForEach]
    static void Glow(ref DynamicBuffer<Ember2> embers)
    {
        foreach (ref var e in embers.AsSpan())
            if (e.Heat > 2) e = new Ember2(e.Heat * 0.5f, e.Glow + e.Heat);
    }

    [ForEach]
    static void Ramp(ref DynamicBuffer<float> heat)
    {
        var s = heat.AsSpan();
        for (int i = 0; i < s.Length; i++) s[i] = s[i] * 0.5f + i;
    }

    [ForEach]
    static void Stamp(ref DynamicBuffer<long> to)
    {
        var d = to.AsSpan();
        for (int i = 1; i < d.Length; i++) d[i] += i * 5;
    }

    [ForEach]
    static void Ease(Span<Rate> rates)
    {
        for (int i = 0; i < rates.Length; i++) rates[i].Value = rates[i].Value * 0.5f + 0.75f; // a batch method's loop over its rows
    }
}

/// <summary>The bodies of <see cref="ElementRules"/> as written: no vector form.</summary>
[EcsSystem]
public ref struct ElementRulesByRow
{
    [ForEach(SimdMode = SimdMode.Off)]
    static void Sums(in DynamicBuffer<float> heat, in DynamicBuffer<int> from, ref Spread spread)
    {
        int sum = 0;
        foreach (int x in from.AsReadOnlySpan()) sum += x;
        long wide = 7;
        var src = from.AsReadOnlySpan();
        for (int i = 0; i < src.Length; i++) wide -= src[i];
        float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
        int above = 0;
        foreach (float h in heat.AsReadOnlySpan())
        {
            lo = MathF.Min(lo, h);
            hi = Math.Max(h, hi);
            if (h > 1) above++;
        }
        spread = new Spread { Sum = sum, Wide = wide, Low = lo, High = hi, Above = above, Total = spread.Total };
    }

    [ForEach(SimdMode = SimdMode.Off)]
    static void Total(in DynamicBuffer<float> heat, ref Spread spread)
    {
        float t = 0;
        foreach (float h in heat.AsReadOnlySpan()) t += h;
        spread.Total = t;
    }

    [ForEach(SimdMode = SimdMode.Off)]
    static void Cool(ref DynamicBuffer<float> heat, in Rate rate)
    {
        float k = rate.Value;
        var s = heat.AsSpan();
        for (int i = 0; i < s.Length; i++) s[i] = s[i] * k + 1f;
    }

    [ForEach(SimdMode = SimdMode.Off)]
    static void Floor(ref DynamicBuffer<float> heat, in Rate rate)
    {
        float k = rate.Value;
        foreach (ref float h in heat.AsSpan()) h = h > k ? h - k : 0f;
    }

    [ForEach(SimdMode = SimdMode.Off)]
    static void Copy(in DynamicBuffer<int> from, ref DynamicBuffer<long> to)
    {
        var src = from.AsReadOnlySpan();
        var dst = to.AsSpan();
        for (int i = 0; i < src.Length; i++) dst[i] = src[i] * 3L + 1;
    }

    [ForEach(SimdMode = SimdMode.Off)]
    static void Glow(ref DynamicBuffer<Ember2> embers)
    {
        foreach (ref var e in embers.AsSpan())
            if (e.Heat > 2) e = new Ember2(e.Heat * 0.5f, e.Glow + e.Heat);
    }

    [ForEach(SimdMode = SimdMode.Off)]
    static void Ramp(ref DynamicBuffer<float> heat)
    {
        var s = heat.AsSpan();
        for (int i = 0; i < s.Length; i++) s[i] = s[i] * 0.5f + i;
    }

    [ForEach(SimdMode = SimdMode.Off)]
    static void Stamp(ref DynamicBuffer<long> to)
    {
        var d = to.AsSpan();
        for (int i = 1; i < d.Length; i++) d[i] += i * 5;
    }

    [ForEach(SimdMode = SimdMode.Off)]
    static void Ease(Span<Rate> rates)
    {
        for (int i = 0; i < rates.Length; i++) rates[i].Value = rates[i].Value * 0.5f + 0.75f; // a batch method's loop over its rows
    }
}

/// <summary>Passes of <see cref="ElementLoopTests"/> called on their own: a parallel element loop and its twin as written.</summary>
public static class ElementPasses
{
    [ForEach(Parallel = true, ChunkSize = 16)]
    internal static void CoolParallel(ref DynamicBuffer<float> heat, in Rate rate)
    {
        float k = rate.Value;
        foreach (ref float h in heat.AsSpan()) h = h * k - 0.5f;
    }

    [ForEach(SimdMode = SimdMode.Off)]
    internal static void CoolRows(ref DynamicBuffer<float> heat, in Rate rate)
    {
        float k = rate.Value;
        foreach (ref float h in heat.AsSpan()) h = h * k - 0.5f;
    }

    private static readonly float Bias = 0.25f;

    private static float Lift(float h, float k) => h * k + 0.5f;

    // the copy reaches the private field and helper through accessors; the loop inlines the helper
    [ForEach]
    internal static void Warm(ref DynamicBuffer<float> heat, in Rate rate)
    {
        float b = Bias + Lift(1f, rate.Value);
        foreach (ref float h in heat.AsSpan()) h = Lift(h, rate.Value) + b;
    }

    [ForEach(SimdMode = SimdMode.Off)]
    internal static void WarmRows(ref DynamicBuffer<float> heat, in Rate rate)
    {
        float b = Bias + Lift(1f, rate.Value);
        foreach (ref float h in heat.AsSpan()) h = Lift(h, rate.Value) + b;
    }
}

public class ElementLoopTests
{
    private static World Make<TSystem>(int entities, int seed, Entity[] made, int threads = 1) where TSystem : struct, allows ref struct
    {
        var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        var random = new Random(seed);
        world.Reserve<DynamicBuffer<float>, Rate, DynamicBuffer<int>, DynamicBuffer<long>, DynamicBuffer<Ember2>, Spread>(entities);
        for (int n = 0; n < entities; n++)
        {
            int length = random.Next(0, 70); // empty, shorter than a vector, whole vectors and a tail
            var e = made[n] = world.CreateEntity(new DynamicBuffer<float>(), new Rate { Value = random.Next(1, 8) * 0.25f },
                new DynamicBuffer<int>(), new DynamicBuffer<long>(), new DynamicBuffer<Ember2>(), new Spread());
            for (int i = 0; i < length; i++)
            {
                e.Get<DynamicBuffer<float>>().Add(random.Next(-40, 40) * 0.375f);
                e.Get<DynamicBuffer<int>>().Add(random.Next(int.MinValue, int.MaxValue));
                e.Get<DynamicBuffer<long>>().Add(0);
                e.Get<DynamicBuffer<Ember2>>().Add(new Ember2(random.Next(0, 20) * 0.5f, random.Next(-5, 5)));
            }
        }

        world.AddSystem<TSystem>();
        return world;
    }

    [Test]
    public void EveryLoopGetsAVectorForm()
    {
        using var world = new World();
        world.AddSystem<ElementRules>();
        string schedule = world.DescribeSchedule().ToString();
        foreach (var loop in world.DescribeSchedule().Groups[0].Stages[0].Systems[0].Loops)
            Assert.That(loop, Does.Contain("loop 'f").And.Contain("': vector ("), schedule);
    }

    [TestCase(1, 1)]
    [TestCase(9, 2)]
    [TestCase(200, 3)]
    public void VectorFormsComputeWhatTheLoopsCompute(int count, int seed)
    {
        var a = new Entity[count];
        var b = new Entity[count];
        using var vector = Make<ElementRules>(count, seed, a);
        using var scalar = Make<ElementRulesByRow>(count, seed, b);
        for (int round = 0; round < 4; round++)
        {
            vector.Update();
            scalar.Update();
            for (int n = 0; n < count; n++)
            {
                string at = $"round {round}, entity {n}";
                var p = a[n].Read<DynamicBuffer<float>>().AsReadOnlySpan();
                var q = b[n].Read<DynamicBuffer<float>>().AsReadOnlySpan();
                Assert.That(p.Length, Is.EqualTo(q.Length), at);
                for (int i = 0; i < p.Length; i++)
                    Assert.That(BitConverter.SingleToInt32Bits(p[i]), Is.EqualTo(BitConverter.SingleToInt32Bits(q[i])), at);
                Assert.That(a[n].Read<DynamicBuffer<long>>().AsReadOnlySpan().SequenceEqual(b[n].Read<DynamicBuffer<long>>().AsReadOnlySpan()), at);
                var x = a[n].Read<DynamicBuffer<Ember2>>().AsReadOnlySpan();
                var y = b[n].Read<DynamicBuffer<Ember2>>().AsReadOnlySpan();
                for (int i = 0; i < x.Length; i++)
                {
                    Assert.That(BitConverter.SingleToInt32Bits(x[i].Heat), Is.EqualTo(BitConverter.SingleToInt32Bits(y[i].Heat)), at);
                    Assert.That(BitConverter.SingleToInt32Bits(x[i].Glow), Is.EqualTo(BitConverter.SingleToInt32Bits(y[i].Glow)), at);
                }
                // integer sums wrap and Min / Max take any order: bit for bit; the float sum (FloatMode.Fast) adds in another order
                Assert.That(a[n].Read<Rate>().Value, Is.EqualTo(b[n].Read<Rate>().Value), at);
                var u = a[n].Read<Spread>();
                var v = b[n].Read<Spread>();
                Assert.That((u.Sum, u.Wide, u.Above), Is.EqualTo((v.Sum, v.Wide, v.Above)), at);
                Assert.That(BitConverter.SingleToInt32Bits(u.Low), Is.EqualTo(BitConverter.SingleToInt32Bits(v.Low)), at);
                Assert.That(BitConverter.SingleToInt32Bits(u.High), Is.EqualTo(BitConverter.SingleToInt32Bits(v.High)), at);
                Assert.That(u.Total, Is.EqualTo(v.Total).Within(1e-3f * (1 + Math.Abs(v.Total))), at);
            }
        }
    }

    [Test]
    public void AMethodCallingPrivateHelpersIsCopiedWithAccessorsToThem()
    {
        const int count = 200;
        var a = new Entity[count];
        var b = new Entity[count];
        using var vector = Make<ElementRules>(count, 4, a);
        using var rows = Make<ElementRulesByRow>(count, 4, b);
        for (int round = 0; round < 3; round++)
        {
            ElementPasses.Warm(vector);
            ElementPasses.WarmRows(rows);
        }

        for (int n = 0; n < count; n++)
            Assert.That(a[n].Read<DynamicBuffer<float>>().AsReadOnlySpan().ToArray(), Is.EqualTo(b[n].Read<DynamicBuffer<float>>().AsReadOnlySpan().ToArray()), $"entity {n}");
        var tree = CSharpSyntaxTree.ParseText("""
            using EasyEcs;
            public static class R
            {
                private static float Scale = 2f;
                private static float Lift(float h) => h + 1f;
                [ForEach]
                static void A(ref DynamicBuffer<float> heat)
                {
                    Scale = Lift(Scale);
                    foreach (ref float h in heat.AsSpan()) h *= 3f;
                }
            }
            """, GeneratorTests.Parse, path: "/src/R.cs");
        GeneratorTests.Driver().RunGeneratorsAndUpdateCompilation(GeneratorTests.Compile(GeneratorTests.Components).AddSyntaxTrees(tree), out var output, out _);
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty, string.Concat(output.SyntaxTrees.Where(GeneratorTests.IsCode).Select(t => t.ToString())));
        string generated = string.Concat(output.SyntaxTrees.Where(GeneratorTests.IsCode).Select(t => t.ToString()));
        Assert.That(generated, Does.Contain("A_0__Scale(owner) = A_0__Lift(owner, A_0__Scale(owner));")
            .And.Contain("UnsafeAccessorKind.StaticField, Name = \"Scale\")]").And.Contain("internal static extern ref float A_0__Scale(")
            .And.Contain("UnsafeAccessorKind.StaticMethod, Name = \"Lift\")]").And.Contain("internal static extern float A_0__Lift("), generated);
    }

    [Test]
    public void ALambdaPassLoopGetsTheVectorFormAndComputesTheLoop()
    {
        const int count = 300;
        var a = new Entity[count];
        var b = new Entity[count];
        using var vector = Make<ElementRules>(count, 9, a);
        using var rows = Make<ElementRulesByRow>(count, 9, b);
        for (int round = 0; round < 3; round++)
        {
            vector.ForEach(static (ref DynamicBuffer<float> heat, in Rate rate) =>
            {
                float k = rate.Value;
                long n = 0;
                foreach (ref float h in heat.AsSpan())
                {
                    h = h * k - 0.5f;
                    if (h > 0) n++;
                }
                heat.Add(n);
            });
            ElementPasses.CoolRows(rows);
            rows.ForEach(static (ref DynamicBuffer<float> heat) =>
            {
                long n = 0;
                foreach (float h in heat.AsReadOnlySpan()) if (h > 0) n++;
                heat.Add(n);
            });
        }

        for (int n = 0; n < count; n++)
            Assert.That(a[n].Read<DynamicBuffer<float>>().AsReadOnlySpan().ToArray(), Is.EqualTo(b[n].Read<DynamicBuffer<float>>().AsReadOnlySpan().ToArray()), $"entity {n}");
        string generated = string.Concat(GeneratorTests.Driver().RunGeneratorsAndUpdateCompilation(GeneratorTests.Compile(GeneratorTests.Components)
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText("""
                using EasyEcs;
                public static class S
                {
                    public static void Run(World w) => w.ForEach(static (ref DynamicBuffer<float> heat) => { foreach (ref float h in heat.AsSpan()) h *= 2; });
                }
                """, GeneratorTests.Parse, path: "/src/S.cs")), out var output, out _).GetRunResult().GeneratedTrees.Select(t => t.ToString()));
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
        Assert.That(generated, Does.Contain("static nint __Elements0(ref float r1, nint i, nint rows)"));
    }

    [TestCase(1)]
    [TestCase(4)]
    public void AParallelElementLoopIsBitForBitOnOneAndFourThreads(int threads)
    {
        const int count = 2_000;
        var a = new Entity[count];
        var b = new Entity[count];
        using var vector = Make<ElementRules>(count, 5, a, threads);
        using var rows = Make<ElementRulesByRow>(count, 5, b);
        for (int round = 0; round < 3; round++)
        {
            ElementPasses.CoolParallel(vector);
            ElementPasses.CoolRows(rows);
        }

        for (int n = 0; n < count; n++)
        {
            var p = a[n].Read<DynamicBuffer<float>>().AsReadOnlySpan();
            var q = b[n].Read<DynamicBuffer<float>>().AsReadOnlySpan();
            for (int i = 0; i < p.Length; i++)
                Assert.That(BitConverter.SingleToInt32Bits(p[i]), Is.EqualTo(BitConverter.SingleToInt32Bits(q[i])), $"entity {n}");
        }
    }

    [Test]
    public void ElementLoopsAllocateNothing()
    {
        var entities = new Entity[1000];
        using var world = Make<ElementRules>(entities.Length, 11, entities, 4);
        void Frame()
        {
            world.Update();
            ElementPasses.CoolParallel(world);
        }

        Allocations.WarmUp(Frame, 50);
        Assert.That(Allocations.Of(() => { for (int i = 0; i < 10; i++) Frame(); }), Is.Zero);
    }

    private const string Decay = """
        public static class R
        {
            [ForEach]
            static void A(ref DynamicBuffer<float> heat, in P p)
            {
                float k = p.V;
                foreach (ref float h in heat.AsSpan()) h = h * k + 1f;
            }
        }
        """;

    [Test]
    public void TheMethodIsCopiedWithTheLoopsVectorFormAndItsLines()
    {
        var tree = CSharpSyntaxTree.ParseText("using EasyEcs;\n" + Decay, GeneratorTests.Parse, path: "/src/Loops.cs");
        GeneratorTests.Driver().RunGeneratorsAndUpdateCompilation(GeneratorTests.Compile(GeneratorTests.Components).AddSyntaxTrees(tree), out var output, out _);
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
        string generated = string.Concat(output.SyntaxTrees.Where(GeneratorTests.IsCode).Select(t => t.ToString()));
        Assert.That(generated, Does.Contain("#nullable restore\n    internal static void A_0(")                // the copy, under the accessor's name, inlined as the method is
            .And.Contain("int __at0 = (__span0.Length) >= global::System.Numerics.Vector<float>.Count ? (int)__Elements0(") // short spans skip the call
            .And.Contain("static nint __Elements0(ref float r1, nint i, nint rows, float x1)")         // the vector form, k as its value
            .And.Contain("__Elements0(ref global::System.Runtime.InteropServices.MemoryMarshal.GetReference(__span0), 0, __span0.Length, k)")
            .And.Contain("__span0.Slice(__at0)")                                                        // the loop as written runs the rest
            .And.Contain("#line 8 \"/src/Loops.cs\"").And.Contain("#line hidden").And.Contain("#line default")
            .And.Not.Contain("static extern void A_0("), generated);
    }

    [TestCase("var s = b.AsSpan(); for (int i = 0; i < s.Length; i++) { if (s[i] < 0) break; s[i] = 1; }", "it leaves the loop early ('break'): a vector loop runs every element")]
    [TestCase("float t = 0; foreach (var x in b.AsSpan()) t += x; b.Add(t);", "it sums floats into 't' in the order of the elements, which one sum per lane does not keep: set FloatMode = FloatMode.Fast to accept a sum that is not bit for bit the loop's")]
    [TestCase("float t = 0; foreach (var x in b.AsSpan()) { t += x; x.ToString(t.ToString()); } b.Add(t);", "it writes 't', a variable of the method, other than as a sum, a Min or a Max ('t += e', 't = Math.Min(t, e)'): a vector loop writes the elements and such variables only")]
    [TestCase("int n = 0; foreach (var x in b.AsSpan()) n = n * 2 + (int)x; b.Add(n);", "it writes 'n', a variable of the method, other than as a sum, a Min or a Max ('n += e', 'n = Math.Min(n, e)'): a vector loop writes the elements and such variables only")]
    [TestCase("var s = b.AsSpan(); var t = new float[4]; for (int i = 0; i < s.Length; i++) s[i] = t[i];", "it reads its index 'i' as a value ('t[i]'), not only as the index of a span (s[i])")]
    [TestCase("var s = b.AsSpan(); for (int i = 1; i < s.Length; i++) s[i] = s[i - 1];", "it reads its index 'i' as a value ('i - 1'), not only as the index of a span (s[i])")]
    public void LoopsLeftScalarNameTheirReason(string body, string reason)
    {
        var diagnostics = GeneratorTests.Analyze([GeneratorTests.Components, $"public static class R {{ [ForEach] static void A(ref DynamicBuffer<float> b) {{ {body} }} }}"]).Result
            .Where(d => d.Id == "EECS055").ToArray();
        Assert.That(diagnostics.Select(d => d.GetMessage()), Is.EqualTo(new[] { $"This loop of [ForEach] method 'A' runs one element at a time, without a vector loop: {reason}" }));
        Assert.That(diagnostics[0].Severity, Is.EqualTo(DiagnosticSeverity.Info));
        Assert.That(diagnostics[0].Location.SourceTree!.ToString().Substring(diagnostics[0].Location.SourceSpan.Start, 3), Does.StartWith("for")); // at the loop's keyword
    }

    [TestCase("[ForEach] static void A(ref DynamicBuffer<float> b) { foreach (ref float x in b.AsSpan()) x *= 2; }")]                         // vectorized
    [TestCase("[ForEach] static void A(ref DynamicBuffer<float> b) { var s = b.AsSpan(); for (int i = 0; i < s.Length; i++) s[i] = i; }")]       // the index as a value
    [TestCase("[ForEach] static void A(ref DynamicBuffer<int> b) { int n = 0; foreach (int x in b.AsSpan()) n = System.Math.Max(n, x); b.Add(n); }")] // a Max into a variable
    [TestCase("[ForEach(SimdMode = SimdMode.Off)] static void A(ref DynamicBuffer<float> b) { foreach (ref float x in b.AsSpan()) if (x < 0) break; }")] // opted out
    [TestCase("[ForEach] static void A(ref DynamicBuffer<int> b) { int n = 0; for (int i = 0; i < 4; i++) n += i; b.Add(n); }")]                   // walks no span
    public void LoopsNotMeantForAVectorFormGetNoMessage(string members)
    {
        var ids = GeneratorTests.Analyze([GeneratorTests.Components, $"public static class R {{ {members} }}"]).Result.Select(d => d.Id);
        Assert.That(ids, Has.None.EqualTo("EECS055"));
    }

    [Test]
    public void ATargetShorterThanTheBoundThrowsAsTheLoopDoes()
    {
        using var world = new World();
        world.AddSystem<ElementRules>();
        var e = world.CreateEntity(new DynamicBuffer<float>(), new Rate { Value = 1 }, new DynamicBuffer<int>(), new DynamicBuffer<long>(),
            new DynamicBuffer<Ember2>());
        for (int i = 0; i < 40; i++) e.Get<DynamicBuffer<int>>().Add(i);
        for (int i = 0; i < 20; i++) e.Get<DynamicBuffer<long>>().Add(0);
        Assert.Throws<IndexOutOfRangeException>(() => world.Update());
        var to = e.Read<DynamicBuffer<long>>().AsReadOnlySpan();
        for (int i = 0; i < 20; i++) Assert.That(to[i], Is.EqualTo(i * 3L + 1)); // the loop as written got that far
    }
}
