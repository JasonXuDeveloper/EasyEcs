using System.Linq;
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

    private const string Pq = "[View] internal ref struct PQ { public ref P P; public ref readonly Q Q; }\n";

    // EECS049 beyond a component both required and excluded: a [WithAny<...>] whose every component is excluded (the
    // method's, its group's), a lambda pass's own filters, a view built over the visited entity.
    [TestCase("[EcsSystem] public ref struct S { [ForEach, WithAny<Q, T>, Without<Q>, Without<T>] static void A(ref P p) => p.V++; }",
        "[ForEach] method 'A' requires one of Q, T ([WithAny<Q, T>]) and excludes each of them ([Without<Q>], [Without<T>]): no entity passes both, so the method never runs")]
    [TestCase("[WithAny<Q, T>] public struct G : ISystemGroup; [UpdateInGroup<G>] [EcsSystem] public ref struct S { [ForEach, Without<Q>, Without<T>] static void A(ref P p) => p.V++; }",
        "requires one of Q, T ([WithAny<Q, T>] on its group G)")]
    [TestCase("public struct G : ISystemGroup, ISystemGroupWithout<T>; [UpdateInGroup<G>] [EcsSystem] public ref struct S { [ForEach, WithAny<Q, T>, Without<Q>] static void A(ref P p) => p.V++; }",
        "excludes each of them ([Without<Q>], ISystemGroupWithout<T> on its group G)")]
    [TestCase("public static class Run { public static void F(World world) => world.ForEach([Without<P>] static (ref P p) => p.V++); }",
        "The lambda of world.ForEach(…) requires 'P' (parameter 'p') and excludes it ([Without<P>]): no entity passes both, so the pass visits no entity")]
    [TestCase("public static class Run { public static void F(World world) => world.ForEach([WithAny<Q, T>, Without<Q>, Without<T>] static (ref P p) => p.V++); }",
        "The lambda of world.ForEach(…) requires one of Q, T")]
    [TestCase(Pq + "public static class Run { public static void F(World world) => world.ForEach([Without<Q>] static (PQ v) => v.P.V++); }",
        "requires 'Q' (field 'Q' of view 'v')")]                                                                                              // a lambda's view
    [TestCase(Pq + "[EcsSystem] public ref struct S { [ForEach, Without<Q>] static void A(Entity e, ref T t) => t.V = e.AsPQ().P.V; }",
        "[ForEach] method 'A' requires 'Q' (e.AsPQ() builds view PQ over its entity) and excludes it ([Without<Q>]): no entity it visits has it, so e.AsPQ() throws on every one")]
    [TestCase(Pq + "public struct G : ISystemGroup, ISystemGroupWithout<P>; [UpdateInGroup<G>] [EcsSystem] public ref struct S { [ForEach] static void A(Entity e, ref T t) { if (e.TryGetPQ(out var v)) t.V = v.Q.V; } }",
        "excludes it (ISystemGroupWithout<P> on its group G): no entity it visits has it, so e.TryGetPQ(out var v) finds none")]
    [TestCase(Pq + "public static class R { [ForEntity, Without<P>] static void A(Entity e, ref T t) => t.V = e.AsPQ().Q.V; }",
        "[ForEntity] method 'A' requires 'P'")]
    public async Task FiltersNoEntityPassesAreReported(string source, string message)
    {
        await AssertReports("EECS049", GeneratorTests.Components, source);
        var found = (await GeneratorTests.Analyze([GeneratorTests.Components, source])).Single(d => d.Id == "EECS049");
        Assert.That(found.GetMessage(), Does.Contain(message));
    }

    [TestCase("[EcsSystem] public ref struct S { [ForEach, WithAny<Q, T>, Without<Q>, Without<Settings>] static void A(ref P p) => p.V++; }")]    // T passes
    [TestCase("[WithAny<Q, T>] public struct G : ISystemGroup; [UpdateInGroup<G>] [EcsSystem] public ref struct S { [ForEach, Without<Q>] static void A(ref P p) => p.V++; }")]
    [TestCase("public static class Run { public static void F(World world) => world.ForEach([Without<Q>] static (ref P p) => p.V++); }")]
    [TestCase("public struct G : ISystemGroup, ISystemGroupWithout<P>; [UpdateInGroup<G>] [EcsSystem] public ref struct S { static void OnUpdate(World world) => world.ForEach(static (ref P p) => p.V++); }")] // a lambda takes no group's filters
    [TestCase(Pq + "[EcsSystem] public ref struct S { [ForEach, Without<Settings>] static void A(Entity e, ref T t) => t.V = e.AsPQ().P.V; }")]   // nothing of PQ excluded
    public Task FiltersSomeEntityPassesAreLeftAlone(string source) => AssertClean(GeneratorTests.Components, source);
}
