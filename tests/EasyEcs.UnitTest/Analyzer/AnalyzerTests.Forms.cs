using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

// EasyEcsAnalyzer.Forms.cs: [EntityMethod], [UpdateInterval] and [Consume] where they cannot apply (EECS081, EECS083,
// EECS085) at their edges; the hand-written forms they replace (EECS080, EECS084) are FormTests'.
public partial class AnalyzerTests
{
    [TestCase("[UpdateInterval(-3)] [EcsSystem] public ref struct S { }", "EECS083")]
    [TestCase("[UpdateInterval(2, Phase = 2)] [EcsSystem] public ref struct S<X> { }", "EECS083")]                       // a phase not below the interval
    [TestCase("[UpdateInterval(2)] public struct NotASystem : IComponent { public int V; }", "EECS083")]
    public Task FormRulesReport(string source, string id) => AssertReports(id, GeneratorTests.Components, source);

    [TestCase("[UpdateInterval(1)] [EcsSystem] public ref struct S { }")]
    [TestCase("[UpdateInterval(4, Phase = 3)] [EcsSystem] public ref struct S<X> { }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach, Consume] static void A(ref P p, in Q q) => p.V += q.V; }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach, Consume, With<T>] static void A(Entity e) { } }")]
    [TestCase("public static class R { [EntityMethod] static void A(Entity e, ref P p, in Q q, Maybe<T> t, int n) => p.V = q.V + n; }")]
    public Task FormRulesLeaveAlone(string source) => AssertClean(GeneratorTests.Components, source);
}

// EECS103: a batch method's vector loop written by hand (HandSimd.cs), each load and store idiom, the shapes of the
// samples, the demo and KernelBenchmark, and every guard that leaves a method alone.
public partial class AnalyzerTests
{
    internal const string Lanes = """
        public record struct Health(int Value) : IComponent;
        public struct Heat : IComponent { public float Value; }
        public struct Speed : IComponent { public float Value; }
        public struct Mass : IComponent { public float Value; }
        public struct Pull : ISingletonComponent { public float Value; }
        public record struct Position(float X, float Y) : IComponent;
        public record struct Velocity(float X, float Y) : IComponent;
        public record struct Clock(float Dt) : ISingletonComponent;
        public struct Burning : IComponent;
        public struct Mixed : IComponent { public float A; public int B; }
        public struct Wide : IComponent { public float A, B, C; }
        public static class Units { public static float Keep(float dt) => 1 - dt; }
        """;

    internal const string Usings = "using System; using System.Numerics; using System.Runtime.Intrinsics; using System.Runtime.InteropServices; "
                                   + "using System.Runtime.CompilerServices;\n";

    /// <summary>The batch methods EECS103 reports, one per idiom (and the FireDamage, StepBatch, StepBatch512 and demo Movement shapes).</summary>
    internal static readonly string[] HandWrittenLoops =
    [
        // FireDamageSystem.Hurt: Cast<C, T> then Cast<T, Vector<T>>, a loop by one, a scalar tail
        """
        [EcsSystem] public ref struct S
        {
            [ForEach, With<Burning>]
            static void Hurt(Span<Health> health)
            {
                var points = MemoryMarshal.Cast<Health, int>(health);
                var lanes = MemoryMarshal.Cast<int, Vector<int>>(points);
                for (int i = 0; i < lanes.Length; i++) lanes[i] -= Vector<int>.One;
                for (int i = lanes.Length * Vector<int>.Count; i < points.Length; i++) points[i]--;
            }
        }
        """,
        // the demo's MovementBatchSystem: Cast<C, Vector128<T>>, two fields per row, a broadcast singleton
        """
        [EcsSystem] public ref struct S
        {
            [ForEach]
            static void Move(Span<Position> positions, ReadOnlySpan<Velocity> velocities, in Clock clock)
            {
                var p = MemoryMarshal.Cast<Position, Vector128<float>>(positions);
                var v = MemoryMarshal.Cast<Velocity, Vector128<float>>(velocities);
                var dt = Vector128.Create(clock.Dt);
                for (int i = 0; i < p.Length; i++)
                {
                    p[i] += v[i] * dt;
                }

                if ((positions.Length & 1) != 0)
                {
                    ref var last = ref positions[^1];
                    last.X += velocities[^1].X * clock.Dt;
                    last.Y += velocities[^1].Y * clock.Dt;
                }
            }
        }
        """,
        // KernelBenchmark's StepBatch: new Vector<T>(s.Slice(i)) loads, CopyTo stores, a loop by Count, ConditionalSelect
        """
        [EcsSystem] public ref struct S
        {
            const float Drag = 0.25f, Limit = 4f;

            [ForEach]
            static void StepBatch(Span<Heat> heat, Span<Speed> speed, ReadOnlySpan<Mass> mass, in Pull pull)
            {
                var h = MemoryMarshal.Cast<Heat, float>(heat);
                var s = MemoryMarshal.Cast<Speed, float>(speed);
                var m = MemoryMarshal.Cast<Mass, float>(mass);
                int i = 0;
                var half = new Vector<float>(0.5f);
                var p = new Vector<float>(pull.Value);
                var drag = new Vector<float>(Drag);
                var limit = new Vector<float>(Limit);
                var hot = new Vector<float>(100f);
                var eighth = new Vector<float>(0.125f);
                for (; i <= h.Length - Vector<float>.Count; i += Vector<float>.Count)
                {
                    var vh = new Vector<float>(h.Slice(i));
                    var vs = new Vector<float>(s.Slice(i));
                    var vm = new Vector<float>(m.Slice(i));
                    var force = vh * half - vm * p;
                    vs = Vector.Max(Vector.Min(vs + force - drag, limit), -limit);
                    vh = Vector.ConditionalSelect(Vector.GreaterThan<float>(vh, hot), hot, vh + vs * eighth);
                    vh.CopyTo(h.Slice(i));
                    vs.CopyTo(s.Slice(i));
                }
                for (; i < h.Length; i++) Step(ref heat[i], ref speed[i], in mass[i], in pull);
            }

            static void Step(ref Heat heat, ref Speed speed, in Mass mass, in Pull pull) { }
        }
        """,
        // KernelBenchmark's StepBatch512: ref locals, LoadUnsafe / StoreUnsafe, the loop inside IsHardwareAccelerated
        """
        [EcsSystem] public ref struct S
        {
            const float Drag = 0.25f, Limit = 4f;

            [ForEach]
            static void StepBatch512(Span<Heat> heat, Span<Speed> speed, ReadOnlySpan<Mass> mass, in Pull pull)
            {
                ref float h = ref Unsafe.As<Heat, float>(ref MemoryMarshal.GetReference(heat));
                ref float s = ref Unsafe.As<Speed, float>(ref MemoryMarshal.GetReference(speed));
                ref float m = ref Unsafe.As<Mass, float>(ref MemoryMarshal.GetReference(mass));
                nint i = 0, n = heat.Length;
                if (Vector512.IsHardwareAccelerated)
                {
                    var half = Vector512.Create(0.5f);
                    var p = Vector512.Create(pull.Value);
                    var drag = Vector512.Create(Drag);
                    var limit = Vector512.Create(Limit);
                    var hot = Vector512.Create(100f);
                    var eighth = Vector512.Create(0.125f);
                    for (; i <= n - Vector512<float>.Count; i += Vector512<float>.Count)
                    {
                        var vh = Vector512.LoadUnsafe(ref h, (nuint)i);
                        var vs = Vector512.LoadUnsafe(ref s, (nuint)i);
                        var vm = Vector512.LoadUnsafe(ref m, (nuint)i);
                        var force = vh * half - vm * p;
                        vs = Vector512.Max(Vector512.Min(vs + force - drag, limit), -limit);
                        vh = Vector512.ConditionalSelect(Vector512.GreaterThan(vh, hot), hot, vh + vs * eighth);
                        vh.StoreUnsafe(ref h, (nuint)i);
                        vs.StoreUnsafe(ref s, (nuint)i);
                    }
                }
                for (; i < n; i++) heat[(int)i].Value += speed[(int)i].Value * pull.Value;
            }
        }
        """,
        // VectorN.Create(s.Slice(i, Count)) loads, two fields per row through flat lanes (KernelBenchmark's Move2Cast)
        """
        [EcsSystem] public ref struct S
        {
            [ForEach]
            static void Move(Span<Position> positions, ReadOnlySpan<Velocity> velocities, in Clock clock)
            {
                var p = MemoryMarshal.Cast<Position, float>(positions);
                var v = MemoryMarshal.Cast<Velocity, float>(velocities);
                var dt = Vector256.Create(clock.Dt);
                int i = 0;
                for (; i + Vector256<float>.Count <= p.Length; i += Vector256<float>.Count)
                    (Vector256.Create(p.Slice(i, Vector256<float>.Count)) + Vector256.Create(v.Slice(i, Vector256<float>.Count)) * dt).CopyTo(p.Slice(i));
                for (; i < p.Length; i++) p[i] += v[i] * clock.Dt;
            }
        }
        """,
    ];

    [TestCaseSource(nameof(HandWrittenLoops))]
    public async Task HandWrittenVectorLoopReports(string source)
    {
        await AssertReports("EECS103", Lanes, Usings + source);
        // One mistake, one diagnostic: no scalar-loop message or generic-call warning on the same method.
        Assert.That(await Count("EECS055", Lanes, Usings + source) + await Count("EECS033", Lanes, Usings + source), Is.Zero);
        Assert.That(await Count("EECS103", Lanes, Usings + source), Is.EqualTo(1));
    }

    [Test]
    public async Task HandWrittenVectorLoopNamesTheMethodForOneRow()
    {
        var found = (await GeneratorTests.Analyze([Lanes, Usings + HandWrittenLoops[0]])).Single(d => d.Id == "EECS103");
        Assert.That(found.GetMessage(), Does.EndWith("Hurt(ref Health health) { health.Value -= 1; }"));
        Assert.That(found.Severity, Is.EqualTo(Microsoft.CodeAnalysis.DiagnosticSeverity.Warning));
    }

    private const string Hurt = "[EcsSystem] public ref struct S { [ForEach{0}] {1} static void Hurt(Span<Health> health{2}) { var lanes = MemoryMarshal.Cast<Health, Vector<int>>(health); "
                                + "{3} for (int i = 0; i < lanes.Length; i{4}) lanes[i] {5}; } }";

    private static string HurtWith(string attribute = "", string modifiers = "", string parameter = "", string prelude = "", string step = "++", string body = "-= Vector<int>.One") =>
        Usings + Hurt.Replace("{0}", attribute).Replace("{1}", modifiers).Replace("{2}", parameter).Replace("{3}", prelude).Replace("{4}", step).Replace("{5}", body);

    [Test]
    public Task HandWrittenVectorLoopReportsTheShortForm() => AssertReports("EECS103", Lanes, HurtWith());

    [TestCase("(SimdMode = SimdMode.Off)", "", "", "", "++", "-= Vector<int>.One")]                                       // kept scalar on purpose
    [TestCase("", "[System.CodeDom.Compiler.GeneratedCode(\"EasyEcs.Generator\", \"1\")]", "", "", "++", "-= Vector<int>.One")] // generated
    [TestCase("", "", ", EntitySpan entities", "", "++", "-= Vector<int>.One")]                                         // the entities: no row form
    [TestCase("", "", "", "", " += 2", "-= Vector<int>.One")]                                                           // a step neither 1 nor Count
    [TestCase("", "", "", "", "++", "= new Vector<int>(Vector.Dot(lanes[i], lanes[i]))")]                                // a dot product
    [TestCase("", "", "", "", "++", "= new Vector<int>(Vector.Sum(lanes[i]))")]                                         // a horizontal sum
    [TestCase("", "", "", "", "++", "= lanes[i + 1 < lanes.Length ? i + 1 : i]")]                                       // a load at another offset
    [TestCase("", "", "", "", "++", "*= Vector<int>.Indices")]                                                          // not a broadcast
    [TestCase("", "", "", "var two = new Vector<int>(Units.Keep(2) > 0 ? 2 : 1);", "++", "*= two")]                    // a call into user code
    [TestCase("", "", "", "var sum = Vector<int>.Zero;", "++", "+= sum += lanes[i]")]                                   // an accumulator across vectors
    [TestCase("", "", "", "", "++", "/= new Vector<int>(3)")]                                                           // integer division
    [TestCase("", "", "", "", "++", "-= new Vector<int>(i)")]                                                           // the index as a value
    public Task HandWrittenVectorLoopGuards(string attribute, string modifiers, string parameter, string prelude, string step, string body) =>
        AssertNoHandWrittenLoop(HurtWith(attribute, modifiers, parameter, prelude, step, body));

    [TestCase("""
        [EcsSystem] public ref struct S { [ForEach] static void Slow(Span<Velocity> velocities, in Clock clock)
        {
            float keep = Units.Keep(clock.Dt);                       // the demo's DragBatchSystem: a user call per row in the plain form
            var v = MemoryMarshal.Cast<Velocity, Vector128<float>>(velocities);
            for (int i = 0; i < v.Length; i++) v[i] *= keep;
        } }
        """)]
    [TestCase("""
        [EcsSystem] public ref struct S { [ForEach] static void A(Span<Mixed> mixed)
        {
            var f = MemoryMarshal.Cast<Mixed, Vector<float>>(mixed);   // two field types in one stream
            for (int i = 0; i < f.Length; i++) f[i] *= 2f;
        } }
        """)]
    [TestCase("""
        [EcsSystem] public ref struct S { [ForEach] static void A(Span<Wide> wide)
        {
            var f = MemoryMarshal.Cast<Wide, Vector<float>>(wide);     // three fields: a vector is no whole number of rows
            for (int i = 0; i < f.Length; i++) f[i] *= 2f;
        } }
        """)]
    [TestCase("""
        [EcsSystem] public ref struct S { [ForEach] static void A(Span<Heat> heat, ReadOnlySpan<Position> positions)
        {
            var h = MemoryMarshal.Cast<Heat, Vector<float>>(heat);     // rows of one field next to rows of two
            var p = MemoryMarshal.Cast<Position, Vector<float>>(positions);
            for (int i = 0; i < h.Length; i++) h[i] += p[i];
        } }
        """)]
    [TestCase("""
        [EcsSystem] public ref struct S { [ForEach] static void A(Span<Heat> heat)
        {
            var h = MemoryMarshal.Cast<Heat, Vector128<float>>(heat);
            for (int i = 0; i < h.Length; i++)
                h[i] = System.Runtime.Intrinsics.X86.Sse.Add(h[i], h[i]);   // an intrinsic of one instruction set
        } }
        """)]
    [TestCase("""
        [EcsSystem] public ref struct S { [ForEach] static void A(Span<Heat> heat)
        {
            var h = MemoryMarshal.Cast<Heat, Vector128<float>>(heat);
            for (int i = 0; i < h.Length; i++) h[i] = Vector128.Shuffle(h[i], Vector128.Create(3, 2, 1, 0)); // a shuffle
        } }
        """)]
    [TestCase("""
        [EcsSystem] public ref struct S { [ForEach] static void A(Span<Heat> heat)
        {
            var h = MemoryMarshal.Cast<Heat, float>(heat);              // a scalar loop: no vector code at all
            for (int i = 0; i < h.Length; i++) h[i] *= 2f;
            var unused = Vector<float>.Count;
        } }
        """)]
    [TestCase("""
        public static class NotAPass { public static void A(Span<Heat> heat)
        {
            var h = MemoryMarshal.Cast<Heat, Vector<float>>(heat);     // not a [ForEach] method
            for (int i = 0; i < h.Length; i++) h[i] *= 2f;
        } }
        """)]
    [TestCase("""
        [EcsSystem] public ref struct S { [ForEach] static void A(Span<Heat> heat, Span<Speed> speed)
        {
            var h = MemoryMarshal.Cast<Heat, Vector<float>>(heat);     // a span the loop does not read
            for (int i = 0; i < h.Length; i++) h[i] *= 2f;
            speed[0].Value = 1;
        } }
        """)]
    [TestCase("""
        [EcsSystem] public ref struct S { static float total; [ForEach] static void A(Span<Heat> heat)
        {
            var h = MemoryMarshal.Cast<Heat, Vector<float>>(heat);
            for (int i = 0; i < h.Length; i++) h[i] *= 2f;
            total += heat.Length;                                       // a tail that writes shared state
        } }
        """)]
    public Task HandWrittenVectorLoopLeavesAlone(string source) => AssertNoHandWrittenLoop(Usings + source);

    private static async Task AssertNoHandWrittenLoop(string source)
    {
        Assert.That((await Diagnose(Lanes, source)).Where(id => id.StartsWith("CS")), Is.Empty, "the case compiles");
        Assert.That(await Count("EECS103", Lanes, source), Is.Zero);
    }
}

// EECS103 v3: a vector loop written by hand over spans of lanes inside a [ForEach] method (HandSimd.Loops), replaced by
// the plain loop over the elements the generator vectorizes; the guards that leave a loop alone.
public partial class AnalyzerTests
{
    internal const string Buffers = """
        public struct Fade : IComponent { public float Value; }
        public struct Gain : IComponent { public float Value; }
        public struct Level { public float Value; }
        """;

    /// <summary>The loops EECS103 reports: a Cast to vectors by one (the buffer decay), loads and stores by Count.</summary>
    internal static readonly string[] HandWrittenElementLoops =
    [
        """
        [EcsSystem] public ref struct S
        {
            [ForEach]
            static void Decay(ref DynamicBuffer<float> heat, in Fade fade)
            {
                float k = fade.Value;
                var s = heat.AsSpan();
                int i = 0;
                if (Vector.IsHardwareAccelerated)
                {
                    var vs = MemoryMarshal.Cast<float, Vector<float>>(s);
                    var vk = new Vector<float>(k);
                    var add = new Vector<float>(0.25f);
                    for (int v = 0; v < vs.Length; v++) vs[v] = vs[v] * vk + add;
                    i = vs.Length * Vector<float>.Count;
                }
                for (; i < s.Length; i++) s[i] = s[i] * k + 0.25f;
            }
        }
        """,
        """
        [EcsSystem] public ref struct S
        {
            [ForEach]
            static void Scale(in DynamicBuffer<Level> from, ref DynamicBuffer<float> to, in Gain gain)
            {
                var src = MemoryMarshal.Cast<Level, float>(from.AsReadOnlySpan());
                var dst = to.AsSpan();
                int i = 0;
                for (; i <= src.Length - Vector<float>.Count; i += Vector<float>.Count)
                    (new Vector<float>(src.Slice(i)) * gain.Value + Vector<float>.One).CopyTo(dst.Slice(i));
                for (; i < src.Length; i++) dst[i] = src[i] * gain.Value + 1f;
            }
        }
        """,
    ];

    [TestCaseSource(nameof(HandWrittenElementLoops))]
    public async Task HandWrittenElementLoopReports(string source)
    {
        await AssertReports("EECS103", Buffers, Usings + source);
        Assert.That(await Count("EECS103", Buffers, Usings + source), Is.EqualTo(1));
    }

    [Test]
    public async Task HandWrittenElementLoopNamesThePlainLoop()
    {
        var found = (await GeneratorTests.Analyze([Buffers, Usings + HandWrittenElementLoops[0]])).Single(d => d.Id == "EECS103");
        Assert.That(found.GetMessage(), Does.EndWith("float vk = k; float add = 0.25f; for (int i = 0; i < s.Length; i++) s[i] = s[i] * vk + add;"));
    }

    // Left alone: no vector loop of the generator's (SimdMode.Off), lanes C# promotes (bytes), no scalar loop for the
    // elements left, a sum across vectors (another order than element by element).
    [TestCase("[ForEach(SimdMode = SimdMode.Off)]", "float", "for (int v = 0; v < vs.Length; v++) vs[v] = vs[v] * vk;", "for (; i < s.Length; i++) s[i] = s[i] * k;")]
    [TestCase("[ForEach]", "byte", "for (int v = 0; v < vs.Length; v++) vs[v] = vs[v] + vs[v];", "for (; i < s.Length; i++) s[i] = (byte)(s[i] + s[i]);")]
    [TestCase("[ForEach]", "float", "for (int v = 0; v < vs.Length; v++) vs[v] = vs[v] * vk;", "")]
    [TestCase("[ForEach]", "float", "var sum = Vector<float>.Zero; for (int v = 0; v < vs.Length; v++) sum += vs[v];", "for (; i < s.Length; i++) s[i] = s[i] * k;")]
    public async Task HandWrittenElementLoopLeavesAlone(string attribute, string lane, string loop, string rest)
    {
        string source = Usings + "[EcsSystem] public ref struct S { " + attribute + $" static void Decay(ref DynamicBuffer<{lane}> heat, in Fade fade) {{ float k = fade.Value; var s = heat.AsSpan(); int i = 0; "
                        + $"if (Vector.IsHardwareAccelerated) {{ var vs = MemoryMarshal.Cast<{lane}, Vector<{lane}>>(s); var vk = new Vector<{lane}>(({lane})k); {loop} i = vs.Length * Vector<{lane}>.Count; }} {rest} }} }}";
        Assert.That((await Diagnose(Buffers, source)).Where(id => id.StartsWith("CS")), Is.Empty, "the case compiles");
        Assert.That(await Count("EECS103", Buffers, source), Is.Zero);
    }

    /// <summary>A cell of one float and the buffer a stencil writes, beside the tiles of <see cref="TileParts"/> (G: 4 by 4).</summary>
    internal const string Stencils = """
        public record struct Warm(float T);
        public record struct NW(float T);
        """;

    /// <summary>
    /// The stencils EECS103 v2 reports: a blur of one CopyTo at a literal width, a wave with vector locals stepping by
    /// Count, a ground's blur at its tile's literal width, and a ground stepped by ref, its columns walked with Store.
    /// </summary>
    internal static readonly string[] HandWrittenStencils =
    [
        """
        [EcsSystem] public ref struct S
        {
            [ForEach]
            static void Blur(Tile<Warm, G> warm, Mut<DynamicBuffer<NW>> next)
            {
                warm.ExchangeHalo();
                var into = MemoryMarshal.Cast<NW, float>(next.Write().AsSpan());
                foreach (var row in warm.Rows())
                {
                    var c = warm.At(row);
                    for (int x = 0; x < 4; x += 4)
                        ((c.Load128<float>(x - 1) + c.Load128<float>(x) + c.Load128<float>(x + 1, 0, 1)) * (1f / 3)).CopyTo(into.Slice(row.Index + x));
                }
            }
        }
        """,
        """
        [EcsSystem] public ref struct S
        {
            [ForEach]
            static void Wave(Tile<Warm, G> warm, Mut<DynamicBuffer<NW>> next)
            {
                warm.ExchangeHalo();
                var into = MemoryMarshal.Cast<NW, float>(next.Write().AsSpan());
                foreach (var row in warm.Rows())
                {
                    var c = warm.At(row);
                    for (int x = 0; x < 4; x += Vector128<float>.Count)
                    {
                        var here = c.Load128<float>(x);
                        var sum = c.Load128<float>(x - 1) + c.Load128<float>(x + 1) - 2f * here;
                        (here + sum * 0.25f).CopyTo(into.Slice(row.Index + x));
                    }
                }
            }
        }
        """,
        """
        [EcsSystem] public ref struct S
        {
            [ForEach]
            static void Blur(Tile<Warm, Ground<G>> warm, Mut<DynamicBuffer<NW>> next)
            {
                warm.ExchangeHalo();
                var into = MemoryMarshal.Cast<NW, float>(next.Write().AsSpan());
                foreach (var row in warm.Rows())
                {
                    var c = warm.At(row);
                    for (int x = 0; x < 4; x += 4)
                        ((c.Load128<float>(x, 0, -1) + c.Load128<float>(x) + c.Load128<float>(x, 0, 1)) * (1f / 3)).CopyTo(into.Slice(row.Index + x));
                }
            }
        }
        """,
        """
        [EcsSystem] public ref struct S
        {
            [ForEach]
            static void Spread(ref Tile<Warm, Ground<G>> warm)
            {
                warm.ExchangeHalo();
                foreach (var row in warm.Rows())
                {
                    var into = warm.Write(row);
                    foreach (var col in warm.At(row).Columns(4))
                    {
                        var here = col.Load128<float>();
                        col.Store(here + (col.Load128<float>(-1) + col.Load128<float>(1) - 2f * here) * 0.25f, into);
                    }
                }
            }
        }
        """,
    ];

    [TestCaseSource(nameof(HandWrittenStencils))]
    public async Task HandWrittenStencilReports(string source)
    {
        await AssertReports("EECS103", GeneratorTests.Components, TileParts, Stencils, Usings + source);
        Assert.That(await Count("EECS103", GeneratorTests.Components, TileParts, Stencils, Usings + source), Is.EqualTo(1));
    }

    [Test]
    public async Task HandWrittenStencilNamesTheCellMethod()
    {
        var found = (await GeneratorTests.Analyze([GeneratorTests.Components, TileParts, Stencils, Usings + HandWrittenStencils[1]])).Single(d => d.Id == "EECS103");
        Assert.That(found.GetMessage(), Does.EndWith(
            "Wave(Cell<Warm, G> warm, out NW next) { float here = warm.Value.T; float sum = warm[-1, 0, 0].T + warm[1, 0, 0].T - 2f * here; next = new NW(here + sum * 0.25f); }"));
    }

    // Left alone: no vector loop of the generator's (SimdMode.Off), a range of rows (ExchangeHalo(from, to)), a comparison, a
    // load at a column that is not x plus a constant, a row of half the tile's width.
    [TestCase("[ForEach(SimdMode = SimdMode.Off)]", "warm.ExchangeHalo();", "c.Load128<float>(x - 1) + c.Load128<float>(x + 1)", "x < 4")]
    [TestCase("[ForEach]", "warm.ExchangeHalo(0, 1);", "c.Load128<float>(x - 1) + c.Load128<float>(x + 1)", "x < 4")]
    [TestCase("[ForEach]", "warm.ExchangeHalo();", "Vector128.Max(c.Load128<float>(x - 1), c.Load128<float>(x + 1))", "x < 4")]
    [TestCase("[ForEach]", "warm.ExchangeHalo();", "c.Load128<float>(0) + c.Load128<float>(x + 1)", "x < 4")]
    [TestCase("[ForEach]", "warm.ExchangeHalo();", "c.Load128<float>(x - 1) + c.Load128<float>(x + 1)", "x < 2")]
    public async Task HandWrittenStencilLeavesAlone(string attribute, string halo, string value, string bound)
    {
        string source = Usings + "[EcsSystem] public ref struct S { " + attribute + " static void Blur(Tile<Warm, G> warm, Mut<DynamicBuffer<NW>> next) { " + halo
                        + " var into = MemoryMarshal.Cast<NW, float>(next.Write().AsSpan()); foreach (var row in warm.Rows()) { var c = warm.At(row); "
                        + $"for (int x = 0; {bound}; x += 4) ({value}).CopyTo(into.Slice(row.Index + x)); }} }} }}";
        Assert.That((await Diagnose(GeneratorTests.Components, TileParts, Stencils, source)).Where(id => id.StartsWith("CS")), Is.Empty, "the case compiles");
        Assert.That(await Count("EECS103", GeneratorTests.Components, TileParts, Stencils, source), Is.Zero);
    }

    // A stepped walk of columns left alone: no vector loop of the generator's, [GaussSeidel] (sweeps in place), a value
    // that is not +0 on the blank rows the step leaves zero (a constant term, a negation, a negative factor: -0), a step
    // of other lanes than the loads', a store into another row.
    [TestCase("[ForEach(SimdMode = SimdMode.Off)]", "col.Load128<float>(-1) + col.Load128<float>(1)", "Columns(4)", "into")]
    [TestCase("[ForEach, GaussSeidel]", "col.Load128<float>(-1) + col.Load128<float>(1)", "Columns(4)", "into")]
    [TestCase("[ForEach]", "col.Load128<float>(-1) + col.Load128<float>(1) + Vector128.Create(1f)", "Columns(4)", "into")]
    [TestCase("[ForEach]", "-col.Load128<float>(-1) + col.Load128<float>(1)", "Columns(4)", "into")]
    [TestCase("[ForEach]", "(col.Load128<float>(-1) + col.Load128<float>(1)) * -0.5f", "Columns(4)", "into")]
    [TestCase("[ForEach]", "col.Load128<float>(-1) + col.Load128<float>(1)", "Columns(2)", "into")]
    [TestCase("[ForEach]", "col.Load128<float>(-1) + col.Load128<float>(1)", "Columns(4)", "warm.Write(new TileRow<Ground<G>>(0, 0))")]
    public async Task HandWrittenStencilStepLeavesAlone(string attribute, string value, string columns, string into)
    {
        string source = Usings + "[EcsSystem] public ref struct S { " + attribute + " static void Spread(ref Tile<Warm, Ground<G>> warm) { warm.ExchangeHalo(); "
                        + $"foreach (var row in warm.Rows()) {{ var into = warm.Write(row); foreach (var col in warm.At(row).{columns}) col.Store({value}, {into}); }} }} }}";
        Assert.That((await Diagnose(GeneratorTests.Components, TileParts, Stencils, source)).Where(id => id.StartsWith("CS")), Is.Empty, "the case compiles");
        Assert.That(await Count("EECS103", GeneratorTests.Components, TileParts, Stencils, source), Is.Zero);
    }
}
