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
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

    private static GeneratorDriver Driver() => CSharpGeneratorDriver.Create(
        [new SystemGenerator().AsSourceGenerator(), new ComponentGenerator().AsSourceGenerator()], parseOptions: Parse,
        driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

    /// <summary>
    /// Errors and the diagnostics of the correctness rules (EECS002-024). The performance rules (EECS025-033) are left
    /// out: the cases here write the shortest code that shows a rule, which often also costs; <see cref="Costs"/> checks them.
    /// </summary>
    private static async Task<string[]> Diagnose(params string[] sources) =>
        (await Analyze(sources)).Where(d => d.Severity == DiagnosticSeverity.Error || d.Id.StartsWith("EECS") && !IsPerformance(d.Id))
            .Select(d => d.Id).Distinct().OrderBy(x => x).ToArray();

    /// <summary>The diagnostics of the performance rules (EECS025-034, EECS039), with their properties for the code fixes.</summary>
    internal static async Task<Diagnostic[]> Costs(params string[] sources) =>
        (await Analyze(sources)).Where(d => IsPerformance(d.Id) || d.Id == "EECS034").OrderBy(d => d.Location.SourceSpan.Start).ToArray();

    private static bool IsPerformance(string id) => string.CompareOrdinal(id, "EECS025") >= 0 && string.CompareOrdinal(id, "EECS033") <= 0 || id is "EECS039" or "EECS046";

    private static async Task<ImmutableArray<Diagnostic>> Analyze(string[] sources)
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(sources), out var output, out _);
        return await output.WithAnalyzers([new EasyEcsAnalyzer()]).GetAllDiagnosticsAsync();
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
    // An Entity used only as an identity (Id, Version, IsAlive, ==, an EntityId) reaches no component: scheduled by the rest.
    [TestCase("[ForEach] static void A(Entity e, in P p) { }", "access.Read<global::P>();")]
    [TestCase("[ForEach] static void A(Entity e, ref P p) { if (e.IsAlive && e != default && e.Version > 0) p.V = e.Id.Index; }", "access.Write<global::P>();", "INoStructuralChanges")]
    [TestCase("[ForEach] static void A(Entity e, ref P p) { EntityId id = e; p.V = Index(e) + id.Index; } static int Index(EntityId id) => id.Index;", "access.Write<global::P>();", "INoStructuralChanges")]
    [TestCase("[ForEach] static void A(Entity e, ref P p) => p.V = e.Has<Q>() ? 1 : 0;", "access.RunsAlone();")]          // reads another table
    [TestCase("[ForEach] static void A(Entity e, ref P p) => p.V = e.Equals(default) ? 1 : 0;", "access.RunsAlone();")]    // a call on it
    [TestCase("[ForEach] static void A(Entity e, ref P p) => p.V = Look(e); static int Look(Entity e) => 0;", "access.RunsAlone();")] // the handle passed on
    [TestCase("[ForEach] static void A(Entity e, ref P p) => p.V = e.World.EntityCount;", "access.RunsAlone();")]
    [TestCase("[ForEach] static void A(System.Span<P> p, System.ReadOnlySpan<Q> q) { }", "access.Write<global::P>();", "access.Read<global::Q>();", "IForEachBatch<global::P, global::Q>")]
    [TestCase("[ForEach, Changed<DynamicBuffer<int>>] static void A(in DynamicBuffer<int> b, ref P p) { }", ".Changed<global::EasyEcs.DynamicBuffer<int>>(0)", "access.Read<global::EasyEcs.DynamicBuffer<int>>();")]
    [TestCase("[ForEach, Changed<T>] static void A(ref P p) { }", "access.Read<global::T>();", ".Changed<global::T>(0)", "Since(system.LastRunTick + 1)", "Writes => 1;")]
    [TestCase("[ForEach] static void A(ref P p, Maybe<Q> q) { if (q.HasValue) p.V = q.Value.V; }", "access.Read<global::Q>();", "IForEachBatch<global::P>", "new global::EasyEcs.MaybeColumn<global::Q>(in entities, false)", "ref global::System.Runtime.CompilerServices.Unsafe.Add(")]
    [TestCase("[ForEach] static void A(ref P p, Maybe<Q> q) { if (q.HasValue) q.Value.V++; }", "access.Write<global::Q>();", "MaybeColumn<global::Q>(in entities, false)")]  // untracked: no stamp
    [TestCase("[ForEach] static void A(ref P p, Maybe<T> t) { if (t.HasValue) t.Value = default; }", "access.Write<global::T>();", "MaybeColumn<global::T>(in entities, true)")]  // tracked: stamped
    [TestCase("[ForEach] static void A(Mut<T> t, in P p) => t.SetIfChanged(new T { V = p.V });", "access.Write<global::T>();", "access.Read<global::P>();",
        "IForEachBatch<global::T, global::P>", "new global::EasyEcs.Mut<global::T>(ref global::System.Runtime.CompilerServices.Unsafe.Add(ref r1, i), in entities, (int)i)",
        "Writes => 1;", "OwnStamps => 1;")]                                                                               // stamped by row
    [TestCase("[ForEach] static void A(Entity e, Mut<T> t) { t.Write().V = 1; e.Destroy(); }", "IForEachEntity<global::T>",
        "new global::EasyEcs.Mut<global::T>(ref c1, entity)", "OwnStamps => 1;", "access.RunsAlone();")]                  // moves: by entity
    [TestCase("[ForEach] static void A(in P p, Mut<Q> q) { if (p.V > 0) q.Write().V = p.V; }", "access.Write<global::Q>();", "Writes => 2;", "OwnStamps => 2;")]   // untracked: a plain ref
    [TestCase("[ForEach] static void A(Mut<T> t, Maybe<Q> q) { if (q.HasValue) t.Write().V = q.Value.V; }", "IForEachBatch<global::T>",
        "new global::EasyEcs.Mut<global::T>(ref global::System.Runtime.CompilerServices.Unsafe.Add(ref r1, i), in entities, (int)i)", "OwnStamps => 1;")]
    [TestCase("[ForEach(Parallel = true)] static void A(Mut<T> t) => t.SetIfChanged(default);", "ForEachEntityParallel<global::T,", "OwnStamps => 1;")]
    [TestCase("[ForEach, ChangedAny<T, T2>, ChangedAny<T2, T>] static void A(ref P p) { } public struct T2 : IComponent, ITrackChanges { public int V; }",
        ".ChangedAny<global::T, global::S.T2>(0)", "access.Read<global::T>();", "access.Read<global::S.T2>();", "Since(system.LastRunTick + 1)")]
    [TestCase("[ForEach, RemovedAny<T, T>] static void A(ref P p) { }", ".RemovedAny<global::T, global::T>(0)", "access.Read<global::T>();", "Since(system.LastRunTick + 1)")]
    [TestCase("[ForEach] void A(in P p) { }", "access.RunsAlone();")]
    [TestCase("[ForEach] static void A(in P p) { } protected override void OnUpdate() { }", "access.RunsAlone();")]
    [TestCase("[ForEach(Slices = 3)] static void A(ref P p) { }", "access.Write<global::P>();", "global::EasyEcs.QueryFilter.Create().Slice((int)(system.RunCount % 3), 3)")]
    [TestCase("[ForEach(Slices = 2, Parallel = true), Without<Q>] static void A(ref P p) { }", "ForEachParallel<", "AFilter0.Slice((int)(system.RunCount % 2), 2)")]
    [TestCase("[ForEach(Slices = 4)] static void A(System.Span<P> p) { }", "ForEachBatch<", "Slice((int)(system.RunCount % 4), 4)")]
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
    // Target-typed creations and typeof: without a registration, the closed type's code is made by reflection (not AOT-safe).
    [TestCase("public class G<T> : EcsSystem where T : struct, IComponent { [ForEach] static void A(ref T t) { } } "
              + "public static class U { static G<P> field = new(); static G<Q> Property { get; } = new(); static System.Type Type = typeof(G<Settings>); "
              + "public static void F() { G<T>? local = new(); global::G<Link> qualified = new(); } }",
        "Register(typeof(global::G<global::P>), new __ForEach<global::P>.Code());", "Register(typeof(global::G<global::Q>), new __ForEach<global::Q>.Code());",
        "Register(typeof(global::G<global::Settings>), new __ForEach<global::Settings>.Code());", "Register(typeof(global::G<global::T>), new __ForEach<global::T>.Code());",
        "Register(typeof(global::G<global::Link>), new __ForEach<global::Link>.Code());")]
    [TestCase("public static class R { [ForEach] static void A<T>(in T t, ref int n) where T : struct, IComponent { } }",
        "public static void A<T>(global::EasyEcs.World world, ref int n) where T : struct, global::EasyEcs.IComponent",
        "internal ref struct AJob0<T> : global::EasyEcs.IForEachBatch<T> where T : struct, global::EasyEcs.IComponent",
        "internal static extern void A_0<T>([global::System.Runtime.CompilerServices.UnsafeAccessorType(\"R\")] object owner, in T t, ref int n) where T : struct, global::EasyEcs.IComponent;")]
    [TestCase("[Without<Q>, WithAny<T>] public struct G : ISystemGroup; [UpdateInGroup<G>] public class S : EcsSystem { [ForEach, Without<Q>] static void A(ref P p) { } [ForEach, With<P>] static void B() { } }",
        "AFilter0 = global::EasyEcs.QueryFilter.Create().Without<global::Q>().WithAny<global::T>();", // once, though both name it
        "BFilter1 = global::EasyEcs.QueryFilter.Create().Without<global::Q>().WithAny<global::T>();", "world.ForEach<global::P, BJob1>")]
    [TestCase("public record struct In<T>(int X) : IComponent; public interface IActor<TSelf> : IGroupWith<In<TSelf>>, IGroupWithout<Q> where TSelf : struct; "
        + "public struct Map : IActor<Map>; [UpdateInGroup<Map>] public class S : EcsSystem { [ForEach] static void A(ref P p) { } }",
        ".With<global::In<global::Map>>()", ".Without<global::Q>()")] // the group's filters through a generic interface
    [TestCase("public static class R<T> where T : struct, IComponent { [ForEach] static void A(in T t, ref int n) { } }",
        "extension<T>(global::R<T>) where T : struct, global::EasyEcs.IComponent", "new __ForEach<T>.AJob0 { x1 = ref n }", "public static class R_1ForEach")]
    public void GenericSystemsAndRunnersAreGenerated(string source, params string[] expected)
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, source), out var output, out _);
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
        string generated = string.Concat(output.SyntaxTrees.Where(t => t.FilePath.EndsWith(".ForEach.g.cs")).Select(t => t.ToString()));
        foreach (var line in expected) Assert.That(generated, Does.Contain(line), generated);
    }

    [Test]
    public void AGenericComponentOfAnotherAssemblyFiltersAGroupThroughItsInterface()
    {
        // A framework assembly defines a generic tag and a group interface over it; a user assembly closes both per
        // actor type: In<Map> through the generic API, IActor<Map> as the group whose systems see only In<Map>.
        var framework = CSharpCompilation.Create("Framework",
            [CSharpSyntaxTree.ParseText("""
                using EasyEcs;
                namespace Framework;
                public struct In<TActor> : IComponent;
                public interface IActor<TSelf> : IComponent, IIndexed<TSelf>, ISystemGroup, IGroupWith<In<TSelf>>
                    where TSelf : struct, IActor<TSelf>, System.IEquatable<TSelf>;
                """, Parse)],
            Basic.References.Append(MetadataReference.CreateFromFile(typeof(World).Assembly.Location)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Driver().RunGeneratorsAndUpdateCompilation(framework, out var built, out _);
        using var image = new System.IO.MemoryStream();
        Assert.That(built.Emit(image).Success, Is.True);

        var user = Compile("""
            using Framework;
            public record struct Tiles(int Count) : IComponent;
            public readonly partial record struct Map(long Id) : IActor<Map> { Map IIndexed<Map>.Key => this; }
            [UpdateInGroup<Map>]
            public class Grow : EcsSystem { [ForEach] static void A(ref Tiles t) => t.Count++; }
            public static class Spawn
            {
                public static long Activate(World world)
                {
                    var map = world.CreateEntity(new Tiles(1), new Map(7));
                    map.Set(new In<Map>());
                    world.Update<Map>();
                    map.SetMap(8);
                    foreach (var e in world.MapIndex.Find(new Map(8))) return e.Map.Id * 10 + e.Read<Tiles>().Count;
                    return -1;
                }
            }
            """).AddReferences(MetadataReference.CreateFromImage(image.ToArray()));
        Driver().RunGeneratorsAndUpdateCompilation(user, out var output, out _);
        Assert.That(output.GetDiagnostics().Where(d => d.Severity >= DiagnosticSeverity.Warning), Is.Empty);
        string generated = string.Concat(output.SyntaxTrees.Where(t => t.FilePath.EndsWith(".ForEach.g.cs")).Select(t => t.ToString()));
        Assert.That(generated, Does.Contain(".With<global::Framework.In<global::Map>>()"), generated);
        string members = string.Concat(output.SyntaxTrees.Where(t => t.ToString().Contains("MapIndex")).Select(t => t.ToString()));
        Assert.That(members, Does.Contain("ValueIndex<global::Map, global::Map> MapIndex"), members);

        // Run it: the group sees the actor, the key component keeps its index.
        using var userImage = new System.IO.MemoryStream();
        Assert.That(output.Emit(userImage).Success, Is.True);
        var context = new System.Runtime.Loader.AssemblyLoadContext("actors", isCollectible: true);
        try
        {
            context.LoadFromStream(new System.IO.MemoryStream(image.ToArray()));
            var spawn = context.LoadFromStream(new System.IO.MemoryStream(userImage.ToArray())).GetType("Spawn")!;
            using var world = new World();
            world.AddSystem((EcsSystem)System.Activator.CreateInstance(spawn.Assembly.GetType("Grow")!)!);
            Assert.That(spawn.GetMethod("Activate")!.Invoke(null, [world]), Is.EqualTo(82L));   // key 8, Tiles grown once
        }
        finally { context.Unload(); }
    }

    [TestCase("[ForEach] static void A(ref P p, ref int n, in int floor, int add) { }",
        "public static void A(global::EasyEcs.World world, ref int n, in int floor, int add)", "x1 = ref n, x2 = ref floor, x3 = add")]
    [TestCase("[ForEach, Changed<T>] static void A(ref P p, ref Clock c, int world) { }",
        "public static void A(global::EasyEcs.World _world, ulong since, int world)", "__ForEach.AFilter0.Since(since)", "s1 = ref _world.SingletonRef")]
    [TestCase("[ForEach(Parallel = true)] static void A(ref P p, in int k) { }", "public int x1;", "x1 = k }", "ForEachParallel<")]
    [TestCase("[ForEach(Slices = 3), Without<Q>] static void A(ref P p, int slice) { }",
        "public static void A(global::EasyEcs.World world, int _slice, int slice)", "__ForEach.AFilter0.Slice(_slice, 3)")]
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
    [TestCase("public struct J : IForEachEntity<P>, INoStructuralChanges { public void Execute(Entity e, ref P p) { } } "
        + "public partial class S : EcsSystem { [ForEach(Parallel = true)] static void A(Entity e, ref P p) { var j = new J(); e.Run<P, J>(ref j); } }", "EECS010")]
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
    [TestCase("public struct G : IGroupWith<T>; [UpdateInGroup<G>] public class S : EcsSystem { [ForEach, Removed<T>] static void A(ref P p) { } }", "EECS016")] // through IGroupWith<T>
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
    [TestCase("public partial class S : EcsSystem { [ForEach] static void A(Mut<Owned> o) { } }", "EECS015")]
    [TestCase("public partial class S : EcsSystem { [ForEach] static void A(System.Span<P> p, Mut<T> t) { } }", "EECS009")]
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
    [TestCase("public record struct Rel(EntityId Target) : IRelation; public partial class S : EcsSystem { [ForEach] static void A(in Handle h) => h.Target.AddRelation(new Rel(default)); }", "EECS013")]
    [TestCase("public partial class S : EcsSystem { [ForEach] static void A(in Handle h) => h.Target.World.SetAll(new Q()); }", "EECS013")]
    [TestCase("public partial class S : EcsSystem { [ForEach] static void A(in Handle h) => h.Target.World.TrimExcess(); }", "EECS013")]
    [TestCase("public partial class S : EcsSystem { [ForEach] static void A(in Handle h) => h.Target.World.LoadEntitiesInto(default, [h.Target], markWritten: false); }", "EECS013")]
    [TestCase("public static class U { public static void F(World world) { System.Threading.Tasks.Task.Run(() => world.RemoveAll<P>()); world.CreateEntity(new P()); } }", "EECS014")]
    [TestCase("public class Base { public static string A(World w) => \"\"; } public class R4 : Base { [ForEach] public static void A(ref P p) { } }", "EECS021")]
    [TestCase("public static class U { public static void M() { [ForEach] static void Local(ref P p) => p.V++; } }", "EECS024")]
    [TestCase("[UpdateBefore<G2<int>>] public class G1 : EcsSystem { [ForEach] static void A(ref P p) { } } [UpdateBefore<G1>] public class G2<X> : EcsSystem { [ForEach] static void B(ref Q q) { } }", "EECS019")]
    [TestCase("public class B1 : EcsSystem { public override int UpdateInterval => 4; } public class B2 : B1 { public override int UpdateInterval => System.Environment.TickCount; }", "EECS018")]
    [TestCase("public class S : EcsSystem { public event System.Action Fired; }", "EECS007")]
    [TestCase("public class S(int n) : EcsSystem { public void Tick() => n++; }", "EECS007")]
    [TestCase("public class K : EcsSystem; public class S : EcsSystem { private readonly K[] _all = []; }", "EECS008")]
    [TestCase("public class S : EcsSystem { private readonly EcsSystem _base; }", "EECS008")]
    [TestCase("file class S7 : EcsSystem { [ForEach] static void A(ref P p) { } }", "EECS022")]
    [TestCase("public class S : EcsSystem { [ForEach(Slices = 2), Changed<T>] static void A(ref P p) { } }", "EECS035")]
    [TestCase("public class S : EcsSystem { [ForEach(Slices = 4), Removed<T>] static void A(ref P p) { } }", "EECS035")]
    [TestCase("public class S : EcsSystem { [ForEach(Slices = 0)] static void A(ref P p) { } }", "EECS035")]
    [TestCase("public class S : EcsSystem { [ForEach(Slices = 2), ChangedAny<T, T>] static void A(ref P p) { } }", "EECS035")]
    [TestCase("public static class R { [ForEach(Slices = -1)] static void A(ref P p) { } }", "EECS035")]
    [TestCase("public static class R { [ForEach(Slices = 2)] static void A(ref P p) { } static void A(World w, int slice) { } }", "EECS021")]
    [TestCase("public struct Mount : IComponent, IUniqueIndexed<EntityId> { public EntityId Rider; public EntityId Key => Rider; }", "EECS036")]
    [TestCase("public struct Seat : IComponent, IUniqueIndexed<int>, IIndexed<string> { public int Id; readonly int IIndexed<int>.Key => Id; readonly string IIndexed<string>.Key => \"\"; }", "EECS036")]
    public async Task MisuseIsReported(string source, string id)
    {
        var errors = await Diagnose(Components, source);
        Assert.That(errors, Does.Contain(id));
        Assert.That(errors.Where(e => !e.StartsWith("CS")), Is.EqualTo(new[] { id }), "exactly one EasyEcs diagnostic");
    }

    [TestCase("public struct Named : IComponent { public string Name; } public class S : EcsSystem { [ForEach] static void A(in Named n, ref P p) => p.V = n.Name.Remove(0, 1).Length; }")]
    [TestCase("public static class U { public static async System.Threading.Tasks.Task F(World world) { await System.Threading.Tasks.Task.Run(() => world.CreateEntity(new P())); world.CreateEntity(new P()); } }")]
    [TestCase("public static class U { public static void F(World world) { System.Threading.Tasks.Task.Run(() => world.CreateEntity(new P())).Wait(); world.CreateEntity(new P()); } }")]
    [TestCase("public class S : EcsSystem { [ForEach] static void C(in P p) { using var w = new World(); w.CreateEntity(new Q()); } }")]
    [TestCase("public class S(int n) : EcsSystem { public int N => n; }")]
    [TestCase("public class S : EcsSystem { [ForEach(Slices = 1), Changed<T>] static void A(ref P p) { } [ForEach(Slices = 8, Parallel = true)] static void B(ref Q q) { } }")]
    [TestCase("public record struct Account(int Id) : IComponent, IUniqueIndexed<int> { public int Key => Id; } public static class U { public static Entity F(World w) => w.AccountIndex.Get(1); }")]
    [TestCase("public static class U { public static bool F(ref DynamicBuffer<int> b) => System.Runtime.CompilerServices.Unsafe.IsNullRef(ref b); }")] // a 'ref readonly' argument is no copy
    public async Task ValidCodeIsNotReported(string source)
    {
        var errors = await Diagnose(Components, source);
        Assert.That(errors, Is.Empty);
    }

    [TestCase("public class S : EcsSystem { [ForEach] static void A(ref Missing m) { } }")]
    [TestCase("public class S : EcsSystem { [ForEach, Removed<>, With<>] static void A(ref P p) { } }")]
    [TestCase("[UpdateBefore<Missing>] public class S : EcsSystem { [ForEach] static void A(ref P p) { } }")]
    public async Task UnfinishedCodeGetsOnlyTheCompilersErrors(string source)
    {
        var errors = await Diagnose(Components, source);
        Assert.That(errors.Where(e => !e.StartsWith("CS")), Is.Empty);
    }

    // The rules of calls and declarations that compile but do not do what they say (EECS036 for plain components,
    // EECS037, EECS038, EECS040-EECS045), and the false negatives of EECS014 and EECS017: each case reports exactly one.
    // EECS037: a hand-written job's Writes.
    [TestCase("public struct J : IForEach<T> { public void Execute(ref T t) => t.V++; public static int Writes => 0; }", "EECS037")]       // written, left out
    [TestCase("public struct J : IForEach<T> { public int N; public void Execute(ref T t) => N += t.V; }", "EECS037")]                        // read, stamped by default
    [TestCase("public struct J : IForEach<T, P> { public int N; public void Execute(ref T t, ref P p) => N += t.V + p.V; public static int Writes => 0b11; }", "EECS037")]
    [TestCase("public struct J : IForEachEntity<P> { public void Execute(Entity e, ref P p) { p.V = 1; e.Set(new Q()); } public static int Writes => 0; }", "EECS037")] // not carried
    [TestCase("public struct J : IForEachBatch<T> { public void Execute(EntitySpan e, System.Span<T> t) => t[0].V = 1; public static int Writes => 0; }", "EECS037")]
    [TestCase("public struct J : IForEach<T> { public void Execute(ref T t) => Helpers.Inc(ref t.V); static int IForEach<T>.Writes => 0; }", "EECS037")]  // explicit
    // EECS038: a changed copy that is dropped.
    [TestCase("public static class U { public static void F(Entity e) { var p = e.Get<P>(); p.V = 1; } }", "EECS038")]
    [TestCase("public static class U { public static void F(Entity e) { var p = e.P; p.V -= 1; } }", "EECS038")]                          // a generated property
    [TestCase("public static class U { public static void F(Entity e) { var t = e.T; t.V++; } }", "EECS038")]                           // ref readonly (tracked)
    [TestCase("public static class U { public static void F(World w) { var c = w.Clock; c.Ticks++; } }", "EECS038")]                    // a singleton
    [TestCase("public static class U { public static void F(World w) { foreach (var item in w.Query<P>()) { var p = item.Component1; p.V++; } } }", "EECS038")]
    [TestCase("public static class U { public static void F(Entity e) { if (e.TryGet<P>(out var p)) p.V = 1; } }", "EECS038")]          // TryGet's copy
    [TestCase("public static class U { public static void F(Entity e) { var p = e.P; if (p.V > 0) p.V = 0; } }", "EECS038")]              // read before, not after
    [TestCase("public class S : EcsSystem { [ForEach] static void A(ref P p, Maybe<Q> q) { if (!q.HasValue) return; var v = q.Value; v.V = p.V; } }", "EECS038")]
    // EECS040: SetIfChanged on a type it cannot compare.
    [TestCase("public struct Named : IComponent, ITrackChanges { public string Name; } public static class U { public static bool F(Entity e) => e.SetIfChanged(new Named()); }", "EECS040")]
    [TestCase("public struct Named : IComponent, ITrackChanges { public string Name; } public class S : EcsSystem { [ForEach] static void A(Mut<Named> n, in P p) { if (p.V > 0) n.SetIfChanged(default); } }", "EECS040")]
    // EECS041: a call a pass refuses, during one.
    [TestCase("public static class U { public static void F(World w) { foreach (var item in w.Query<P>()) w.TrimExcess(); } }", "EECS041")]
    [TestCase("public static class U { public static void F(World w, WorldSnapshot s) { foreach (var item in w.Query<P>()) if (item.Component1.V < 0) w.Restore(s); } }", "EECS041")]
    [TestCase("public static class U { public static void F(World w, World other, EntityId[] ids) { foreach (var item in other.Query<P>()) w.MoveEntities(other, ids); } }", "EECS041")]
    [TestCase("public class S : EcsSystem { [ForEach] static void A(Entity e, ref P p) { if (p.V < 0) e.World.Capture(); } }", "EECS041")]
    [TestCase("public class S : EcsSystem { [ForEach] void A(ref P p) { if (p.V < 0) World.Save(new System.Buffers.ArrayBufferWriter<byte>()); } }", "EECS041")]
    [TestCase("public static class R { [ForEach] static void A(World w, ref P p) { if (p.V < 0) w.TrimExcess(); } }", "EECS041")]
    [TestCase("public struct J : IForEachEntity<P> { public void Execute(Entity e, ref P p) { if (p.V < 0) e.World.TrimExcess(); } }", "EECS041")]
    // EECS042: an indexed component changed while its index is walked.
    [TestCase("public static class U { public static void F(World w) { foreach (var e in w.Index<Owned, EntityId>().Find(default)) e.Set(new Owned()); } }", "EECS042")]
    [TestCase("public static class U { public static void F(World w) { foreach (var e in w.OwnedIndex.Find(default)) e.RemoveOwned(); } }", "EECS042")]  // generated
    [TestCase("public static class U { public static void F(World w, EntityId id) { foreach (var c in w.Children(id)) c.Disable<ChildOf>(); } }", "EECS042")]
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
    [TestCase("public static class U { public static void F(Entity e) => e.Remove<Destroyed>(); }", "EECS045")]
    [TestCase("public static class U { public static void F(Entity e) => e.Set(new Disabled<P>()); }", "EECS045")]
    [TestCase("public static class U { public static Entity F(World w) => w.CreateEntity(new P(), new Destroyed()); }", "EECS045")]
    [TestCase("public static class U { public static int F(World w) => w.RemoveAll<Disabled<P>>(); }", "EECS045")]
    [TestCase("public static class U { public static Prefab F() => new Prefab().Set(new Destroyed()); }", "EECS045")]
    [TestCase("public static class U { public static void F(Entity e) => e.GetOrAdd<Disabled<Q>>(); }", "EECS045")]
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
    [TestCase("public struct J : IForEach<T, P> { public void Execute(ref T t, ref P p) => p.V = t.V; public static int Writes => 0b10; }")]
    [TestCase("public struct J : IForEach<P> { public int N; public void Execute(ref P p) => N += p.V; }")]                           // untracked: no stamp
    [TestCase("public struct J : IForEach<T> { public void Execute(ref T t) => t.V++; }")]                                           // the default: all
    [TestCase("public struct J : IForEach<P> { public void Execute(ref P p) => p.V++; public static int Writes => 0; }")]           // untracked, no move
    [TestCase("public struct J : IForEach<T> { public void Execute(ref T t) { } public static int Writes => 1; public static int OwnStamps => 1; }")]
    [TestCase("public struct J : IForEach<T> { public int N; public void Execute(ref T t) => N += Helpers.Look(new P { V = t.V }); public static int Writes => Bits(); static int Bits() => 1; }")] // computed
    [TestCase("public static class U { public static void F(Entity e) { var p = e.Get<P>(); p.V = 1; e.Set(p); } }")]
    [TestCase("public static class U { public static int F(Entity e) { var p = e.P; p.V++; return p.V; } }")]
    [TestCase("public static class U { public static int F(Entity e, int n) { int s = 0; var p = e.P; for (int i = 0; i < n; i++) { s += p.V; p.V++; } return s; } }")]
    [TestCase("public static class U { public static void F(Entity e) { ref var p = ref e.P; p.V = 1; } }")]
    [TestCase("public static class U { public static System.Func<int> F(Entity e) { var p = e.P; p.V = 1; return () => p.V; } }")]
    [TestCase("public static class U { public static int F(Entity e) { var p = e.P; return p.V; } }")]
    [TestCase("public static class U { public static int F(Entity e) => e.TryGet<P>(out var p) ? p.V : 0; }")]
    [TestCase("public record struct Named(string Name) : IComponent, ITrackChanges; public static class U { public static bool F(Entity e) => e.SetIfChanged(new Named(\"a\")); }")]
    [TestCase("public static class U { public static bool F(Entity e) => e.SetIfChanged(new T { V = 1 }); }")]                      // unmanaged: its bytes
    [TestCase("public static class U { public static void F(World w, World other) { foreach (var item in w.Query<P>()) other.TrimExcess(); } }")]
    [TestCase("public static class U { public static void F(World w) { foreach (var item in w.Query<P>()) { } w.TrimExcess(); } }")]
    [TestCase("public class S : EcsSystem { [ForEach] static void A(ref P p) { using var w = new World(); w.TrimExcess(); } }")]
    [TestCase("public static class U { public static void F(World w) { foreach (var e in w.OwnedIndex.Find(default)) e.Destroy(); } }")]
    [TestCase("public static class U { public static void F(World w) { foreach (var e in w.OwnedIndex.Find(default)) e.Set(new P()); } }")]
    [TestCase("public static class U { public static void F(World w, System.Collections.Generic.List<Entity> found) { foreach (var e in w.OwnedIndex.Find(default)) found.Add(e); foreach (var e in found) e.RemoveOwned(); } }")]
    [TestCase("public readonly record struct Shop(long Id) : IComponent, IIndexed<Shop> { Shop IIndexed<Shop>.Key => this; }")]
    [TestCase("public readonly struct Shop : IComponent, IIndexed<Shop>, System.IEquatable<Shop> { public readonly long Id; public Shop Key => this; public bool Equals(Shop o) => Id == o.Id; }")] // no record: no printed properties
    [TestCase("public static class U { public static int F(World w) => w.Count<P>(QueryFilter.Create().Slice(0, 1)); }")]           // one slice: the whole tables
    [TestCase("public static class U { public static int F(World w) { var f = QueryFilter.Create().Slice(0, 2); f = QueryFilter.Create(); return w.Count<P>(f); } }")]
    [TestCase("public struct J : IForEach<P> { public void Execute(ref P p) => p.V++; } public static class U { public static void F(World w) { var j = new J(); w.ForEach<P, J>(ref j, QueryFilter.Create().Slice(0, 2)); } }")]
    [TestCase("public static class U { public static bool F(Entity e) => e.Has<Destroyed>() || e.IsDisabled<P>(); }")]
    [TestCase("public static class U { public static int F(World w) => w.Count<P>(QueryFilter.Create().With<Destroyed>().Without<Disabled<Q>>()); }")]
    [TestCase("public class S : EcsSystem { [ForEach, With<Disabled<P>>] static void A(ref Q q) => q.V++; }")]
    [TestCase("public static class U { public static void F(Entity e) { e.Disable<P>(); e.Enable<P>(); } }")]
    [TestCase("public class U { World _w = new(); void Work() => _w.CreateEntity(); public async System.Threading.Tasks.Task F() { await System.Threading.Tasks.Task.Run(Work); _w.CreateEntity(); } }")] // awaited
    [TestCase("public class U { World _w = new(); void Work() => _w.CreateEntity(); public void F() { Work(); _w.CreateEntity(); } }")]         // no hand-off
    [TestCase("public static class U { static void Work(object s) { using var w = new World(); w.CreateEntity(); } public static void F() => System.Threading.ThreadPool.QueueUserWorkItem(Work); }")] // a world of its own
    [TestCase("public static class U { public static void F(Entity e) => e.Set(new DynamicBuffer<EntityId>()); }")]
    public async Task UsageValidCodeIsNotReported(string source)
    {
        var errors = await Diagnose(Components, Extra, source);
        Assert.That(errors, Is.Empty);
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
        var errors = await Diagnose(Components, "public partial class S : EcsSystem { [ForEach] static void A(in DynamicBuffer<int> b) => b.Add(1); }");
        Assert.That(errors.Any(e => e.StartsWith("CS")), Is.True, string.Join(", ", errors));
    }

    [Test]
    public void TheModuleTellsWorldsHowToIndexItsUniqueComponents()
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, """
            public record struct Account(int Id) : IComponent, IUniqueIndexed<int> { public int Key => Id; }
            public record struct Mount(EntityId Rider) : IComponent, IUniqueIndexed<EntityId> { public EntityId Key => Rider; }
            """), out var output, out _);
        var source = output.SyntaxTrees.Single(t => t.FilePath.EndsWith("EasyEcs.SavedTypes.g.cs")).ToString();
        Assert.That(source, Does.Contain("global::EasyEcs.SavedTypes.Component<global::Account>()"));
        Assert.That(source, Does.Contain("global::EasyEcs.SavedTypes.UniqueIndex<global::Account, int>()"));
        Assert.That(source, Does.Not.Contain("UniqueIndex<global::Mount"));            // an EntityId key cannot be unique (EECS036)
        Assert.That(source, Does.Not.Contain("UniqueIndex<global::Owned"));            // IIndexed only
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
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
            "public struct L; public partial class S : EcsSystem { [ForEach] static void A(ref P p, in DynamicBuffer<L> b) { } [ForEach(Slices = 4)] static void D(ref Q q) { } }" +
            "public class G<T> : EcsSystem where T : struct, IComponent { [ForEach] static void B(ref T t) { } } public class H : G<P>;" +
            "public static class U { static void C(World w) { foreach (var i in w.Query<P>()) i.P.V++; } }", Parse);
        var other = CSharpSyntaxTree.ParseText("public class Unrelated { }", Parse);
        var compilation = Compile().AddSyntaxTrees(system, other);

        var driver = Driver().RunGenerators(compilation);
        driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(other,
            CSharpSyntaxTree.ParseText("public class Unrelated { int x; }", Parse)));

        var steps = driver.GetRunResult().Results.SelectMany(r => r.TrackedSteps).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.SelectMany(p => p.Value));
        foreach (var name in new[] { "ForEachMethods", "SystemUpdates", "ClosedSystems", "SystemCode", "Components", "ExtensionMembers", "BufferUses", "BufferElements", "QueryShapes", "SavedComponents", "SavedBuffers", "ModuleInitializers" })
            Assert.That(steps[name].SelectMany(s => s.Outputs).Select(o => o.Reason),
                Is.All.AnyOf(IncrementalStepRunReason.Cached, IncrementalStepRunReason.Unchanged), name);
    }

    [Test]
    public void GeneratedNamesNeverCollide()
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, """
            public class S : EcsSystem
            {
                [ForEach] static void A(ref P p) { }
                [ForEach] static void A(ref Q q) { }      // an overload
                [ForEach] static void Code(ref P p) { }   // the generated class's name
                [ForEach] static void world(ref P p) { }  // the generated local's name
            }
            public static class R { [ForEach] static void Count(in P p, ref int job) => job++; }
            namespace N { public struct A_B : IComponent { public int V; } public static class A { public struct B : IComponent { public int V; } } }
            public struct HP : IComponent { public int V; }
            public struct Hp : IComponent { public int V; }                           // file names ignore case
            namespace Shared
            {
                // Members two types of a namespace would both get: each gives them up, and keeps the others.
                public struct Dead : IComponent;                                       // entity.IsDead
                public struct IsDead : IComponent { public bool V; }                   // entity.IsDead
                public struct Target : IComponent { public int V; }                    // entity.HasTarget
                public struct HasTarget : IComponent { public int V; }                 // entity.HasTarget
                public struct Shield : IComponent { public int V; }                    // entity.IsShieldDisabled
                public struct ShieldDisabled : IComponent;                             // entity.IsShieldDisabled
                public struct Zone : IComponent, IIndexed<int> { public int V; public int Key => V; }  // world.ZoneIndex
                public struct ZoneIndex : ISingletonComponent { public int V; }        // world.ZoneIndex
                public struct Loot { public int V; }                                    // LootEcsPrefabBufferExtensions
                public struct LootPrefab { public int V; }                              // LootPrefabEcsBufferExtensions
                public static class Use
                {
                    public static bool F(Entity e, World w, ref DynamicBuffer<Loot> a, ref DynamicBuffer<LootPrefab> b) =>
                        e.Has<Dead>() && e.Has<IsDead>() && e.SetDead().HasIsDead && e.Has<Target>() && e.Get<HasTarget>().V > 0 && e.Target.V > 0
                        && e.IsDisabled<Shield>() && e.SetShieldDisabled().HasShield && w.Index<Zone, int>().Count(1) > 0
                        && w.HasZoneIndex && e.HasLoot && e.HasLootPrefab;
                }
            }
            """), out var output, out var problems);
        Assert.That(problems, Is.Empty);
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()), Is.Empty);
        string generated = string.Join("\n", output.SyntaxTrees.Where(t => t.FilePath.Contains("Shared.")).Select(t => t.ToString()));
        foreach (var member in new[] { "bool IsDead\n", "bool HasTarget\n", " ZoneIndex\n" })
            Assert.That(generated, Does.Not.Contain(member), member);
        // A prefab has IsShieldDisabled once (the tag's), so it keeps it; the entity would have it twice.
        var classes = generated.Split("[global::System.CodeDom.Compiler.GeneratedCode(");
        Assert.That(classes.Where(c => c.Contains("bool IsShieldDisabled")).Select(c => c.Split('"')[1]), Is.EqualTo(new[] { "EasyEcs.Generator.Prefab" }));
        Assert.That(generated, Does.Contain("class LootEcsPrefabBufferExtensions").And.Contain("class LootPrefabEcsBufferExtensions"));
    }

    [Test]
    public void EdgeInputsCompile()
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, """
            using System.Diagnostics.CodeAnalysis;
            public enum Color { Red, Blue }
            public struct @class : IComponent { public int V; }                                    // a keyword name
            public struct @event : ISingletonComponent { public int V; }
            public struct Defaults : IComponent
            {
                public float F; public Color C; public double D; public decimal M; public long L;
                public Defaults(float f = 1.5f, Color c = Color.Blue, double d = double.NaN, decimal m = 2.5m, long l = -3, string? s = null) { F = f; C = c; D = d; M = m; L = l; }
            }
            public struct This : IComponent { public int this_; public This(int @this) => this_ = @this; }   // a parameter named this
            public struct Self : IComponent { public int This; }
            public struct Twice : IComponent { public int X; public int x; }                        // X and x
            public unsafe struct Fixed : IComponent { public fixed int A[4]; }
            public unsafe struct Pointer : IComponent { public int* P; }
            public struct Required : IComponent { public required int A; public int B; public Required(int b) => B = b; }
            public struct Sets : IComponent { public required int A; [SetsRequiredMembers] public Sets(int a) => A = a; }
            file struct Secret : IComponent { public int V; }
            public static class O1 { public struct State : IComponent { public int V; } }
            public static class O2 { public struct State : IComponent { public int V; } }
            public struct Unsafe : IComponent { public int V; }                                     // a CompilerServices name
            internal struct Cfg { public int V; }
            public struct Buffer : IComponent { public int V; }                                     // Prefab.SetBuffer takes SetBuffer
            public struct Props : IComponent { public int Hp { get; set; } public float Speed { get; init; } }  // auto-properties
            public struct Locked : IComponent { public int Hp { get; private set; } }              // not settable: no SetLocked
            public struct Ally : IRelation { public EntityId Target { get; init; } public int Weight; }
            public record struct Bag : IRelation { public EntityId Target { get; set; } public string Name { get; set; } }  // a record's own members
            public static class R
            {
                [ForEach] static void @default(ref P p) { }
                [ForEach] static void Uses(ref P p, Cfg cfg) { }                                    // internal context: internal entry point
                [ForEach] static void Typed<world>(ref P p, world w) where world : struct { }       // a type parameter named world
                [ForEach] static void Span(ref P p, System.ReadOnlySpan<int> table) { }
            }
            public class S : EcsSystem
            {
                [ForEach] static ref int Bump(ref P p) => ref p.V;                                  // a ref return
                [ForEach] static void @default(ref Q q) { }
                [ForEach] static void Owned(Entity owner, ref P p) { }                              // the accessor's instance name
                [ForEach] void Mine(ref P p, in Q owner, in Q _owner) { }
            }
            public static class U
            {
                public static void Use(Entity e, World w)
                {
                    e.SetDefaults(); e.SetDefaults(2f, Color.Red); e.SetThis(1); e.SetSelf(2); e.SetSets(3); _ = e.@class; _ = w.@event;
                    e.Set(new O1.State()); e.Set(new O2.State());
                    e.SetBuffer(1).SetProps(hp: 1, speed: 2f).AddAlly(e, 3).AddBag(e, "bag").Set(new Locked());
                    _ = new Prefab().SetProps(1, 2f).Set(new Buffer { V = 5 }).HasBuffer;
                }
                static void Links(in DynamicBuffer<Ally> allies, in DynamicBuffer<Bag> bags) { }
            }
            """), out var output, out var problems);
        Assert.That(problems, Is.Empty);
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()), Is.Empty);
        string generated = string.Join("\n", output.SyntaxTrees.Where(t => t.FilePath.Contains(".g.cs")).Select(t => t.ToString()));
        Assert.That(generated, Does.Contain("Entity SetBuffer(int v)").And.Not.Contain("Prefab SetBuffer("));   // the prefab's own SetBuffer<T> would win
        Assert.That(generated, Does.Contain("SetProps(int hp, float speed) => @this.Set(new global::Props { Hp = hp, Speed = speed })")
            .And.Contain("AddAlly(global::EasyEcs.EntityId target, int weight) => @this.AddRelation(new global::Ally { Target = target, Weight = weight })")
            .And.Not.Contain("SetLocked("));
    }

    [Test]
    public void AFriendAssemblyUsesTheMembersOfItsLibrary()
    {
        // A library and a friend (InternalsVisibleTo) both iterating Read<P>: one class for the shape, the library's.
        // The friend reads an internal component through its generated properties too (CS0570 on internal ones).
        var library = Compile("""
            [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Test")]
            namespace L;
            public struct P : IComponent { public int V; }
            internal struct Q : IComponent { public int V; }
            internal struct Clock : ISingletonComponent { public int Ticks; }
            internal struct Zone : IComponent, IIndexed<int> { public int V; public int Key => V; }
            internal struct Dead : IComponent;
            public static class LU { public static int F(World w) { int s = 0; foreach (var i in w.Read<P>()) s += i.P.V; foreach (var i in w.Query<Q>()) i.Q.V++; return s; } }
            """).WithAssemblyName("Lib");
        Driver().RunGeneratorsAndUpdateCompilation(library, out var built, out _);
        using var image = new System.IO.MemoryStream();
        var emitted = built.Emit(image);
        Assert.That(emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()), Is.Empty);

        var friend = Compile("""
            using L;
            public static class TU
            {
                public static int F(World w, Entity e)
                {
                    int s = 0;
                    foreach (var i in w.Read<P>()) s += i.P.V;
                    foreach (var i in w.Query<Q>()) i.Q.V++;
                    e.SetQ(1).SetZone(2).SetDead();
                    w.SetClock(3);
                    return e.HasQ && e.IsDead && e.Q.V + e.Zone.V + w.Clock.Ticks + w.ZoneIndex.Count(2) > s ? 1 : 0;
                }
            }
            """).AddReferences(MetadataReference.CreateFromImage(image.ToArray()));
        Driver().RunGeneratorsAndUpdateCompilation(friend, out var output, out var problems);
        Assert.That(problems, Is.Empty);
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()), Is.Empty);
        Assert.That(output.SyntaxTrees.Where(t => t.FilePath.Contains("EasyEcs.QueryItem.")), Is.Empty);   // both shapes are the library's
    }

    [Test]
    public void ATupleWriteThroughAMaybeIsAWrite()
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components,
            "public class S : EcsSystem { [ForEach] static void A(ref P p, Maybe<Q> q) { if (q.HasValue) (q.Value.V, p.V) = (1, 2); } }"),
            out var output, out _);
        string generated = string.Join("\n", output.SyntaxTrees.Where(t => t.FilePath.EndsWith(".ForEach.g.cs")).Select(t => t.ToString()));
        Assert.That(generated, Does.Contain("access.Write<global::Q>();"));
    }

    [Test]
    public void APartialMethodIsJudgedByItsBody()
    {
        var diagnostics = Diagnose(Components, """
            public partial class S : EcsSystem
            {
                [ForEach(Parallel = true)] static partial void A(Entity e, ref P p);
                static partial void A(Entity e, ref P p) { if (e.Has<Q>()) p.V++; }
            }
            """).Result;
        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public void ARefReturningMethodRuns()
    {
        using var world = new World();
        var e = world.CreateEntity(new Counted { V = 1 });
        world.AddSystem<RefReturning>();
        world.Update();
        Assert.That(e.Get<Counted>().V, Is.EqualTo(2));
    }

    public struct Counted : IComponent { public int V; }

    public class RefReturning : EcsSystem
    {
        [ForEach] private static ref int Bump(ref Counted c)
        {
            c.V++;
            return ref c.V;
        }
    }

    [Test]
    public void OverloadedRunnersWithOneEntryPointAreReported()
    {
        var diagnostics = Diagnose(Components, "public static class R { [ForEach] static void A(ref P p) { } [ForEach] static void A(ref Q q) { } }").Result;
        Assert.That(diagnostics, Does.Contain("EECS021"));
    }

    [Test]
    public void AnotherSliceCountReemitsOnlyItsSystem()
    {
        var sliced = CSharpSyntaxTree.ParseText("using EasyEcs;\n" + Components + "public class S : EcsSystem { [ForEach(Slices = 2)] static void A(ref P p) { } }", Parse);
        var other = CSharpSyntaxTree.ParseText("using EasyEcs;\npublic class O : EcsSystem { [ForEach(Slices = 2)] static void B(ref Q q) { } }", Parse);
        var compilation = Compile().AddSyntaxTrees(sliced, other);
        var driver = Driver().RunGenerators(compilation);
        driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(sliced, CSharpSyntaxTree.ParseText(
            "using EasyEcs;\n" + Components + "public class S : EcsSystem { [ForEach(Slices = 3)] static void A(ref P p) { } }", Parse)));

        var reasons = Steps(driver)["SystemCode"].SelectMany(s => s.Outputs).Select(o => o.Reason).ToArray();
        Assert.That(reasons.Count(r => r is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged), Is.EqualTo(1)); // O
        Assert.That(reasons.Count(r => r == IncrementalStepRunReason.Modified), Is.EqualTo(1));                                      // S
        string generated = driver.GetRunResult().GeneratedTrees.Select(t => t.ToString()).Single(t => t.Contains("Name = \"A\""));
        Assert.That(generated, Does.Contain("Slice((int)(system.RunCount % 3), 3)"));
    }

    [Test]
    public void AnEditAboveAMethodKeepsItsCode()
    {
        const string system = "public class S : EcsSystem { [ForEach] static void A(ref P p) { } [ForEach] static void B(in P p) { } }";
        var tree = CSharpSyntaxTree.ParseText("using EasyEcs;\n" + Components + system, Parse);
        var compilation = Compile().AddSyntaxTrees(tree);
        var driver = Driver().RunGenerators(compilation);
        driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(tree,
            CSharpSyntaxTree.ParseText("using EasyEcs;\n// a comment moves every method\n" + Components + system, Parse)));

        var steps = Steps(driver);
        foreach (var name in new[] { "SystemUpdates", "SystemCode" })
            Assert.That(steps[name].SelectMany(s => s.Outputs).Select(o => o.Reason),
                Is.All.AnyOf(IncrementalStepRunReason.Cached, IncrementalStepRunReason.Unchanged), name);
    }

    [Test]
    public void ANewBufferElementLeavesTheOthersCached()
    {
        var first = CSharpSyntaxTree.ParseText("using EasyEcs;\npublic struct L1; public struct L2; public static class U { static DynamicBuffer<L1> a; static DynamicBuffer<L2> b; }", Parse);
        var compilation = Compile().AddSyntaxTrees(first);
        var driver = Driver().RunGenerators(compilation);
        driver = driver.RunGenerators(compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText("using EasyEcs;\npublic struct L3; public static class V { static DynamicBuffer<L3> c; }", Parse)));

        var reasons = Steps(driver)["BufferElements"].SelectMany(s => s.Outputs).Select(o => o.Reason).ToArray();
        Assert.That(reasons.Count(r => r is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged), Is.EqualTo(2));
        Assert.That(reasons.Count(r => r == IncrementalStepRunReason.New), Is.EqualTo(1));
    }

    [Test]
    public void TheArityGeneratorIsCachedBetweenEdits()
    {
        var other = CSharpSyntaxTree.ParseText("public class Unrelated { }", Parse);
        var compilation = CSharpCompilation.Create("EasyEcs.Core", [other], Basic.References, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new ArityGenerator().AsSourceGenerator()], parseOptions: Parse,
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGenerators(compilation);
        driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(other, CSharpSyntaxTree.ParseText("public class Unrelated { int x; }", Parse)));

        Assert.That(Steps(driver)["IsEasyEcsCore"].SelectMany(s => s.Outputs).Select(o => o.Reason),
            Is.All.AnyOf(IncrementalStepRunReason.Cached, IncrementalStepRunReason.Unchanged));
    }

    private static System.Collections.Generic.Dictionary<string, System.Collections.Generic.IEnumerable<IncrementalGeneratorRunStep>> Steps(GeneratorDriver driver) =>
        driver.GetRunResult().Results.SelectMany(r => r.TrackedSteps).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.SelectMany(p => p.Value));

    [Test]
    public void AMaybeOfAStructuralMethodIsLookedUpPerEntity()
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components,
            "public partial class S : EcsSystem { [ForEach] static void A(Entity e, ref P p, Maybe<Q> q) { if (q.HasValue) e.Destroy(); } }"),
            out var output, out _);
        string generated = string.Join("\n", output.SyntaxTrees.Where(t => t.FilePath.EndsWith(".ForEach.g.cs")).Select(t => t.ToString()));
        Assert.That(generated, Does.Contain("IForEachEntity<global::P>").And.Contain("global::EasyEcs.Maybe<global::Q>.ForRead(entity)"));
    }

    [Test]
    public void ARunnerKeepsTheCallersRefValuesInLocalsPerRun()
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, """
            public static class R
            {
                [ForEach] static void Sum(in P p, ref int total, ref int last) => last = total += p.V;
                [ForEach] static void Kill(Entity e, in P p, ref int killed) { killed++; e.Destroy(); }
            }
            """), out var output, out _);
        string generated = string.Join("\n", output.SyntaxTrees.Where(t => t.FilePath.EndsWith(".ForEach.g.cs")).Select(t => t.ToString()));
        Assert.That(generated, Does.Contain("SumJob0 : global::EasyEcs.IForEachBatch<global::P>")
            .And.Contain("var l1 = x1;").And.Contain("var l2 = x2;").And.Contain("ref l1, ref l2")
            .And.Contain("x1 = l1;").And.Contain("x2 = l2;"));
        Assert.That(generated, Does.Contain("KillJob1 : global::EasyEcs.IForEachEntity<global::P>"));   // a structural method keeps the ref
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
        Assert.That(generated, Does.Match(@"struct ReadJob\d+[^\n]*INoStructuralChanges"));
        Assert.That(generated, Does.Not.Match(@"struct WriteJob\d+[^\n]*INoStructuralChanges"));
        Assert.That(generated, Does.Not.Match(@"struct GrowJob\d+[^\n]*INoStructuralChanges"));
        Assert.That(generated, Does.Match(@"struct CheckJob\d+[^\n]*INoStructuralChanges"));
        Assert.That(generated, Does.Not.Match(@"struct OffJob\d+[^\n]*INoStructuralChanges"));
        Assert.That(generated, Does.Not.Match(@"struct OnJob\d+[^\n]*INoStructuralChanges"));
        Assert.That(generated, Does.Match(@"struct FillJob\d+[^\n]*INoStructuralChanges"));                // buffer and tracked writes
        Assert.That(generated, Does.Not.Match(@"struct DropJob\d+[^\n]*INoStructuralChanges"));
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
        Assert.That(output.SyntaxTrees.Count(t => t.FilePath.Contains("Split.") && t.FilePath.EndsWith(".Component.g.cs")), Is.EqualTo(1));
        Assert.That(generated, Does.Contain("SetP(int v) => @this.Set(new global::P { V = v })"));          // from the fields
        Assert.That(generated, Does.Contain("public global::EasyEcs.Entity RemoveP()"));
        Assert.That(generated, Does.Contain("public ref readonly global::T T"));                            // tracked: read-only
        Assert.That(generated, Does.Contain("public ref global::T WriteT()"));                              // and a ref write
        Assert.That(generated, Does.Contain("public bool SetTIfChanged(int v) => @this.SetIfChanged(new global::T { V = v })")); // only a new value
        Assert.That(generated, Does.Contain("public bool SetOwnedIfChanged("));                              // indexed: no index update when equal
        Assert.That(generated, Does.Not.Contain("SetPIfChanged"));                                         // not watched: a plain write is as cheap
        Assert.That(generated, Does.Not.Contain("WriteOwned"));                                            // indexed: Set only
        Assert.That(generated, Does.Contain("public ref readonly global::Owned Owned"));                    // indexed: read-only
        Assert.That(generated, Does.Contain("ValueIndex<global::Owned, global::EasyEcs.EntityId> OwnedIndex"));
        Assert.That(generated, Does.Contain("public ref global::Clock Clock"));                             // a singleton
        Assert.That(generated, Does.Contain("public void SetClock(int ticks)"));
        Assert.That(generated, Does.Contain("public ref global::Clock GetOrAddClock()"));
        Assert.That(generated, Does.Contain("SetSpeed(float V) => @this.Set(new global::Game.Speed(V))"));  // from the constructor
        Assert.That(generated, Does.Contain("public bool IsTagged"));
        Assert.That(generated, Does.Contain("SetTagged() => @this.Set(default(global::Tagged))"));
        // An internal component's class is internal, its members public: an internal extension property is unusable
        // from a friend assembly (AFriendAssemblyUsesTheMembersOfItsLibrary).
        Assert.That(generated, Does.Contain("internal static class HiddenEcsExtensions").And.Contain("public ref global::Game.Hidden Hidden")
            .And.Not.Contain("internal ref"));
        Assert.That(generated, Does.Not.Contain("public ref global::World World").And.Contain("public ref readonly global::World World")); // Entity.World; a prefab has none
        Assert.That(generated, Does.Contain("public bool HasWorld"));
        Assert.That(generated, Does.Not.Contain("SetFixed"));
        Assert.That(generated, Does.Not.Contain("Secret"));
    }

    [Test]
    public void NestedNamesakesGetNoMembers()
    {
        // Nested structs named like another struct of their namespace: entity.DisableHealth(), QueryFilter.InHealth,
        // world.HealthIndex, entity.HasLoot... would be ambiguous (CS0121) wherever the namespace is imported.
        const string source = """
            namespace N
            {
                public static class O1 { public struct Health : IComponent, IGroup { public int V; public int Group => V; } }
                public static class O2 { public struct Health : IComponent, IIndexed<int> { public int V; public int Key => V; } }
                public static class O3 { public record struct Loot(EntityId Target) : IRelation; }
                public static class O4 { public record struct Loot(EntityId Target) : IRelation; }
                public record struct Mark(int V);                                    // a buffer's element
                public static class O5 { public struct Mark : IComponent { public int V; } }
                public static class O6 { public record struct Health(int V); }      // an element named like components
                public static class U
                {
                    static DynamicBuffer<O3.Loot> a; static DynamicBuffer<O4.Loot> b; static DynamicBuffer<Mark> c; static DynamicBuffer<O6.Health> d;
                    public static bool Use(Entity e, World w) => e.Disable<O1.Health>() && e.Has<O2.Health>() && e.HasMark
                        && w.Index<O2.Health, int>().Count(1) > 0 && QueryFilter.Create().InGroup<O1.Health>(1).Equals(default);
                }
            }
            """;
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, source), out var output, out var diagnostics);
        Assert.That(diagnostics, Is.Empty);
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()), Is.Empty);
        string generated = string.Join("\n", output.SyntaxTrees.Where(t => t.FilePath.EndsWith(".Component.g.cs") || t.FilePath.EndsWith(".Buffer.g.cs")).Select(t => t.ToString()));
        foreach (var member in new[] { "Health\n", "HasHealth", "DisableHealth", "EnableHealth", "IsHealthDisabled", "InHealth", "HealthIndex", "SetHealth",
                     "HasLoot", "LinksLoot", "AddLoot", "RemoveLoot", "LootIndex", "global::N.O5.Mark Mark", "SetMark(int" })
            Assert.That(generated, Does.Not.Contain(member), member);
        Assert.That(generated, Does.Contain("public bool HasMark"));                        // the top-level element keeps its own
    }

    [Test]
    public void ARelationNamedLikeAnEntityMemberKeepsItsOtherMembers()
    {
        // Entity.HasRelation takes HasRelation: LinksRelation is another name, still generated.
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, """
            public record struct Relation(EntityId Target) : IRelation;
            public static class U { static DynamicBuffer<Relation> r; public static bool Use(Entity e, Entity f) => e.LinksRelation(f); }
            """), out var output, out var diagnostics);
        Assert.That(diagnostics, Is.Empty);
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()), Is.Empty);
        string generated = string.Join("\n", output.SyntaxTrees.Where(t => t.FilePath.EndsWith(".Buffer.g.cs")).Select(t => t.ToString()));
        Assert.That(generated, Does.Contain("public bool LinksRelation(global::EasyEcs.EntityId target)"));
        Assert.That(generated, Does.Not.Contain("bool HasRelation").And.Not.Contain("AddRelation(global::EasyEcs.EntityId Target)"));
    }

    [Test]
    public void EasyEcsOwnComponentsHaveTheirMembers()
    {
        // Generated in EasyEcs itself: every referencing assembly has them, with no generated file of its own.
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, """
            public static class U
            {
                public static int Use(World w, Entity e, Entity parent)
                {
                    e.SetChildOf(parent);
                    int n = e.HasChildOf && e.TryGetChildOf(out var link) && link.Parent == e.ChildOf.Parent ? w.ChildOfIndex.Count(parent) : 0;
                    if (e.SetChildOfIfChanged(parent) || e.IsDestroyed) n++;
                    var recipe = new Prefab().SetChildOf(parent);
                    return recipe.HasChildOf && recipe.ChildOf.Parent == parent.Id && e.RemoveChildOf().IsAlive ? n : -1;
                }
            }
            """), out var output, out var diagnostics);
        Assert.That(diagnostics, Is.Empty);
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()), Is.Empty);
        Assert.That(output.SyntaxTrees.Where(t => t.FilePath.Contains("ChildOf") || t.FilePath.Contains("Destroyed")), Is.Empty);

        // What the world refuses is not offered: ChildOf is never disabled, Destroyed is the world's to add and remove.
        var extensions = typeof(World).Assembly.GetExportedTypes().Where(t => t.Name is "ChildOfEcsExtensions" or "DestroyedEcsExtensions"
            or "ChildOfEcsPrefabExtensions" or "DestroyedEcsPrefabExtensions").ToArray();
        Assert.That(extensions.Select(t => t.Name), Is.EquivalentTo(new[] { "ChildOfEcsExtensions", "ChildOfEcsPrefabExtensions", "DestroyedEcsExtensions" }));
        var names = extensions.SelectMany(t => t.GetMethods()).Select(m => m.Name).ToArray();
        Assert.That(names, Has.None.Contains("DisableChildOf").And.None.Contains("EnableChildOf").And.None.Contains("IsChildOfDisabled")
            .And.None.Contains("SetDestroyed").And.None.Contains("RemoveDestroyed"));
    }

    [Test]
    public void GroupsGetANamedFilter()
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, """
            public record struct Zone(int Id) : IComponent, IGroup { public int Group => Id; }
            public static class U { public static QueryFilter North => QueryFilter.Create().InZone(1); }
            """), out var output, out var diagnostics);
        Assert.That(diagnostics, Is.Empty);
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
        string generated = string.Join("\n", output.SyntaxTrees.Select(t => t.ToString()));
        Assert.That(generated, Does.Contain("public global::EasyEcs.QueryFilter InZone(int group) => @this.InGroup<global::Zone>(group);"));
    }

    [Test]
    public async Task BufferElementsGetNamedMembers()
    {
        const string source = """
            namespace Game;                                                  // members live beside the element
            public struct Q2 : IComponent, ITrackChanges { public int V; }
            public record struct Loot(int Item);
            internal struct Note { public int V; }
            public record struct Friend(EntityId Target) : IRelation;
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
                public static Prefab Chest() => new Prefab().SetLoot(new Loot(1), new Loot(2)).RemoveLoot().SetLoot(new Loot(3));
                public static int ChestSize(Prefab chest) => chest.HasLoot ? chest.Loot.Count : 0;
                public static bool Friends(Entity e) => e.HasFriend && e.Friend.Count > 0 && e.RemoveFriend().IsAlive;
                public static int Befriend(World w, Entity e, Entity f) =>
                    e.AddFriend(f).LinksFriend(f) ? w.FriendIndex.Count(f) + e.UnlinkFriend(f) : 0;
                static void Links(in DynamicBuffer<Friend> friends) { }
                static void Notes(ref DynamicBuffer<Note> notes, in DynamicBuffer<int> ints, in DynamicBuffer<P> components) { }
            }
            """;
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, source), out var output, out var diagnostics);
        Assert.That(diagnostics, Is.Empty);
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
        string generated = string.Join("\n", output.SyntaxTrees.Where(t => t.FilePath.EndsWith(".Buffer.g.cs")).Select(t => t.ToString()));
        Assert.That(generated, Does.Contain("public ref readonly global::EasyEcs.DynamicBuffer<global::Game.Loot> Loot"));
        Assert.That(generated, Does.Contain("internal static class NoteEcsBufferExtensions").And.Contain("public ref global::EasyEcs.DynamicBuffer<global::Game.Note> WriteNote()"));
        Assert.That(generated, Does.Contain("public global::EasyEcs.Entity AddFriend(global::EasyEcs.EntityId Target) => @this.AddRelation(new global::Game.Friend(Target));")
            .And.Contain("public int UnlinkFriend(global::EasyEcs.EntityId target)").And.Contain("public bool LinksFriend(global::EasyEcs.EntityId target)")
            .And.Contain("public global::EasyEcs.RelationIndex<global::Game.Friend> FriendIndex"));
        Assert.That(output.SyntaxTrees.Count(t => t.FilePath.EndsWith(".Buffer.g.cs")), Is.EqualTo(3)); // not int, not a component
        Assert.That(generated, Does.Not.Contain("WriteFriend").And.Not.Contain("int RemoveFriend(")); // links: AddRelation / UnlinkFriend
        Assert.That(generated, Does.Contain("public global::EasyEcs.Prefab SetLoot(params global::System.ReadOnlySpan<global::Game.Loot> items)")
            .And.Not.Contain("Prefab SetFriend"));                                                     // a recipe holds no links
        Assert.That(await Diagnose(Components, source), Is.Empty);                                    // no EECS012 on the members
    }

    [Test]
    public async Task QueryItemsGetNamedMembers()
    {
        const string source = """
            namespace Game { public struct P : IComponent { public int V; } }
            public static class U
            {
                public static int Sum(World w)
                {
                    int sum = 0;
                    foreach (var item in w.Query<P, Q>()) { item.P.V += item.Q.V; sum += item.P.V; }        // refs
                    foreach (var item in w.Query<Owned>()) sum += item.Owned.Owner.Index;                     // read-only
                    foreach (var item in w.Query<P, Game.P>()) sum += item.Component1.V + item.Component2.V;  // one name, two types
                    foreach (var item in w.Query<P, Q>(QueryFilter.Create().With<T>())) sum += item.Q.V;       // the same shape
                    foreach (var item in w.Read<P, Q>()) sum += item.P.V + item.Q.V;                         // read-only, stamps nothing
                    return sum;
                }
            }
            """;
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, source), out var output, out var diagnostics);
        Assert.That(diagnostics, Is.Empty);
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
        var files = output.SyntaxTrees.Where(t => t.FilePath.Contains("EasyEcs.QueryItem.")).Select(t => t.ToString()).ToArray();
        Assert.That(files, Has.Length.EqualTo(3), "one file per shape; P with Game.P has no names to give");
        string generated = string.Join("\n", files);
        Assert.That(generated, Does.Contain("public ref global::P P").And.Contain("get => ref @this.Component2;")
            .And.Contain("public ref readonly global::Owned Owned"));
        string read = files.Single(f => f.Contains("extension(global::EasyEcs.ReadItem<global::P, global::Q> @this)"));
        Assert.That(read, Does.Contain("public ref readonly global::P P").And.Not.Contain("public ref global::"));
        Assert.That(await Diagnose(Components, source), Is.Empty);
    }

    [Test]
    public void ANewQueryShapeLeavesTheOthersCached()
    {
        var first = CSharpSyntaxTree.ParseText("using EasyEcs;\npublic static class U { static void A(World w) { foreach (var i in w.Query<P>()) i.P.V++; foreach (var i in w.Query<Q>()) i.Q.V++; } }", Parse);
        var compilation = Compile(Components).AddSyntaxTrees(first);
        var driver = Driver().RunGenerators(compilation);
        driver = driver.RunGenerators(compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText("using EasyEcs;\npublic static class V { static void B(World w) { foreach (var i in w.Query<P, Q>()) i.P.V++; } }", Parse)));

        var reasons = Steps(driver)["QueryShapes"].SelectMany(s => s.Outputs).Select(o => o.Reason).ToArray();
        Assert.That(reasons.Count(r => r is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged), Is.EqualTo(2));
        Assert.That(reasons.Count(r => r == IncrementalStepRunReason.New), Is.EqualTo(1));
    }

    private const string Extra = """
        public struct Counter : IComponent { public int V; public void Bump() => V++; public readonly int Peek() => V; }
        public struct Boss : IComponent;
        public struct Loot { public int Item; }
        public static class Helpers { public static void Inc(ref int v) => v++; public static int Look(in P p) => p.V; }
        """;

    // EECS025: a write access that only reads.
    [TestCase("public class S : EcsSystem { [ForEach] static void A(ref T t, ref P p) => p.V = t.V; }", "EECS025")]
    [TestCase("public class S : EcsSystem { [ForEach] static void A(ref P p, ref Q q) => p.V = q.V; }", "EECS025")]
    [TestCase("public class S : EcsSystem { [ForEach] static void A(ref P p, ref Clock c) => p.V = c.Ticks; }", "EECS025")]
    [TestCase("public class S : EcsSystem { [ForEach] static void A(System.Span<P> p, ref Clock c) { for (int i = 0; i < p.Length; i++) c.Ticks += p[i].V; } }", "EECS025")]
    [TestCase("public class S : EcsSystem { [ForEach] static void A(ref Counter c, ref P p) => p.V = c.Peek(); }", "EECS025")]          // a readonly method
    [TestCase("public class S : EcsSystem { [ForEach] static void A(ref P p, ref Q q) => p.V = Helpers.Look(q.V == 0 ? p : default); }", "EECS025")]
    [TestCase("public static class U { public static int F(Entity e) => e.Get<T>().V; }", "EECS025")]
    [TestCase("public static class U { public static int F(Entity e) => e.WriteT().V; }", "EECS025")]
    // EECS026: a parameter the method does not use.
    [TestCase("public class S : EcsSystem { [ForEach] static void A(in P p, ref Clock c) => c.Ticks++; }", "EECS026")]
    [TestCase("public class S : EcsSystem { [ForEach] static void A(Entity e, ref P p) => p.V++; }", "EECS026")]
    [TestCase("public class S : EcsSystem { [ForEach] static void A(ref P p, World w) => p.V++; }", "EECS026")]
    [TestCase("public class S : EcsSystem { [ForEach] static void A(ref P p, ref Clock c) => p.V++; }", "EECS026")]
    [TestCase("public class S : EcsSystem { [ForEach] static void A(ref P p, ref T t) => p.V++; }", "EECS026")]                       // stamps: no fix
    [TestCase("public class S : EcsSystem { [ForEach, Changed<T>] static void A(in T t, ref P p) => p.V++; }", "EECS026")]
    // EECS027: a lookup per row.
    [TestCase("public class S : EcsSystem { [ForEach] static void A(Entity e, ref P p) => p.V = e.Q.V; }", "EECS027")]
    [TestCase("public class S : EcsSystem { [ForEach] static void A(Entity e, ref P p) { if (e.HasQ) p.V++; } }", "EECS027")]
    [TestCase("public class S : EcsSystem { [ForEach] void A(ref P p) => p.V = World.Clock.Ticks; }", "EECS027")]
    [TestCase("public class S : EcsSystem { [ForEach] static void A(ref P p, World w) => p.V = w.HasClock ? 1 : 0; }", "EECS027")]
    // EECS028: allocations in code that runs every update.
    [TestCase("public class S : EcsSystem { [ForEach] static void A(ref P p) { var l = new System.Collections.Generic.List<int>(); p.V = l.Count; } }", "EECS028")]
    [TestCase("public class S : EcsSystem { [ForEach] static void A(ref P p, in Q q) => p.V = new[] { q.V }.Length; }", "EECS028")]
    [TestCase("public class S : EcsSystem { [ForEach] static void A(ref P p, in Q q) { int v = q.V; System.Func<int> f = () => v; p.V = f(); } }", "EECS028")]
    [TestCase("public class S : EcsSystem { [ForEach] static void A(ref P p, in Q q) => p.V = $\"{q.V}\".Length; }", "EECS028")]
    [TestCase("public class S : EcsSystem { [ForEach] static void A(ref P p, in Q q) => p.V = q.V.ToString().Length; }", "EECS028")]
    [TestCase("public class S : EcsSystem { [ForEach] static void A(ref P p, in Q q) { object o = q.V; p.V = o.GetHashCode(); } }", "EECS028")]
    [TestCase("public class S : EcsSystem { [ForEach] static void A(ref P p, in Q q) => p.V = System.Linq.Enumerable.Count(System.Linq.Enumerable.Range(0, q.V)); }", "EECS028")]
    [TestCase("public class S : EcsSystem { protected override void OnUpdate() => World.Clock.Ticks += new System.Text.StringBuilder().Length; }", "EECS028")]
    // EECS029: an instance [ForEach] method that uses nothing of its system.
    [TestCase("public class S : EcsSystem { [ForEach] void A(ref P p) => p.V++; }", "EECS029")]
    // EECS030: a structural change entity by entity over a query.
    [TestCase("public static class U { public static void F(World w) { foreach (var item in w.Query<P>()) item.Entity.Destroy(); } }", "EECS030")]
    [TestCase("public static class U { public static void F(World w) { foreach (var item in w.Query<P>()) item.Entity.Set(new Q { V = 1 }); } }", "EECS030")]
    [TestCase("public static class U { public static void F(World w) { foreach (var item in w.Query<P, Q>()) item.Entity.RemoveQ(); } }", "EECS030")]
    [TestCase("public static class U { public static void F(World w) { foreach (var item in w.Query<P>()) { item.Entity.DisableP(); } } }", "EECS030")]
    // EECS031: entities created one by one in a counted loop.
    [TestCase("public static class U { public static void F(World w, int n) { for (int i = 0; i < n; i++) w.CreateEntity(new P { V = i }); } }", "EECS031")]
    [TestCase("public static class U { public static void F(World w, Entity[] all) { for (int i = 0; i < all.Length; i++) all[i] = w.CreateEntity(new P()); } }", "EECS031")]
    [TestCase("public static class U { public static void F(World w) { for (int i = 0; i < 10_000; i++) w.CreateEntity(new P()); } }", "EECS031")]
    [TestCase("public static class U { public static void F(World w) { var all = new Entity[10_000]; for (int i = 0; i < all.Length; i++) all[i] = w.CreateEntity(new P { V = i }); } }", "EECS031")]
    [TestCase("public static class U { public static void F(World w, Entity[] more) { var all = new Entity[64]; all = more; for (int i = 0; i < all.Length; i++) all[i] = w.CreateEntity(new P { V = i }); } }", "EECS031")]
    [TestCase("public static class U { public static void F(World w, Prefab p, Entity[] all) { for (int i = 0; i < all.Length; i++) all[i] = w.Instantiate(p, new P()); } }", "EECS031")]
    [TestCase("public static class U { public static void F(World w, Prefab p, int n) { for (int i = 0; i < n; i++) w.Instantiate(p, new P { V = i }); } }")] // no table to reserve
    [TestCase("public static class U { public static void F(World w, Prefab p, Entity[] all, int n) { for (int i = 0; i < n; i++) w.Instantiate(p, all, new P()); } }")] // batches
    // EECS032: query work the world does in one call.
    [TestCase("public static class U { public static bool F(World w) => w.Count<P>() > 0; }", "EECS032")]
    [TestCase("public static class U { public static bool F(World w) => 0 == w.Count<P>(); }", "EECS032")]
    [TestCase("public static class U { public static int F(World w) { int n = 0; foreach (var item in w.Query<P>()) n++; return n; } }", "EECS032")]
    [TestCase("public static class U { public static int F(World w) { int n = 0; foreach (var item in w.Query<P>()) { if (!item.Entity.Has<Q>()) continue; n += item.Component1.V; } return n; } }", "EECS032", "EECS033")]
    // EECS033: the generated member.
    [TestCase("public static class U { public static void F(Entity e) => e.Get<P>().V = 1; }", "EECS033")]
    [TestCase("public static class U { public static bool F(Entity e) => e.Has<P>(); }", "EECS033")]
    [TestCase("public static class U { public static bool F(Entity e) => e.Has<Boss>(); }", "EECS033")]
    [TestCase("public static class U { public static void F(Entity e) => e.Set(new Boss()); }", "EECS033")]
    [TestCase("public static class U { public static void F(Entity e) => e.Remove<P>(); }", "EECS033")]
    [TestCase("public static class U { public static int F(World w) => w.GetSingleton<Clock>().Ticks; }", "EECS033")]
    [TestCase("public static class U { public static int F(World w, Entity e) => w.Index<Owned, EntityId>().Count(e); }", "EECS033")]
    [TestCase("public static class U { public static EntityId F(Entity e) => e.Read<ChildOf>().Parent; }", "EECS033")]          // EasyEcs's ChildOf has its members
    [TestCase("public static class U { public static bool F(Entity e) => e.Has<Destroyed>(); }", "EECS033")]
    [TestCase("public static class U { public static int F(World w) { int n = 0; foreach (var item in w.Query<P>()) n += item.Component1.V; return n; } }", "EECS033")]
    [TestCase("public static class U { public static void F(Entity e) => e.GetOrAdd<DynamicBuffer<Loot>>().Add(new Loot()); static DynamicBuffer<Loot> named; }", "EECS033")]
    // EECS034: a world update from a system.
    [TestCase("public class S : EcsSystem { protected override void OnUpdate() => World.Update(); }", "EECS034")]
    [TestCase("public class S : EcsSystem { [ForEach] static void A(Entity e, in P p) { if (p.V == 0) e.World.Update(); } }", "EECS034")]
    public async Task CostsAreReported(string source, params string[] ids)
    {
        var costs = await Costs(Components, Extra, source);
        Assert.That(costs.Select(d => d.Id).Distinct().OrderBy(x => x), Is.EqualTo(ids),
            string.Join("\n", costs.Select(d => d.GetMessage())));
        Assert.That((await Diagnose(Components, Extra, source)).Where(e => e.StartsWith("CS")), Is.Empty, "the case compiles");
    }

    // A warning costs, a suggestion (Info) is the same call written shorter, an error is code that cannot work (D29).
    [TestCase("EECS025", DiagnosticSeverity.Warning)]
    [TestCase("EECS026", DiagnosticSeverity.Warning)]
    [TestCase("EECS027", DiagnosticSeverity.Warning)]
    [TestCase("EECS028", DiagnosticSeverity.Warning)]
    [TestCase("EECS029", DiagnosticSeverity.Warning)]
    [TestCase("EECS030", DiagnosticSeverity.Warning)]
    [TestCase("EECS031", DiagnosticSeverity.Warning)]
    [TestCase("EECS032", DiagnosticSeverity.Warning)]
    [TestCase("EECS033", DiagnosticSeverity.Info)]
    [TestCase("EECS034", DiagnosticSeverity.Error)]
    public void SeveritySaysWhatARuleCosts(string id, DiagnosticSeverity severity) =>
        Assert.That(new EasyEcsAnalyzer().SupportedDiagnostics.Single(d => d.Id == id).DefaultSeverity, Is.EqualTo(severity));

    // The tricky "written" cases: every form of a write keeps the write access.
    [TestCase("public class S : EcsSystem { [ForEach] static void A(ref P p) => p.V = 1; }")]
    [TestCase("public class S : EcsSystem { [ForEach] static void A(ref P p) => p.V++; }")]
    [TestCase("public class S : EcsSystem { [ForEach] static void A(ref P p) => p.V += 2; }")]
    [TestCase("public class S : EcsSystem { [ForEach] static void A(ref P p) => p = default; }")]
    [TestCase("public class S : EcsSystem { [ForEach] static void A(ref P p) => Helpers.Inc(ref p.V); }")]
    [TestCase("public class S : EcsSystem { [ForEach] static void A(ref Counter c) => c.Bump(); }")]                                    // a mutating method
    [TestCase("public class S : EcsSystem { [ForEach] static void A(ref P p) { ref var x = ref p; x.V = 2; } }")]                         // a ref local
    [TestCase("public class S : EcsSystem { [ForEach] static void A(ref P p) { ref int v = ref p.V; v = 3; } }")]
    [TestCase("public class S : EcsSystem { [ForEach] static void A(ref DynamicBuffer<int> b) => b.Add(1); }")]                         // a 'this ref' extension
    [TestCase("public class S : EcsSystem { [ForEach] static void A(System.Span<P> p) => p.Fill(default); }")]                            // a Span method
    [TestCase("public class S : EcsSystem { [ForEach] static void A(System.Span<P> p) { foreach (ref var x in p) x.V++; } }")]
    [TestCase("public class S : EcsSystem { [ForEach] static void A(System.Span<P> p) => p[0].V = 1; }")]
    [TestCase("public class S : EcsSystem { [ForEach] static void A(ref P p) => (p.V, _) = (1, 2); }")]                                // a deconstruction
    [TestCase("public static class U { public static void F(Entity e) { ref var t = ref e.Get<T>(); t.V++; } }")]
    [TestCase("public static class U { public static void F(Entity e) => e.Get<T>().V = 1; }")]
    [TestCase("public static class U { public static void F(Entity e) => e.WriteT().V = 1; }")]
    public async Task WritesKeepTheWriteAccess(string source)
    {
        var costs = await Costs(Components, Extra, source);
        Assert.That(costs.Select(d => d.Id), Has.None.EqualTo("EECS025").And.None.EqualTo("EECS026"), string.Join("\n", costs.Select(d => d.GetMessage())));
    }

    // Not reported: used, filters, branches, small or reserved creation, and the code each rule leaves alone.
    [TestCase("public class S : EcsSystem { [ForEach] static void A(in P p, ref Clock c) { } }")]                                           // being written
    [TestCase("public class S : EcsSystem { [ForEach, With<P>] static void A(ref Clock c) => c.Ticks++; }")]
    [TestCase("public class S : EcsSystem { [ForEach] static void A(Entity e, ref P p) { if (p.V > 0) p.V = e.Q.V; } }")]       // some rows only
    [TestCase("public class S : EcsSystem { [ForEach] static void A(Entity e, ref P p) { if (p.V > 0) return; p.V = e.Q.V; } }")]
    [TestCase("public class S : EcsSystem { [ForEach] static void A(Entity e, ref P p) { if (e.HasQ) e.RemoveQ(); p.V++; } }")]    // changed here
    [TestCase("public class S : EcsSystem { [ForEach] static void A(Entity e, ref P p) => p.V = e.Owned.Owner.Index; }")]               // indexed
    [TestCase("public class S : EcsSystem { [ForEach] static void A(ref P p, in Q q) { if (q.V < 0) throw new System.InvalidOperationException($\"{q.V}\"); p.V = q.V; } }")]
    [TestCase("public class S : EcsSystem { [ForEach] static void A(ref P p, in Q q) { System.Diagnostics.Debug.Assert(q.V >= 0, $\"{q.V}\"); p.V = q.V; } }")]
    [TestCase("public class S : EcsSystem { [ForEach] static void A(in P p, in Q q) => System.Console.WriteLine($\"{p.V} {q.V}\"); }")]
    [TestCase("public class S : EcsSystem { [ForEach] static void A(ref P p, in Q q) { System.Func<int, int> f = static x => x + 1; p.V = f(q.V); } }")]
    [TestCase("public class S : EcsSystem { public override int UpdateInterval => 20; protected override void OnUpdate() => World.Clock.Ticks += new int[4].Length; }")]
    [TestCase("public class S : EcsSystem { [ForEach] void A(ref P p) => p.V = World.UpdateCount > 0 ? 1 : 0; }")]                      // uses its system
    [TestCase("public static class U { public static void F(World w) { foreach (var item in w.Query<P>()) item.Entity.Set(new P { V = 1 }); } }")]   // overwrites
    [TestCase("public static class U { public static void F(World w) { foreach (var item in w.Query<P>()) item.Entity.Set(new Q { V = item.Entity.Id.Index }); } }")]
    [TestCase("public static class U { public static void F(World w) { for (int i = 0; i < 100; i++) w.CreateEntity(new P()); } }")]
    [TestCase("public static class U { public static void F(World w) { var all = new Entity[100]; for (int i = 0; i < all.Length; i++) all[i] = w.CreateEntity(new P { V = i }); } }")]
    [TestCase("public static class U { public static void F(World w, int n) { for (int i = 0; i < n; i++) if (w.Count<P>() < i) w.CreateEntity(new P { V = i }); } }")]   // now and then
    [TestCase("public static class U { public static void F(World w, int n) { for (int r = 0; r < n; r++) for (int i = 0; i < 8; i++) w.CreateEntity(new P { V = i }); } }")]   // the inner loop's
    [TestCase("public static class U { public static void F(World w, int n) { w.Reserve<P>(n); for (int i = 0; i < n; i++) w.CreateEntity(new P { V = i }); } }")]
    [TestCase("public class S : EcsSystem { protected override void OnUpdate() { for (int i = 0; i < World.Clock.Ticks; i++) World.CreateEntity(new P()); } }")]
    [TestCase("public static class U { public static bool F(World w) => w.Count<P>() > 2; }")]
    [TestCase("public static class U { public static void F(Entity e) => e.GetOrAdd<DynamicBuffer<Loot>>().Add(new Loot()); }")]          // the only name of the buffer
    [TestCase("public partial class S : EcsSystem { [ForEach] static void A(ref P p) { } }")]
    [TestCase("public static class U { public static EntityId F(World w, Entity e) { foreach (var item in w.Query<P, ChildOf>()) _ = item.ChildOf; return e.ChildOf.Parent; } }")]   // EasyEcs's own component, by its members
    public async Task CheapCodeIsNotReported(string source)
    {
        var costs = await Costs(Components, Extra, source);
        Assert.That(costs.Select(d => d.Id), Is.Empty, string.Join("\n", costs.Select(d => d.GetMessage())));
    }

    // Mut<T> parameters (EECS025 never written, EECS039 untracked), the cold calls of EECS028, and the lookups and
    // allocations of a hand-written job's Execute (EECS027, EECS028).
    [TestCase("public class S : EcsSystem { [ForEach] static void A(Mut<T> t, ref P p) => p.V = t.Value.V; }", "EECS025")]
    [TestCase("public class S : EcsSystem { [ForEach] static void A(Mut<P> p, in Q q) { if (q.V > 0) p.Write().V = q.V; } }", "EECS039")]
    [TestCase("public class S : EcsSystem { [ForEach] static void A(Mut<P> p, in Q q) { if (q.V > p.Value.V) p.Write() = new P { V = q.V }; } }", "EECS039")]
    [TestCase("public class S : EcsSystem { protected override void OnUpdate() => World.TrimExcess(); }", "EECS028")]
    [TestCase("public class S : EcsSystem { protected override void OnUpdate() { if (World.Describe().Length > 9) World.Clock.Ticks++; } }", "EECS028")]
    [TestCase("public class S : EcsSystem { protected override void OnUpdate() { if (World.DescribeSchedule().Groups.Length > 9) World.Clock.Ticks++; } }", "EECS028")]
    [TestCase("public class S : EcsSystem { protected override void OnUpdate() { if (World.Capture() == null) World.Clock.Ticks++; } }", "EECS028")]
    [TestCase("public class S : EcsSystem { protected override void OnUpdate() => World.Instantiate(new Prefab()); }", "EECS028")]
    [TestCase("public class S : EcsSystem { [ForEach] static void A(Entity e, ref P p) => p.V = e.Describe().Length; }", "EECS028")]
    [TestCase("public struct J : IForEachEntity<P> { public void Execute(Entity e, ref P p) => p.V = e.Read<Q>().V; }", "EECS027", "EECS033")]
    [TestCase("public struct J : IForEach<P> { public void Execute(ref P p) { var all = new System.Collections.Generic.List<int>(); p.V = all.Count; } }", "EECS028")]
    // EECS046: a query that only reads tracked components.
    [TestCase("public static class U { public static int F(World w) { int n = 0; foreach (var item in w.Query<T, P>()) n += item.T.V + item.P.V; return n; } }", "EECS046")]
    [TestCase("public static class U { public static int F(World w) { int n = 0; foreach (var item in w.Query<DynamicBuffer<P>>()) n += item.Component1.Count; return n; } }", "EECS046")]
    [TestCase("public static class U { public static void F(World w, System.Collections.Generic.List<Entity> all) { foreach (var item in w.Query<T>()) all.Add(item.Entity); } }", "EECS046")]
    public async Task UsageCostsAreReported(string source, params string[] ids)
    {
        var costs = await Costs(Components, Extra, source);
        Assert.That(costs.Select(d => d.Id).Distinct().OrderBy(x => x), Is.EqualTo(ids),
            string.Join("\n", costs.Select(d => d.GetMessage())));
        Assert.That((await Diagnose(Components, Extra, source)).Where(e => e.StartsWith("CS")), Is.Empty, "the case compiles");
    }

    [TestCase("public class S : EcsSystem { [ForEach] static void A(Mut<T> t, in P p) { if (p.V > 0) t.Write().V = p.V; } }")]       // written
    [TestCase("public class S : EcsSystem { [ForEach] static void A(Mut<T> t, in P p) => t.SetIfChanged(new T { V = p.V }); }")]
    [TestCase("public class S : EcsSystem { [ForEach] static void A(Mut<P> p, in Q q) => p.SetIfChanged(new P { V = q.V }); }")]       // a comparison: not a plain ref
    [TestCase("public class S : EcsSystem { [ForEach] static void A(Mut<T> t, in P p) { if (p.V > 0) Helpers.Inc(ref t.Write().V); } }")]
    [TestCase("public class S : EcsSystem { public override int UpdateInterval => 1200; protected override void OnUpdate() => World.TrimExcess(); }")] // its cadence
    [TestCase("public struct J : IForEach<P> { public void Execute(ref P p) => p.V++; }")]
    [TestCase("public static class U { public static void F(World w) { foreach (var item in w.Query<T>()) item.T.V++; } }")]                      // a write
    [TestCase("public static class U { public static void F(World w) { foreach (var item in w.Query<T, P>()) { ref var t = ref item.T; t.V = item.P.V; } } }")]
    [TestCase("public static class U { public static void F(World w) { foreach (var item in w.Query<T, P>()) item.P.V = item.T.V; } }")]           // writes P: a Query
    [TestCase("public static class U { public static int F(World w) { int n = 0; foreach (var item in w.Query<P>()) n += item.P.V; return n; } }")]  // untracked
    [TestCase("public static class U { public static int F(World w) { int n = 0; foreach (var item in w.Read<T>()) n += item.T.V; return n; } }")]   // already Read
    public async Task UsageCheapCodeIsNotReported(string source)
    {
        var costs = await Costs(Components, Extra, source);
        Assert.That(costs.Select(d => d.Id), Is.Empty, string.Join("\n", costs.Select(d => d.GetMessage())));
    }

    [TestCase("EECS036", DiagnosticSeverity.Error)]
    [TestCase("EECS037", DiagnosticSeverity.Warning)]
    [TestCase("EECS038", DiagnosticSeverity.Warning)]
    [TestCase("EECS039", DiagnosticSeverity.Info)]
    [TestCase("EECS046", DiagnosticSeverity.Warning)]
    [TestCase("EECS040", DiagnosticSeverity.Error)]
    [TestCase("EECS041", DiagnosticSeverity.Error)]
    [TestCase("EECS042", DiagnosticSeverity.Error)]
    [TestCase("EECS043", DiagnosticSeverity.Error)]
    [TestCase("EECS044", DiagnosticSeverity.Error)]
    [TestCase("EECS045", DiagnosticSeverity.Error)]
    public void UsageRulesHaveTheirSeverity(string id, DiagnosticSeverity severity) =>
        Assert.That(new EasyEcsAnalyzer().SupportedDiagnostics.Single(d => d.Id == id).DefaultSeverity, Is.EqualTo(severity));

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
