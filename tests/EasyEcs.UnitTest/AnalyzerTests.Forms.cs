using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

// EasyEcsAnalyzer.Forms.cs: [ForEntity], [UpdateInterval] and [Consume] where they cannot apply (EECS081, EECS083,
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
    [TestCase("public static class R { [ForEntity] static void A(Entity e, ref P p, in Q q, Maybe<T> t, int n) => p.V = q.V + n; }")]
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
