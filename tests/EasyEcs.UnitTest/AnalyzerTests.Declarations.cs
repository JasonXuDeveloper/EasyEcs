using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using EasyEcs.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using NUnit.Framework;
using static EasyEcs.UnitTest.GeneratorTests;

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

    [TestCase("[EcsSystem] public ref struct S { private struct H : IComponent; [ForEach] static void A(ref H h) { } }", "EECS022")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, int x) { } }", "EECS002")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(P p) { } }", "EECS002")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(World w) { } }", "EECS003")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach, Without<P>] static void A(Entity e) { } }", "EECS003")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref Clock c) { } }", "EECS003")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A<T>(ref T t) where T : struct, IComponent { } }", "EECS004")] // nothing gives T
    [TestCase("public partial class S { [ForEach] void A(ref P p) { } }", "EECS005")]                         // outside a system: static
    [TestCase("public class O { private static class R { [ForEach] static void A(ref P p) { } } }", "EECS022")]   // a runner's type is extended
    [TestCase("public static partial class R { [ForEach] static void A(P p) { } }", "EECS002")]                   // a component by value
    [TestCase("public static partial class R { [ForEach] static void A(in P p, ref int n) { } static void A(World w, ref int n) { } }", "EECS021")]
    [TestCase("public static partial class R { [ForEach(Parallel = true)] static void A(ref P p, ref int n) { } }", "EECS010")]
    [TestCase("public class Bad : IComponent { }", "EECS006")]
    [TestCase("public record Bad : ISingletonComponent;", "EECS006")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(System.Span<P> p, ref Q q) { } }", "EECS009")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(System.Span<P> p, Entity e) { } }", "EECS009")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(EntitySpan es, ref P p) { } }", "EECS009")]           // an EntitySpan needs spans
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(EntitySpan es, System.Span<P> p) => es[0].Destroy(); }", "EECS013")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(System.Span<Clock> c) { } }", "EECS002")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(ref P p, ref Clock c) { } }", "EECS010")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(System.Span<P> p) { } }", "EECS010")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(Entity e, ref P p) => e.Destroy(); }", "EECS010")]
    [TestCase("public struct J : IForEachEntity<P>, INoStructuralChanges { public void Execute(Entity e, ref P p) { } } "
        + "[EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(Entity e, ref P p) { var j = new J(); e.Run<P, J>(ref j); } }", "EECS010")]
    [TestCase("[EcsSystem] public ref struct S { static int _kills; static void K() => _kills++; }", "EECS007")]
    [TestCase("[EcsSystem] public ref struct S { public static int Kills { get; set; } }", "EECS007")]
    [TestCase("[EcsSystem] public ref struct A { public static void Go() { } } [EcsSystem] public ref struct S { static void OnUpdate() => A.Go(); }", "EECS008")]
    [TestCase("[EcsSystem] public ref struct S { static int _kills; }", "EECS007")]
    [TestCase("[EcsSystem] public ref struct A { } [EcsSystem] public ref struct S { static void OnUpdate(World world) { if (world.TryGetSystem<A>(out var a)) a.Enabled = false; } }", "EECS008")]
    [TestCase("[EcsSystem] public ref struct A { } [EcsSystem] public ref struct S { static void OnUpdate(World world) => world.System<A>().Enabled = false; }", "EECS008")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(in Handle l) => l.Target.Destroy(); }", "EECS013")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(in Handle l) => l.Target.Clone(); }", "EECS013")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(ref P p, in Handle l) => l.Target.Set(new Q()); }", "EECS013")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(Entity e, in Handle l) { if (e.Has<P>()) l.Target.Set(new Q()); } }", "EECS013")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(in Handle l) => l.Target.RemoveP(); }", "EECS013")]   // generated members
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(Entity e, in Handle l) { if (e.HasP && e.P.V > 0) l.Target.SetQ(1); } }", "EECS013")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(Entity e, in Handle l) { if (e.Settings.V > 0) l.Target.SetQ(1); } }", "EECS013")] // a read named Set…
    [TestCase("public static class U { public static void F(Entity[] es) => System.Threading.Tasks.Parallel.For(0, es.Length, i => es[i].RemoveP()); }", "EECS014")]
    [TestCase("public static class U { public static void F(World w) { System.Threading.Tasks.Task.Run(() => w.SetClock(1)); w.RemoveClock(); } }", "EECS014")]
    [TestCase("public static class U { public static void F(World w) { System.Threading.Tasks.Task.Run(() => w.CreateEntity()); w.CreateEntity(); } }", "EECS014")]
    [TestCase("public static class U { public static void F(World w, Prefab p) { System.Threading.Tasks.Task.Run(() => w.Instantiate(p)); w.Instantiate(p); } }", "EECS014")]
    [TestCase("public static class U { public static void F(Entity[] es) => System.Threading.Tasks.Parallel.For(0, es.Length, i => es[i].Destroy()); }", "EECS014")]
    [TestCase("public class U { World _w = new(); void F() => new System.Threading.Thread(() => { _w.Update(); }).Start(); void G() => _w.Update(); }", "EECS014")]
    [TestCase("public static class U { public static void F(Entity e) { for (int i = 0; i < 4; i++) System.Threading.ThreadPool.QueueUserWorkItem(_ => e.Remove<P>()); } }", "EECS014")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach, Removed<T>] static void A(in T t, ref P p) { } }", "EECS016")]
    [TestCase("[With<T>] public struct G : ISystemGroup; [UpdateInGroup<G>] [EcsSystem] public ref struct S { [ForEach, Removed<T>] static void A(ref P p) { } }", "EECS016")] // the group's filter
    [TestCase("public struct G : ISystemGroupWith<T>; [UpdateInGroup<G>] [EcsSystem] public ref struct S { [ForEach, Removed<T>] static void A(ref P p) { } }", "EECS016")] // through ISystemGroupWith<T>
    [TestCase("[Without<Q>] public struct NotAGroup : IComponent;", "EECS023")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach, Removed<T>, With<T>] static void A(ref P p) { } }", "EECS016")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach, Removed<T>] static void A(Entity e) { } }", "EECS003")]       // T is no query anchor
    [TestCase("public record struct Target(Entity Of) : IComponent;", "EECS017")]                   // a handle in a component
    [TestCase("public struct Party : IComponent { public Entity[] Members; }", "EECS017")]
    [TestCase("public struct Leader : ISingletonComponent { public Entity Player; }", "EECS017")]
    [TestCase("public struct Pair { public Entity A; } public struct Duel : IComponent { public Pair Sides; }", "EECS017")]
    [TestCase("public static class U { public static void F(Entity e) => e.Get<Owned>() = default; }", "EECS015")]
    [TestCase("public static class U { public static void F(Entity e) => e.GetOrAdd<Owned>(); }", "EECS015")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref Owned o) { } }", "EECS015")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(System.Span<Owned> o) { } }", "EECS015")]
    [TestCase("public static class U { public static void F(World w) { foreach (var item in w.Query<Owned>()) item.Component1 = default; } }", "EECS015")]
    [TestCase("public static class U { public static void F(World w) { foreach (var item in w.Query<Owned>()) item.Component1.Owner = default; } }", "EECS015")]
    [TestCase("public static class U { public static void F(World w) { foreach (var item in w.Query<Owned>()) { ref var o = ref item.Component1; } } }", "EECS015")]
    [TestCase("public struct J : IForEach<Owned> { public void Execute(ref Owned o) => o = default; }", "EECS015")]
    [TestCase("[UpdateInterval(0)] [EcsSystem] public ref struct S { }", "EECS083")]
    [TestCase("[UpdateInterval(4, Phase = 4)] [EcsSystem] public ref struct S { }", "EECS083")]
    [TestCase("[UpdateInterval(4, Phase = -1)] [EcsSystem] public ref struct S { }", "EECS083")]
    [TestCase("[UpdateInterval(4)] public class NotASystem { }", "EECS083")]
    [TestCase("public static class R { [Consume] static void A(ref P p) { } }", "EECS085")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Slices = 2), Consume] static void A(ref P p) { } }", "EECS085")]
    [TestCase("public static class R { [ForEntity] void A(ref P p) { } }", "EECS081")]
    [TestCase("public static class R { [ForEntity] static void A(System.Span<P> p) { } }", "EECS081")]
    [TestCase("public static class R { [ForEntity, ForEach] static void A(ref P p) { } }", "EECS081")]
    [TestCase("public static class R { [ForEntity] static void A(ref P p, Scratch<int> s) { } }", "EECS081")]
    [TestCase("public static class R { [ForEntity] static void A(ref P p, int n) { } static void A(Entity e, int n) { } }", "EECS021")]
    [TestCase("public static class R { [ForEntity] static void A(P p) { } }", "EECS002")]
    [TestCase("public static class R { [ForEntity] static void A(int n) { } }", "EECS003")]
    [TestCase("[UpdateBefore<B>, UpdateAfter<C>] [EcsSystem] public ref struct A { } [EcsSystem] public ref struct B { } [UpdateAfter<B>] [EcsSystem] public ref struct C { }", "EECS019")]
    [TestCase("public struct G : ISystemGroup; [UpdateInGroup<G>] [EcsSystem] public ref struct A { } [UpdateAfter<A>] [EcsSystem] public ref struct B { }", "EECS020")]
    [TestCase("public struct Zone : IComponent, IGroup { public int Id; public int Group => Id; } [EcsSystem] public ref struct S { [ForEach] static void A(ref Zone z) { } }", "EECS015")]
    [TestCase("public struct Link : IRelation { public EntityId Target { get; set; } } public static class U { public static void F(Entity e, Link l) => e.Get<DynamicBuffer<Link>>().Add(l); }", "EECS015")]
    [TestCase("public struct Link : IRelation { public EntityId Target { get; set; } } [EcsSystem] public ref struct S { [ForEach] static void A(ref DynamicBuffer<Link> links) { } }", "EECS015")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, Maybe<Owned> o) { } }", "EECS015")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(Mut<Owned> o) { } }", "EECS015")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(System.Span<P> p, Mut<T> t) { } }", "EECS009")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, in Grid g) => g.Cells.Add(1); }", "EECS011")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, in Grid g) => g.Cells.Clear(); }", "EECS011")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, in Grid g) => g.Slots[0] += 1; }", "EECS011")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(in Holder h) => h.Box.V++; }", "EECS011")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(in Holder h) => h.Box.V = 1; }", "EECS011")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(System.ReadOnlySpan<Holder> h) => h[0].Box.V++; }", "EECS011")]
    [TestCase("public struct Bag : IComponent { public DynamicBuffer<int> Items; }", "EECS012")]
    [TestCase("public static class U { public static int F(Entity e) { var b = e.Read<DynamicBuffer<int>>(); return b.Count; } }", "EECS012")]
    [TestCase("public static class U { public static int F(DynamicBuffer<int> b) => b.Count; }", "EECS012")]
    [TestCase("public static class U { public static DynamicBuffer<int> F(Entity e) => e.Read<DynamicBuffer<int>>(); }", "EECS012")]
    [TestCase("public static class U { public static void F(Entity a, Entity b) => b.Set(a.Read<DynamicBuffer<int>>()); }", "EECS012")]
    [TestCase("public static class U { public static void F(Entity a, Entity b) => b.Get<DynamicBuffer<int>>() = a.Read<DynamicBuffer<int>>(); }", "EECS012")]
    [TestCase("public static class U { public static void F(Entity a) { var b = new DynamicBuffer<int>(); a.Set(b); } }", "EECS012")]
    [TestCase("public record struct Rel(EntityId Target) : IRelation; [EcsSystem] public ref struct S { [ForEach] static void A(in Handle h) => h.Target.Link(new Rel(default)); }", "EECS013")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(in Handle h) => h.Target.World.SetAll(new Q()); }", "EECS013")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(in Handle h) => h.Target.World.TrimExcess(); }", "EECS013")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(in Handle h) => h.Target.World.LoadEntitiesInto(default, [h.Target], markWritten: false); }", "EECS013")]
    [TestCase("public static class U { public static void F(World world) { System.Threading.Tasks.Task.Run(() => world.RemoveAll<P>()); world.CreateEntity(new P()); } }", "EECS014")]
    [TestCase("public class Base { public static string A(World w) => \"\"; } public class R4 : Base { [ForEach] public static void A(ref P p) { } }", "EECS021")]
    [TestCase("public static class U { public static void M() { [ForEach] static void Local(ref P p) => p.V++; } }", "EECS024")]
    [TestCase("[UpdateBefore<G2<int>>] [EcsSystem] public ref struct G1 { [ForEach] static void A(ref P p) { } } [UpdateBefore<G1>] [EcsSystem] public ref struct G2<X> { [ForEach] static void B(ref Q q) { } }", "EECS019")]
    [TestCase("[EcsSystem] public ref struct S { public static event System.Action Fired; }", "EECS007")]
    [TestCase("[EcsSystem] public ref struct S { public static int Count { get; private set; } }", "EECS007")]
    [TestCase("[EcsSystem] public ref struct A { [ForEntity] static void Hit(ref P p) { } } [EcsSystem] public ref struct S { [ForEach] static void B(Entity e, ref Q q) => A.Hit(e); }", "EECS008")] // a generated entry point
    [TestCase("public class S : IComponent;", "EECS006")]
    [TestCase("[EcsSystem] public class S { }", "EECS074")]
    [TestCase("[EcsSystem] public struct S { }", "EECS074")]
    [TestCase("[EcsSystem] public record struct S;", "EECS074")]
    [TestCase("[EcsSystem] public ref struct S { int _n; }", "EECS075")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] void A(ref P p) { } }", "EECS075")]
    [TestCase("[EcsSystem] public ref struct S { public int N { get; set; } }", "EECS075")]
    [TestCase("[EcsSystem] public ref struct S { public S(int n) { } }", "EECS075")]
    [TestCase("[EcsSystem] public ref struct S { void OnUpdate() { } }", "EECS075")]       // an instance hook: static first
    [TestCase("[EcsSystem] public ref struct S { static int OnUpdate() => 0; }", "EECS076")]
    [TestCase("[EcsSystem] public ref struct S { static void OnUpdate(int n) { } }", "EECS076")]
    [TestCase("[EcsSystem] public ref struct S { static void OnUpdate(World a, World b) { } }", "EECS076")]
    [TestCase("[EcsSystem] public ref struct S { static void OnUpdate(ref World w) { } }", "EECS076")]
    [TestCase("[EcsSystem] public ref struct S { static void OnUpdate() { } static void OnUpdate(World w) { } }", "EECS076")]   // an overload
    [TestCase("[EcsSystem] public ref struct S { static void OnUpdate<T>() { } }", "EECS076")]
    [TestCase("[EcsSystem] public ref struct S { static void OnUpate(World world) { } }", "EECS090")]       // misspelled
    [TestCase("[EcsSystem] public ref struct S { static void Update(World world) { } }", "EECS090")]        // another engine's name
    [TestCase("[EcsSystem] public ref struct S { static void OnStart() { } }", "EECS090")]
    [TestCase("[EcsSystem] public ref struct S { static void onDestroy(SystemHandle s) { } }", "EECS090")]
    [TestCase("public struct NotOne; public static class U { public static void F(World w) => w.AddSystem<NotOne>(); }", "EECS077")]
    [TestCase("public struct NotOne; public static class U { public static bool F(World w) => w.HasSystem<NotOne>(); }", "EECS077")]
    [TestCase("public struct NotOne; [UpdateAfter<NotOne>] [EcsSystem] public ref struct S { }", "EECS077")]
    [TestCase("[EcsSystem] file ref struct S7 { [ForEach] static void A(ref P p) { } }", "EECS022")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Slices = 2), Changed<T>] static void A(ref P p) { } }", "EECS035")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Slices = 4), Removed<T>] static void A(ref P p) { } }", "EECS035")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Slices = 0)] static void A(ref P p) { } }", "EECS035")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Slices = 2), ChangedAny<T, T>] static void A(ref P p) { } }", "EECS035")]
    [TestCase("public static class R { [ForEach(Slices = -1)] static void A(ref P p) { } }", "EECS035")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach(ChunkRows = 64)] static void A(ref P p) { } }", "EECS054")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Parallel = true, ChunkRows = -1)] static void A(ref P p) { } }", "EECS054")]
    [TestCase("public static class R { [ForEach(Slices = 2)] static void A(ref P p) { } static void A(World w, int slice) { } }", "EECS021")]
    [TestCase("public struct Mount : IComponent, IUniqueIndexed<EntityId> { public EntityId Rider; public EntityId Key => Rider; }", "EECS036")]
    [TestCase("public struct Seat : IComponent, IUniqueIndexed<int>, IIndexed<string> { public int Id; readonly int IIndexed<int>.Key => Id; readonly string IIndexed<string>.Key => \"\"; }", "EECS036")]
    [TestCase("public struct Sum : ISingletonComponent, IReduction<Sum> { public int N; public static Sum Identity => default; public static Sum Combine(Sum a, Sum b) => new() { N = a.N + b.N }; } [EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(in P p, ref Reduce<Sum> a, ref Reduce<Sum> b) { a.Value.N += p.V; b.Value.N++; } }", "EECS057")]
    [TestCase("public struct Sum : ISingletonComponent, IReduction<Sum> { public int N; public static Sum Identity => default; public static Sum Combine(Sum a, Sum b) => new() { N = a.N + b.N }; } [EcsSystem] public ref struct S { [ForEach] static void A(in P p, ref Reduce<Sum> a, ref Sum s) { a.Value.N += p.V; s.N++; } }", "EECS057")]
    [TestCase("public struct Sum : ISingletonComponent, IReduction<Sum> { public int N; public static Sum Identity => default; public static Sum Combine(Sum a, Sum b) => new() { N = a.N + b.N }; } [EcsSystem] public ref struct S { [ForEach] static void A(in P p, ref Reduce<Sum> a) => a.Value.N += p.V; [ForEach] static void B(in Q q, ref Reduce<Sum> b) => b.Value.N += q.V; }", "EECS057")]
    [TestCase("public struct Bag : IComponent { public System.Collections.Generic.List<int> Items; } public struct Work { public System.Collections.Generic.List<int> Items; } [EcsSystem] public ref struct S { [ForEach] static void A(ref Bag bag, Scratch<Work> work) { work.Value.Items ??= new(); bag.Items = work.Value.Items; } }", "EECS058")]
    [TestCase("public struct Bag : IComponent { public int[] Items; } [EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(ref Bag bag, Scratch<int[]> work) { work.Value ??= new int[8]; bag.Items = work.Value; } }", "EECS058")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(ref P p, Lookup<P> ps) => p.V = ps[default].HasValue ? 1 : 0; }", "EECS059")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(Mut<P> p, in Q q, Lookup<P> ps) => p.Write().V = ps.Has(default) ? q.V : 0; }", "EECS059")]
    [TestCase("public record struct G(int Pos) : IComponent, ITile<int> { int IIndexed<int>.Key => Pos; static int ITileShape.Width => 4; static int ITileShape.Depth => 4; static int ITile<int>.Beside(int k, TileFaces f) => k + 1; } [EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(Tile<byte, G> t, Mut<DynamicBuffer<byte>> b) => b.Write().SetCount(t.Cells.Length); }", "EECS059")]
    [TestCase("public record struct G(int Pos) : IComponent, ITile<int> { int IIndexed<int>.Key => Pos; static int ITileShape.Width => 4; static int ITileShape.Depth => 4; static int ITile<int>.Beside(int k, TileFaces f) => k + 1; } [EcsSystem] public ref struct S { [ForEach] static void A(Tile<byte, G> t, ref DynamicBuffer<byte> b) => b.SetCount(t.Cells.Length); }", "EECS059")]
    [TestCase("public struct Sum : ISingletonComponent, IReduction<Sum> { public int N; public static Sum Identity => default; public static Sum Combine(Sum a, Sum b) => new() { N = a.N + b.N }; } [EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(in P p, Reduce<Sum> a) => a.Value.N += p.V; }", "EECS002")]
    [TestCase("public record struct G(int Pos) : IComponent, ITile<int> { int IIndexed<int>.Key => Pos; static int ITileShape.Width => 4; static int ITileShape.Depth => 4; static int ITile<int>.Beside(int k, TileFaces f) => k + 1; static int ITileShape.Halo => 5; }", "EECS072")]
    [TestCase("public record struct G(int Pos) : IComponent, ITile<int> { int IIndexed<int>.Key => Pos; static int ITileShape.Width => 4; static int ITileShape.Depth => 4; static int ITile<int>.Beside(int k, TileFaces f) => k + 1; static int ITileShape.Halo => 3; static int ITileShape.Height => 2; static TileFaces ITileShape.Faces => TileFaces.All; }", "EECS072")]
    [TestCase("public record struct G(int Pos) : IComponent, ITile<int> { int IIndexed<int>.Key => Pos; static int ITileShape.Width => 4; static int ITileShape.Depth => 65; static int ITile<int>.Beside(int k, TileFaces f) => k + 1; }", "EECS072")]
    [TestCase("public record struct G(int Pos) : IComponent, ITile<int> { int IIndexed<int>.Key => Pos; static int ITileShape.Width => 4; static int ITileShape.Depth => 4; static int ITile<int>.Beside(int k, TileFaces f) => k + 1; static int ITileShape.Halo => 0; }", "EECS072")]
    [TestCase("public record struct G(int Pos) : IComponent, ITile<int> { int IIndexed<int>.Key => Pos; static int ITileShape.Width => 4; static int ITileShape.Depth => 4; static int ITile<int>.Beside(int k, TileFaces f) => k + 1; } [EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(Tile<byte, G> t, Tile<byte, G> u, Mut<DynamicBuffer<int>> b) => b.Write().SetCount(t.Cells.Length + u.Cells.Length); }", "EECS073")]
    [TestCase("public record struct H(int V); [EcsSystem] public ref struct S { [ForEach] static void A(in P p, Emit<H> a, Emit<H> b) { a.Add(new H(p.V)); b.Add(new H(1)); } }", "EECS070")]
    [TestCase("public record struct H(int V); [EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(in P p, Emit<H> a, in Emitted<H> seen) => a.Add(new H(seen.Items.Count)); }", "EECS070")]
    [TestCase("public record struct H(int V); [EcsSystem] public ref struct S { [ForEach] static void A(in P p, ref Emit<H> a) => a.Add(new H(p.V)); }", "EECS002")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(ref P p) => p.V = System.Random.Shared.Next(10); }", "EECS071")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p) => p.V = System.Random.Shared.Next(); }", "EECS071")]
    [TestCase("public static class R { [ForEach] public static void A(ref P p) => p.V = new System.Random(3).Next(); }", "EECS071")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p) => p.V = new System.Random().Next(5); }", "EECS071")]
    [TestCase("[EcsSystem] public ref struct S { static readonly System.Random R = new(1); [ForEach] static void A(ref P p) => p.V = R.Next(); }", "EECS071")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, Entity e, in EntityRandom r) => p.V = r.Range(e.Id, 0, 0, 6) + r.Range(e.Id, 0, 0, 6); }", "EECS088")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, Entity e, in EntityRandom r) { if (r.Chance(e.Id, 2, 0.5f)) p.V = r.Range(e.Id, 2, 0, 6); } }", "EECS088")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, Entity e, in EntityRandom r) { for (int i = 0; i < 3; i++) p.V += r.Range(e.Id, 0, 1, 7); } }", "EECS088")]
    [TestCase("public static class R { [ForEach] static void A(ref P p, in Link l, in Clock c) => p.V = EntityRandom.Range(7, l.Target, (ulong)c.Ticks, 1, 0, 6) * EntityRandom.Range(7, l.Target, (ulong)c.Ticks, 1, 0, 6); }", "EECS088")]
    [TestCase("public struct J : IForEachEntity<P> { public EntityRandom R; public void Execute(Entity e, ref P p) => p.V = R.Range(e.Id, 4, 0, 6) - R.Range(e.Id, 4, 0, 6); }", "EECS088")] // a job's Execute
    public async Task MisuseIsReported(string source, string id)
    {
        var errors = await Diagnose(Components, source);
        Assert.That(errors, Does.Contain(id));
        Assert.That(errors.Where(e => !e.StartsWith("CS")), Is.EqualTo(new[] { id }), "exactly one EasyEcs diagnostic");
    }

    [TestCase("public static class Cache { [System.ThreadStatic] public static System.Collections.Generic.List<int> List; } [EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(ref P p) { var l = Cache.List ??= new(); l.Clear(); l.Add(p.V); p.V = l.Count; } }")]
    [TestCase("public struct Work { public System.Collections.Generic.List<int> Items; } [EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(ref P p, Scratch<Work> work) { ref var w = ref work.Value; var items = w.Items ??= new(); items.Clear(); items.Add(p.V); p.V = items.Count; } }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(ref P p, Scratch<int[]> work) { if (work.Value == null) work.Value = new int[8]; work.Value[0] = p.V; p.V = work.Value[0]; } }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(ref Q q, in Link link, Lookup<P> ps) { var t = ps[link.Target]; q.V = t.HasValue ? t.Value.V : 0; } }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(in P p, ref Q q, Lookup<P> ps) => q.V = ps.Has(default) ? p.V : 0; }")]
    [TestCase("public record struct G(int Pos) : IComponent, ITile<int> { int IIndexed<int>.Key => Pos; static int ITileShape.Width => 4; static int ITileShape.Depth => 4; static int ITile<int>.Beside(int k, TileFaces f) => k + 1; } [EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(Tile<byte, G> t, Mut<DynamicBuffer<int>> next) { var pad = t.Pad(); foreach (var row in pad.Rows(TileReads.Layer)) next.Write().AsSpan()[row.Index] = pad.At(row)[1, 0, 0]; } }")]
    [TestCase("public struct Named : IComponent { public string Name; } [EcsSystem] public ref struct S { [ForEach] static void A(in Named n, ref P p) => p.V = n.Name.Remove(0, 1).Length; }")]
    [TestCase("public static class U { public static async System.Threading.Tasks.Task F(World world) { await System.Threading.Tasks.Task.Run(() => world.CreateEntity(new P())); world.CreateEntity(new P()); } }")]
    [TestCase("public static class U { public static void F(World world) { System.Threading.Tasks.Task.Run(() => world.CreateEntity(new P())).Wait(); world.CreateEntity(new P()); } }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void C(in P p) { using var w = new World(); w.CreateEntity(new Q()); } }")]
    [TestCase("[EcsSystem] public ref struct S { public const int N = 3; public static int Twice => N * 2; }")]
    [TestCase("[EcsSystem] public ref struct X { } public static class U { public static void F(World w) { if (w.TryGetSystem<X>(out var x)) x.Enabled = false; } }")] // a handle writes through
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Slices = 1), Changed<T>] static void A(ref P p) { } [ForEach(Slices = 8, Parallel = true)] static void B(ref Q q) { } }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Parallel = true, ChunkRows = 64)] static void A(ref P p) { } [ForEach(Parallel = true, ChunkRows = 0)] static void B(ref Q q) { } }")]
    [TestCase("public record struct Account(int Id) : IComponent, IUniqueIndexed<int> { public int Key => Id; } public static class U { public static Entity F(World w) => w.AccountIndex.Get(1); }")]
    [TestCase("public static class U { public static bool F(ref DynamicBuffer<int> b) => System.Runtime.CompilerServices.Unsafe.IsNullRef(ref b); }")] // a 'ref readonly' argument is no copy
    [TestCase("public static class U { public static bool F(in DynamicBuffer<int> b) => System.Runtime.CompilerServices.Unsafe.IsNullRef(in b); }")]
    [TestCase("public static class U { public static bool F(Entity e) { ref var b = ref e.TryGetRef<DynamicBuffer<int>>(out _); return !System.Runtime.CompilerServices.Unsafe.IsNullRef(in b) && b.Count > 0; } }")]
    [TestCase("public static class U { public static bool F(Entity e) { ref readonly var b = ref e.TryGetRef<DynamicBuffer<int>>(out _); return System.Runtime.CompilerServices.Unsafe.IsNullRef(in b); } }")]
    [TestCase("public struct Sum : ISingletonComponent, IReduction<Sum> { public int N; public static Sum Identity => default; public static Sum Combine(Sum a, Sum b) => new() { N = a.N + b.N }; } [EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(in P p, ref Reduce<Sum> sum, in Sum last) => sum.Value.N += p.V + last.N; }")]
    [TestCase("public struct Sum : ISingletonComponent, IReduction<Sum> { public int N; public static Sum Identity => default; public static Sum Combine(Sum a, Sum b) => new() { N = a.N + b.N }; } [EcsSystem] public ref struct S { [ForEach] static void A(in P p, Maybe<Q> q, ref Reduce<Sum> sum) => sum.Combine(new Sum { N = p.V + (q.HasValue ? q.Value.V : 0) }); }")]
    [TestCase("public struct Sum : ISingletonComponent, IReduction<Sum> { public int N; public static Sum Identity => default; public static Sum Combine(Sum a, Sum b) => new() { N = a.N + b.N }; } public static class R { [ForEach(Parallel = true, ChunkRows = 1024)] public static void A(in P p, ref Reduce<Sum> sum) => sum.Value.N += p.V; }")]
    [TestCase("public record struct G(int Pos) : IComponent, ITile<int> { int IIndexed<int>.Key => Pos; static int ITileShape.Width => 4; static int ITileShape.Depth => 4; static int ITile<int>.Beside(int k, TileFaces f) => k + 1; static int ITileShape.Halo => 4; static TileNeighborhood ITileShape.Neighborhood => TileNeighborhood.All; }")]
    [TestCase("public record struct G(int Pos) : IComponent, ITile<int> { int IIndexed<int>.Key => Pos; static int ITileShape.Width => 8; static int ITileShape.Depth => 1; static int ITileShape.Halo => 3; static TileFaces ITileShape.Faces => TileFaces.West | TileFaces.East; static int ITile<int>.Beside(int k, TileFaces f) => k + 1; }")]
    [TestCase("public record struct G(int Pos) : IComponent, ITile<int> { public static int H = 9; int IIndexed<int>.Key => Pos; static int ITileShape.Width => 4; static int ITileShape.Depth => 4; static int ITileShape.Halo => H; static int ITile<int>.Beside(int k, TileFaces f) => k + 1; }")]
    [TestCase("public record struct G(int Pos) : IComponent, ITile<int> { int IIndexed<int>.Key => Pos; static int ITileShape.Width => 4; static int ITileShape.Depth => 4; static int ITile<int>.Beside(int k, TileFaces f) => k + 1; } public record struct V(byte B); [EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(Tile<byte, G> t, Tile<V, G> u, Mut<DynamicBuffer<int>> b) => b.Write().SetCount(t.Cells.Length + u.Cells.Length); }")]
    [TestCase("public record struct H(int V); [EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(in P p, Emit<H> hits) => hits.Add(new H(p.V)); }")]
    [TestCase("public record struct H(int V); public struct Sum : ISingletonComponent, IReduction<Sum> { public int N; public static Sum Identity => default; public static Sum Combine(Sum a, Sum b) => new() { N = a.N + b.N }; } public static class R { [ForEach(Parallel = true)] public static void A(in P p, ref Reduce<Sum> sum, Emit<H> hits) { sum.Value.N += p.V; hits.AddRange(new H(p.V), new H(0)); } }")]
    [TestCase("public record struct H(int V); [EcsSystem] public ref struct S { [ForEach] static void A(in P p, Emit<H> hits) => hits.Add(new H(p.V)); [ForEach] static void B(in Q q, Emit<H> hits) => hits.Add(new H(q.V)); }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(ref P p, Entity e, in EntityRandom random) => p.V = random.Range(e.Id, 0, 0, 10) + (random.Chance(e.Id, 1, 0.5f) ? 1 : 0); }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, Entity e, in EntityRandom r) { if (p.V > 0) p.V = r.Range(e.Id, 0, 0, 6); else p.V = r.Range(e.Id, 0, 6, 9); } }")] // never both
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, Entity e, in EntityRandom r) { switch (p.V) { case 0: p.V = r.Range(e.Id, 0, 0, 6); break; default: p.V = r.Range(e.Id, 0, 6, 9); break; } } }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, Entity e, in EntityRandom r) { for (int i = 0; i < 3; i++) p.V += r.Range(e.Id, 1 + (uint)i, 1, 7); } }")] // the iteration's stream
    [TestCase("public static class U { public static int F(Entity[] es, in EntityRandom r) { int n = 0; foreach (var e in es) n += r.Range(e.Id, 0, 0, 6); return n; } }")] // an entity per iteration
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, Entity e, in Link l, in EntityRandom r) => p.V = r.Range(e.Id, 0, 0, 6) + r.Range(l.Target, 0, 0, 6); }")] // two entities
    [TestCase("public static class U { public static bool F(in EntityRandom r, EntityId id) => r.Bits(id, 3) == EntityRandom.Bits(r.Seed, id, r.Tick, 3) && r.Float(id, 3) == r.Float(id, 3); }")] // outside a pass: a check of the function
    [TestCase("[EcsSystem] public ref struct S { static void OnUpdate(World w) => Tick(w); static void Tick(World w) { } }")]   // called: a helper
    [TestCase("[EcsSystem] public ref struct S { static void OnUpdate() { } static void Update() { } }")]                 // the hook exists
    [TestCase("[EcsSystem] public ref struct S { static int Update(World w) => 0; static void Run(int n) { } }")]          // no hook's signature
    [TestCase("public static class U { static void OnUpate(World w) { } static void OnUpdate(int n) { } }")]             // not a system
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void Update(ref P p) => p.V++; }")]                    // a [ForEach] method
    public async Task ValidCodeIsNotReported(string source)
    {
        var errors = await Diagnose(Components, source);
        Assert.That(errors, Is.Empty);
    }

    // A message names the method as what it is ([ForEach] or [ForEntity]), what it costs there, and the parameter at fault.
    [TestCase("public static class H { [ForEntity] static void A(ref P p, in Q q, int n) => p.V += n; }", "EECS026",
        "[ForEntity] method 'A' does not use parameter 'q': its column is still loaded on every call; require the component with [With<Q>] instead")]
    [TestCase("public static class H { [ForEntity] static void A(Entity e, ref P p, int n) => p.V += n; }", "EECS026",
        "[ForEntity] method 'A' does not use parameter 'e': remove it")]
    [TestCase("public static class H { [ForEntity] static void A(ref P p, ref Q q) => p.V = q.V; }", "EECS025",
        "Parameter 'q' of [ForEntity] method 'A' (ref) is a write access, but the code only reads it: the system is scheduled as the component's writer, so no parallel stage runs it beside a reader; take it by 'in'")]
    [TestCase("public static class H { [ForEntity] static void A(ref T t, int v) { if (v > 0) t.V = v; } }", "EECS053",
        "Parameter 't' of [ForEntity] method 'A' takes T by 'ref', but some calls return without a write, and every call stamps the row as changed; take it as a Mut<T> and write through Write() or SetIfChanged")]
    [TestCase("[View] public ref struct VT { public ref T T; } [EcsSystem] public ref struct S { [ForEach] static void A(VT v, in P p) { if (p.V > 0) v.T.V = p.V; } }", "EECS053",
        "[ForEach] method 'A' writes T by 'ref' through field 'T' of view VT, but some rows pass through without a write, and every row it visits is stamped as changed; make the field a Mut<T> and write through Write() or SetIfChanged")]
    [TestCase("[View] public ref struct VT { public ref T T; public void Up(int n) { if (n > 0) T.V += n; } }", "EECS053",
        "View member 'Up' writes T by 'ref' through field 'T' of view VT, but some paths through it return without a write, and every row the view is built over is stamped as changed; make the field a Mut<T> and write through Write() or SetIfChanged")]
    [TestCase("public static class H { [ForEntity] static void A(Entity e, ref P p) => p.V = e.Read<Q>().V; }", "EECS027",
        "[ForEntity] method 'A' looks up 'Q' with 'e.Read<Q>()' beside the lookup its entry point makes; take it as a parameter ('in Q' or 'ref Q'), or a Maybe<Q> when some entities lack it")]
    [TestCase("public static class H { [ForEntity] static void A(ref P p) => p.V = new System.Collections.Generic.List<int>().Count; }", "EECS028",
        "'new System.Collections.Generic.List<int>()' allocates a new List in 'A', which runs on every call")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p) { var l = new System.Collections.Generic.List<int> { p.V }; p.V = l.Count; } }", "EECS028",
        "'new System.Collections.Generic.List<int> { p.V }' allocates a new List in 'A', which runs for every entity it visits, each update; a Scratch<System.Collections.Generic.List<int>> parameter keeps one per thread for the next rows (clear it before use)")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(in P p, ref Clock c) => c.Ticks += p.V; }", "EECS010",
        "[ForEach] method 'A' cannot run in parallel: 'ref Clock c' writes a singleton, which every chunk would race on: take it as 'in' to read it; a result per chunk goes into a 'ref Reduce<T>' (sums, minimums, maximums) or an Emit<T> (records), combined when the pass ends")]
    [TestCase("public static class R { [ForEach(Parallel = true)] static void A(ref P p, ref int n) => p.V = n; }", "EECS010",
        "[ForEach] method 'A' cannot run in parallel: it takes 'n' by 'ref', which every chunk would race on: pass it by value or 'in', or collect per-chunk results with a Reduce<T> or an Emit<T>")]
    [TestCase("public static class R { [ForEach(Parallel = true)] static void A(ref P p, System.ReadOnlySpan<int> s) => p.V = s[0]; }", "EECS010",
        "[ForEach] method 'A' cannot run in parallel: it takes 's', a ReadOnlySpan<int>, which the job the threads share cannot hold (a ref struct lives on one stack): pass an array, or read the data through a Lookup<T> or a singleton")]
    [TestCase("public static class H { [ForEntity] static void A(P p) { } }", "EECS002",
        "Parameter 'p' of [ForEntity] method 'A' must be a 'ref'/'in' IComponent or ISingletonComponent struct, a Span/ReadOnlySpan of an IComponent struct, a Maybe, Mut or Lookup of one, a 'ref Reduce' of a singleton, a Scratch, an Emit of a struct, an Entity or the World (outside a system, also a value the caller passes, of any other type)")]
    public async Task MessagesNameWhatTheyMean(string source, string id, string message)
    {
        var found = (await Analyze([Components, source])).Where(d => d.Id == id).Select(d => d.GetMessage()).ToArray();
        Assert.That(found, Is.EqualTo(new[] { message }));
    }

    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref Missing m) { } }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach, Removed<>, With<>] static void A(ref P p) { } }")]
    [TestCase("[UpdateBefore<Missing>] [EcsSystem] public ref struct S { [ForEach] static void A(ref P p) { } }")]
    public async Task UnfinishedCodeGetsOnlyTheCompilersErrors(string source)
    {
        var errors = await Diagnose(Components, source);
        Assert.That(errors.Where(e => !e.StartsWith("CS")), Is.Empty);
    }

    [Test]
    public async Task AStructOfAnotherAssemblyHoldingAnEntityIsReported()
    {
        var library = CSharpCompilation.Create("Library",
            [CSharpSyntaxTree.ParseText("namespace Library; public struct Pair { public EasyEcs.Entity A; public int B; } public struct Ids { public EasyEcs.EntityId A; }", Parse)],
            Basic.References.Append(MetadataReference.CreateFromFile(typeof(World).Assembly.Location)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var image = new System.IO.MemoryStream();
        Assert.That(library.Emit(image).Success, Is.True);
        var reference = MetadataReference.CreateFromImage(image.ToArray());
        async Task<string[]> Ids(string source)
        {
            var user = CSharpCompilation.Create("User", [CSharpSyntaxTree.ParseText("using EasyEcs;\n" + source, Parse)],
                Basic.References.Append(MetadataReference.CreateFromFile(typeof(World).Assembly.Location)).Append(reference),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            Driver().RunGeneratorsAndUpdateCompilation(user, out var output, out _);
            return (await output.WithAnalyzers([new EasyEcsAnalyzer()]).GetAnalyzerDiagnosticsAsync()).Select(d => d.Id).Distinct().ToArray();
        }

        Assert.That(await Ids("public struct Holds : IComponent { public Library.Pair P; }"), Does.Contain("EECS017"));
        Assert.That(await Ids("public static class U { public static void F(Entity e) => e.Set(new DynamicBuffer<Library.Pair>()); }"), Does.Contain("EECS017"));
        Assert.That(await Ids("public struct Holds : IComponent { public Library.Ids P; }"), Does.Not.Contain("EECS017"));
    }

    [Test]
    public async Task ABufferPassedByRefIsNoCopy()
    {
        var errors = await Diagnose(Components, """
            public static class U
            {
                static ref T Same<T>(ref T value) => ref value;
                public static int F(Entity e) { ref var b = ref Same(ref e.Get<DynamicBuffer<int>>()); return b.Count; }
            }
            """);
        Assert.That(errors, Is.Empty, string.Join(", ", errors));
    }

    [Test]
    public async Task ABufferReadThroughInCannotBeWritten()
    {
        var errors = await Diagnose(Components, "[EcsSystem] public ref struct S { [ForEach] static void A(in DynamicBuffer<int> b) => b.Add(1); }");
        Assert.That(errors.Any(e => e.StartsWith("CS")), Is.True, string.Join(", ", errors));
    }

    [Test]
    public void APartialMethodIsJudgedByItsBody()
    {
        var diagnostics = Diagnose(Components, """
            [EcsSystem] public ref partial struct S
            {
                [ForEach(Parallel = true)] static partial void A(Entity e, ref P p);
                static partial void A(Entity e, ref P p) { if (e.Has<Q>()) p.V++; }
            }
            """).Result;
        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public void OverloadedRunnersWithOneEntryPointAreReported()
    {
        var diagnostics = Diagnose(Components, "public static class R { [ForEach] static void A(ref P p) { } [ForEach] static void A(ref Q q) { } }").Result;
        Assert.That(diagnostics, Does.Contain("EECS021"));
    }
}
