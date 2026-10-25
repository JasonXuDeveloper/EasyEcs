using System.Threading.Tasks;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

// EasyEcsAnalyzer.Usage.cs: calls and declarations that compile but do not do what they say (EECS037-038, EECS040-045,
// EECS047-051), in lambdas, local functions, generated members and [ForEntity] methods.
public partial class AnalyzerTests
{
    [TestCase("public struct Named : IComponent, ITracked { public string Name; } public static class R { [ForEntity] static void A(Mut<Named> n, in P p) { if (p.V > 0) n.SetIfChanged(default); } }", "EECS040")]
    [TestCase("public static class U { public static System.Action F(World w) => () => { foreach (var item in w.Query<P>()) w.TrimExcess(); }; }", "EECS041")]   // in a lambda
    [TestCase("public static class U { public static void F(World w) { foreach (var item in w.Read<P>()) w.Restore(default!); } }", "EECS041")]
    [TestCase("public static class U { public static void F(World w) { void Walk() { foreach (var e in w.OwnedIndex.Find(default)) e.SetOwned(default); } Walk(); } }", "EECS042")] // a local function
    [TestCase("public static class U { public static int F(World w) => w.Count<P>(QueryFilter.Create().With<Q>().Slice(0, 3)); }", "EECS044")]
    [TestCase("public static class U { public static void F(Entity e) => e.Set(new Retired()); }", "EECS045")]
    [TestCase("public static class U { public static void F(Entity e) { void G() { ref var p = ref e.Get<P>(); e.Set(new Q()); p.V = 1; } G(); } }", "EECS047")]
    [TestCase("public static class U { public static void F(Entity e) { ref var p = ref e.Get<P>(); e.Destroy(); p.V = 1; } }", "EECS047")]
    [TestCase("public static class R { [ForEntity] static void A(ref P p, World w) { if (p.V < 0) w.Update(); } }", "EECS051")]
    [TestCase("public static class R { [ForEach, With<Q>, Without<Q>] static void A(ref P p) => p.V++; }", "EECS049")]                                       // a runner
    public Task UsageRulesReport(string source, string id) => AssertReports(id, GeneratorTests.Components, GeneratorTests.Extra, source);

    [TestCase("public struct Named : IComponent, ITracked { public string Name; } public static class U { public static void F(Entity e) => e.Set(new Named()); }")] // Set compares nothing
    [TestCase("public static class U { public static void F(World w) { foreach (var item in w.Query<P>()) { } w.Restore(default!); } }")]
    [TestCase("public static class U { public static void F(World w, System.Collections.Generic.List<Entity> all) { foreach (var e in w.OwnedIndex.Find(default)) all.Add(e); } }")]
    [TestCase("public static class U { public static int F(World w, int n) => w.Count<P>(QueryFilter.Create().Slice(n % 1, 1)); }")]
    [TestCase("public static class U { public static void F(Entity e) { ref var p = ref e.Get<P>(); p.V = 1; e.Destroy(); } }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach, WithAny<Q, T>, Without<Q>] static void A(ref P p) => p.V++; }")]                                    // T still passes
    public Task UsageRulesLeaveAlone(string source) => AssertClean(GeneratorTests.Components, GeneratorTests.Extra, source);
}
