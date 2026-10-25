using System.Threading.Tasks;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

// EasyEcsAnalyzer.Systems.cs: systems as declarations (EECS007, EECS008, EECS074-EECS077, EECS090), on generic,
// nested and partial systems, lambdas in a system's code, and the forms each rule leaves alone.
public partial class AnalyzerTests
{
    // EECS007: mutable static state, in a generic, nested or partial system, an auto-property with an initializer.
    [TestCase("[EcsSystem] public ref struct S<X> { static int _n; }", "EECS007")]
    [TestCase("public static class Outer { [EcsSystem] public ref struct S { static int _n; } }", "EECS007")]
    [TestCase("[EcsSystem] public ref partial struct S { } public ref partial struct S { static int _n; }", "EECS007")]
    [TestCase("[EcsSystem] public ref struct S { public static int N { get; set; } = 3; }", "EECS007")]
    // EECS008: another system's state or code, from a lambda of a hook, through a generic system, a readonly field.
    [TestCase("[EcsSystem] public ref struct A { public static int Go() => 1; } [EcsSystem] public ref struct S { static void OnUpdate() { System.Func<int> f = static () => A.Go(); f(); } }", "EECS008")]
    [TestCase("[EcsSystem] public ref struct G<X> { public static void Go() { } } [EcsSystem] public ref struct S { static void OnUpdate() => G<int>.Go(); }", "EECS008")]
    [TestCase("[EcsSystem] public ref struct A { public static readonly int[] Table = [1]; } [EcsSystem] public ref struct S { static void OnUpdate(World w) => w.Clock.Ticks = A.Table[0]; }", "EECS008")]
    // EECS074: a struct that is no ref struct (the attribute admits structs only).
    [TestCase("[EcsSystem] public record struct S;", "EECS074")]
    [TestCase("[EcsSystem] public readonly struct S { }", "EECS074")]
    // EECS075: instance members of a generic system, an instance event.
    [TestCase("[EcsSystem] public ref struct S<X> { void M() { } }", "EECS075")]
    [TestCase("[EcsSystem] public ref struct S { public event System.Action E; }", "EECS075")]
    // EECS076: the hooks' signatures.
    [TestCase("[EcsSystem] public ref struct S { static void OnCreate(in World w) { } }", "EECS076")]
    [TestCase("[EcsSystem] public ref struct S { static void OnDestroy(World w, int n) { } }", "EECS076")]
    [TestCase("[EcsSystem] public ref struct S { static System.Threading.Tasks.Task OnUpdate() => System.Threading.Tasks.Task.CompletedTask; }", "EECS076")]
    [TestCase("[EcsSystem] public ref struct S { static void OnUpdate(SystemHandle a, SystemHandle b) { } }", "EECS076")]
    // EECS090: names one or two edits from a hook, aliases of other engines, any case.
    [TestCase("[EcsSystem] public ref struct S { static void OnCreated(World w) { } }", "EECS090")]
    [TestCase("[EcsSystem] public ref struct S { static void onupdate(SystemHandle s) { } }", "EECS090")]
    [TestCase("[EcsSystem] public ref struct S { static void Initialize() { } }", "EECS090")]
    [TestCase("[EcsSystem] public ref struct S { static void Dispose(World w) { } }", "EECS090")]
    [TestCase("[EcsSystem] public ref struct S<X> { static void Tick(World w) { } }", "EECS090")]
    // EECS077: every API that names a system.
    [TestCase("public struct NotOne; public static class U { public static void F(World w) => w.RemoveSystem<NotOne>(); }", "EECS077")]
    [TestCase("public struct NotOne; public static class U { public static void F(World w) => w.System<NotOne>().Enabled = false; }", "EECS077")]
    [TestCase("public struct NotOne; public static class U { public static bool F(World w) => w.TryGetSystem<NotOne>(out _); }", "EECS077")]
    [TestCase("public struct NotOne; [UpdateBefore<NotOne>] [EcsSystem] public ref struct S { }", "EECS077")]
    [TestCase("public ref struct NotMarked { } public static class U { public static void F(World w) => w.AddSystem<NotMarked>(); }", "EECS077")]
    public Task SystemRulesReport(string source, string id) => AssertReports(id, GeneratorTests.Components, source);

    [TestCase("[EcsSystem] public ref struct S { static readonly System.Collections.Generic.List<int> Scratch = new(); const int Max = 3; public static int Limit { get; } = 4; public static int Twice => Max * 2; }")] // readonly, const, get-only, computed
    [TestCase("[EcsSystem] public ref struct S { public struct Inner { public int V; } public static class Help { public static int N; } }")] // nested types are no members of the system
    [TestCase("[EcsSystem] public ref struct A { public const int Max = 3; } [EcsSystem] public ref struct S { static void OnUpdate(World w) => w.Clock.Ticks = A.Max; }")] // a constant: a value
    [TestCase("[EcsSystem] public ref struct G<X> { static void Go() { } static void OnUpdate() => G<int>.Go(); }")] // its own generic definition
    [TestCase("[EcsSystem] public ref struct A { } public static class U { public static int F(World w) => w.TryGetSystem<A>(out var a) ? 1 : 0; }")] // outside a system
    [TestCase("[EcsSystem] public readonly ref struct S { [ForEach] static void A(ref P p) => p.V++; }")]
    [TestCase("[EcsSystem] public ref struct S { static void OnUpdate(SystemHandle s, World w) { } static void OnCreate(World w) { } static void OnDestroy(SystemHandle s) { } }")] // either order, either one
    [TestCase("[EcsSystem] public ref struct S { static void OnUpdating(World w) { } static void Execute(int n) { } static void Init<X>() { } }")] // three edits, no hook's parameters, generic
    [TestCase("[EcsSystem] public ref struct S { static void OnUpdate() => Step(); static void Step() { } }")] // called: a helper
    [TestCase("[EcsSystem] public ref struct S { void Update() { } }", "EECS075")]                         // an instance method: EECS075 alone
    [TestCase("[EcsSystem] public ref struct G<X> { } public static class U { public static void F(World w) { w.AddSystem<G<int>>(); w.RemoveSystem<G<int>>(); } }")] // a closed generic system
    public async Task SystemRulesLeaveAlone(string source, string only = null)
    {
        if (only == null) await AssertClean(GeneratorTests.Components, source);
        else await AssertReports(only, GeneratorTests.Components, source);
    }

    // One report per mistake: each field of a partial system once, a hook's problem once.
    [TestCase("[EcsSystem] public ref partial struct S { static int _a; } public ref partial struct S { static int _b; }", "EECS007", 2)]
    [TestCase("[EcsSystem] public ref struct S { static int OnUpdate(World w) => 0; }", "EECS076", 1)]
    public async Task SystemRulesReportEachMistakeOnce(string source, string id, int count) =>
        Assert.That(await Count(id, GeneratorTests.Components, source), Is.EqualTo(count));
}
