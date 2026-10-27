using System;
using System.Linq;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>
/// Lambda passes: <c>world.ForEach(static (ref P p, in V v) =&gt; …)</c> and <c>ForEachParallel</c>, the values before
/// the lambda passed to it, compiled by the generator into the job a [ForEach] method gets.
/// </summary>
public class LambdaPassTests
{
    public record struct Heat(float Value) : IComponent;
    public record struct Flow(float Value) : IComponent;
    public record struct Tally(int Value) : IComponent;
    public record struct Gold(int Value) : IComponent, ITracked;

    public struct Totals : ISingletonComponent, IReduction<Totals>
    {
        public long Sum;
        public int Count;

        public static Totals Identity => default;
        public static Totals Combine(Totals left, Totals right) => new() { Sum = left.Sum + right.Sum, Count = left.Count + right.Count };
    }

    private const float Damping = 0.5f; // a private constant: written into the generated body as its value
    private static readonly float Drift = 0.25f; // a private static: the body runs through its delegate
    internal static float Bias = 1f; // reachable from the generated file

    private static World Filled(int count)
    {
        var world = new World();
        world.Reserve<Heat, Flow, Tally>(count);
        for (int i = 0; i < count; i++)
            world.CreateEntity(new Heat(i), new Flow(i % 7 - 3), new Tally(i));
        return world;
    }

    [Test]
    public void ALambdaRunsForEveryMatchingEntity()
    {
        using var world = Filled(100);
        world.CreateEntity(new Heat(1000)); // no Flow: not visited
        world.ForEach(static (ref Heat h, in Flow f) => h.Value += f.Value * Damping);
        int i = 0;
        foreach (var item in world.Read<Heat, Flow>())
        {
            Assert.That(item.Heat.Value, Is.EqualTo(i + (i % 7 - 3) * 0.5f));
            i++;
        }

        Assert.That(i, Is.EqualTo(100));
        int untouched = 0;
        foreach (var item in world.Read<Heat>()) if (item.Heat.Value == 1000) untouched++;
        Assert.That(untouched, Is.EqualTo(1));
    }

    [Test]
    public void ValuesBeforeTheLambdaAreItsParameters()
    {
        using var world = Filled(10);
        float dt = 2, scale = 3;
        world.ForEach(dt, scale, static (ref Heat h, float dt, in Flow f, float scale) => h.Value = f.Value * dt * scale + Bias);
        foreach (var item in world.Read<Heat, Flow>())
            Assert.That(item.Heat.Value, Is.EqualTo(item.Flow.Value * 6 + 1));
    }

    [Test]
    public void FiltersAreTheLambdasAttributes()
    {
        using var world = Filled(10);
        world.CreateEntity(new Tally(-1), new Dead());
        world.ForEach([Without<Dead>] static (ref Tally t) => t.Value = 7);
        foreach (var item in world.Read<Tally>())
            Assert.That(item.Tally.Value, Is.EqualTo(item.Entity.Has<Dead>() ? -1 : 7));
    }

    // A lambda finding a component of a non-unique index by key gets the index built before the pass, as a method does
    // (regression: its Lookup held none, and TryFind threw).
    [Test]
    public void ALambdaFindsByAKeyOfANonUniqueIndex()
    {
        using var world = new World();
        world.CreateEntity(new Callsign("bravo"));
        world.CreateEntity(new Callsign("bravo"));
        var seeker = world.CreateEntity(new Tally(0));
        world.ForEach(static (ref Tally t, Lookup<Callsign> signs) => t.Value = signs.Count("bravo") * 10 + (signs.TryFind("alpha", out _) ? 1 : 0));
        Assert.That(seeker.Get<Tally>().Value, Is.EqualTo(20));
    }

    [Test]
    public void AReductionLandsInItsSingleton()
    {
        using var world = Filled(1000);
        world.ForEach(static (in Tally t, ref Reduce<Totals> totals) => totals.Value = Totals.Combine(totals.Value, new Totals { Sum = t.Value, Count = 1 }));
        Assert.That(world.GetSingleton<Totals>().Sum, Is.EqualTo(999 * 1000 / 2));
        Assert.That(world.GetSingleton<Totals>().Count, Is.EqualTo(1000));
    }

    [Test]
    public void AChangeFilterLooksBackFromTheTickPassedFirst()
    {
        using var world = new World();
        var paid = new Entity[10];
        for (int i = 0; i < paid.Length; i++) paid[i] = world.CreateEntity(new Gold(i), new Tally(0));
        ulong mark = world.ChangeTick;
        world.Update(); // the next tick
        paid[3].Set(new Gold(30));
        paid[7].Set(new Gold(70));
        int bonus = 1;
        world.ForEach(mark + 1, bonus, [Changed<Gold>] static (ref Tally t, in Gold g, int bonus) => t.Value = g.Value + bonus);
        for (int i = 0; i < paid.Length; i++)
            Assert.That(paid[i].Read<Tally>().Value, Is.EqualTo(i is 3 or 7 ? i * 10 + 1 : 0));
        world.ForEach(world.ChangeTick + 1, [Changed<Gold>] static (ref Tally t) => t.Value = -1); // nothing written since
        Assert.That(paid[3].Read<Tally>().Value, Is.EqualTo(31));
    }

    [Test]
    public void ABodyNamingAPrivateMemberRunsThroughItsDelegate()
    {
        using var world = Filled(10);
#pragma warning disable EECS096 // the delegate is what this test runs
        world.ForEach(static (ref Heat h) => h.Value += Drift);
#pragma warning restore EECS096
        foreach (var item in world.Read<Heat>()) Assert.That(item.Heat.Value % 1, Is.EqualTo(0.25f));
    }

    [Test]
    public void ParallelLambdasMatchOneThread([Values(1, 4)] int threads)
    {
        using var serial = Filled(100_000);
        using var parallel = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        parallel.Reserve<Heat, Flow, Tally>(100_000);
        for (int i = 0; i < 100_000; i++) parallel.CreateEntity(new Heat(i), new Flow(i % 7 - 3), new Tally(i));
        float dt = 0.1f;
        serial.ForEach(dt, static (ref Heat h, in Flow f, float dt) => h.Value = MathF.Sqrt(MathF.Abs(h.Value)) + f.Value * dt);
        parallel.ForEachParallel(dt, static (ref Heat h, in Flow f, float dt) => h.Value = MathF.Sqrt(MathF.Abs(h.Value)) + f.Value * dt);
        var a = serial.Read<Heat>().GetEnumerator();
        foreach (var item in parallel.Read<Heat>())
        {
            Assert.That(a.MoveNext());
            Assert.That(BitConverter.SingleToInt32Bits(item.Heat.Value), Is.EqualTo(BitConverter.SingleToInt32Bits(a.Current.Heat.Value)));
        }
    }

    // ---------------- the generated code ----------------

    /// <summary>The generated files of lambda passes in <paramref name="source"/>, and the compilation's errors.</summary>
    private static (string Code, string[] Errors) Generate(string source)
    {
        GeneratorTests.Driver().RunGeneratorsAndUpdateCompilation(GeneratorTests.Compile(GeneratorTests.Components, source), out var output, out _);
        string code = string.Join("\n", output.SyntaxTrees.Where(t => t.FilePath.EndsWith(".Lambdas.g.cs")).Select(t => t.ToString()));
        return (code, output.GetDiagnostics().Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).Select(d => d.ToString()).ToArray());
    }

    [Test]
    public void ACallIsInterceptedByTheJobOfItsLambda()
    {
        var (code, errors) = Generate("""
            namespace Game;
            public static class Run
            {
                const int Step = 2;
                public static void F(World world, int bonus) => world.ForEach(bonus, [Without<Q>] static (ref P p, int bonus, in Clock c) => p.V += bonus * Step + c.Ticks);
            }
            """);
        Assert.That(errors, Is.Empty, code);
        var diagnostics = GeneratorTests.Analyze([GeneratorTests.Components, "namespace Game; public static class Run { const int Step = 2; public static void F(World world, int bonus) => world.ForEach(bonus, [Without<Q>] static (ref P p, int bonus, in Clock c) => p.V += bonus * Step + c.Ticks); }"]).Result;
        Assert.That(code, Does.Contain("[global::System.Runtime.CompilerServices.InterceptsLocation(1, \"")
            .And.Contain("internal static void Lambda0(this global::EasyEcs.World __world, int bonus, global::System.Delegate __body)")
            .And.Contain("internal static void Lambda_0(object __owner, ref global::P p, int bonus, in global::Clock c)")
            .And.Contain("p.V += bonus * ((int)(2)) + c.Ticks;") // the constant written as its value
            .And.Contain("QueryFilter.Create().Without<global::Q>()")
            .And.Contain("internal ref struct LambdaJob0"), code + string.Join("\n", diagnostics.Select(d => d.ToString())));
    }

    [Test]
    public void APlainBodyGetsAVectorLoop()
    {
        var (code, errors) = Generate("public static class Run { public static void F(World world, int n) => world.ForEach(n, static (ref P p, in Q q, int n) => p.V += q.V * n); }");
        Assert.That(errors, Is.Empty, code);
        Assert.That(code, Does.Contain("Vector512"), code);
    }

    [Test]
    public void AChangeFilterTakesItsTickFirst()
    {
        var (code, errors) = Generate("public static class Run { public static void F(World world, ulong since, int n) => world.ForEach(since, n, [Changed<T>] static (ref P p, int n) => p.V += n); }");
        Assert.That(errors, Is.Empty, code);
        Assert.That(code, Does.Contain("internal static void Lambda0(this global::EasyEcs.World __world, ulong __since, int n, global::System.Delegate __body)")
            .And.Contain("LambdaFilter0.Since(__since)").And.Contain(".Changed<global::T>(0)"), code);
    }

    // ---------------- the rules ----------------

    private static async System.Threading.Tasks.Task<Microsoft.CodeAnalysis.Diagnostic[]> Rules(string call) =>
        (await GeneratorTests.Analyze([GeneratorTests.Components, $"public static class Run {{ public static void F(World world, int n) => {call}; }}"]))
        .Where(d => d.Id is "EECS095" or "EECS096" or "EECS097").ToArray();

    private static string Show(Microsoft.CodeAnalysis.Diagnostic[] diagnostics) => string.Join("\n", diagnostics.Select(d => d.ToString()));

    [TestCase("world.ForEach(static (ref P p) => p.V++)")]
    [TestCase("world.ForEach(n, static (ref P p, in Q q, int n) => p.V += q.V * n)")]
    [TestCase("world.ForEachParallel([Without<Q>] static (ref P p, in Clock c) => p.V += c.Ticks)")]
    [TestCase("world.ForEach(9UL, n, [Changed<T>] static (ref P p, in T t, int n) => p.V += t.V * n)")]
    [TestCase("world.ForEach(1UL, n, [Removed<Q>] static (ref P p, int n) => p.V += n)")]
    public async System.Threading.Tasks.Task ACompiledLambdaPassReportsNothing(string call)
    {
        var diagnostics = await Rules(call);
        Assert.That(diagnostics, Is.Empty, Show(diagnostics));
    }

    [TestCase("world.ForEach((ref P p) => p.V += n)", "captures 'n'", "n")]                              // the fix passes n
    [TestCase("world.ForEach(static (ref P p) => p.V)", "returns a value", null)]                          // a value no statement drops
    [TestCase("world.ForEach(n, static (ref P p) => p.V++)", "which takes 0", null)]                        // a value it does not take
    [TestCase("world.ForEach(static (ref P p, int n) => p.V += n)", "a value the call does not pass", null)]
    [TestCase("world.ForEachParallel(static (ref P p, World w) => w.CreateEntity())", "in parallel", null)]
    [TestCase("world.ForEach(static async (ref P p) => { })", "async", null)]
    [TestCase("world.ForEach([Changed<T>] static (ref P p) => p.V++)", "the tick to look back from", null)] // no tick
    [TestCase("world.ForEach(n, [Changed<T>] static (ref P p, int n) => p.V += n)", "the tick to look back from", null)] // not a ulong
    public async System.Threading.Tasks.Task AnUncompiledLambdaPassIsAnError(string call, string reason, string captured)
    {
        var diagnostics = await Rules(call);
        Assert.That(diagnostics.Select(d => d.Id), Is.EqualTo(new[] { "EECS095" }), Show(diagnostics));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain(reason));
        diagnostics[0].Properties.TryGetValue(EasyEcs.Generator.EasyEcsAnalyzer.CapturedValues, out var values);
        Assert.That(values, Is.EqualTo(captured));
    }

    // ---------------- edges ----------------

    private static async System.Threading.Tasks.Task<Microsoft.CodeAnalysis.Diagnostic[]> RulesIn(string members) =>
        (await GeneratorTests.Analyze([GeneratorTests.Components, $"public class Run {{ {members} }}"]))
        .Where(d => d.Id is "EECS095" or "EECS096" or "EECS097" || d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToArray();

    // Forms a [ForEach] method takes, each compiled into its job: no error, no fallback (EECS097 may name a scalar reason).
    [TestCase("world.ForEach(static (Mut<T> t, in P p) => t.Write().V = p.V)")]                                    // Mut<T>
    [TestCase("world.ForEach(static (ref readonly P p, ref Q q) => q.V = p.V)")]                                   // ref readonly
    [TestCase("world.ForEach(static (Entity e, ref P p) => p.V = e.Id.Index)")]                                    // the entity
    [TestCase("world.ForEach(n, static (ref P p, int n) => { if (p.V > n) return; p.V += n; })")]                 // a return per row
    [TestCase("world.ForEach(static (ref P p) => { var v = p.V; p.V = v * 2; })")]                                 // var locals
    [TestCase("world.ForEach(static (ref P p) => { static int Twice(int v) => v * 2; p.V = Twice(p.V); })")]       // a local function
    [TestCase("world.ForEach(static (ref P p) => { })")]                                                           // an empty body
    [TestCase("world.ForEach(static (ref P p) => {\n#if DEBUG\n        p.V++;\n#endif\n    })")]                       // a directive
    [TestCase("world.ForEach(static (ref P p, in Q q) => p.V = q.V > 0 ? q.V : -q.V)")]                            // a ternary
    [TestCase("world.ForEach(static (ref P p, World w) => w.TrimExcess())")]                                       // the World, one thread
    [TestCase("world.ForEach([With<Q>] static (in Clock c) => { })")]                                              // no component: a [With<T>]
    public async System.Threading.Tasks.Task AnEdgeCompiles(string call)
    {
        var diagnostics = (await RulesIn($"public static void F(World world, int n) => {call};")).Where(d => d.Id != "EECS097").ToArray();
        Assert.That(diagnostics, Is.Empty, Show(diagnostics));
    }

    [Test]
    public async System.Threading.Tasks.Task ASetFilterCompiles()
    {
        var diagnostics = await RulesIn("public struct T2 : IComponent, ITracked { public int V; } " +
            "public static void F(World world) => world.ForEach(1UL, [ChangedAny<T, T2>] static (ref P p) => p.V++);");
        Assert.That(diagnostics.Where(d => d.Id != "EECS097"), Is.Empty, Show(diagnostics));
    }

    // Edges EECS095 refuses, with the reason and whether its fix applies (the values it would pass).
    [TestCase("world.ForEach(default(System.Delegate))", "no lambda written at the call", null)]
    [TestCase("world.ForEach(n, n, n, n, (ref P p, int a, int b, int c, int d) => p.V += a + b + c + d + n)", "captures 'n'", null)] // a fifth value: no fix
    [TestCase("world.ForEach((ref P p) => p.V = n++)", "captures 'n'", null)]                                     // writes what it captures: no fix
    [TestCase("world.ForEach(n, static (ref P p, ref int n) => n++)", "by ref", null)]
    [TestCase("world.ForEach(1.5f, static (ref P p, int n) => p.V += n)", "value 1 before the lambda", null)]       // of another type
    [TestCase("world.ForEach(static (ref P p, Maybe<Q> q) => p.V++)", "does not take", null)]
    [TestCase("world.ForEach([ForEach] static (ref P p) => p.V++)", "does not apply to a lambda pass", null)]
    [TestCase("world.ForEach(n, static (int n) => { })", "takes 0 components", null)]
    [TestCase("world.ForEachParallel(static (ref P p, ref Clock c) => c.Ticks += p.V)", "writes a singleton", null)]
    public async System.Threading.Tasks.Task AnEdgeIsAnError(string call, string reason, string captured)
    {
        var diagnostics = await RulesIn($"public static void F(World world, int n) => {call};");
        Assert.That(diagnostics.Select(d => d.Id), Is.EqualTo(new[] { "EECS095" }), Show(diagnostics));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain(reason).And.Not.Contain("{0}").And.Not.Contain("{1}"));
        diagnostics[0].Properties.TryGetValue(EasyEcs.Generator.EasyEcsAnalyzer.CapturedValues, out var values);
        Assert.That(values, Is.EqualTo(captured));
    }

    [Test]
    public async System.Threading.Tasks.Task ALambdaReadingThisHasNoFix()
    {
        var diagnostics = await RulesIn("int _step = 2; public void F(World world) => world.ForEach((ref P p) => p.V += _step);");
        Assert.That(diagnostics.Select(d => d.Id), Is.EqualTo(new[] { "EECS095" }), Show(diagnostics));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("'this'"));
        Assert.That(diagnostics[0].Properties.ContainsKey(EasyEcs.Generator.EasyEcsAnalyzer.CapturedValues), Is.False);
    }

    [Test]
    public async System.Threading.Tasks.Task AComponentOfATypeParameterIsAnError()
    {
        var diagnostics = await RulesIn("public static void F<TC>(World world) where TC : struct, IComponent => world.ForEach(static (ref TC c) => { });");
        Assert.That(diagnostics.Select(d => d.Id), Is.EqualTo(new[] { "EECS095" }), Show(diagnostics));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("type parameter"));
    }

    // What generated code cannot reach (EECS096), and what it can (nothing).
    [TestCase("private static class H { public static int X = 2; } public static void F(World world) => world.ForEach(static (ref P p) => p.V += H.X);", "H")]
    [TestCase("protected static int X = 2; public static void F(World world) => world.ForEach(static (ref P p) => p.V += X);", "X")]
    [TestCase("public static void F(World world) => world.ForEach(static (ref P p) => p.V += Hidden.X);", "Hidden")]       // file-local
    [TestCase("internal static int X = 2; public static void F(World world) => world.ForEach(static (ref P p) => p.V += X);", null)]
    [TestCase("public static int X() => 2; public static void F(World world) => world.ForEach(static (ref P p) => p.V += X());", null)]
    public async System.Threading.Tasks.Task AnUnreachableNameRunsThroughTheDelegate(string members, string named)
    {
        var diagnostics = (await GeneratorTests.Analyze([GeneratorTests.Components,
            $"public class Run {{ {members} }} file static class Hidden {{ public static int X = 2; }}"]))
            .Where(d => d.Id is "EECS095" or "EECS096" || d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToArray();
        if (named == null) Assert.That(diagnostics, Is.Empty, Show(diagnostics));
        else
        {
            Assert.That(diagnostics.Select(d => d.Id), Is.EqualTo(new[] { "EECS096" }), Show(diagnostics));
            Assert.That(diagnostics[0].GetMessage(), Does.Contain(named));
        }
    }

    [Test]
    public async System.Threading.Tasks.Task AStructuralBodyIsNotCalledScalar()
    {
        // A body that may change structure stays a scalar job (the method's would too, unless Kernel.Structural takes
        // it), and is not worth an Info: its reason is its purpose.
        var diagnostics = await Rules("world.ForEach(static (Entity e, ref P p) => { if (p.V < 0) e.Destroy(); })");
        Assert.That(diagnostics, Is.Empty, Show(diagnostics));
    }

    [Test]
    public void ADirectiveInTheBodyIsCopied()
    {
        var (code, errors) = Generate("public static class Run { public static void F(World world) => world.ForEach(static (ref P p) => {\n#if DEBUG\n        p.V++;\n#endif\n    }); }");
        Assert.That(errors, Is.Empty, code);
        Assert.That(code, Does.Contain("#if DEBUG"), code);
    }

    [Test]
    public async System.Threading.Tasks.Task ABodyNamingAPrivateMemberIsAWarning()
    {
        var diagnostics = (await GeneratorTests.Analyze([GeneratorTests.Components,
            "public static class Run { private static int Step = 2; public static void F(World world) => world.ForEach(static (ref P p) => p.V += Step); }"]))
            .Where(d => d.Id is "EECS095" or "EECS096" or "EECS097").ToArray();
        Assert.That(diagnostics.Select(d => d.Id), Is.EqualTo(new[] { "EECS096" }), Show(diagnostics));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("Step"));
        Assert.That(diagnostics[0].AdditionalLocations, Has.Count.EqualTo(1)); // the declaration the fix makes internal
        // A constant is written as its value, so a private one is no reason.
        Assert.That((await GeneratorTests.Analyze([GeneratorTests.Components,
            "public static class Run { private const int Step = 2; public static void F(World world) => world.ForEach(static (ref P p) => p.V += Step); }"]))
            .Where(d => d.Id is "EECS095" or "EECS096" or "EECS097"), Is.Empty);
    }

    [Test]
    public async System.Threading.Tasks.Task AScalarBodyNamesItsReason()
    {
        var diagnostics = await Rules("world.ForEach(static (ref P p) => p.V = (int)System.MathF.Atan(p.V))");
        Assert.That(diagnostics.Select(d => d.Id), Is.EqualTo(new[] { "EECS097" }), Show(diagnostics));
        Assert.That(diagnostics[0].Severity, Is.EqualTo(Microsoft.CodeAnalysis.DiagnosticSeverity.Info));
    }

    [Test]
    public void TheVectorLoopEqualsOneRowAtATime()
    {
        using var world = Filled(1003); // a tail past every vector width
        float dt = 0.1f;
        var expected = new float[1003];
        int i = 0;
        foreach (var item in world.Read<Heat, Flow>()) expected[i++] = item.Heat.Value * 0.5f + item.Flow.Value * dt;
        world.ForEach(dt, static (ref Heat h, in Flow f, float dt) => h.Value = h.Value * 0.5f + f.Value * dt);
        i = 0;
        foreach (var item in world.Read<Heat>())
            Assert.That(BitConverter.SingleToInt32Bits(item.Heat.Value), Is.EqualTo(BitConverter.SingleToInt32Bits(expected[i++])));
    }

    [Test]
    public void ALambdaPassAllocatesNothing()
    {
        using var world = Filled(1000);
        float dt = 0.5f;
        void Run() => world.ForEach(dt, static (ref Heat h, in Flow f, float dt) => h.Value += f.Value * dt);
        Run();
        Assert.That(Allocations.Of(Run), Is.Zero);
        void Parallel() => world.ForEachParallel(dt, static (ref Heat h, in Flow f, float dt) => h.Value += f.Value * dt);
        Parallel();
        Assert.That(Allocations.Of(Parallel), Is.Zero);
    }
}
