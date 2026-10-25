using System.Threading.Tasks;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

// EasyEcsAnalyzer.cs: the declaration and [ForEach] rules (EECS002-006, EECS009-024, EECS035-036, EECS054, EECS056-059,
// EECS070, EECS072-073) on generic, nested and partial types, runners, local functions, and the thresholds they check.
public partial class AnalyzerTests
{
    private const string Ten = """
        public struct C0 : IComponent { public int V; } public struct C1 : IComponent { public int V; }
        public struct C2 : IComponent { public int V; } public struct C3 : IComponent { public int V; }
        public struct C4 : IComponent { public int V; } public struct C5 : IComponent { public int V; }
        public struct C6 : IComponent { public int V; } public struct C7 : IComponent { public int V; }
        public struct C8 : IComponent { public int V; } public struct C9 : IComponent { public int V; }
        public interface IMarked : IComponent { }
        """;

    // EECS003 at its threshold: ten components, one above nine.
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref C0 a, in C1 b, in C2 c, in C3 d, in C4 e, in C5 f, in C6 g, in C7 h, in C8 i, in C9 j) => a.V = j.V; }", "EECS003")]
    [TestCase("public static class R { [ForEntity] static void A(ref C0 a, in C1 b, in C2 c, in C3 d, in C4 e, in C5 f, in C6 g, in C7 h, in C8 i, in C9 j) => a.V = j.V; }", "EECS003")]
    // EECS005: an instance method of a generic or nested class.
    [TestCase("public class G<X> { [ForEach] void A(ref P p) { } }", "EECS005")]
    [TestCase("public static class O { public class R { [ForEach] void A(ref P p) { } } }", "EECS005")]
    // EECS006: a generic class, a class through an interface of the user's.
    [TestCase("public class Bad<X> : IComponent { }", "EECS006")]
    [TestCase("public class Bad : IMarked { }", "EECS006")]
    [TestCase("public class Bad : ISingletonComponent { public int V; }", "EECS006")]
    // EECS012: a property and a static field of a buffer, a buffer given to a generic method by value.
    [TestCase("public static class U { public static DynamicBuffer<int> B { get; set; } }", "EECS012")]
    [TestCase("public static class U { static DynamicBuffer<int> _b; }", "EECS012")]
    [TestCase("public static class U { static int Keep<X>(X x) => 0; public static int F(Entity e) => Keep(e.Read<DynamicBuffer<int>>()); }", "EECS012")]
    // EECS015: an indexed component through an interface of the user's.
    [TestCase("public interface IKeyed : IComponent, IIndexed<int> { } public struct K : IKeyed { public int V; public readonly int Key => V; } [EcsSystem] public ref struct S { [ForEach] static void A(ref K k) { } }", "EECS015")]
    // EECS017: an Entity in a nullable, a tuple, a generic struct.
    [TestCase("public struct C : IComponent { public Entity? Maybe; }", "EECS017")]
    [TestCase("public struct C : IComponent { public (Entity, int) Pair; }", "EECS017")]
    [TestCase("public struct Box<X> { public X Value; } public struct C : IComponent { public Box<Entity> Held; }", "EECS017")]
    // EECS019: three systems in a cycle; EECS020: groups of generic systems.
    [TestCase("[UpdateBefore<B>] [EcsSystem] public ref struct A { } [UpdateBefore<C>] [EcsSystem] public ref struct B { } [UpdateBefore<A>] [EcsSystem] public ref struct C { }", "EECS019")]
    [TestCase("public struct G : ISystemGroup; [UpdateInGroup<G>] [EcsSystem] public ref struct A<X> { } [UpdateBefore<A<int>>] [EcsSystem] public ref struct B { }", "EECS020")]
    // EECS023: WithAny on a component, a filter on a plain struct.
    [TestCase("[WithAny<P, Q>] public struct NotAGroup : IComponent;", "EECS023")]
    [TestCase("[With<P>] public struct NotAGroup;", "EECS023")]
    // EECS024: [ForEntity] on a local function, a local function in a hook.
    [TestCase("public static class U { public static void M() { [ForEntity] static void Local(ref P p) => p.V++; } }", "EECS024")]
    [TestCase("[EcsSystem] public ref struct S { static void OnUpdate() { [ForEach] static void Local(ref P p) => p.V++; } }", "EECS024")]
    public Task DeclarationRulesReport(string source, string id) => AssertReports(id, GeneratorTests.Components, Ten, source);

    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref C0 a, in C1 b, in C2 c, in C3 d, in C4 e, in C5 f, in C6 g, in C7 h, in C8 i) => a.V = i.V; }")] // nine: the most
    [TestCase("[EcsSystem] public ref struct S { [ForEach, Changed<T>] static void A(Entity e) { } [ForEach, With<P>] static void B(Entity e) { } }")]         // none, with a filter
    [TestCase("public static class R { [ForEach] public static void A<X>(ref X x) where X : struct, IComponent { } }")]                                        // a runner's generic method: its entry point takes X
    [TestCase("[EcsSystem] public ref struct G<X> where X : struct, IComponent { [ForEach] static void A(ref X x) { } }")]                                     // a generic system
    [TestCase("public static class R { [ForEach] static void A(ref P p, int n, string name) => p.V = n + name.Length; }")]                                     // a runner's values
    [TestCase("public class R { [ForEach] static void A(ref P p) { } }")]                                                                                      // static in a class
    [TestCase("public interface IOwn : IComponent { } public struct Ok : IOwn { public int V; }")]                                                              // an interface extends the marker
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(System.Span<P> p, System.ReadOnlySpan<Q> q, in Clock c) { } [ForEach] static void B(EntitySpan es, System.Span<P> p) { } }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(ref P p, in Clock c) => p.V = c.Ticks; }")]                          // a singleton read
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref Holder h) => h.Box.V++; [ForEach] static void B(in Holder h, ref P p) => p.V = h.Box.V; }")]
    [TestCase("public static class U { public static void F(Entity[] es) => System.Threading.Tasks.Parallel.For(0, es.Length, i => _ = es[i].Has<P>()); }")]  // reads on threads
    [TestCase("public static class U { public static void F(World w) => System.Threading.Tasks.Task.Run(() => w.Count<P>()); }")]
    [TestCase("public struct Rel : IComponent, IIndexed<int> { public int V; public readonly int Key => V; } [EcsSystem] public ref struct S { [ForEach] static void A(in Rel r, ref P p, ReadOnlyMaybe<Owned> o) => p.V = r.V; }")]
    [TestCase("public struct C : IComponent { public static Entity Last; public EntityId Id; } public struct NotData { public Entity E; }")]                     // static, an id, no component
    [TestCase("[UpdateBefore<B>] [EcsSystem] public ref struct A { } [UpdateBefore<C>] [EcsSystem] public ref struct B { } [EcsSystem] public ref struct C { }")] // a chain
    [TestCase("public struct G : ISystemGroup; [UpdateInGroup<G>] [EcsSystem] public ref struct A { } [UpdateInGroup<G>, UpdateAfter<A>] [EcsSystem] public ref struct B { }")] // one group
    [TestCase("public static class R { [ForEach] static void A(ref P p, int n) { } static void A(World w, string n) { } }")]                                   // another signature
    [TestCase("[WithAny<P, Q>, Without<T>] public struct G : ISystemGroup;")]
    [TestCase("public static class U { public static int M() { static int Local(int x) => x; return Local(1); } }")]
    public Task DeclarationRulesLeaveAlone(string source) => AssertClean(GeneratorTests.Components, Ten, source);

    // One report per mistake: a cycle on each of its edges.
    [TestCase("[UpdateAfter<B>] [EcsSystem] public ref struct A { } [UpdateAfter<A>] [EcsSystem] public ref struct B { }", "EECS019", 2)]
    [TestCase("[UpdateAfter<S>] [EcsSystem] public ref struct S { }", "EECS019", 1)]                              // ordered against itself
    [TestCase("public struct C : IComponent { public Entity A, B; }", "EECS017", 2)]                               // each field
    public async Task DeclarationRulesReportEachMistakeOnce(string source, string id, int count) =>
        Assert.That(await Count(id, GeneratorTests.Components, source), Is.EqualTo(count));
}
