using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>An element of a buffer of struct elements: each field one lane.</summary>
public record struct Ember2(float Heat, float Glow);

public struct Rate : IComponent { public float Value; }

/// <summary>
/// [ForEach] methods that run row by row and loop over their buffers' spans (Kernel.Loops.cs): the generator copies
/// each with its loops' vector forms. <see cref="ElementRulesByRow"/> runs the same bodies with SimdMode.Off, as
/// written; the two worlds must end alike, bit for bit.
/// </summary>
[EcsSystem]
public ref struct ElementRules
{
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
}

/// <summary>The bodies of <see cref="ElementRules"/> as written: no vector form.</summary>
[EcsSystem]
public ref struct ElementRulesByRow
{
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
}

public class ElementLoopTests
{
    private static World Make<TSystem>(int entities, int seed, Entity[] made, int threads = 1) where TSystem : struct, allows ref struct
    {
        var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        var random = new Random(seed);
        world.Reserve<DynamicBuffer<float>, Rate, DynamicBuffer<int>, DynamicBuffer<long>, DynamicBuffer<Ember2>>(entities);
        for (int n = 0; n < entities; n++)
        {
            int length = random.Next(0, 70); // empty, shorter than a vector, whole vectors and a tail
            var e = made[n] = world.CreateEntity(new DynamicBuffer<float>(), new Rate { Value = random.Next(1, 8) * 0.25f },
                new DynamicBuffer<int>(), new DynamicBuffer<long>(), new DynamicBuffer<Ember2>());
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
            }
        }
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
        Assert.That(generated, Does.Contain("MethodImplOptions.NoInlining)]\n    internal static void A_0(")    // the copy, under the accessor's name
            .And.Contain("static nint __Elements0(ref float r1, nint i, nint rows, float x1)")         // the vector form, k as its value
            .And.Contain("__Elements0(ref global::System.Runtime.InteropServices.MemoryMarshal.GetReference(__span0), 0, __span0.Length, k)")
            .And.Contain("__span0.Slice(__at0)")                                                        // the loop as written runs the rest
            .And.Contain("#line 8 \"/src/Loops.cs\"").And.Contain("#line hidden").And.Contain("#line default")
            .And.Not.Contain("static extern void A_0("), generated);
    }

    [TestCase("var s = b.AsSpan(); for (int i = 0; i < s.Length; i++) { if (s[i] < 0) break; s[i] = 1; }", "it leaves the loop early ('break'): a vector loop runs every element")]
    [TestCase("float t = 0; foreach (var x in b.AsSpan()) t += x; b.Add(t);", "it writes 't', a variable of the method: a vector loop writes only the elements")]
    [TestCase("var s = b.AsSpan(); for (int i = 0; i < s.Length; i++) s[i] = i;", "it reads its index 'i' as a value ('s[i] = i'), not only as the index of a span (s[i])")]
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
