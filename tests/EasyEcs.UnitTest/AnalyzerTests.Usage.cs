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
    public Task UsageRulesReport(string source, string id) => AssertReports(id, GeneratorTests.Components, Extra, source);

    [TestCase("public struct Named : IComponent, ITracked { public string Name; } public static class U { public static void F(Entity e) => e.Set(new Named()); }")] // Set compares nothing
    [TestCase("public static class U { public static void F(World w) { foreach (var item in w.Query<P>()) { } w.Restore(default!); } }")]
    [TestCase("public static class U { public static void F(World w, System.Collections.Generic.List<Entity> all) { foreach (var e in w.OwnedIndex.Find(default)) all.Add(e); } }")]
    [TestCase("public static class U { public static int F(World w, int n) => w.Count<P>(QueryFilter.Create().Slice(n % 1, 1)); }")]
    [TestCase("public static class U { public static void F(Entity e) { ref var p = ref e.Get<P>(); p.V = 1; e.Destroy(); } }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach, WithAny<Q, T>, Without<Q>] static void A(ref P p) => p.V++; }")]                                    // T still passes
    public Task UsageRulesLeaveAlone(string source) => AssertClean(GeneratorTests.Components, Extra, source);

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

    // The rules of calls and declarations that compile but do not do what they say (EECS036 for plain components,
    // EECS037, EECS038, EECS040-EECS045), and the false negatives of EECS014 and EECS017: each case reports exactly one.
    // EECS037: a hand-written job's WriteMask.
    [TestCase("public struct J : IForEach<T> { public void Execute(ref T t) => t.V++; public static int WriteMask => 0; }", "EECS037")]    // written, left out
    [TestCase("public struct J : IForEach<T> { public int N; public void Execute(ref T t) => N += t.V; }", "EECS037")]                        // read, stamped by default
    [TestCase("public struct J : IForEach<T, P> { public int N; public void Execute(ref T t, ref P p) => N += t.V + p.V; public static int WriteMask => 0b11; }", "EECS037")]
    [TestCase("public struct J : IForEachEntity<P> { public void Execute(Entity e, ref P p) { p.V = 1; e.Set(new Q()); } public static int WriteMask => 0; }", "EECS037")] // not carried
    [TestCase("public struct J : IForEachBatch<T> { public void Execute(EntitySpan e, System.Span<T> t) => t[0].V = 1; public static int WriteMask => 0; }", "EECS037")]
    [TestCase("public struct J : IForEach<T> { public void Execute(ref T t) => Helpers.Inc(ref t.V); static int IForEach<T>.WriteMask => 0; }", "EECS037")]  // explicit
    // EECS038: a changed copy that is dropped.
    [TestCase("public static class U { public static void F(Entity e) { var p = e.Get<P>(); p.V = 1; } }", "EECS038")]
    [TestCase("public static class U { public static void F(Entity e) { var p = e.P; p.V -= 1; } }", "EECS038")]                          // a generated property
    [TestCase("public static class U { public static void F(Entity e) { var t = e.T; t.V++; } }", "EECS038")]                           // ref readonly (tracked)
    [TestCase("public static class U { public static void F(World w) { var c = w.Clock; c.Ticks++; } }", "EECS038")]                    // a singleton
    [TestCase("public static class U { public static void F(World w) { foreach (var item in w.Query<P>()) { var p = item.Component1; p.V++; } } }", "EECS038")]
    [TestCase("public static class U { public static void F(Entity e) { if (e.TryGet<P>(out var p)) p.V = 1; } }", "EECS038")]          // TryGet's copy
    [TestCase("public static class U { public static void F(Entity e) { var p = e.P; if (p.V > 0) p.V = 0; } }", "EECS038")]              // read before, not after
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, Maybe<Q> q) { if (!q.HasValue) return; var v = q.Value; v.V = p.V; } }", "EECS038")]
    [TestCase("public static class U { public static System.Action F(Entity e) => () => { var p = e.Get<P>(); p.V = 1; }; }", "EECS038")]        // in a lambda
    [TestCase("public static class U { public static void F(Entity e) { void G() { var p = e.P; p.V = 2; } G(); } }", "EECS038")]                  // in a local function
    [TestCase("public static class U { public static void F(Entity e) { var c = e.Get<Counter>(); c.Bump(); } }", "EECS038")]                      // a mutating method
    // EECS040: SetIfChanged on a type it cannot compare.
    [TestCase("public struct Named : IComponent, ITracked { public string Name; } public static class U { public static bool F(Entity e) => e.SetIfChanged(new Named()); }", "EECS040")]
    [TestCase("public struct Named : IComponent, ITracked { public string Name; } [EcsSystem] public ref struct S { [ForEach] static void A(Mut<Named> n, in P p) { if (p.V > 0) n.SetIfChanged(default); } }", "EECS040")]
    // EECS041: a call a pass refuses, during one.
    [TestCase("public static class U { public static void F(World w) { foreach (var item in w.Query<P>()) w.TrimExcess(); } }", "EECS041")]
    [TestCase("public static class U { public static void F(World w, WorldSnapshot s) { foreach (var item in w.Query<P>()) if (item.Component1.V < 0) w.Restore(s); } }", "EECS041")]
    [TestCase("public static class U { public static void F(World w, World other, EntityId[] ids) { foreach (var item in other.Query<P>()) w.MoveEntities(other, ids); } }", "EECS041")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(Entity e, ref P p) { if (p.V < 0) e.World.Capture(); } }", "EECS041")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, World world) { if (p.V < 0) world.Save(new System.Buffers.ArrayBufferWriter<byte>()); } }", "EECS041")]
    [TestCase("public static class R { [ForEach] static void A(World w, ref P p) { if (p.V < 0) w.TrimExcess(); } }", "EECS041")]
    [TestCase("public struct J : IForEachEntity<P> { public void Execute(Entity e, ref P p) { if (p.V < 0) e.World.TrimExcess(); } }", "EECS041")]
    // EECS042: an indexed component changed while its index is walked.
    [TestCase("public static class U { public static void F(World w) { foreach (var e in w.Index<Owned, EntityId>().Find(default)) e.Set(new Owned()); } }", "EECS042")]
    [TestCase("public static class U { public static void F(World w) { foreach (var e in w.OwnedIndex.Find(default)) e.SetOwned(default); } }", "EECS042")]  // generated
    [TestCase("public static class U { public static void F(World w, EntityId id, EntityId other) { foreach (var c in w.Children(id)) c.Set(new ChildOf(other)); } }", "EECS042")]
    [TestCase("public static class U { public static void F(World w, EntityId id, EntityId other) { foreach (var c in w.Descendants(id)) c.Set(new ChildOf(other)); } }", "EECS042")]
    [TestCase("public static class U { public static void F(World w, EntityId a, EntityId b) { foreach (var e in w.Relations<Rel>().Find(a)) e.Link(new Rel(b)); } }", "EECS042")]
    [TestCase("public static class U { public static void F(World w) { foreach (var e in w.OwnedIndex.Find(default)) w.CreateEntity(new Owned()); } }", "EECS042")] // a creation
    [TestCase("public static class U { public static void F(World w, EntityId a, EntityId b) { foreach (var e in w.OwnedIndex.Find(a)) { e.Set(new Owned { Owner = b }); if (b.Index > 0) break; } } }", "EECS042")]
    [TestCase("public struct Rank : IComponent, IIndexed<int> { public int V; public readonly int Key => V; } public static class U { public static void F(World w) { foreach (var e in w.RankIndex.Ascending()) e.Set(new Rank { V = 1 }); } }", "EECS042")]
    // EECS043: a record struct key that prints itself.
    [TestCase("public readonly record struct Shop(long Id) : IComponent, IIndexed<Shop> { public Shop Key => this; }", "EECS043")]
    [TestCase("public interface IActor<TSelf> : IComponent, IIndexed<TSelf> where TSelf : struct, IActor<TSelf>, System.IEquatable<TSelf>; public readonly record struct Shop(long Id) : IActor<Shop> { public Shop Key => this; }", "EECS043")]
    [TestCase("public readonly record struct Seat(long Id) : IComponent, IUniqueIndexed<Seat> { public Seat Key => this; }", "EECS043")]
    // EECS044: a sliced filter where every row is needed, and a constant slice out of range.
    [TestCase("public static class U { public static int F(World w) => w.Count<P>(QueryFilter.Create().Slice(0, 2)); }", "EECS044")]
    [TestCase("public static class U { public static bool F(World w) { var f = QueryFilter.Create().Slice(1, 4).With<Q>(); return w.Any<P>(f); } }", "EECS044")]
    [TestCase("public static class U { static readonly QueryFilter Half = QueryFilter.Create().Slice(0, 2); public static void F(World w) { w.DestroyAll<P>(Half); } }", "EECS044")]
    [TestCase("public static class U { public static int F(World w, int n) => w.SetAll(new Q(), QueryFilter.Create().Slice(0, n)); }", "EECS044")]
    [TestCase("public static class U { public static QueryFilter F() => QueryFilter.Create().Slice(3, 2); }", "EECS044")]
    [TestCase("public static class U { public static QueryFilter F() => QueryFilter.Create().Slice(0, 0); }", "EECS044")]
    // EECS045: the world's own tags written by hand.
    [TestCase("public static class U { public static void F(Entity e) => e.Remove<Retired>(); }", "EECS045")]
    [TestCase("public static class U { public static void F(Entity e) => e.Set(new Disabled<P>()); }", "EECS045")]
    [TestCase("public static class U { public static Entity F(World w) => w.CreateEntity(new P(), new Retired()); }", "EECS045")]
    [TestCase("public static class U { public static int F(World w) => w.RemoveAll<Disabled<P>>(); }", "EECS045")]
    [TestCase("public static class U { public static Prefab F() => new Prefab().Set(new Retired()); }", "EECS045")]
    [TestCase("public static class U { public static void F(Entity e) => e.GetOrAdd<Disabled<Q>>(); }", "EECS045")]
    // EECS047: a ref used after a structural change.
    [TestCase("public static class U { public static void F(Entity e) { ref var p = ref e.Get<P>(); e.Set(new Q()); p.V = 1; } }", "EECS047")]
    [TestCase("public static class U { public static void F(World w) { foreach (var item in w.Query<P>()) { item.Entity.Set(new Q()); item.Component1.V = 1; } } }", "EECS047")]
    [TestCase("public static class U { public static void F(World w, Entity e) { ref var p = ref e.P; w.CreateEntity(new P()); p.V = 1; } }", "EECS047")]
    [TestCase("public static class U { public static void F(World w, Entity e) { ref int v = ref e.Get<P>().V; w.Update(); v = 1; } }", "EECS047")]
    [TestCase("public static class U { public static int F(Entity e) { ref readonly var p = ref e.Read<P>(); e.Set(new Boss()); return p.V; } }", "EECS047")]
    [TestCase("public static class U { public static void F(Entity e, Entity other) { ref var p = ref e.Get<P>(); other.Destroy(); p.V = 1; } }", "EECS047")] // the last row moves into the hole
    // EECS048: a component that cannot be disabled.
    [TestCase("public static class U { public static void F(Entity e) => e.Disable<ChildOf>(); }", "EECS048")]
    [TestCase("public static class U { public static void F(Entity e) => e.Disable<Lease>(); }", "EECS048")]
    [TestCase("public static class U { public static int F(World w) => w.DisableAll<ChildOf>(); }", "EECS048")]
    [TestCase("public static class U { public static void F(World w, EntityId id) { foreach (var c in w.Children(id)) c.Disable<ChildOf>(); } }", "EECS048")]
    // EECS049: filters no entity passes.
    [TestCase("[EcsSystem] public ref struct S { [ForEach, With<Q>, Without<Q>] static void A(ref P p) => p.V++; }", "EECS049")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach, Without<P>] static void A(ref P p) => p.V++; }", "EECS049")]
    [TestCase("[With<Q>] public struct G : ISystemGroup; [UpdateInGroup<G>] [EcsSystem] public ref struct S { [ForEach, Without<Q>] static void A(ref P p) => p.V++; }", "EECS049")]
    [TestCase("public struct G : ISystemGroup, ISystemGroupWithout<Q>; [UpdateInGroup<G>] [EcsSystem] public ref struct S { [ForEach] static void A(ref P p, in Q q) => p.V += q.V; }", "EECS049")]
    [TestCase("public static class R { [ForEach, Without<T>] static void A(Mut<T> t) => t.Write().V++; }", "EECS049")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach, Changed<T>, Without<T>] static void A(ref P p) => p.V++; }", "EECS049")]
    // EECS050: a second group in one filter.
    [TestCase("public static class U { public static QueryFilter F() => QueryFilter.Create().InGroup<Zone>(1).InGroup<Zone>(2); }", "EECS050")]
    [TestCase("public static class U { public static QueryFilter F(int a) => QueryFilter.Create().InGroup<Zone>(a).With<P>().InGroup<Area>(a); }", "EECS050")]
    [TestCase("public static class U { public static QueryFilter F() => QueryFilter.Create().InZone(1).InZone(2); }", "EECS050")]  // generated
    // EECS051: a world update during a pass (EECS034 from a system).
    [TestCase("public static class U { public static void F(World w) { foreach (var item in w.Read<P>()) w.Update(); } }", "EECS051")]
    [TestCase("public static class R { [ForEach] static void A(World w, ref P p) { if (p.V < 0) w.Update(); } }", "EECS051")]
    [TestCase("[EcsSystem] public ref struct S { static void OnUpdate(World world) { foreach (var item in world.Read<P>()) world.Update(); } }", "EECS034")]
    // EECS036: a plain component with two keys.
    [TestCase("public struct Seat : IComponent, IIndexed<int>, IIndexed<string> { readonly int IIndexed<int>.Key => 0; readonly string IIndexed<string>.Key => \"\"; }", "EECS036")]
    // EECS014: continuations, method groups, timers, and TrimExcess.
    [TestCase("public static class U { public static void F(World w, System.Threading.Tasks.Task t) { t.ContinueWith(_ => w.CreateEntity()); w.CreateEntity(); } }", "EECS014")]
    [TestCase("public class U { World _w = new(); void Work() => _w.CreateEntity(); public void F() { System.Threading.Tasks.Task.Run(Work); _w.CreateEntity(); } }", "EECS014")]
    [TestCase("public class U { World _w = new(); void Work(object s) => _w.CreateEntity(); public void F() { System.Threading.ThreadPool.QueueUserWorkItem(Work); _w.Update(); } }", "EECS014")]
    [TestCase("public class U { World _w = new(); public void F(System.Timers.Timer t) { t.Elapsed += (_, _) => _w.CreateEntity(); _w.Update(); } }", "EECS014")]
    [TestCase("public static class U { public static void F(World w) { System.Threading.Tasks.Task.Run(() => w.TrimExcess()); w.TrimExcess(); } }", "EECS014")]
    // EECS017: a buffer of handles.
    [TestCase("public static class U { public static void F(Entity e) => e.Set(new DynamicBuffer<Entity>()); }", "EECS017")]
    [TestCase("public struct Pair { public Entity A; } public static class U { public static void F(Entity e) => e.Get<DynamicBuffer<Pair>>().Add(default); }", "EECS017")]
    public async Task UsageMisuseIsReported(string source, string id)
    {
        var errors = await Diagnose(Components, Extra, source);
        Assert.That(errors, Does.Contain(id));
        Assert.That(errors.Where(e => !e.StartsWith("CS")), Is.EqualTo(new[] { id }), "exactly one EasyEcs diagnostic");
        Assert.That(errors.Where(e => e.StartsWith("CS")), Is.Empty, "the case compiles");
    }

    // The same rules leave alone: the code that does what it says.
    [TestCase("public struct J : IForEach<T, P> { public void Execute(ref T t, ref P p) => p.V = t.V; public static int WriteMask => 0b10; }")]
    [TestCase("public struct J : IForEach<P> { public int N; public void Execute(ref P p) => N += p.V; }")]                           // untracked: no stamp
    [TestCase("public struct J : IForEach<T> { public void Execute(ref T t) => t.V++; }")]                                           // the default: all
    [TestCase("public struct J : IForEach<P> { public void Execute(ref P p) => p.V++; public static int WriteMask => 0; }")]        // untracked, no move
    [TestCase("public struct J : IForEach<T> { public void Execute(ref T t) { } public static int WriteMask => 1; public static int SelfStamped => 1; }")]
    [TestCase("public struct J : IForEach<T> { public int N; public void Execute(ref T t) => N += Helpers.Look(new P { V = t.V }); public static int WriteMask => Bits(); static int Bits() => 1; }")] // computed
    [TestCase("public static class U { public static void F(Entity e) { var p = e.Get<P>(); p.V = 1; e.Set(p); } }")]
    [TestCase("public static class U { public static int F(Entity e) { var p = e.P; p.V++; return p.V; } }")]
    [TestCase("public static class U { public static int F(Entity e, int n) { int s = 0; var p = e.P; for (int i = 0; i < n; i++) { s += p.V; p.V++; } return s; } }")]
    [TestCase("public static class U { public static void F(Entity e) { ref var p = ref e.P; p.V = 1; } }")]
    [TestCase("public static class U { public static System.Func<int> F(Entity e) { var p = e.P; p.V = 1; return () => p.V; } }")]
    [TestCase("public static class U { public static int F(Entity e) { var p = e.P; return p.V; } }")]
    [TestCase("public static class U { public static int F(Entity e) => e.TryGet<P>(out var p) ? p.V : 0; }")]
    [TestCase("public record struct Named(string Name) : IComponent, ITracked; public static class U { public static bool F(Entity e) => e.SetIfChanged(new Named(\"a\")); }")]
    [TestCase("public static class U { public static bool F(Entity e) => e.SetIfChanged(new T { V = 1 }); }")]                      // unmanaged: its bytes
    [TestCase("public static class U { public static void F(World w, World other) { foreach (var item in w.Query<P>()) other.TrimExcess(); } }")]
    [TestCase("public static class U { public static void F(World w) { foreach (var item in w.Query<P>()) { } w.TrimExcess(); } }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p) { using var w = new World(); w.TrimExcess(); } }")]
    [TestCase("public static class U { public static void F(World w) { foreach (var e in w.OwnedIndex.Find(default)) e.Destroy(); } }")]
    [TestCase("public static class U { public static void F(World w) { foreach (var e in w.OwnedIndex.Find(default)) e.Set(new P()); } }")]
    [TestCase("public static class U { public static void F(World w, System.Collections.Generic.List<Entity> found) { foreach (var e in w.OwnedIndex.Find(default)) found.Add(e); foreach (var e in found) e.RemoveOwned(); } }")]
    [TestCase("public readonly record struct Shop(long Id) : IComponent, IIndexed<Shop> { Shop IIndexed<Shop>.Key => this; }")]
    [TestCase("public readonly struct Shop : IComponent, IIndexed<Shop>, System.IEquatable<Shop> { public readonly long Id; public Shop Key => this; public bool Equals(Shop o) => Id == o.Id; }")] // no record: no printed properties
    [TestCase("public static class U { public static int F(World w) => w.Count<P>(QueryFilter.Create().Slice(0, 1)); }")]           // one slice: the whole tables
    [TestCase("public static class U { public static int F(World w) { var f = QueryFilter.Create().Slice(0, 2); f = QueryFilter.Create(); return w.Count<P>(f); } }")]
    [TestCase("public struct J : IForEach<P> { public void Execute(ref P p) => p.V++; } public static class U { public static void F(World w) { var j = new J(); w.ForEach<P, J>(ref j, QueryFilter.Create().Slice(0, 2)); } }")]
    [TestCase("public static class U { public static bool F(Entity e) => e.Has<Retired>() || e.IsDisabled<P>(); }")]
    [TestCase("public static class U { public static int F(World w) => w.Count<P>(QueryFilter.Create().With<Retired>().Without<Disabled<Q>>()); }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach, With<Disabled<P>>] static void A(ref Q q) => q.V++; }")]
    [TestCase("public static class U { public static void F(Entity e) { e.Disable<P>(); e.Enable<P>(); } }")]
    [TestCase("public class U { World _w = new(); void Work() => _w.CreateEntity(); public async System.Threading.Tasks.Task F() { await System.Threading.Tasks.Task.Run(Work); _w.CreateEntity(); } }")] // awaited
    [TestCase("public class U { World _w = new(); void Work() => _w.CreateEntity(); public void F() { Work(); _w.CreateEntity(); } }")]         // no hand-off
    [TestCase("public static class U { static void Work(object s) { using var w = new World(); w.CreateEntity(); } public static void F() => System.Threading.ThreadPool.QueueUserWorkItem(Work); }")] // a world of its own
    [TestCase("public static class U { public static void F(Entity e) => e.Set(new DynamicBuffer<EntityId>()); }")]
    // A 'ref readonly' local reads (EECS037), a readonly method reads and a copy written back is kept (EECS038).
    [TestCase("public struct J : IForEach<T> { public int N; public void Execute(ref T t) { ref readonly var x = ref t; N += x.V; } public static int WriteMask => 0; }")]
    [TestCase("public static class U { public static int F(Entity e) { var c = e.Get<Counter>(); return c.Peek(); } }")]
    [TestCase("public static class U { public static void F(Entity e) { var c = e.Get<Counter>(); c.Bump(); e.Set(c); } }")]
    // EECS041 / EECS042: a walk left right after the change, removals, dropped links.
    [TestCase("public static class U { public static void F(World w, EntityId a, EntityId b) { foreach (var e in w.OwnedIndex.Find(a)) { e.Set(new Owned { Owner = b }); break; } } }")]
    [TestCase("public static class U { public static Entity F(World w, EntityId a, EntityId b) { foreach (var e in w.OwnedIndex.Find(a)) { e.Set(new Owned { Owner = b }); return e; } return default; } }")]
    [TestCase("public static class U { public static void F(World w, EntityId a) { foreach (var e in w.OwnedIndex.Find(a)) { if (e.Id.Index > 3) { e.Set(new Owned()); throw new System.InvalidOperationException(); } } } }")]
    [TestCase("public static class U { public static void F(World w) { foreach (var e in w.OwnedIndex.Find(default)) e.RemoveOwned(); } }")]
    [TestCase("public static class U { public static void F(World w, EntityId a) { foreach (var e in w.Relations<Rel>().Find(a)) e.Unlink<Rel>(a); } }")]
    [TestCase("public static class U { public static void F(World w, EntityId id) { foreach (var c in w.Descendants(id)) c.Remove<ChildOf>(); } }")]
    // EECS043: a record that prints itself its own way.
    [TestCase("public readonly record struct Shop(long Id) : IComponent, IIndexed<Shop> { public Shop Key => this; public override string ToString() => $\"Shop {Id}\"; }")]
    [TestCase("public readonly record struct Shop(long Id) : IComponent, IIndexed<Shop> { public Shop Key => this; private bool PrintMembers(System.Text.StringBuilder b) { b.Append(Id); return true; } }")]
    // EECS047: no move (a removal flips a bit, a Set of the component the ref holds overwrites it), a branch, a ref taken again.
    [TestCase("public static class U { public static void F(Entity e) { ref var p = ref e.Get<P>(); e.Remove<Q>(); p.V = 1; } }")]
    [TestCase("public static class U { public static void F(Entity e) { ref var p = ref e.Get<P>(); e.Set(new P { V = 2 }); p.V++; } }")]
    [TestCase("public static class U { public static void F(Entity e) { ref var p = ref e.Get<P>(); if (p.V > 0) e.Set(new Q()); else p.V = 1; } }")]
    [TestCase("public static class U { public static void F(Entity e) { ref var p = ref e.Get<P>(); e.Set(new Q()); p = ref e.Get<P>(); p.V = 1; } }")]
    [TestCase("public static class U { public static void F(Entity e) { ref var p = ref e.Get<P>(); p.V = 1; e.Set(new Q()); } }")]
    [TestCase("public static class U { public static int F(World w) { int n = 0; foreach (var item in w.Query<P>()) { item.Entity.Destroy(); n += item.Component1.V; } return n; } }")] // a hole: the row stays
    [TestCase("public static class U { public static void F(World w) { foreach (var item in w.Query<P>()) { item.Component1.V = 1; item.Entity.Set(new Q()); } } }")]
    // EECS048-EECS051: the forms that work.
    [TestCase("public static class U { public static int F(World w) => w.DisableAll<P>() + w.EnableAll<ChildOf>(); }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach, With<Q>, Without<Boss>] static void A(ref P p) => p.V++; }")]
    [TestCase("[Without<Q>] public struct G : ISystemGroup; [UpdateInGroup<G>] [EcsSystem] public ref struct S { [ForEach, With<Boss>] static void A(ref P p) => p.V++; }")]
    [TestCase("public static class U { public static QueryFilter F(int a, int b) => QueryFilter.Create().InGroup<Zone>(1).InGroup<Zone>(1).InGroup<Zone>(a).InZone(b); }")]
    [TestCase("public static class U { public static void F(World w, World other) { foreach (var item in w.Query<P>()) other.Update(); w.Update(); } }")]
    public async Task UsageValidCodeIsNotReported(string source)
    {
        var errors = await Diagnose(Components, Extra, source);
        Assert.That(errors, Is.Empty);
    }

    [TestCase("foreach (var item in w.Read<P>()) w.TrimExcess();", "this foreach over Read")]
    [TestCase("foreach (var item in w.Query<P>()) w.TrimExcess();", "this foreach over Query")]
    public async Task ARefusedCallNamesTheLoopItRunsIn(string loop, string named)
    {
        var diagnostics = await Analyze([Components, "public static class U { public static void F(World w) { " + loop + " } }"]);
        Assert.That(diagnostics.Single(d => d.Id == "EECS041").GetMessage(), Does.Contain(named));
    }

    // Mut<T> parameters (EECS025 never written, EECS039 untracked), the cold calls of EECS028, and the lookups and
    // allocations of a hand-written job's Execute (EECS027, EECS028).
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(Mut<T> t, ref P p) => p.V = t.Value.V; }", "EECS025")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(Mut<P> p, in Q q) { if (q.V > 0) p.Write().V = q.V; } }", "EECS039")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(Mut<P> p, in Q q) { if (q.V > p.Value.V) p.Write() = new P { V = q.V }; } }", "EECS039")]
    [TestCase("[EcsSystem] public ref struct S { static void OnUpdate(World world) => world.TrimExcess(); }", "EECS028")]
    [TestCase("[EcsSystem] public ref struct S { static void OnUpdate(World world) { if (world.DescribeTables().Length > 9) world.Clock.Ticks++; } }", "EECS028")]
    [TestCase("[EcsSystem] public ref struct S { static void OnUpdate(World world) { if (world.DescribeSchedule().Groups.Length > 9) world.Clock.Ticks++; } }", "EECS028")]
    [TestCase("[EcsSystem] public ref struct S { static void OnUpdate(World world) { if (world.Capture() == null) world.Clock.Ticks++; } }", "EECS028")]
    [TestCase("[EcsSystem] public ref struct S { static void OnUpdate(World world) => world.Instantiate(new Prefab()); }", "EECS028")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(Entity e, ref P p) => p.V = e.Describe().Length; }", "EECS028")]
    [TestCase("public struct J : IForEachEntity<P> { public void Execute(Entity e, ref P p) => p.V = e.Read<Q>().V; }", "EECS027")] // EECS033 defers
    [TestCase("public struct J : IForEach<P> { public void Execute(ref P p) { var all = new System.Collections.Generic.List<int>(); p.V = all.Count; } }", "EECS028")]
    // EECS046: a query that only reads tracked components.
    [TestCase("public static class U { public static int F(World w) { int n = 0; foreach (var item in w.Query<T, P>()) n += item.T.V + item.P.V; return n; } }", "EECS046")]
    [TestCase("public static class U { public static int F(World w) { int n = 0; foreach (var item in w.Query<DynamicBuffer<P>>()) n += item.Component1.Count; return n; } }", "EECS046")]
    [TestCase("public static class U { public static void F(World w, System.Collections.Generic.List<Entity> all) { foreach (var item in w.Query<T>()) all.Add(item.Entity); } }", "EECS046")]
    [TestCase("public static class U { public static int F(World w) { int n = 0; foreach (var item in w.Query<T>()) { ref readonly var t = ref item.Component1; n += t.V; } return n; } }", "EECS033", "EECS046")]
    [TestCase("public static class U { public static int F(World w) { int n = 0; foreach (QueryItem<T> item in w.Query<T>()) n += item.T.V; return n; } }", "EECS046")] // typed by hand: no fix
    // EECS053: a tracked component written by ref on some rows only (an early return, a branch, a loop that may not run).
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref T t, in P p) { if (p.V > 0) t.V = p.V; } }", "EECS053")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref T t, in P p) { if (p.V > 0) return; t.V = p.V; } }", "EECS053")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref T t, in P p) { if (p.V > 0) t.V = 1; else if (p.V < 0) t.V = 2; } }", "EECS053")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref T t, in P p) { switch (p.V) { case 1: t.V = 1; break; case 2: return; default: t.V = 3; break; } } }", "EECS053")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref DynamicBuffer<Loot> loot, in P p) { if (p.V > 0) loot.Add(new Loot { Item = p.V }); } }", "EECS053")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref T t, in DynamicBuffer<Loot> hits) { foreach (var hit in hits) t.V -= hit.Item; } }", "EECS053")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref T t, in P p) { if (p.V > t.V) Helpers.Inc(ref t.V); } }", "EECS053")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(ref T t, in P p) { if (p.V == 0) return; t.V++; } }", "EECS053")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(Entity e, ref T t, in P p) { if (p.V < 0) { e.Destroy(); return; } t.V = p.V; } }", "EECS053")]
    // EECS052: a component added right after the entity is created.
    [TestCase("public static class U { public static void F(World w) { var e = w.CreateEntity(new P()); e.Set(new Q { V = 1 }); } }", "EECS052")]
    [TestCase("public static class U { public static void F(World w) { var e = w.CreateEntity(); e.SetBoss(); } }", "EECS052")]
    [TestCase("public static class U { public static void F(World w, Prefab p) { Entity e = w.Instantiate(p, new P()); e.Set(new Q()); } }", "EECS052")]
    [TestCase("public static class U { static readonly Prefab Room = new Prefab().Set(new P()).SetAbsent<Q>(); public static void F(World w) { var e = w.Instantiate(Room); e.Set(new Q()); } }")] // the column is there
    [TestCase("public static class U { public static void F(World w) { var p = new Prefab().Set(new P()); p.SetAbsent<Q>(); var e = w.Instantiate(p); e.Set(new Q()); } }")]            // set on the local
    [TestCase("public static class U { static readonly Prefab Room = new Prefab().Set(new P()).SetAbsent<T>(); public static void F(World w) { var e = w.Instantiate(Room); e.Set(new Q()); } }", "EECS052")] // another column
    public async Task UsageCostsAreReported(string source, params string[] ids)
    {
        var costs = await Costs(Components, Extra, source);
        Assert.That(costs.Select(d => d.Id).Distinct().OrderBy(x => x), Is.EqualTo(ids),
            string.Join("\n", costs.Select(d => d.GetMessage())));
        Assert.That((await Diagnose(Components, Extra, source)).Where(e => e.StartsWith("CS")), Is.Empty, "the case compiles");
    }

    // EECS053 is left out: every path writes, the stamp is wanted, the component is not tracked, a batch method, a throw.
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref T t, in P p) { if (p.V > 0) t.V = p.V; else t.V = 0; } }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref T t, in P p) { if (p.V > 0) { t.V = p.V; return; } t.V = 0; } }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref T t, in P p) { t.V = 0; if (p.V > 0) t.V = p.V; } }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref T t, in P p) => t.V = p.V; }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref T t, in P p) => t.V = p.V > 0 ? p.V : t.V; }")]       // written on every row
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref T t, in P p) { if (p.V < 0) throw new System.InvalidOperationException(); t.V = p.V; } }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref T t, in P p) { while (true) { t.V++; if (t.V > p.V) return; } } }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, in Q q) { if (q.V > 0) p.V = q.V; } }")]                   // not tracked
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(System.Span<T> t, System.ReadOnlySpan<P> p) { for (int i = 0; i < t.Length; i++) if (p[i].V > 0) t[i].V = p[i].V; } }")] // batch
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref T t, in P p) { try { if (p.V > 0) return; } finally { t.V = p.V; } } }")]  // a finally: left out
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(Mut<T> t, in P p) { if (p.V > 0) t.Write().V = p.V; } }")]       // written
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(Mut<T> t, in P p) => t.SetIfChanged(new T { V = p.V }); }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(Mut<P> p, in Q q) => p.SetIfChanged(new P { V = q.V }); }")]       // a comparison: not a plain ref
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(Mut<T> t, in P p) { if (p.V > 0) Helpers.Inc(ref t.Write().V); } }")]
    [TestCase("[UpdateInterval(1200)] [EcsSystem] public ref struct S { static void OnUpdate(World world) => world.TrimExcess(); }")] // its cadence
    [TestCase("public struct J : IForEach<P> { public void Execute(ref P p) => p.V++; }")]
    [TestCase("public static class U { public static void F(World w) { foreach (var item in w.Query<T>()) item.T.V++; } }")]                      // a write
    [TestCase("public static class U { public static void F(World w) { foreach (var item in w.Query<T, P>()) { ref var t = ref item.T; t.V = item.P.V; } } }")]
    [TestCase("public static class U { public static void F(World w) { foreach (var item in w.Query<T, P>()) item.P.V = item.T.V; } }")]           // writes P: a Query
    [TestCase("public static class U { public static int F(World w) { int n = 0; foreach (var item in w.Query<P>()) n += item.P.V; return n; } }")]  // untracked
    [TestCase("public static class U { public static int F(World w) { int n = 0; foreach (var item in w.Read<T>()) n += item.T.V; return n; } }")]   // already Read
    [TestCase("public static class U { public static void F(World w) { foreach (var item in w.Query<P, ChildOf>()) item.Entity.Disable<ChildOf>(); } }")] // no DisableAll (EECS048)
    [TestCase("public static class U { public static void F(World w) { var e = w.CreateEntity(new P()); e.Set(new P { V = 1 }); } }")]            // given: overwritten
    [TestCase("public static class U { public static void F(World w) { var e = w.CreateEntity(new P()); e.Set(new Link { Target = e }); } }")]   // reads the entity
    [TestCase("public static class U { public static void F(World w) { var e = w.CreateEntity(new P()); e.Set(new Q { V = w.Count<P>() }); } }")] // a call: runs after the creation
    [TestCase("public static class U { public static void F(World w) { var e = w.CreateEntity(new P()); e.SetZone(2); } }")]               // grouped
    [TestCase("public static class U { public static void F(World w) { var e = w.CreateEntity(new P()); w.Update(); e.Set(new Q()); } }")]       // not right after
    public async Task UsageCheapCodeIsNotReported(string source)
    {
        var costs = await Costs(Components, Extra, source);
        Assert.That(costs.Select(d => d.Id), Is.Empty, string.Join("\n", costs.Select(d => d.GetMessage())));
    }

    [TestCase("EECS036", DiagnosticSeverity.Error)]
    [TestCase("EECS037", DiagnosticSeverity.Warning)]
    [TestCase("EECS038", DiagnosticSeverity.Warning)]
    [TestCase("EECS039", DiagnosticSeverity.Warning)]
    [TestCase("EECS046", DiagnosticSeverity.Warning)]
    [TestCase("EECS040", DiagnosticSeverity.Error)]
    [TestCase("EECS041", DiagnosticSeverity.Error)]
    [TestCase("EECS042", DiagnosticSeverity.Error)]
    [TestCase("EECS043", DiagnosticSeverity.Error)]
    [TestCase("EECS044", DiagnosticSeverity.Error)]
    [TestCase("EECS045", DiagnosticSeverity.Error)]
    [TestCase("EECS047", DiagnosticSeverity.Warning)]
    [TestCase("EECS048", DiagnosticSeverity.Error)]
    [TestCase("EECS049", DiagnosticSeverity.Error)]
    [TestCase("EECS050", DiagnosticSeverity.Error)]
    [TestCase("EECS051", DiagnosticSeverity.Error)]
    [TestCase("EECS052", DiagnosticSeverity.Warning)]
    [TestCase("EECS053", DiagnosticSeverity.Warning)]
    public void UsageRulesHaveTheirSeverity(string id, DiagnosticSeverity severity) =>
        Assert.That(new EasyEcsAnalyzer().SupportedDiagnostics.Where(d => d.Id == id).Select(d => d.DefaultSeverity), Is.Not.Empty.And.All.EqualTo(severity)); // every descriptor of the id (EECS025 has a compilation-end one)
}
