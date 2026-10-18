using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using EasyEcs.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>Drives SystemGenerator and EasyEcsAnalyzer through Roslyn: output, diagnostics and incremental caching.</summary>
public class GeneratorTests
{
    private static readonly CSharpParseOptions Parse = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);

    private static CSharpCompilation Compile(params string[] sources) => CSharpCompilation.Create("Test",
        sources.Select(s => CSharpSyntaxTree.ParseText("using EasyEcs;\n" + s, Parse)),
        Basic.References.Append(MetadataReference.CreateFromFile(typeof(World).Assembly.Location)),
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    private static GeneratorDriver Driver() => CSharpGeneratorDriver.Create(
        [new SystemGenerator().AsSourceGenerator(), new ComponentGenerator().AsSourceGenerator()], parseOptions: Parse,
        driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

    private static async Task<string[]> Diagnose(params string[] sources)
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(sources), out var output, out _);
        var analyzed = output.WithAnalyzers([new EasyEcsAnalyzer()]);
        var all = await analyzed.GetAllDiagnosticsAsync();
        return all.Where(d => d.Severity == DiagnosticSeverity.Error || d.Id.StartsWith("EECS")).Select(d => d.Id).Distinct().OrderBy(x => x).ToArray();
    }

    private const string Components = """
        public struct P : IComponent { public int V; }
        public struct Q : IComponent { public int V; }
        public struct T : IComponent, ITrackChanges { public int V; }
        public struct Settings : IComponent { public int V; }                                     // named like a writer
        public struct Clock : ISingletonComponent { public int Ticks; }
        public class Box { public int V; }
        public struct Holder : IComponent { public Box Box; }                                     // a class, no collection
        public struct Link : IComponent { public EntityId Target; }                               // a relation: an id
        public struct Owned : IComponent, IIndexed<EntityId> { public EntityId Owner; public EntityId Key => Owner; } // indexed
        #pragma warning disable EECS017
        public struct Handle : IComponent { public Entity Target; }                               // a handle reaches a world
        #pragma warning restore EECS017
        public struct Grid : ISingletonComponent { public System.Collections.Generic.List<int> Cells; public int[] Slots; }
        """;

    [Test]
    public async Task ValidSystemsGenerateCompilingCode()
    {
        var errors = await Diagnose(Components, """
            namespace Game;
            public struct Quests : IComponent                                    // collections are allowed (written through ref)
            {
                public System.Collections.Generic.Dictionary<int, int> Progress;
                public int[] Slots;
                public EntityId Owner;
            }
            public partial class Outer
            {
                public partial class S : EcsSystem
                {
                    [ForEach, With<Q>] static void A(ref P p) => p.V++;
                    private readonly System.Collections.Generic.List<int> _scratch = []; // readonly: allowed
                    public int Speed => 3;                                              // computed: allowed
                    [ForEach, Without<Q>] void B(Entity e, in P p, World w) => _scratch.Clear();
                    [ForEach] static void C(in P p, ref Clock clock, in Clock read) => clock.Ticks += read.Ticks; // singletons
                    [ForEach] static void D(System.Span<P> p, System.ReadOnlySpan<Q> q, ref Clock clock) => clock.Ticks += p.Length; // batch
                    [ForEach] static void E(Entity e, in P p, ref Clock clock) { if (e.Has<Q>()) clock.Ticks += e.Id.Index; } // read-only entity
                    [ForEach] static void F(ref Holder h, in Q q, ref Grid g) { g.Cells.Add(q.V); h.Box.V++; } // written by ref
                    [ForEach] static void G(in Holder h, ref P p, in Grid g) => p.V = g.Cells.Contains(1) ? g.Cells[0] : h.Box.V; // only read
                    [ForEach, Changed<DynamicBuffer<int>>] static void H(ref DynamicBuffer<int> items, in DynamicBuffer<long> read) => items.Add(read.Count); // buffers
                    [ForEach] static void I(Entity e, in DynamicBuffer<long> read) { ref var items = ref e.Get<DynamicBuffer<int>>(); items.Clear(); foreach (var x in read) items.Add((int)x); }
                    [ForEach, With<Q>] static void J(Entity e, World w) { e.Get<DynamicBuffer<int>>() = default; w.CreateEntity(new P(), new DynamicBuffer<int>()); }
                    [ForEach] static void K(Entity e, in Handle link) { e.Remove<Q>(); link.Target.Destroy(); } // changes structure: runs alone
                    public override int UpdateInterval { get { return 4; } }
                    public override int UpdatePhase => 1;
                }
            }
            public static class Indexed
            {
                public static int Read(World w, Entity e)                                               // reads by Read, in, ref readonly
                {
                    int n = e.Read<Owned>().Owner.Index + e.Owned.Owner.Index;
                    foreach (var item in w.Query<Owned>()) { ref readonly var o = ref item.Component1; n += o.Owner.Index + item.Component1.Owner.Index; }
                    e.Set(new Owned { Owner = e });                                                     // writes by Set
                    return n;
                }
                public struct Count : IForEach<Owned> { public int N; public void Execute(ref Owned o) => N += o.Owner.Index; } // a job that reads
            }
            public partial class Reparent : EcsSystem
            {
                [ForEach] static void B(Entity e, in Owned o) => e.Set(new Owned { Owner = o.Owner });
            }
            public static class Threads
            {
                public static System.Threading.Tasks.Task Count(World w) => System.Threading.Tasks.Task.Run(() => w.Count<P>()); // reads
                public static void Owner(World w) { System.Action spawn = () => w.CreateEntity(); spawn(); } // the caller's thread
                public static void Own(World[] worlds)                                                      // a thread per world
                {
                    foreach (var world in worlds) new System.Threading.Thread(() => { while (true) world.Update(); }).Start();
                    System.Threading.Tasks.Parallel.For(0, worlds.Length, i => worlds[i].Update());
                    System.Threading.Tasks.Task.Run(() => new World().CreateEntity());
                }
            }
            """);
        Assert.That(errors, Is.Empty);
    }

    [TestCase("[ForEach] static void A(ref P p, in Q q, in Clock c) { }", "access.Write<global::P>();", "access.Read<global::Q>();", "access.ReadSingleton<global::Clock>();")]
    [TestCase("[ForEach] static void A(Entity e, in P p) { }", "access.RunsAlone();")]
    [TestCase("[ForEach] static void A(System.Span<P> p, System.ReadOnlySpan<Q> q) { }", "access.Write<global::P>();", "access.Read<global::Q>();", "IForEachBatch<global::P, global::Q>")]
    [TestCase("[ForEach, Changed<DynamicBuffer<int>>] static void A(in DynamicBuffer<int> b, ref P p) { }", ".Changed<global::EasyEcs.DynamicBuffer<int>>(0)", "access.Read<global::EasyEcs.DynamicBuffer<int>>();")]
    [TestCase("[ForEach, Changed<T>] static void A(ref P p) { }", "access.Read<global::T>();", ".Changed<global::T>(0)", "Since(system.LastRunTick + 1)", "Writes => 1;")]
    [TestCase("[ForEach] static void A(ref P p, Maybe<Q> q) { if (q.HasValue) p.V = q.Value.V; }", "access.Read<global::Q>();", "IForEachEntity<global::P>", "INoStructuralChanges", "global::EasyEcs.Maybe<global::Q>.ForRead(entity)")]
    [TestCase("[ForEach] static void A(ref P p, Maybe<Q> q) { if (q.HasValue) q.Value.V++; }", "access.Write<global::Q>();", "global::EasyEcs.Maybe<global::Q>.ForWrite(entity)")]
    [TestCase("[ForEach] static void A(ref P p, Maybe<Q> q) { if (q.HasValue) q.Value = default; }", "access.Write<global::Q>();", "ForWrite(entity)")]
    [TestCase("[ForEach] void A(in P p) { }", "access.RunsAlone();")]
    [TestCase("[ForEach] static void A(in P p) { } protected override void OnUpdate() { }", "access.RunsAlone();")]
    public void AccessIsDeclaredFromParameters(string members, params string[] expected)
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, $"public partial class S : EcsSystem {{ {members} }}"),
            out var output, out _);
        string generated = output.SyntaxTrees.Single(t => t.FilePath.EndsWith(".ForEach.g.cs")).ToString();
        foreach (var line in expected) Assert.That(generated, Does.Contain(line));
        if (!expected.Contains("access.RunsAlone();")) Assert.That(generated, Does.Not.Contain("RunsAlone"));
    }

    [TestCase("public class G<T> : EcsSystem where T : struct, IComponent { [ForEach] static void A(ref T t, in P p) { } } public class H : G<Q>; "
              + "public static class U { public static void F(World w) { w.AddSystem<G<P>>(); w.AddSystem(new G<Q>()); } }",
        "file static class __ForEach<T> where T : struct, global::EasyEcs.IComponent", "UnsafeAccessorType(\"G`1[[!0]]\")",
        "access.Write<T>();", "RegisterGeneric(typeof(global::G<>), typeof(__ForEach<>.Code));",
        "Register(typeof(global::G<global::P>), new __ForEach<global::P>.Code());", "Register(typeof(global::G<global::Q>), new __ForEach<global::Q>.Code());")]
    [TestCase("public static class R { [ForEach] static void A<T>(in T t, ref int n) where T : struct, IComponent { } }",
        "public static void A<T>(global::EasyEcs.World world, ref int n) where T : struct, global::EasyEcs.IComponent",
        "internal ref struct AJob<T> : global::EasyEcs.IForEach<T>, global::EasyEcs.INoStructuralChanges where T : struct, global::EasyEcs.IComponent",
        "internal static extern void A<T>([UnsafeAccessorType(\"R\")] object owner, in T t, ref int n) where T : struct, global::EasyEcs.IComponent;")]
    [TestCase("[Without<Q>, WithAny<T>] public struct G : ISystemGroup; [UpdateInGroup<G>] public class S : EcsSystem { [ForEach, Without<Q>] static void A(ref P p) { } [ForEach, With<P>] static void B() { } }",
        "AFilter = global::EasyEcs.QueryFilter.Create().Without<global::Q>().WithAny<global::T>();", // once, though both name it
        "BFilter = global::EasyEcs.QueryFilter.Create().Without<global::Q>().WithAny<global::T>();", "world.ForEach<global::P, BJob>")]
    [TestCase("public static class R<T> where T : struct, IComponent { [ForEach] static void A(in T t, ref int n) { } }",
        "extension<T>(global::R<T>) where T : struct, global::EasyEcs.IComponent", "new __ForEach<T>.AJob { x1 = ref n }", "public static class R_1ForEach")]
    public void GenericSystemsAndRunnersAreGenerated(string source, params string[] expected)
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, source), out var output, out _);
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
        string generated = string.Concat(output.SyntaxTrees.Where(t => t.FilePath.EndsWith(".ForEach.g.cs")).Select(t => t.ToString()));
        foreach (var line in expected) Assert.That(generated, Does.Contain(line), generated);
    }

    [TestCase("[ForEach] static void A(ref P p, ref int n, in int floor, int add) { }",
        "public static void A(global::EasyEcs.World world, ref int n, in int floor, int add)", "x1 = ref n, x2 = ref floor, x3 = add")]
    [TestCase("[ForEach, Changed<T>] static void A(ref P p, ref Clock c, int world) { }",
        "public static void A(global::EasyEcs.World _world, ulong since, int world)", "__ForEach.AFilter.Since(since)", "s1 = ref _world.SingletonRef")]
    [TestCase("[ForEach(Parallel = true)] static void A(ref P p, in int k) { }", "public int x1;", "x1 = k }", "ForEachParallel<")]
    public void RunnersGetAnEntryPoint(string members, params string[] expected)
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, $"public static partial class R {{ {members} }}"),
            out var output, out _);
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
        string generated = output.SyntaxTrees.Single(t => t.FilePath.EndsWith(".ForEach.g.cs")).ToString();
        foreach (var line in expected) Assert.That(generated, Does.Contain(line), generated);
        Assert.That(generated, Does.Not.Contain("GeneratedUpdate").And.Not.Contain("GeneratedAccess"));
    }

    [TestCase("[ForEach] static void A(Entity e, in P p) { if (e.Has<Q>()) _ = e.Id; }", true, "")]
    [TestCase("[ForEach] static void A(Entity e, ref P p, in Q q) => e.Remove<Q>();", false, "Writes => 1;")]
    [TestCase("[ForEach] static void A(Entity e, in P p) => Use(e); static void Use(Entity e) { }", false, "Writes => 0;")]
    [TestCase("[ForEach] static void A(in P p, World w) { }", false, "Writes => 0;")]
    public void StructuralChangesAreDetectedFromTheMethodBody(string members, bool noStructuralChanges, string writes)
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, $"public partial class S : EcsSystem {{ {members} }}"),
            out var output, out _);
        string generated = output.SyntaxTrees.Single(t => t.FilePath.EndsWith(".ForEach.g.cs")).ToString();
        Assert.That(generated.Contains("INoStructuralChanges"), Is.EqualTo(noStructuralChanges));
        Assert.That(generated, Does.Contain(writes));
    }

    [TestCase("public class S : EcsSystem { private struct H : IComponent; [ForEach] static void A(ref H h) { } }", "EECS022")]
    [TestCase("public partial class S : EcsSystem { [ForEach] static void A(ref P p, int x) { } }", "EECS002")]
    [TestCase("public partial class S : EcsSystem { [ForEach] static void A(P p) { } }", "EECS002")]
    [TestCase("public partial class S : EcsSystem { [ForEach] static void A(World w) { } }", "EECS003")]
    [TestCase("public partial class S : EcsSystem { [ForEach, Without<P>] static void A(Entity e) { } }", "EECS003")]
    [TestCase("public partial class S : EcsSystem { [ForEach] static void A(ref Clock c) { } }", "EECS003")]
    [TestCase("public class S : EcsSystem { [ForEach] static void A<T>(ref T t) where T : struct, IComponent { } }", "EECS004")] // nothing gives T
    [TestCase("public partial class S { [ForEach] void A(ref P p) { } }", "EECS005")]                         // outside a system: static
    [TestCase("public class O { private static class R { [ForEach] static void A(ref P p) { } } }", "EECS022")]   // a runner's type is extended
    [TestCase("public static partial class R { [ForEach] static void A(P p) { } }", "EECS002")]                   // a component by value
    [TestCase("public static partial class R { [ForEach] static void A(in P p, ref int n) { } static void A(World w, ref int n) { } }", "EECS021")]
    [TestCase("public static partial class R { [ForEach(Parallel = true)] static void A(ref P p, ref int n) { } }", "EECS010")]
    [TestCase("public class Bad : IComponent { }", "EECS006")]
    [TestCase("public record Bad : ISingletonComponent;", "EECS006")]
    [TestCase("public partial class S : EcsSystem { [ForEach] static void A(System.Span<P> p, ref Q q) { } }", "EECS009")]
    [TestCase("public partial class S : EcsSystem { [ForEach] static void A(System.Span<P> p, Entity e) { } }", "EECS009")]
    [TestCase("public partial class S : EcsSystem { [ForEach] static void A(EntitySpan es, ref P p) { } }", "EECS009")]           // an EntitySpan needs spans
    [TestCase("public partial class S : EcsSystem { [ForEach] static void A(EntitySpan es, System.Span<P> p) => es[0].Destroy(); }", "EECS013")]
    [TestCase("public partial class S : EcsSystem { [ForEach] static void A(System.Span<Clock> c) { } }", "EECS002")]
    [TestCase("public partial class S : EcsSystem { [ForEach(Parallel = true)] void A(ref P p) { } }", "EECS010")]
    [TestCase("public partial class S : EcsSystem { [ForEach(Parallel = true)] static void A(ref P p, ref Clock c) { } }", "EECS010")]
    [TestCase("public partial class S : EcsSystem { [ForEach(Parallel = true)] static void A(System.Span<P> p) { } }", "EECS010")]
    [TestCase("public partial class S : EcsSystem { [ForEach(Parallel = true)] static void A(Entity e, ref P p) => e.Destroy(); }", "EECS010")]
    [TestCase("public class S : EcsSystem { int _kills; void K() => _kills++; }", "EECS007")]
    [TestCase("public class S : EcsSystem { public int Kills { get; set; } }", "EECS007")]
    [TestCase("public class A : EcsSystem { } public class S : EcsSystem { private readonly A _a = new(); }", "EECS008")]
    [TestCase("public class S : EcsSystem { static int _kills; }", "EECS007")]
    [TestCase("public class A : EcsSystem { } public class S : EcsSystem { protected override void OnUpdate() => World.GetSystem<A>().Enabled = false; }", "EECS008")]
    [TestCase("public partial class S : EcsSystem { [ForEach] static void A(in Handle l) => l.Target.Destroy(); }", "EECS013")]
    [TestCase("public partial class S : EcsSystem { [ForEach] static void A(in Handle l) => l.Target.Clone(); }", "EECS013")]
    [TestCase("public partial class S : EcsSystem { [ForEach(Parallel = true)] static void A(ref P p, in Handle l) => l.Target.Set(new Q()); }", "EECS013")]
    [TestCase("public partial class S : EcsSystem { [ForEach] static void A(Entity e, in Handle l) { if (e.Has<P>()) l.Target.Set(new Q()); } }", "EECS013")]
    [TestCase("public partial class S : EcsSystem { [ForEach] static void A(in Handle l) => l.Target.RemoveP(); }", "EECS013")]   // generated members
    [TestCase("public partial class S : EcsSystem { [ForEach] static void A(Entity e, in Handle l) { if (e.HasP && e.P.V > 0) l.Target.SetQ(1); } }", "EECS013")]
    [TestCase("public class S : EcsSystem { [ForEach] static void A(Entity e, in Handle l) { if (e.Settings.V > 0) l.Target.SetQ(1); } }", "EECS013")] // a read named Set…
    [TestCase("public static class U { public static void F(Entity[] es) => System.Threading.Tasks.Parallel.For(0, es.Length, i => es[i].RemoveP()); }", "EECS014")]
    [TestCase("public static class U { public static void F(World w) { System.Threading.Tasks.Task.Run(() => w.SetClock(1)); w.RemoveClock(); } }", "EECS014")]
    [TestCase("public static class U { public static void F(World w) { System.Threading.Tasks.Task.Run(() => w.CreateEntity()); w.CreateEntity(); } }", "EECS014")]
    [TestCase("public static class U { public static void F(World w, Prefab p) { System.Threading.Tasks.Task.Run(() => w.Instantiate(p)); w.Instantiate(p); } }", "EECS014")]
    [TestCase("public static class U { public static void F(Entity[] es) => System.Threading.Tasks.Parallel.For(0, es.Length, i => es[i].Destroy()); }", "EECS014")]
    [TestCase("public class U { World _w = new(); void F() => new System.Threading.Thread(() => { _w.Update(); }).Start(); void G() => _w.Update(); }", "EECS014")]
    [TestCase("public static class U { public static void F(Entity e) { for (int i = 0; i < 4; i++) System.Threading.ThreadPool.QueueUserWorkItem(_ => e.Remove<P>()); } }", "EECS014")]
    [TestCase("public partial class S : EcsSystem { [ForEach, Removed<T>] static void A(in T t, ref P p) { } }", "EECS016")]
    [TestCase("[With<T>] public struct G : ISystemGroup; [UpdateInGroup<G>] public class S : EcsSystem { [ForEach, Removed<T>] static void A(ref P p) { } }", "EECS016")] // the group's filter
    [TestCase("[Without<Q>] public struct NotAGroup : IComponent;", "EECS023")]
    [TestCase("public partial class S : EcsSystem { [ForEach, Removed<T>, With<T>] static void A(ref P p) { } }", "EECS016")]
    [TestCase("public partial class S : EcsSystem { [ForEach, Removed<T>] static void A(Entity e) { } }", "EECS003")]       // T is no query anchor
    [TestCase("public record struct Target(Entity Of) : IComponent;", "EECS017")]                   // a handle in a component
    [TestCase("public struct Party : IComponent { public Entity[] Members; }", "EECS017")]
    [TestCase("public struct Leader : ISingletonComponent { public Entity Player; }", "EECS017")]
    [TestCase("public struct Pair { public Entity A; } public struct Duel : IComponent { public Pair Sides; }", "EECS017")]
    [TestCase("public static class U { public static void F(Entity e) => e.Get<Owned>() = default; }", "EECS015")]
    [TestCase("public static class U { public static void F(Entity e) => e.GetOrAdd<Owned>(); }", "EECS015")]
    [TestCase("public partial class S : EcsSystem { [ForEach] static void A(ref Owned o) { } }", "EECS015")]
    [TestCase("public partial class S : EcsSystem { [ForEach] static void A(System.Span<Owned> o) { } }", "EECS015")]
    [TestCase("public static class U { public static void F(World w) { foreach (var item in w.Query<Owned>()) item.Component1 = default; } }", "EECS015")]
    [TestCase("public static class U { public static void F(World w) { foreach (var item in w.Query<Owned>()) item.Component1.Owner = default; } }", "EECS015")]
    [TestCase("public static class U { public static void F(World w) { foreach (var item in w.Query<Owned>()) { ref var o = ref item.Component1; } } }", "EECS015")]
    [TestCase("public struct J : IForEach<Owned> { public void Execute(ref Owned o) => o = default; }", "EECS015")]
    [TestCase("public class S : EcsSystem { public override int UpdatePhase => (int)World.UpdateCount; }", "EECS018")]
    [TestCase("[UpdateBefore<B>, UpdateAfter<C>] public class A : EcsSystem { } public class B : EcsSystem { } [UpdateAfter<B>] public class C : EcsSystem { }", "EECS019")]
    [TestCase("public struct G : ISystemGroup; [UpdateInGroup<G>] public class A : EcsSystem { } [UpdateAfter<A>] public class B : EcsSystem { }", "EECS020")]
    [TestCase("public class S : EcsSystem { public override int UpdateInterval { get { return World.EntityCount; } } }", "EECS018")]
    [TestCase("public struct Zone : IComponent, IGroup { public int Id; public int Group => Id; } public partial class S : EcsSystem { [ForEach] static void A(ref Zone z) { } }", "EECS015")]
    [TestCase("public struct Link : IRelation { public EntityId Target { get; set; } } public static class U { public static void F(Entity e, Link l) => e.Get<DynamicBuffer<Link>>().Add(l); }", "EECS015")]
    [TestCase("public struct Link : IRelation { public EntityId Target { get; set; } } public partial class S : EcsSystem { [ForEach] static void A(ref DynamicBuffer<Link> links) { } }", "EECS015")]
    [TestCase("public partial class S : EcsSystem { [ForEach] static void A(ref P p, Maybe<Owned> o) { } }", "EECS015")]
    [TestCase("public partial class S : EcsSystem { [ForEach] static void A(ref P p, in Grid g) => g.Cells.Add(1); }", "EECS011")]
    [TestCase("public partial class S : EcsSystem { [ForEach] static void A(ref P p, in Grid g) => g.Cells.Clear(); }", "EECS011")]
    [TestCase("public partial class S : EcsSystem { [ForEach] static void A(ref P p, in Grid g) => g.Slots[0] += 1; }", "EECS011")]
    [TestCase("public partial class S : EcsSystem { [ForEach] static void A(in Holder h) => h.Box.V++; }", "EECS011")]
    [TestCase("public partial class S : EcsSystem { [ForEach] static void A(in Holder h) => h.Box.V = 1; }", "EECS011")]
    [TestCase("public partial class S : EcsSystem { [ForEach] static void A(System.ReadOnlySpan<Holder> h) => h[0].Box.V++; }", "EECS011")]
    [TestCase("public struct Bag : IComponent { public DynamicBuffer<int> Items; }", "EECS012")]
    [TestCase("public static class U { public static int F(Entity e) { var b = e.Read<DynamicBuffer<int>>(); return b.Count; } }", "EECS012")]
    [TestCase("public static class U { public static int F(DynamicBuffer<int> b) => b.Count; }", "EECS012")]
    [TestCase("public static class U { public static DynamicBuffer<int> F(Entity e) => e.Read<DynamicBuffer<int>>(); }", "EECS012")]
    [TestCase("public static class U { public static void F(Entity a, Entity b) => b.Set(a.Read<DynamicBuffer<int>>()); }", "EECS012")]
    [TestCase("public static class U { public static void F(Entity a, Entity b) => b.Get<DynamicBuffer<int>>() = a.Read<DynamicBuffer<int>>(); }", "EECS012")]
    [TestCase("public static class U { public static void F(Entity a) { var b = new DynamicBuffer<int>(); a.Set(b); } }", "EECS012")]
    public async Task MisuseIsReported(string source, string id)
    {
        var errors = await Diagnose(Components, source);
        Assert.That(errors, Does.Contain(id));
        Assert.That(errors.Where(e => !e.StartsWith("CS")), Is.EqualTo(new[] { id }), "exactly one EasyEcs diagnostic");
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
        var errors = await Diagnose(Components, "public partial class S : EcsSystem { [ForEach] static void A(in DynamicBuffer<int> b) => b.Add(1); }");
        Assert.That(errors.Any(e => e.StartsWith("CS")), Is.True, string.Join(", ", errors));
    }

    [Test]
    public void TheModuleRegistersItsComponentsSingletonsAndBuffersForLoading()
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components,
            "public partial class S : EcsSystem { [ForEach] static void A(in DynamicBuffer<P> b) { } }"), out var output, out _);
        var source = output.SyntaxTrees.Single(t => t.FilePath.EndsWith("EasyEcs.SavedTypes.g.cs")).ToString();
        Assert.That(source, Does.Contain("[global::System.Runtime.CompilerServices.ModuleInitializer]"));
        foreach (var call in new[] { "Component<global::P>()", "Component<global::T>()", "Singleton<global::Clock>()",
                     "Component<global::EasyEcs.DynamicBuffer<global::P>>()" })
            Assert.That(source, Does.Contain("global::EasyEcs.SavedTypes." + call), call);
        // Link's id is a public field, Owned's an auto-property's backing field: both are reached through UnsafeAccessor.
        Assert.That(source, Does.Contain("global::EasyEcs.SavedTypes.EntityIds<global::Link>(static (ref value) => ref Field"));
        Assert.That(source, Does.Contain("Name = \"Target\""));
        Assert.That(source, Does.Contain("Name = \"<Owner>k__BackingField\"").Or.Contain("Name = \"Owner\""));
        Assert.That(source, Does.Not.Contain("EntityIds<global::Handle>"));  // an Entity handle is not an id
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
    }

    [Test]
    public void UnmanagedComponentsAreWrittenCanonicallyWithAHashOfTheirFields()
    {
        string Registration(string fields)
        {
            Driver().RunGeneratorsAndUpdateCompilation(Compile("public struct R : IComponent { " + fields + " }"), out var output, out _);
            var source = output.SyntaxTrees.Single(t => t.FilePath.EndsWith("EasyEcs.SavedTypes.g.cs")).ToString();
            return source.Split('\n').Single(l => l.Contains("SavedTypes.Raw<global::R>")).Trim();
        }

        var original = Registration("public int A; public float B;");
        Assert.That(Registration("public int A; public float B;"), Is.EqualTo(original));
        Assert.That(Registration("public float A; public int B;"), Is.Not.EqualTo(original));   // same size, other fields
        Assert.That(Registration("public int B; public float A;"), Is.Not.EqualTo(original));   // renamed
        Driver().RunGeneratorsAndUpdateCompilation(Compile("public struct M : IComponent { public string S; }"), out var managed, out _);
        Assert.That(managed.SyntaxTrees.Single(t => t.FilePath.EndsWith("EasyEcs.SavedTypes.g.cs")).ToString(), Does.Not.Contain("Raw<"));
    }

    [Test]
    public void UnrelatedEditsHitTheCache()
    {
        var system = CSharpSyntaxTree.ParseText("using EasyEcs;\n" + Components +
            "public partial class S : EcsSystem { [ForEach] static void A(ref P p, in DynamicBuffer<int> b) { } }" +
            "public class G<T> : EcsSystem where T : struct, IComponent { [ForEach] static void B(ref T t) { } } public class H : G<P>;", Parse);
        var other = CSharpSyntaxTree.ParseText("public class Unrelated { }", Parse);
        var compilation = Compile().AddSyntaxTrees(system, other);

        var driver = Driver().RunGenerators(compilation);
        driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(other,
            CSharpSyntaxTree.ParseText("public class Unrelated { int x; }", Parse)));

        var steps = driver.GetRunResult().Results.SelectMany(r => r.TrackedSteps).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.SelectMany(p => p.Value));
        foreach (var name in new[] { "ForEachMethods", "SystemUpdates", "ClosedSystems", "SystemCode", "Components", "BufferElements", "SavedComponents", "SavedBuffers", "ModuleInitializers" })
            Assert.That(steps[name].SelectMany(s => s.Outputs).Select(o => o.Reason),
                Is.All.AnyOf(IncrementalStepRunReason.Cached, IncrementalStepRunReason.Unchanged), name);
    }

    [Test]
    public void GeneratedReadsKeepMethodsStable()
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, """
            public partial class S : EcsSystem
            {
                [ForEach] static void Read(Entity e, ref Q q) { if (e.HasP && e.TryGetT(out var t)) q.V = e.P.V + t.V; }
                [ForEach] static void Write(Entity e, ref Q q) => e.RemoveP();
                [ForEach] static void Grow(Entity e, ref Q q) => e.GetOrAddP().V = q.V;
                [ForEach] static void Check(Entity e, ref Q q) { if (e.IsPDisabled || e.IsDisabled<T>()) q.V = 0; }
                [ForEach] static void Off(Entity e, ref Q q) => e.DisableP();
                [ForEach] static void On(Entity e, ref Q q) => e.Enable<T>();
                [ForEach] static void Fill(Entity e, ref Q q) { if (e.HasItem && e.Item.Count < 4) e.WriteItem().Add(new Item(q.V)); e.WriteT().V++; }
                [ForEach] static void Drop(Entity e, ref Q q) => e.RemoveItem();
            }
            public record struct Item(int V);
            public static class U { public static void Use(Entity e) => e.GetOrAdd<DynamicBuffer<Item>>(); }
            """), out var output, out _);
        string generated = string.Join("\n", output.SyntaxTrees.Where(t => t.FilePath.EndsWith(".ForEach.g.cs")).Select(t => t.ToString()));
        Assert.That(generated, Does.Match(@"struct ReadJob[^\n]*INoStructuralChanges"));
        Assert.That(generated, Does.Not.Match(@"struct WriteJob[^\n]*INoStructuralChanges"));
        Assert.That(generated, Does.Not.Match(@"struct GrowJob[^\n]*INoStructuralChanges"));
        Assert.That(generated, Does.Match(@"struct CheckJob[^\n]*INoStructuralChanges"));
        Assert.That(generated, Does.Not.Match(@"struct OffJob[^\n]*INoStructuralChanges"));
        Assert.That(generated, Does.Not.Match(@"struct OnJob[^\n]*INoStructuralChanges"));
        Assert.That(generated, Does.Match(@"struct FillJob[^\n]*INoStructuralChanges"));                // buffer and tracked writes
        Assert.That(generated, Does.Not.Match(@"struct DropJob[^\n]*INoStructuralChanges"));
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
    }

    [Test]
    public void ComponentsGetNamedMembers()
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, """
            namespace Game { public record struct Speed(float V) : IComponent; internal struct Hidden : IComponent { public int V; } }
            public struct World : IComponent { public int V; }           // an Entity member takes the name
            public struct Tagged : IComponent;                              // a tag
            public struct Fixed : IComponent { public readonly int V; }    // no public constructor, a read-only field: no setter
            public class Outer { private struct Secret : IComponent { public int V; } }
            public partial struct Split : IComponent { public int V; }      // two parts with a base list: one file
            public partial struct Split : System.IEquatable<Split> { public bool Equals(Split other) => V == other.V; }
            #nullable enable
            public struct Named : IComponent { public string? Name; }
            """), out var output, out var diagnostics);
        Assert.That(diagnostics, Is.Empty);
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
        string generated = string.Join("\n", output.SyntaxTrees.Where(t => t.FilePath.EndsWith(".Component.g.cs")).Select(t => t.ToString()));
        Assert.That(generated, Does.Contain("public ref global::P P"));
        Assert.That(generated, Does.Contain("public bool HasP"));
        Assert.That(generated, Does.Contain("public bool TryGetP(out global::P value)"));
        Assert.That(generated, Does.Contain("public ref global::P GetOrAddP()"));
        Assert.That(generated, Does.Not.Contain("GetOrAddOwned"));                                         // indexed: Set only
        Assert.That(generated, Does.Contain("[global::System.CodeDom.Compiler.GeneratedCode(\"EasyEcs.Generator\""));
        Assert.That(generated, Does.Contain("SetNamed(string? name)"));
        Assert.That(output.SyntaxTrees.Count(t => t.FilePath.EndsWith("Split.Component.g.cs")), Is.EqualTo(1));
        Assert.That(generated, Does.Contain("SetP(int v) => @this.Set(new global::P { V = v })"));          // from the fields
        Assert.That(generated, Does.Contain("public global::EasyEcs.Entity RemoveP()"));
        Assert.That(generated, Does.Contain("public ref readonly global::T T"));                            // tracked: read-only
        Assert.That(generated, Does.Contain("public ref global::T WriteT()"));                              // and a ref write
        Assert.That(generated, Does.Not.Contain("WriteOwned"));                                            // indexed: Set only
        Assert.That(generated, Does.Contain("public ref readonly global::Owned Owned"));                    // indexed: read-only
        Assert.That(generated, Does.Contain("ValueIndex<global::Owned, global::EasyEcs.EntityId> OwnedIndex"));
        Assert.That(generated, Does.Contain("public ref global::Clock Clock"));                             // a singleton
        Assert.That(generated, Does.Contain("public void SetClock(int ticks)"));
        Assert.That(generated, Does.Contain("SetSpeed(float V) => @this.Set(new global::Game.Speed(V))"));  // from the constructor
        Assert.That(generated, Does.Contain("public bool IsTagged"));
        Assert.That(generated, Does.Contain("SetTagged() => @this.Set(default(global::Tagged))"));
        Assert.That(generated, Does.Contain("internal ref global::Game.Hidden Hidden"));
        Assert.That(generated, Does.Not.Contain("World World"));
        Assert.That(generated, Does.Contain("public bool HasWorld"));
        Assert.That(generated, Does.Not.Contain("SetFixed"));
        Assert.That(generated, Does.Not.Contain("Secret"));
    }

    [Test]
    public async Task BufferElementsGetNamedMembers()
    {
        const string source = """
            namespace Game;                                                  // members live beside the element
            public struct Q2 : IComponent, ITrackChanges { public int V; }
            public record struct Loot(int Item);
            internal struct Note { public int V; }
            public static class U
            {
                public static int Use(Entity e)
                {
                    e.GetOrAddLoot().Add(new Game.Loot(1));
                    e.WriteLoot().Add(new Game.Loot(2));
                    e.WriteQ2().V = e.Loot.Count;
                    int sum = 0;
                    foreach (var loot in e.Loot) sum += loot.Item;
                    return e.HasLoot && e.RemoveLoot().IsAlive ? sum : 0;
                }
                public static Entity Spawn(World w) => w.CreateEntity(new DynamicBuffer<Loot>());   // names the buffer
                static void Notes(ref DynamicBuffer<Note> notes, in DynamicBuffer<int> ints, in DynamicBuffer<P> components) { }
            }
            """;
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, source), out var output, out var diagnostics);
        Assert.That(diagnostics, Is.Empty);
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
        string generated = string.Join("\n", output.SyntaxTrees.Where(t => t.FilePath.EndsWith(".Buffer.g.cs")).Select(t => t.ToString()));
        Assert.That(generated, Does.Contain("public ref readonly global::EasyEcs.DynamicBuffer<global::Game.Loot> Loot"));
        Assert.That(generated, Does.Contain("internal ref global::EasyEcs.DynamicBuffer<global::Game.Note> WriteNote()"));
        Assert.That(output.SyntaxTrees.Count(t => t.FilePath.EndsWith(".Buffer.g.cs")), Is.EqualTo(2)); // not int, not a component
        Assert.That(await Diagnose(Components, source), Is.Empty);                                    // no EECS012 on the members
    }

    /// <summary>Framework references of the running test host (enough for the generated code to compile).</summary>
    internal static class Basic
    {
        public static readonly ImmutableArray<MetadataReference> References = ((string)System.AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(System.IO.Path.PathSeparator)
            .Where(p => System.IO.Path.GetFileName(p).StartsWith("System.") || System.IO.Path.GetFileName(p) is "netstandard.dll" or "mscorlib.dll")
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToImmutableArray();
    }
}
