using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EasyEcs.CodeFixes;
using EasyEcs.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>Applies EasyEcsCodeFixes to the diagnostics of EasyEcsAnalyzer in a workspace: the fixed code compiles clean.</summary>
public class CodeFixTests
{
    private const string Data = """
        using EasyEcs;
        using RO = System.ReadOnlySpan<Holder>;
        public struct P : IComponent { public int V; }
        public struct Clock : ISingletonComponent { public int Ticks; }
        public class Box { public int V; }
        public struct Holder : IComponent { public System.Collections.Generic.List<int> Items; }
        public struct Q : IComponent { public int V; }
        public struct T : IComponent, ITracked { public int V; }
        public struct Boss : IComponent;

        """;

    [TestCase("EECS002", "Take it by 'in' (read)",
        "[EcsSystem] public ref struct S { [ForEach] static void A(P p, ref Clock c) => c.Ticks += p.V; }",
        "static void A(in P p, ref Clock c)")]
    [TestCase("EECS002", "Take it by 'ref' (write)",
        "[EcsSystem] public ref struct S { [ForEach] static void A(P p) => p.V++; }",
        "static void A(ref P p)")]
    [TestCase("EECS090", "Rename it to OnUpdate",
        "[EcsSystem] public ref struct S { static void OnUpate(World world) => world.GetSingleton<Clock>().Ticks++; }",
        "{ static void OnUpdate(World world) =>")]
    [TestCase("EECS005", "Make it static",
        "public class Runner { [ForEach] void A(ref P p) => p.V++; }",
        "[ForEach] static void A(ref P p)")]
    [TestCase("EECS005", "Make it static",
        "public class Runner { [ForEach] public void A(ref P p) => p.V++; }",
        "[ForEach] public static void A(ref P p)")]
    [TestCase("EECS006", "Make it a struct",
        "public sealed class R : IComponent { public int V; }",
        "public struct R : IComponent { public int V; }")]
    [TestCase("EECS006", "Make it a struct",
        "public record R(int V) : IComponent;",
        "public record struct R(int V) : IComponent;")]
    [TestCase("EECS010", "Run it on one thread",
        "[EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(ref P p, ref Clock c) => c.Ticks += p.V; }",
        "[ForEach] static void A")]
    [TestCase("EECS011", "Take it by 'ref'",
        "[EcsSystem] public ref struct S { [ForEach] static void A(in Holder h) => h.Items.Add(1); }",
        "static void A(ref Holder h)")]
    [TestCase("EECS011", "Take it by 'ref'",
        "[EcsSystem] public ref struct S { [ForEach] static void A(System.ReadOnlySpan<Holder> h) => h[0].Items.Add(1); }",
        "static void A(System.Span<Holder> h)")]
    [TestCase("EECS012", "Take it by 'in' (read)",
        "public static class Use { public static int Count(DynamicBuffer<int> items) => items.Count; }",
        "Count(in DynamicBuffer<int> items)")]
    [TestCase("EECS022", "Make it internal",
        "[EcsSystem] public ref struct S { private struct Hidden : IComponent { public int V; } [ForEach] static void A(ref Hidden h) => h.V++; }",
        "internal struct Hidden")]
    [TestCase("EECS022", "Make it internal",
        "[EcsSystem] public ref struct S {\n    struct Hidden : IComponent { public int V; }\n    [ForEach] static void A(ref Hidden h) => h.V++; }",
        "\n    internal struct Hidden")]
    [TestCase("EECS002", "Take it by 'in' (read)",
        "[EcsSystem] public ref struct S { [ForEach] static void A(\n        P p, ref Clock c) => c.Ticks += p.V; }",
        "(\n        in P p, ref Clock c)")]
    [TestCase("EECS005", "Make it static",
        "public class Runner { static int x = 1; [ForEach] void A(ref P p) => p.V += x; }",
        "[ForEach] static void A(ref P p)")]
    [TestCase("EECS011", "Take it by 'ref'",
        "[EcsSystem] public ref struct S { [ForEach] static void A(RO h) => h[0].Items.Add(1); }",
        "static void A(System.Span<Holder> h)")]
    [TestCase("EECS011", "Take it by 'ref'",
        "public struct Grid : ISingletonComponent { public System.Collections.Generic.List<int> Cells; }\n"
        + "[EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(ref P p, in Holder h) => h.Items.Add(p.V); }",
        "ref Holder h) => h.Items.Add(p.V); }")] // a component's ref is fine in parallel: each row is one thread's
    [TestCase("EECS012", "Take it by 'ref' (write)",
        "public static class Use { public static int Count(DynamicBuffer<int> items) => items.Count;\n"
        + "  public static int Call(Entity e) { ref var b = ref e.Get<DynamicBuffer<int>>(); return Count(b); } }",
        "return Count(ref b);")]
    [TestCase("EECS022", "Make it internal",
        "[EcsSystem] public ref struct S { private class Outer { public struct C : IComponent { public int V; } } [ForEach] static void A(ref Outer.C c) => c.V++; }",
        "internal class Outer")]
    [TestCase("EECS022", "Make it internal",
        "[EcsSystem] public ref partial struct S { private partial class Outer { internal struct C : IComponent { public int V; } } [ForEach] static void A(ref Outer.C c) => c.V++; }\n"
        + "public ref partial struct S { private partial class Outer { } }",
        "internal partial class Outer { } }")]
    [TestCase("EECS023", "Remove the filter",
        "[With<P>] public struct R : IComponent { public int V; }",
        "public struct R : IComponent")]
    [TestCase("EECS023", "Remove the filter",
        "[System.Serializable, With<P>] public struct R : IComponent { public int V; }",
        "[System.Serializable] public struct R")]
    [TestCase("EECS025", "Take it by 'in'",
        "[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, ref Clock c) => c.Ticks += p.V; }",
        "static void A(in P p, ref Clock c)")]
    [TestCase("EECS025", "Take it as a ReadOnlyMaybe<Q>",
        "[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, Maybe<Q> q) { if (q.HasValue) p.V = q.Value.V; } }",
        "static void A(ref P p, ReadOnlyMaybe<Q> q)")]
    [TestCase("EECS015", "Take it as a ReadOnlyMaybe<Owned>",
        "public struct Owned : IComponent, IIndexed<int> { public int K; public readonly int Key => K; } [EcsSystem] public ref struct S { [ForEach] static void A(ref P p, Maybe<Owned> o) { if (o.HasValue) p.V = o.Value.K; } }",
        "static void A(ref P p, ReadOnlyMaybe<Owned> o)")]
    [TestCase("EECS025", "Take it as a ReadOnlySpan",
        "[EcsSystem] public ref struct S { [ForEach] static void A(System.Span<P> p, ref Clock c) { for (int i = 0; i < p.Length; i++) c.Ticks += p[i].V; } }",
        "static void A(System.ReadOnlySpan<P> p, ref Clock c)")]
    [TestCase("EECS025", "Read it with Read<T>()",
        "public static class U { public static int F(Entity e) => e.Get<T>().V; }",
        "=> e.Read<T>().V;")]
    [TestCase("EECS025", "Read it with .T",
        "public static class U { public static int F(Entity e) => e.WriteT().V; }",
        "=> e.T.V;")]
    [TestCase("EECS026", "Require it with [With<P>]",
        "[EcsSystem] public ref struct S { [ForEach] static void A(in P p, ref Clock c) => c.Ticks++; }",
        "[ForEach, With<P>] static void A(ref Clock c)")]
    [TestCase("EECS026", "Remove the parameter",
        "[EcsSystem] public ref struct S { [ForEach] static void A(Entity e, ref P p) => p.V++; }",
        "[ForEach] static void A(ref P p)")]
    [TestCase("EECS026", "Remove the parameter",
        "[EcsSystem] public ref struct S { [ForEach, Changed<T>] static void A(in T t, ref P p) => p.V++; }",
        "[ForEach, Changed<T>] static void A(ref P p)")]
    [TestCase("EECS030", "Make it one bulk call",
        "public static class U { public static void F(World w) { foreach (var item in w.Query<P>()) item.Entity.Destroy(); } }",
        "{ w.DestroyAll<P>(); }")]
    [TestCase("EECS030", "Make it one bulk call",
        "public static class U { public static void F(World w, QueryFilter f) { foreach (var item in w.Query<P>(f)) item.Entity.Set(new Q { V = 1 }); } }",
        "{ w.SetAll(new Q { V = 1 }, (f).With<P>()); }")]
    [TestCase("EECS030", "Make it one bulk call",
        "public static class U { public static void F(World w) { foreach (var item in w.Query<P, Q>()) item.Entity.RemoveQ(); } }",
        "{ w.RemoveAll<Q>(QueryFilter.Create().With<P>().With<Q>()); }")]
    [TestCase("EECS031", "Create them in one call",
        "public static class U { public static void F(World w, Entity[] all) { for (int i = 0; i < all.Length; i++) all[i] = w.CreateEntity(new P()); } }",
        "{ w.CreateEntities(all, new P()); }")]
    [TestCase("EECS031", "Create them in one call",
        "public static class U { public static void F(World w, Prefab p, Entity[] all) { for (int i = 0; i < all.Length; i++) all[i] = w.Instantiate(p, new P(), new Q { V = 1 }); } }",
        "{ w.Instantiate(p, all, new P(), new Q { V = 1 }); }")]
    [TestCase("EECS031", "Reserve room first",
        "public static class U { public static void F(World w, int n)\n    {\n        for (int i = 0; i < n; i++) w.CreateEntity(new P { V = i });\n    }\n}",
        "    {\n        w.Reserve<P>(n);\n        for (int i = 0; i < n; i++)")]
    [TestCase("EECS032", "Use Any",
        "public static class U { public static bool F(World w) => w.Count<P>() > 0; }",
        "=> w.Any<P>();")]
    [TestCase("EECS032", "Use Any",
        "public static class U { public static bool F(World w) => w.Count<P>() == 0; }",
        "=> !w.Any<P>();")]
    [TestCase("EECS032", "Count them with Count",
        "public static class U { public static int F(World w) { int n = 0; foreach (var item in w.Query<P>()) n++; return n; } }",
        "{ int n = 0; n += w.Count<P>(); return n; }")]
    [TestCase("EECS033", "Use .P",
        "public static class U { public static void F(Entity e) => e.Get<P>().V = 1; }",
        "=> e.P.V = 1;")]
    [TestCase("EECS033", "Use .WriteT",
        "public static class U { public static void F(Entity e) => e.Get<T>().V = 1; }",
        "=> e.WriteT().V = 1;")]
    [TestCase("EECS033", "Use .SetTIfChanged",
        "public static class U { public static void F(Entity e, int v) { if (e.T.V != v) e.SetT(v); } }",
        "{ e.SetTIfChanged(v); }")]
    [TestCase("EECS033", "Use .GetOrAddHp",
        "public record struct Hp(int V) : IComponent; public static class U { public static int F(Entity e) => e.GetOrAdd(new Hp(3)).V; }",
        "=> e.GetOrAddHp(3).V;")]
    [TestCase("EECS033", "Use .T",
        "public static class U { public static int F(Entity e) => e.Read<T>().V; }",
        "=> e.T.V;")]
    [TestCase("EECS033", "Use .HasP",
        "public static class U { public static bool F(Entity e) => e.Has<P>(); }",
        "=> e.HasP;")]
    [TestCase("EECS033", "Use .SetBoss",
        "public static class U { public static void F(Entity e) => e.Set(new Boss()); }",
        "=> e.SetBoss();")]
    [TestCase("EECS033", "Use .SetHpIfChanged",
        "public record struct Hp(int V) : IComponent, ITracked; public static class U { public static bool F(Entity e) => e.SetIfChanged(new Hp(3)); }",
        "=> e.SetHpIfChanged(3);")]
    [TestCase("EECS033", "Use .Clock",
        "public static class U { public static int F(World w) => w.GetSingleton<Clock>().Ticks; }",
        "=> w.Clock.Ticks;")]
    [TestCase("EECS033", "Use .P",
        "public static class U { public static int F(World w) { int n = 0; foreach (var item in w.Query<P>()) n += item.Component1.V; return n; } }",
        "n += item.P.V;")]
    [TestCase("EECS037", "Declare WriteMask => 0",
        "public struct J : IForEach<T> { public int N; public void Execute(ref T t) => N += t.V; }",
        "public struct J : IForEach<T> { public static int WriteMask => 0; public int N;")]
    [TestCase("EECS037", "Declare WriteMask => 0b1",
        "public struct J : IForEach<T>\n{\n    public void Execute(ref T t) => t.V++;\n    public static int WriteMask => 0;\n}",
        "    public static int WriteMask => 0b1;\n}")]
    [TestCase("EECS038", "Keep the ref",
        "public static class U { public static void F(Entity e) { var p = e.Get<P>(); p.V = 1; } }",
        "{ ref var p = ref e.Get<P>(); p.V = 1; }")]
    [TestCase("EECS038", "Keep the ref",
        "public static class U { public static void F(Entity e) { T t = e.Read<T>(); t.V = 1; } }",
        "{ ref T t = ref e.Get<T>(); t.V = 1; }")]
    [TestCase("EECS038", "Keep the ref",
        "public static class U { public static void F(Entity e) { var t = e.T; t.V++; } }",
        "{ ref var t = ref e.WriteT(); t.V++; }")]
    [TestCase("EECS046", "Read without stamping",
        "public static class U { public static int F(World w) { int n = 0; foreach (var item in w.Query<T>()) n += item.T.V; return n; } }",
        "foreach (var item in w.Read<T>()) n += item.T.V;")]
    [TestCase("EECS039", "Take it by 'ref'",
        "[EcsSystem] public ref struct S { [ForEach] static void A(Mut<P> p, in Q q) { if (q.V > p.Value.V) p.Write().V = q.V; } }",
        "static void A(ref P p, in Q q) { if (q.V > p.V) p.V = q.V; }")]
    [TestCase("EECS025", "Take it by 'in'",
        "[EcsSystem] public ref struct S { [ForEach] static void A(Mut<T> t, ref P p) => p.V = t.Value.V; }",
        "static void A(in T t, ref P p) => p.V = t.V;")]
    [TestCase("EECS015", "Read it with Read<T>()",
        "public struct Owned : IComponent, IIndexed<int> { public int K; public readonly int Key => K; } public static class U { public static int F(Entity e) => e.Get<Owned>().K; }",
        "=> e.Read<Owned>().K;")]
    [TestCase("EECS015", "Bind it 'ref readonly'",
        "public struct Owned : IComponent, IIndexed<int> { public int K; public readonly int Key => K; } public static class U { public static int F(World w) { int n = 0; foreach (var item in w.Query<Owned>()) { ref var o = ref item.Component1; n += o.K; } return n; } }",
        "ref readonly var o = ref item.Component1;")]
    [TestCase("EECS015", "Take it by 'in'",
        "public struct Owned : IComponent, IIndexed<int> { public int K; public readonly int Key => K; } [EcsSystem] public ref struct S { [ForEach] static void A(ref Owned o, ref Clock c) => c.Ticks += o.K; }",
        "static void A(in Owned o, ref Clock c)")]
    [TestCase("EECS015", "Take it as a ReadOnlySpan",
        "public struct Owned : IComponent, IIndexed<int> { public int K; public readonly int Key => K; } [EcsSystem] public ref struct S { [ForEach] static void A(System.Span<Owned> o, ref Clock c) => c.Ticks += o[0].K; }",
        "static void A(System.ReadOnlySpan<Owned> o, ref Clock c)")]
    [TestCase("EECS007", "Make it readonly",
        "[EcsSystem] public ref struct S { private static System.Collections.Generic.List<int> _scratch = new(); public static int Count => _scratch.Count; }",
        "private static readonly System.Collections.Generic.List<int> _scratch = new();")]
    [TestCase("EECS007", "Make it readonly",
        "[EcsSystem] public ref struct S { private static int _limit; static S() { _limit = 3; } public static int Limit => _limit; }",
        "private static readonly int _limit;")]
    [TestCase("EECS080", "Write it as a [ForEntity] method",
        "public struct Closed : IComponent;\npublic struct Buy : IForEachEntity<P, Q>\n{\n    public int Quantity;\n    public static int WriteMask => 0b01;\n\n    // Sells and closes the shop when it runs out.\n    public void Execute(Entity shop, ref P p, ref Q q)\n    {\n        p.V -= Quantity;\n        if (p.V == 0) shop.Set(new Closed());\n    }\n}\n"
        + "public static class Mailman\n{\n    private static readonly QueryFilter Open = QueryFilter.Create().Without<Closed>();\n\n    public static bool Send(Entity shop, int quantity)\n    {\n        var buy = new Buy { Quantity = quantity };\n        return shop.Run<P, Q, Buy>(ref buy, Open);\n    }\n}",
        "public static class Buy\n{\n    // Sells and closes the shop when it runs out.\n    [ForEntity, Without<Closed>]\n    static void Run(Entity shop, ref P p, in Q q, int quantity)\n    {\n        p.V -= quantity;\n        if (p.V == 0) shop.Set(new Closed());\n    }\n}\npublic static class Mailman\n{\n    public static bool Send(Entity shop, int quantity)\n    {\n        return Buy.Run(shop, quantity);\n    }\n}")]
    [TestCase("EECS080", "Write it as a [ForEntity] method",
        "public struct Read : IForEachEntity<P> { public int Scale; public static int WriteMask => 0; public void Execute(Entity e, ref P p) => System.Console.WriteLine(p.V * Scale); }\n"
        + "public static class U { public static void F(Entity a, Entity b) { var r = new Read { Scale = 2 }; a.Run<P, Read>(ref r); var s = new Read(); b.Run<P, Read>(ref s); } }",
        "static void Run(in P p, int scale) => System.Console.WriteLine(p.V * scale);")]
    [TestCase("EECS081", "Make it static",
        "public static class R { [ForEntity] void A(ref P p) => p.V++; }",
        "[ForEntity] static void A(ref P p)")]
    [TestCase("EECS084", "Consume its rows with [Consume]",
        "public struct Hit : IComponent { public int Amount; }\n[EcsSystem] public ref struct S\n{\n    [ForEach]\n    static void A(Entity e, in Hit hit, ref P p)\n    {\n        p.V -= hit.Amount;\n        e.Destroy();\n    }\n}",
        "    [ForEach, Consume]\n    static void A(in Hit hit, ref P p)\n    {\n        p.V -= hit.Amount;\n    }")]
    [TestCase("EECS084", "Consume its rows with [Consume]",
        "public struct Hit : IComponent { public int Amount; }\n[EcsSystem] public ref struct S\n{\n    [ForEach]\n    static void A(Entity e, in Hit hit, ref P p)\n    {\n        if (e.Has<Q>()) p.V = 0;\n        e.Destroy();\n    }\n}",
        "    [ForEach, Consume]\n    static void A(Entity e, in Hit hit, ref P p)\n    {\n        if (e.Has<Q>()) p.V = 0;\n    }")]
    [TestCase("EECS017", "Store its EntityId",
        "public struct Target : IComponent { public Entity Of; }\npublic static class U { public static bool F(Entity e, Entity other) { e.Set(new Target { Of = other }); return e.Read<Target>().Of.Id == other.Id; } }",
        "public struct Target : IComponent { public EntityId Of; }")]
    [TestCase("EECS043", "Implement it explicitly",
        "public readonly record struct Shop(long Id) : IComponent, IIndexed<Shop> { public Shop Key => this; }",
        "{ Shop IIndexed<Shop>.Key => this; }")]
    [TestCase("EECS052", "Create it with the component",
        "public static class U { public static void F(World w) { var e = w.CreateEntity(new P()); e.Set(new Q { V = 1 }); } }",
        "{ var e = w.CreateEntity(new P(), new Q { V = 1 }); }")]
    [TestCase("EECS052", "Create it with the component",
        "public static class U { public static Entity F(World w)\n    {\n        var e = w.CreateEntity<P>(new P());\n        e.SetBoss();\n        return e;\n    }\n}",
        "        var e = w.CreateEntity(new P(), new Boss());\n        return e;\n")]
    [TestCase("EECS052", "Create it with the component",
        "public static class U { public static Entity F(World w, Prefab p) { Entity e = w.Instantiate(p); e.Set(new Q()); return e; } }",
        "{ Entity e = w.Instantiate(p, new Q()); return e; }")]
    [TestCase("EECS027", "Take it as a parameter ('in Q q')",
        "[EcsSystem] public ref struct S { [ForEach] static void A(Entity e, ref P p) => p.V = e.Get<Q>().V; }",
        "static void A(Entity e, ref P p, in Q q) => p.V = q.V; }")]
    [TestCase("EECS027", "Take it as a parameter ('in Q q')",
        "[EcsSystem] public ref struct S { [ForEach] static void A(Entity e, ref P p) { p.V = e.Q.V; if (p.V > 0) p.V += e.Read<Q>().V; } }",
        "static void A(Entity e, ref P p, in Q q) { p.V = q.V; if (p.V > 0) p.V += q.V; } }")]                 // every lookup of the type
    [TestCase("EECS027", "Take it as a parameter ('ref Q q')",
        "[EcsSystem] public ref struct S { [ForEach] static void A(Entity e, in P p) => e.Get<Q>().V = p.V; }",
        "static void A(Entity e, in P p, ref Q q) => q.V = p.V; }")]
    [TestCase("EECS027", "Take it as a parameter ('in Clock clock')",
        "[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, World w) => p.V = w.Clock.Ticks; }",
        "static void A(ref P p, World w, in Clock clock) => p.V = clock.Ticks; }")]
    [TestCase("EECS027", "Take it as a parameter ('in Clock clock')",
        "[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, World world) => p.V = world.GetSingleton<Clock>().Ticks; }",
        "static void A(ref P p, World world, in Clock clock) => p.V = clock.Ticks; }")]
    [TestCase("EECS027", "Use parameter 'q'",
        "[EcsSystem] public ref struct S { [ForEach] static void A(Entity e, ref P p, in Q q) => p.V = e.Read<Q>().V + q.V; }",
        "static void A(Entity e, ref P p, in Q q) => p.V = q.V + q.V; }")]
    [TestCase("EECS054", "Remove ChunkRows",
        "[EcsSystem] public ref struct S { [ForEach(ChunkRows = 64)] static void A(ref P p) => p.V++; }",
        "[ForEach] static void A(ref P p)")]
    [TestCase("EECS054", "Remove ChunkRows",
        "[EcsSystem] public ref struct S { [ForEach(Parallel = true, ChunkRows = -1)] static void A(ref P p) => p.V++; }",
        "[ForEach(Parallel = true)] static void A(ref P p)")]
    [TestCase("EECS061", "Run it on one thread",
        "public struct Sum : IForEach<P>, INoStructuralChanges { public int Total; public void Execute(ref P p) => Total += p.V; }\n"
        + "public static class U { public static int F(World w) { var s = new Sum(); w.ForEachParallel<P, Sum>(ref s, QueryFilter.Create().Without<Q>()); return s.Total; } }",
        "w.ForEach<P, Sum>(ref s, QueryFilter.Create().Without<Q>());")]
    [TestCase("EECS061", "Run it on one thread",
        "public struct Sum : IForEachBatch<P>, INoStructuralChanges { public int Total; public void Execute(EntitySpan e, System.Span<P> p) => Total += p.Length; }\n"
        + "public static class U { public static int F(World w) { var s = new Sum(); w.ForEachBatchParallel<P, Sum>(ref s); return s.Total; } }",
        "w.ForEachBatch<P, Sum>(ref s);")]
    [TestCase("EECS061", "Run it on one thread",
        "public struct Sum : IForEach<P>, INoStructuralChanges { public int Total; public void Execute(ref P p) => Total += p.V; public static bool Merges => false; public static void Merge<TSelf>(ref TSelf job, in TSelf chunk) where TSelf : struct { } }\n"
        + "public static class U { public static int F(World w) { var s = new Sum(); w.ForEachParallel<P, Sum>(ref s); return s.Total; } }",
        "w.ForEach<P, Sum>(ref s);")]
    [TestCase("EECS071", "Draw from EntityRandom",
        "[EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(ref P p) => p.V = System.Random.Shared.Next(1, 7); }",
        "static void A(ref P p, Entity entity, in EntityRandom random) => p.V = random.Range(entity.Id, 0, 1, 7); }")]
    [TestCase("EECS071", "Draw from EntityRandom",
        "[EcsSystem] public ref struct S { [ForEach] static void A(ref Q q, Entity e, in EntityRandom r) => q.V = r.Range(e.Id, 0, 0, 9) + (System.Random.Shared.NextSingle() < 0.5f ? 1 : 0); }",
        "q.V = r.Range(e.Id, 0, 0, 9) + (r.Float(e.Id, 1) < 0.5f ? 1 : 0); }")]
    [TestCase("EECS088", "Give it a stream of its own",
        "[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, Entity e, in EntityRandom r) => p.V = r.Range(e.Id, 0, 1, 7) + r.Range(e.Id, 0, 1, 7) * r.Range(e.Id, 1, 1, 7); }",
        "p.V = r.Range(e.Id, 0, 1, 7) + r.Range(e.Id, 2, 1, 7) * r.Range(e.Id, 1, 1, 7); }")]   // the next stream no draw uses
    [TestCase("EECS062", "Make it partial",
        "public static class Outer { internal class Mid { public struct Census : ISingletonComponent { [Sum] public int Alive; } } }",
        "public static partial class Outer { internal partial class Mid { public partial struct Census")]
    [TestCase("EECS062", "Make it partial",
        "class Outer\n{\n    [System.Serializable]\n    struct Census : ISingletonComponent { [Sum] public int Alive; }\n}",
        "partial class Outer\n{\n    [System.Serializable]\n    partial struct Census")]
    [TestCase("EECS065", "Declare it with attributes",
        "public struct C : ISingletonComponent, IReduction<C> { public int N; public float L; public static C Identity => new C { L = float.MaxValue }; public static C Combine(C a, C b) => new C { N = a.N + b.N, L = System.Math.Min(a.L, b.L) }; }",
        "public partial struct C : ISingletonComponent { [Sum] public int N; [Min] public float L; }")]
    [TestCase("EECS065", "Declare it with attributes",
        "public record struct C(int N, long M) : ISingletonComponent, IReduction<C>\n{\n    public static C Identity => new C { M = long.MinValue };\n\n    public static C Combine(C a, C b) => new C { N = a.N + b.N, M = System.Math.Max(a.M, b.M) };\n}",
        "public partial record struct C([Sum] int N, [Max] long M) : ISingletonComponent\n{\n}")]
    [TestCase("EECS066", "Fix the Identity",
        "public struct C : ISingletonComponent, IReduction<C> { public float L; public int First; public static C Identity => default; public static C Combine(C a, C b) => new C { L = System.MathF.Min(a.L, b.L), First = a.First < 0 ? b.First : a.First }; }",
        "public static C Identity => new C { L = float.PositiveInfinity };")]
    [TestCase("EECS066", "Fix the Identity",
        "public struct C : ISingletonComponent, IReduction<C> { public int Lo, Hi, First; public static C Identity => new C { Lo = int.MaxValue, First = -1 }; public static C Combine(C a, C b) => new C { Lo = System.Math.Min(a.Lo, b.Lo), Hi = System.Math.Max(a.Hi, b.Hi), First = a.First < 0 ? b.First : a.First }; }",
        "public static C Identity => new C { Lo = int.MaxValue, Hi = int.MinValue, First = -1 };")]
    [TestCase("EECS066", "Declare it with attributes",
        "public struct C : ISingletonComponent, IReduction<C> { public int Lo; public static C Identity => default; public static C Combine(C a, C b) => new C { Lo = System.Math.Min(a.Lo, b.Lo) }; }",
        "public partial struct C : ISingletonComponent { [Min] public int Lo; }")]
    [TestCase("EECS067", "Declare it with [IndexKey]",
        "public struct M : IComponent, IIndexed<int> { public int BlocksDug; public int Key => BlocksDug; }",
        "public partial struct M : IComponent { [IndexKey] public int BlocksDug; }")]
    [TestCase("EECS067", "Declare it with [UniqueKey]",
        "public record struct A(int Id) : IComponent, IUniqueIndexed<int> { readonly int IIndexed<int>.Key => Id; }",
        "public partial record struct A([UniqueKey] int Id) : IComponent { }")]
    [TestCase("EECS067", "Declare it with [IndexKey]",
        "namespace N\n{\n    public readonly struct Tile : IComponent, IIndexed<Tile>, System.IEquatable<Tile>\n    {\n        public readonly int X;\n        Tile IIndexed<Tile>.Key => this;\n        public bool Equals(Tile o) => X == o.X;\n    }\n}",
        "namespace N\n{\n    [IndexKey]\n    public readonly partial struct Tile : IComponent, System.IEquatable<Tile>\n    {\n        public readonly int X;\n        public bool Equals(Tile o) => X == o.X;\n    }\n}")]
    [TestCase("EECS067", "Declare it with [UniqueKey]",
        "public struct D : IComponent, IDirectIndexed<int> { public int Cell; public int Key { get => Cell; } public static int Slots => 8; public static int SlotOf(int key) => key; }",
        "public partial struct D : IComponent, IDirectIndexed<int> { [UniqueKey] public int Cell; public static int Slots")]  // IDirectIndexed stays
    [TestCase("EECS067", "Declare it with [Group]",
        "public struct Z : IComponent, IGroup { public int Zone; public int Group => Zone; }",
        "public partial struct Z : IComponent { [Group] public int Zone; }")]
    [TestCase("EECS067", "Declare it with [Group]",
        "public enum E { A, B }\npublic record struct Z(E Biome, int Hp) : IComponent, EasyEcs.IGroup { readonly int IGroup.Group => (int)this.Biome; }",
        "public partial record struct Z([Group] E Biome, int Hp) : IComponent { }")]
    [TestCase("EECS043", "Declare it with [IndexKey]",
        "public readonly record struct Shop(long Id) : IComponent, IIndexed<Shop> { public Shop Key => this; }",
        "[IndexKey]\npublic readonly partial record struct Shop(long Id) : IComponent { }")]
    [TestCase("EECS069", "Mark it [NinoType]",
        "namespace Nino.Core { public sealed class NinoTypeAttribute : System.Attribute; }\npublic struct Name : IComponent { public string Text; }", // Nino's attribute, as its package declares it
        "[Nino.Core.NinoType]\npublic partial struct Name : IComponent { public string Text; }")]
    [TestCase("EECS069", "Mark it [NotSaved]",
        "namespace N\n{\n    public record struct Wire(object Socket) : IComponent;\n}",
        "namespace N\n{\n    [NotSaved]\n    public record struct Wire(object Socket) : IComponent;\n}")]
    [TestCase("EECS074", "Make it a ref struct",
        "[EcsSystem] public struct S { [ForEach] static void A(ref P p) => p.V++; }",
        "[EcsSystem] public ref struct S {")]
    [TestCase("EECS074", "Make it a ref struct",
        "[EcsSystem] public partial struct S { [ForEach] static void A(ref P p) => p.V++; }",
        "[EcsSystem] public ref partial struct S {")]
    [TestCase("EECS075", "Make it static",
        "[EcsSystem] public ref struct S { [ForEach] void A(ref P p) => p.V++; }",
        "[ForEach] static void A(ref P p) => p.V++;")]
    [TestCase("EECS075", "Make it static",
        "[EcsSystem] public ref struct S { void OnUpdate(World world) => world.GetSingleton<Clock>().Ticks++; }",
        "static void OnUpdate(World world)")]
    public async Task FixesTheDiagnostic(string id, string title, string source, string expected)
    {
        var document = Document(Data + source, generate: string.CompareOrdinal(id, "EECS025") >= 0);
        var diagnostic = (await Analyze(document)).First(d => d.Id == id && d.Location.SourceSpan.Start >= Data.Length); // not one of Data's

        var actions = new List<CodeAction>();
        var context = new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), CancellationToken.None);
        await new EasyEcsCodeFixes().RegisterCodeFixesAsync(context);
        var action = actions.SingleOrDefault(a => a.Title == title);
        Assert.That(action, Is.Not.Null, string.Join(", ", actions.Select(a => a.Title)));

        var operation = (await action!.GetOperationsAsync(CancellationToken.None)).OfType<ApplyChangesOperation>().Single();
        var fixedDocument = operation.ChangedSolution.GetDocument(document.Id)!;
        var text = (await fixedDocument.GetTextAsync()).ToString();
        Assert.That(text, Does.Contain(expected));
        Assert.That((await Analyze(fixedDocument)).Where(d => d.Id == id && d.Location.SourceSpan.Start >= Data.Length), Is.Empty, text);
        var errors = (await fixedDocument.Project.GetCompilationAsync())!.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error);
        Assert.That(errors, Is.Empty, text);
    }

    [TestCase("EECS005", "public class Runner { int x = 1; [ForEach] void A(ref P p) => p.V += x; }")]
    [TestCase("EECS005", "public class Runner { int X() => 1; [ForEach] void A(ref P p) => p.V += this.X(); }")]
    [TestCase("EECS006", "public partial class R : IComponent { public int V; }\npublic partial class R { }")]
    [TestCase("EECS006", "public class Base { } public class R : Base, IComponent { public int V; }")]
    [TestCase("EECS006", "public class R : IComponent { protected int V; }")]
    [TestCase("EECS011", "public struct Grid : ISingletonComponent { public System.Collections.Generic.List<int> Cells; }\n"
                         + "[EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(ref P p, in Grid g) => g.Cells.Add(p.V); }")]
    [TestCase("EECS010", "[EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(ref P p, World world) => p.V = world.Clock.Ticks; }",
        "Run it on one thread")]                                                                                    // reaches the world
    [TestCase("EECS012", "public interface IUse { int Count(DynamicBuffer<int> items); }\n"
                         + "public class Use : IUse { public int Count(DynamicBuffer<int> items) => items.Count; }")]
    [TestCase("EECS012", "public static class Use { public static int Count(DynamicBuffer<int> items) => items.Count;\n"
                         + "  public static int Call(Entity e) => Count(e.Read<DynamicBuffer<int>>()); }", "Take it by 'in' (read)")]
    [TestCase("EECS022", "file struct C : IComponent { public int V; } [EcsSystem] public ref struct S { [ForEach] static void A(ref C c) => c.V++; }")]
    [TestCase("EECS026", "[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, ref T t) => p.V++; }")]  // the stamp: [With<T>] would change it
    [TestCase("EECS025", "public static class R { [ForEach] static void A(ref P p, ref int n) => n += p.V;\n"
                         + "  public static int Call() { P p = default; int n = 0; A(ref p, ref n); return n; } }")]       // a caller passes 'ref'
    [TestCase("EECS032", "public static class U { public static bool F(World w) => w.Count<P>(QueryFilter.Create().With<Q>()) > 0; }", "Use Any")]
    [TestCase("EECS017", "public struct Target : IComponent { public Entity Of; } public static class U { public static void F(Entity e) => e.Read<Target>().Of.Destroy(); }")]
    [TestCase("EECS007", "[EcsSystem] public ref struct S { static int _kills; static void K() => _kills++; }")]
    [TestCase("EECS007", "[EcsSystem] public ref struct S { private static int _limit; static S() { System.Action a = () => _limit = 3; a(); } }")]
    [TestCase("EECS043", "public readonly record struct Shop(long Id) : IComponent, IIndexed<Shop> { public Shop Key => this; } public static class U { public static long F(Shop s) => s.Key.Id; }")]
    [TestCase("EECS015", "public struct Owned : IComponent, IIndexed<int> { public int K; public readonly int Key => K; } public static class U { public static void F(Entity e) => e.Get<Owned>().K = 1; }")]
    [TestCase("EECS015", "public struct Owned : IComponent, IIndexed<int> { public int K; public readonly int Key => K; } [EcsSystem] public ref struct S { [ForEach] static void A(ref Owned o) => o.K++; }")]
    [TestCase("EECS038", "public struct Owned : IComponent, IIndexed<int> { public int K; public readonly int Key => K; } public static class U { public static void F(Entity e) { var o = e.Read<Owned>(); o.K = 1; } }")]
    [TestCase("EECS039", "public static class R { [ForEach] public static void B(Mut<P> p, in Q q) { if (q.V > 0) p.Write().V = q.V; }\n"
                         + "  public static void Call() { P p = default; B(new Mut<P>(ref p, default), default); } }")]                 // a caller passes a Mut
    [TestCase("EECS046", "public static class U { public static int F(World w) { int n = 0; foreach (QueryItem<T> item in w.Query<T>()) n += item.Component1.V; return n; } }")] // typed by hand
    [TestCase("EECS027", "[EcsSystem] public ref struct S { [ForEach] static void A(Entity e, ref P p) { int q = 1; p.V = e.Get<Q>().V + q; } }")]   // the name is taken
    [TestCase("EECS027", "[EcsSystem] public ref struct S { [ForEach] static void A(Entity e, ref P p) { p.V = e.Get<Q>().V; if (e.TryGet<Q>(out var x)) p.V += x.V; } }")] // a TryGet stays
    [TestCase("EECS027", "[EcsSystem] public ref struct S { [ForEach] static void A(Entity e, ref P p, Maybe<Q> m) { p.V = e.Get<Q>().V; if (m.HasValue) p.V++; } }")]   // taken as a Maybe
    [TestCase("EECS027", "[EcsSystem] public ref struct S { [ForEach] static void A(Entity e, ref P p, in Q q) => e.Get<Q>().V = p.V + q.V; }")]                     // 'in' cannot write
    [TestCase("EECS027", "public static class R { [ForEach] public static void A(Entity e, ref P p) => p.V = e.Get<Q>().V; public static void Call(Entity e) { P p = default; A(e, ref p); } }")] // a caller
    [TestCase("EECS061", "public struct Sum : IForEach<P>, INoStructuralChanges { public int Total; public void Execute(ref P p) => Total += p.V; }\n"
                         + "public static class U { public static int F(World w) { var s = new Sum(); w.ForEachParallel<P, Sum>(ref s, default, 64); return s.Total; } }")] // its chunk size: chosen for threads
    [TestCase("EECS081", "public class R { int x = 1; [ForEntity] void A(ref P p) => p.V += x; }")]                              // uses its instance
    [TestCase("EECS071", "[EcsSystem] public ref struct S { [ForEach] static void A(ref P p) => p.V = (int)System.Random.Shared.NextInt64(9); }")] // no EntityRandom twin
    [TestCase("EECS071", "[EcsSystem] public ref struct S { [ForEach] static void A(System.Span<P> p) => p[0].V = System.Random.Shared.Next(); }")] // a batch method: no Entity
    public async Task OffersNoFixThatBreaksTheCode(string id, string source, string only = null)
    {
        var document = Document(Data + source);
        var actions = new List<CodeAction>();
        foreach (var diagnostic in (await Analyze(document)).Where(d => d.Id == id))
            await new EasyEcsCodeFixes().RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic, (a, _) => actions.Add(a), CancellationToken.None));
        Assert.That(actions.Select(a => a.Title), only == null ? Is.Empty : Is.EquivalentTo(new[] { only }));
        Assert.That(await Analyze(document), Has.Some.Matches<Diagnostic>(d => d.Id == id), "the case reports " + id);
    }

    // The split of a component (EECS091: the fields no frequent pass touches; EECS055: the few fields a method uses): the
    // moved fields become a component declared after the old one, and every use follows them, so the program does what
    // it did: a pass over moved fields only takes the new component, a pass over both gains it, creations and Set give
    // both, Remove removes both, entity.C.X and entity.Get<C>().X read the new one.
    private const string Body = "public struct Body : IComponent { public float X, VX; public string Name; public long Born; public long Kind; }\n"
        + "[EcsSystem] public ref struct Movement { [ForEach] static void Move(ref Body b) => b.X += b.VX; }\n";

    [TestCase("EECS091", "Move Name, Born and Kind into a new component BodyName",
        Body + "[EcsSystem, UpdateInterval(10)] public ref struct Aging { [ForEach] static void Age(ref Body b) => b.Born++; }\n"
             + "[EcsSystem, UpdateInterval(10)] public ref struct Report { [ForEach] static void Log(in Body b, ref P p) => p.V = (int)b.X + b.Name.Length; }\n"
             + "public static class Spawner\n{\n"
             + "    public static Entity Spawn(World w) => w.CreateEntity(new Body { X = 1, Name = \"a\" }, new P());\n"
             + "    public static Prefab Ship() => new Prefab().Set(new Body { VX = 2 });\n"
             + "    public static string NameOf(Entity e) => e.Body.Name;\n"
             + "    public static float XOf(Entity e) => e.Body.X;\n"
             + "    public static long BornOf(Entity e) => e.Get<Body>().Born;\n"
             + "    public static void Drop(Entity e) => e.Remove<Body>();\n"
             + "    public static bool Is(Entity e) => e.Has<Body>();\n}",
        new[]
        {
            "public struct Body : IComponent { public float X, VX; }\npublic struct BodyName : IComponent { public string Name; public long Born; public long Kind; }\n",
            "static void Age(ref BodyName b) => b.Born++;",
            "static void Log(in Body b, in BodyName bName, ref P p) => p.V = (int)b.X + bName.Name.Length;",
            "w.CreateEntity(new Body { X = 1 }, new BodyName { Name = \"a\" }, new P())",
            "new Prefab().Set(new Body { VX = 2 }, new BodyName())",
            "e.BodyName.Name", "e.Body.X", "e.Get<BodyName>().Born", "e.Remove<Body, BodyName>()", "e.Has<Body>()",
        })]
    [TestCase("EECS055", "Move A and B into a new component FiveA",
        "public struct Five : IComponent\n{\n    public float A, B, C, D, E;\n}\n"
        + "[EcsSystem] public ref struct S\n{\n    [ForEach] static void Mix(ref Five t) => t.A = t.B;\n    [ForEach] static void Both(ref Five t, ref P p) => p.V = (int)(t.A + t.C + t.D + t.E);\n}\n"
        + "public static class Spawner { public static void Spawn(World w) => w.CreateEntity(new Five { A = 1, C = 2 }); }",
        new[]
        {
            "public struct Five : IComponent\n{\n    public float C, D, E;\n}\n\npublic struct FiveA : IComponent\n{\n    public float A, B;\n}\n",
            "static void Mix(ref FiveA t) => t.A = t.B;",
            "static void Both(ref Five t, ref FiveA tA, ref P p) => p.V = (int)(tA.A + t.C + t.D + t.E);",
            "w.CreateEntity(new Five { C = 2 }, new FiveA { A = 1 })",
        })]
    public async Task SplitsAComponent(string id, string title, string source, string[] expected)
    {
        var document = Document(Data + source, generate: true);
        var diagnostic = (await Analyze(document)).First(d => d.Id == id && d.Properties.TryGetValue("Fix", out var fix) && fix == "Split");
        var actions = new List<CodeAction>();
        await new EasyEcsCodeFixes().RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic, (a, _) => actions.Add(a), CancellationToken.None));
        Assert.That(actions.Select(a => a.Title), Is.EqualTo(new[] { title }));

        var operation = (await actions[0].GetOperationsAsync(CancellationToken.None)).OfType<ApplyChangesOperation>().Single();
        var fixedDocument = operation.ChangedSolution.GetDocument(document.Id)!;
        var text = (await fixedDocument.GetTextAsync()).ToString();
        foreach (var part in expected) Assert.That(text, Does.Contain(part));
        var errors = (await fixedDocument.Project.GetCompilationAsync())!.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error);
        Assert.That(errors, Is.Empty, text);
        var message = diagnostic.GetMessage();
        Assert.That((await Analyze(fixedDocument)).Where(d => d.Id == id && d.Location.SourceSpan.Start >= Data.Length).Select(d => d.GetMessage()),
            Has.None.EqualTo(message), text);
    }

    // What the split cannot rewrite and keep exact: the component used whole (a copy, a helper's parameter, a ref local,
    // a query), a member of the component reading a moved field, a tracked component (its Changed<T> would miss the
    // moved fields), a constructor, a saved world (its format would change), a pass method called directly.
    [TestCase("public static class U { public static int F(in Body b) => b.Name.Length; }")]
    [TestCase("public static class U { public static void F(Entity e) { var b = new Body { Born = 1 }; e.Set(b); } }")]
    [TestCase("public static class U { public static void F(Entity e) { ref var b = ref e.Body; b.Born = 2; } }")]
    [TestCase("public static class U { public static long F(World w) { long n = 0; foreach (var item in w.Query<Body>()) n += item.Body.Born; return n; } }")]
    [TestCase("public static class U { public static void F(World w, System.Buffers.ArrayBufferWriter<byte> output) => w.Save(output); }")]
    [TestCase("public static class U { public static Body F() => default; }")]
    [TestCase("public static class R { [ForEach] public static void Age(ref Body b) => b.Born++; public static void Call() { var b = default(Body); Age(ref b); } }")]
    [TestCase("", "public struct Body : IComponent { public float X, VX; public string Name; public long Born; public long Kind; public readonly string Label => Name; }")]
    [TestCase("", "public struct Body : IComponent, ITracked { public float X, VX; public string Name; public long Born; public long Kind; }")]
    [TestCase("", "public struct Body : IComponent { public float X, VX; public string Name; public long Born; public long Kind; public Body(string name) { X = VX = 0; Name = name; Born = Kind = 0; } }")]
    public async Task OffersNoSplitThatChangesTheProgram(string source, string declaration = null)
    {
        var text = Body + source;
        if (declaration != null) text = text.Replace(Body.Substring(0, Body.IndexOf('\n')), declaration);
        var document = Document(Data + text, generate: true);
        var diagnostic = (await Analyze(document)).First(d => d.Id == "EECS091");
        var actions = new List<CodeAction>();
        await new EasyEcsCodeFixes().RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic, (a, _) => actions.Add(a), CancellationToken.None));
        Assert.That(actions, Is.Empty, string.Join(", ", actions.Select(a => a.Title)));
    }

    // The migration of a system of the old form (a class deriving from EcsSystem, which no longer exists: CS0246): the
    // members turn static, the hooks take the world and the handle they use, the members the base class gave become
    // the handle's, and the calls between the methods pass them on.
    [TestCase("public class Move : EcsSystem { [ForEach] void A(ref P p) => p.V++; }",
        "[EcsSystem]\npublic ref struct Move { [ForEach] static void A(ref P p) => p.V++; }")]
    [TestCase("public sealed class Tick : EcsSystem\n{\n    protected override void OnUpdate() => World.GetSingleton<Clock>().Ticks++;\n}",
        "[EcsSystem]\npublic ref struct Tick\n{\n    static void OnUpdate(World world) => world.GetSingleton<Clock>().Ticks++;\n}")]
    [TestCase("public class Once : EcsSystem { protected override void OnUpdate() { if (RunCount > 2) Enabled = false; } }",
        "static void OnUpdate(SystemHandle system) { if (system.RunCount > 2) system.Enabled = false; } }")]
    [TestCase("public class Spawn : EcsSystem { protected override void OnCreate() => Make(); void Make() => World.CreateEntity(new P()); }",
        "static void OnCreate(World world) => Make(world); static void Make(World world) => world.CreateEntity(new P()); }")]
    [TestCase("[UpdateInterval(2)] internal class Pace : EcsSystem { private const int Step = 2; [ForEach] void A(ref P p) => p.V += Step; }",
        "[UpdateInterval(2), EcsSystem] internal ref struct Pace { private const int Step = 2; [ForEach] static void A(ref P p) => p.V += Step; }")]
    public async Task MigratesASystemOfTheOldForm(string source, string expected)
    {
        var document = Document(Data + source);
        var actions = await MigrationsOf(document);
        Assert.That(actions.Select(a => a.Title), Is.EqualTo(new[] { "Make it an [EcsSystem] ref struct" }));
        var operation = (await actions[0].GetOperationsAsync(CancellationToken.None)).OfType<ApplyChangesOperation>().Single();
        var fixedDocument = operation.ChangedSolution.GetDocument(document.Id)!;
        var text = (await fixedDocument.GetTextAsync()).ToString();
        Assert.That(text, Does.Contain(expected));
        var errors = (await fixedDocument.Project.GetCompilationAsync())!.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error);
        Assert.That(errors, Is.Empty, text);
        Assert.That((await Analyze(fixedDocument)).Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty, text);
    }

    // What the migration cannot write mechanically: configuration taken by a constructor (it goes into a singleton),
    // a base class of other systems, a [ForEach] method that reads the system's run (the handle is no [ForEach]
    // parameter).
    [TestCase("public class Slow(int every) : EcsSystem { [ForEach] void A(ref P p) => p.V += every; }")]
    [TestCase("public class Slow : EcsSystem { private readonly int _every; public Slow(int every) => _every = every; [ForEach] void A(ref P p) => p.V += _every; }")]
    [TestCase("public abstract class Base : EcsSystem { [ForEach] void A(ref P p) => p.V++; }")]
    [TestCase("public class Count : EcsSystem { [ForEach] void A(ref P p) => p.V = (int)RunCount; }")]
    public async Task OffersNoMigrationThatBreaksTheCode(string source)
    {
        var document = Document(Data + source);
        Assert.That((await document.Project.GetCompilationAsync())!.GetDiagnostics(), Has.Some.Matches<Diagnostic>(d => d.Id == "CS0246"));
        Assert.That(await MigrationsOf(document), Is.Empty);
    }

    private static async Task<List<CodeAction>> MigrationsOf(Document document)
    {
        var actions = new List<CodeAction>();
        foreach (var diagnostic in (await document.Project.GetCompilationAsync())!.GetDiagnostics().Where(d => d.Id == "CS0246"))
            await new EasyEcsCodeFixes().RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic, (a, _) => actions.Add(a), CancellationToken.None));
        return actions;
    }

    // Declarations whose old form no longer compiles, migrated from the compiler's error: single-type WithAny
    // attributes and calls (CS0305) merged into one WithAny of several types (a lone one is With).
    [TestCase("CS0305", "[EcsSystem] public ref struct S { [ForEach, WithAny<P>, WithAny<Q>] static void A(ref T t) => t.V++; }",
        "[ForEach, WithAny<P, Q>] static void A", "Write it as WithAny<P, Q>")]
    [TestCase("CS0305", "[EcsSystem] public ref struct S { [ForEach, With<T>]\n  [WithAny<P>]\n  [WithAny<Q>]\n  static void A(ref T t) => t.V++; }",
        "[ForEach, With<T>]\n  [WithAny<P, Q>]\n  static void A", "Write it as WithAny<P, Q>")]
    [TestCase("CS0305", "[EcsSystem] public ref struct S { [ForEach, WithAny<P>] static void A(ref T t) => t.V++; }",
        "[ForEach, With<P>] static void A", "Write it as With<P>")]
    [TestCase("CS0305", "[WithAny<P>, WithAny<Q>] public struct G : ISystemGroup;", "[WithAny<P, Q>] public struct G", "Write it as WithAny<P, Q>")]
    [TestCase("CS0305", "public static class U { static readonly QueryFilter F = QueryFilter.Create().WithAny<P>().Without<T>().WithAny<Q>(); public static int N(World w) => w.Count<T>(F); }",
        "QueryFilter.Create().WithAny<P, Q>().Without<T>();", "Write it as WithAny<P, Q>")]
    [TestCase("CS0305", "public static class U { public static int N(World w) => w.Count<T>(QueryFilter.Create().WithAny<P>()); }",
        "QueryFilter.Create().With<P>()", "Write it as With<P>")]
    [TestCase("CS0246", "[EcsSystem] public ref struct S { [OnUpdate] static void OnUpdate(World world) { } }",
        "{ static void OnUpdate(World world) { } }", "Remove [OnUpdate]: the name makes it the hook")]
    [TestCase("CS0246", "[EcsSystem]\npublic ref struct S\n{\n    /// <summary>Spawns.</summary>\n    [OnCreate]\n    static void Spawn(World world) => world.CreateEntity(new P());\n    static void Again(World w) => Spawn(w);\n}",
        "    /// <summary>Spawns.</summary>\n    static void OnCreate(World world) => world.CreateEntity(new P());\n    static void Again(World w) => OnCreate(w);", "Rename it to OnCreate: the name makes it the hook")]
    [TestCase("CS0246", "[EcsSystem] public ref struct S { [OnDestroy, System.Obsolete] static void Bye() { } }",
        "{ [System.Obsolete] static void OnDestroy() { } }", "Rename it to OnDestroy: the name makes it the hook")]
    [TestCase("CS0592", "/// <summary>Zones.</summary>\n[GroupBy]\npublic partial record struct Zone([Group] int Id) : IComponent;",
        "/// <summary>Zones.</summary>\npublic partial record struct Zone([GroupBy] int Id) : IComponent;", "Move [GroupBy] onto the [Group] member")]
    [TestCase("CS0592", "[GroupBy, System.Serializable] public partial struct Hall : IComponent { [Group] public int Floor; }",
        "[System.Serializable] public partial struct Hall : IComponent { [GroupBy] public int Floor; }", "Move [GroupBy] onto the [Group] member")]
    [TestCase("CS0592", "[GroupBy] public record struct Room(int Id) : IComponent, IGroup { public int Group => Id; }",
        "public record struct Room(int Id) : IComponent, IGroup { [GroupBy] public int Group => Id; }", "Move [GroupBy] onto the Group property")]
    [TestCase("CS0200", "public static class C { public static void Step(World w) { w.GetSingleton<EntityRandom>().Tick++; w.Update(); } }",
        "Step(World w) { w.Update(); }", "Remove it: the world advances EntityRandom's tick on every update")]
    [TestCase("CS0200", "public static class C { public static void Step(World w) { w.GetSingleton<EntityRandom>().Tick = 3; } }",
        "Step(World w) { }", "Remove it: the world advances EntityRandom's tick on every update")]
    [TestCase("CS1729", "public static class C { public static EntityRandom R() => new EntityRandom(1, 2); }",
        "=> new EntityRandom(1);", "Drop the tick: the world advances it on every update")]
    [TestCase("CS1739", "public static class C { public static EntityRandom R() => new EntityRandom(Seed: 1, Tick: 2); }",
        "=> new EntityRandom(Seed: 1);", "Drop the tick: the world advances it on every update")]
    public async Task MigratesADeclarationOfTheOldForm(string id, string source, string expected, string title)
    {
        var document = Document(Data + source);
        var actions = new List<CodeAction>();
        var compiled = (await document.Project.GetCompilationAsync())!.GetDiagnostics();
        foreach (var diagnostic in compiled.Where(d => d.Id == id).Take(1))
            await new EasyEcsCodeFixes().RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic, (a, _) => actions.Add(a), CancellationToken.None));
        Assert.That(actions.Select(a => a.Title), Is.EqualTo(new[] { title }), string.Join("\n", compiled));
        var operation = (await actions[0].GetOperationsAsync(CancellationToken.None)).OfType<ApplyChangesOperation>().Single();
        var fixedDocument = operation.ChangedSolution.GetDocument(document.Id)!;
        var text = (await fixedDocument.GetTextAsync()).ToString();
        Assert.That(text, Does.Contain(expected));
        var errors = (await fixedDocument.Project.GetCompilationAsync())!.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error);
        Assert.That(errors, Is.Empty, text);
    }

    [Test]
    public async Task ByValueParameterOfOtherTypesHasNoFix()
    {
        var document = Document(Data + "[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, Box b) { } }");
        var diagnostic = (await Analyze(document)).First(d => d.Id == "EECS002");
        var actions = new List<CodeAction>();
        await new EasyEcsCodeFixes().RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic, (a, _) => actions.Add(a), CancellationToken.None));
        Assert.That(actions, Is.Empty, "a class is no component: nothing mechanical to do");
    }

    /// <summary>A one-file project; with <paramref name="generate"/> the generators run as in a build (the generated members exist).</summary>
    private static Document Document(string source, bool generate = false)
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "Test", "Test", LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            parseOptions: CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview),
            metadataReferences: GeneratorTests.Basic.References.Append(MetadataReference.CreateFromFile(typeof(World).Assembly.Location)),
            analyzerReferences: generate ? [new Generators()] : []));
        return workspace.AddDocument(project.Id, "Test.cs", SourceText.From(source));
    }

    /// <summary>The package's generators, run by the workspace as a build runs them: the generated members exist.</summary>
    private sealed class Generators : AnalyzerReference
    {
        public override string FullPath => "EasyEcs.Generator";
        public override object Id => typeof(Generators);
        public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(string language) => [];
        public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzersForAllLanguages() => [];
        // The same instances every call: the workspace maps each generator back to its reference.
        private readonly ImmutableArray<ISourceGenerator> _generators =
            [new SystemGenerator().AsSourceGenerator(), new ComponentGenerator().AsSourceGenerator(), new PartGenerator().AsSourceGenerator()];

        public override ImmutableArray<ISourceGenerator> GetGenerators(string language) => _generators;
    }

    private static async Task<Diagnostic[]> Analyze(Document document)
    {
        var compilation = (await document.Project.GetCompilationAsync())!;
        var all = await compilation.WithAnalyzers([new EasyEcsAnalyzer()]).GetAnalyzerDiagnosticsAsync();
        return all.Where(d => d.Id.StartsWith("EECS")).ToArray();
    }
}
