using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using EasyEcs.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using NUnit.Framework;
using static EasyEcs.UnitTest.AnalyzerTests;

namespace EasyEcs.UnitTest;

/// <summary>Drives SystemGenerator and EasyEcsAnalyzer through Roslyn: output, diagnostics and incremental caching.</summary>
public class GeneratorTests
{
    /// <summary>A file of generated [ForEach] code: a system's (<c>.System.g.cs</c>) or another type's (<c>.ForEach.g.cs</c>).</summary>
    internal static bool IsCode(SyntaxTree tree) => tree.FilePath.EndsWith(".System.g.cs") || tree.FilePath.EndsWith(".ForEach.g.cs");

    // The interceptors of lambda passes are taken from EasyEcs.Generated, as the package's build files set it (EasyEcs.props).
    internal static readonly CSharpParseOptions Parse = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview)
        .WithFeatures([new("InterceptorsNamespaces", "EasyEcs.Generated")]);

    internal static CSharpCompilation Compile(params string[] sources) => CSharpCompilation.Create("Test",
        sources.Select(s => CSharpSyntaxTree.ParseText("using EasyEcs;\n" + s, Parse)),
        Basic.References.Append(MetadataReference.CreateFromFile(typeof(World).Assembly.Location)),
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

    internal static GeneratorDriver Driver() => CSharpGeneratorDriver.Create(
        [new SystemGenerator().AsSourceGenerator(), new ComponentGenerator().AsSourceGenerator(), new PartGenerator().AsSourceGenerator(),
            new ViewGenerator().AsSourceGenerator(), new PrefabGenerator().AsSourceGenerator()], parseOptions: Parse,
        driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

    internal static async Task<ImmutableArray<Diagnostic>> Analyze(string[] sources)
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(sources), out var output, out _);
        return await output.WithAnalyzers(EasyEcsAnalyzer.All).GetAllDiagnosticsAsync();
    }

    internal const string Components = """
        public struct P : IComponent { public int V; }
        public struct Q : IComponent { public int V; }
        public struct T : IComponent, ITracked { public int V; }
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
                [UpdateInterval(4, Phase = 1)]
                [EcsSystem] public ref struct S
                {
                    [ForEach, With<Q>] static void A(ref P p) => p.V++;
                    private static readonly System.Collections.Generic.List<int> _scratch = []; // readonly: allowed
                    public static int Speed => 3;                                       // computed: allowed
                    [ForEach, Without<Q>] static void B(Entity e, in P p, World w) => _scratch.Clear();
                    [ForEach] static void C(in P p, ref Clock clock, in Clock read) => clock.Ticks += read.Ticks; // singletons
                    [ForEach] static void D(System.Span<P> p, System.ReadOnlySpan<Q> q, ref Clock clock) => clock.Ticks += p.Length; // batch
                    [ForEach] static void E(Entity e, in P p, ref Clock clock) { if (e.Has<Q>()) clock.Ticks += e.Id.Index; } // read-only entity
                    [ForEach] static void F(ref Holder h, in Q q, ref Grid g) { g.Cells.Add(q.V); h.Box.V++; } // written by ref
                    [ForEach] static void G(in Holder h, ref P p, in Grid g) => p.V = g.Cells.Contains(1) ? g.Cells[0] : h.Box.V; // only read
                    [ForEach, Changed<DynamicBuffer<int>>] static void H(ref DynamicBuffer<int> items, in DynamicBuffer<long> read) => items.Add(read.Count); // buffers
                    [ForEach] static void I(Entity e, in DynamicBuffer<long> read) { ref var items = ref e.Get<DynamicBuffer<int>>(); items.Clear(); foreach (var x in read) items.Add((int)x); }
                    [ForEach, With<Q>] static void J(Entity e, World w) { e.Get<DynamicBuffer<int>>() = default; w.CreateEntity(new P(), new DynamicBuffer<int>()); }
                    [ForEach] static void K(Entity e, in Handle link) { e.Remove<Q>(); link.Target.Destroy(); } // changes structure: runs alone
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
            [EcsSystem] public ref struct Reparent
            {
                [ForEach] static void B(Entity e, in Owned o) => e.Set(new Owned { Owner = o.Owner });
                [ForEach] static void C(ref P p, ReadOnlyMaybe<Owned> o) { if (o.HasValue) p.V = o.Value.Owner.Index; } // reads an optional indexed component
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
    [TestCase("[ForEach] static void A(Entity e, ref P p) => p.V = e.Has<Q>() ? 1 : 0;", "access.RunAlone();")]           // reads another table
    [TestCase("[ForEach] static void A(Entity e, ref P p) => p.V = e.Equals(default) ? 1 : 0;", "access.RunAlone();")]     // a call on it
    [TestCase("[ForEach] static void A(Entity e, ref P p) => p.V = Look(e); static int Look(Entity e) => 0;", "access.RunAlone();")] // the handle passed on
    [TestCase("[ForEach] static void A(Entity e, ref P p) => p.V = e.World.EntityCount;", "access.RunAlone();")]
    [TestCase("[ForEach] static void A(System.Span<P> p, System.ReadOnlySpan<Q> q) { }", "access.Write<global::P>();", "access.Read<global::Q>();", "IForEachBatch<global::P, global::Q>")]
    [TestCase("[ForEach, Changed<DynamicBuffer<int>>] static void A(in DynamicBuffer<int> b, ref P p) { }", ".Changed<global::EasyEcs.DynamicBuffer<int>>(0)", "access.Read<global::EasyEcs.DynamicBuffer<int>>();")]
    [TestCase("[ForEach, Changed<T>] static void A(ref P p) { }", "access.Read<global::T>();", ".Changed<global::T>(0)", "Since(system.LastRunTick + 1)", "WriteMask => 1;")]
    [TestCase("[ForEach] static void A(ref P p, Maybe<Q> q) { if (q.HasValue) p.V = q.Value.V; }", "access.Write<global::Q>();", "IForEachBatch<global::P>", "new global::EasyEcs.MaybeColumn<global::Q>(in entities, false)", "ref global::System.Runtime.CompilerServices.Unsafe.Add(")]
    [TestCase("[ForEach] static void A(ref P p, Maybe<Q> q) { if (q.HasValue) q.Value.V++; }", "access.Write<global::Q>();", "MaybeColumn<global::Q>(in entities, false)")]  // untracked: no stamp
    [TestCase("[ForEach] static void A(ref P p, ReadOnlyMaybe<Q> q) { if (q.HasValue) p.V = q.Value.V; }", "access.Read<global::Q>();", "MaybeColumn<global::Q>(in entities, false)")]
    [TestCase("[ForEach] static void A(ref P p, Maybe<T> t) { if (t.HasValue) t.Value = default; }", "access.Write<global::T>();", "MaybeColumn<global::T>(in entities, true)")]  // tracked: stamped
    [TestCase("[ForEach] static void A(Mut<T> t, in P p) => t.SetIfChanged(new T { V = p.V });", "access.Write<global::T>();", "access.Read<global::P>();",
        "IForEachBatch<global::T, global::P>", "new global::EasyEcs.Mut<global::T>(ref global::System.Runtime.CompilerServices.Unsafe.Add(ref r1, i), in u0, (int)i)", "var u0 = global::EasyEcs.MutColumn.Of<global::T>(in entities);",
        "WriteMask => 1;", "SelfStamped => 1;")]                                                                          // stamped by row
    [TestCase("[ForEach] static void A(Entity e, Mut<T> t) { t.Write().V = 1; e.Destroy(); }", "IForEachEntity<global::T>",
        "new global::EasyEcs.Mut<global::T>(ref c1, entity)", "SelfStamped => 1;", "access.RunAlone();")]                 // moves: by entity
    [TestCase("[ForEach] static void A(in P p, Mut<Q> q) { if (p.V > 0) q.Write().V = p.V; }", "access.Write<global::Q>();", "WriteMask => 2;", "SelfStamped => 2;")]  // untracked: a plain ref
    [TestCase("[ForEach] static void A(Mut<T> t, Maybe<Q> q) { if (q.HasValue) t.Write().V = q.Value.V; }", "IForEachBatch<global::T>",
        "new global::EasyEcs.Mut<global::T>(ref global::System.Runtime.CompilerServices.Unsafe.Add(ref r1, i), in u0, (int)i)", "var u0 = global::EasyEcs.MutColumn.Of<global::T>(in entities);", "SelfStamped => 1;")]
    [TestCase("[ForEach(Parallel = true)] static void A(Mut<T> t) => t.SetIfChanged(default);", "ForEachEntityParallel<global::T,", "SelfStamped => 1;")]
    [TestCase("[ForEach, ChangedAny<T, T2>, ChangedAny<T2, T>] static void A(ref P p) { } public struct T2 : IComponent, ITracked { public int V; }",
        ".ChangedAny<global::T, global::S.T2>(0)", "access.Read<global::T>();", "access.Read<global::S.T2>();", "Since(system.LastRunTick + 1)")]
    [TestCase("[ForEach, RemovedAny<T, T>] static void A(ref P p) { }", ".RemovedAny<global::T, global::T>(0)", "access.Read<global::T>();", "Since(system.LastRunTick + 1)")]
    [TestCase("[ForEach] static void A(in P p, World world) { }", "access.RunAlone();")]                        // reaches the world
    [TestCase("[ForEach] static void A(in P p) { } static void OnUpdate() { }", "access.RunAlone();")]       // an OnUpdate method
    [TestCase("[ForEach] static void A(ref P p) { }", "internal static readonly global::EasyEcs.QueryFilter AFilter0 = default;",
        "world.Count<global::P>(in __ForEach.AFilter0)", "(ref job0, in __ForEach.AFilter0);")]                          // no filter: no zeroed temporary
    [TestCase("[ForEach(Slices = 3)] static void A(ref P p) { }", "access.Write<global::P>();", "(ref job0, __ForEach.AFilter0.Slice((int)(system.RunCount % 3), 3));")]
    [TestCase("[ForEach(Slices = 2, Parallel = true), Without<Q>] static void A(ref P p) { }", "ForEachBatchParallel<", "AFilter0.Slice((int)(system.RunCount % 2), 2)")]
    [TestCase("[ForEach(Parallel = true, ChunkRows = 100)] static void A(ref P p) { }", "ForEachBatchParallel<", "(ref job0, in __ForEach.AFilter0, chunkRows: 100);")]
    [TestCase("[ForEach(Slices = 4)] static void A(System.Span<P> p) { }", "ForEachBatch<", "Slice((int)(system.RunCount % 4), 4)")]
    public void AccessIsDeclaredFromParameters(string members, params string[] expected)
        => AccessOf("", members, expected);

    // A tile job keeps the neighbors in a field: a local of the inlined Execute would be zeroed on every row.
    [TestCase("public record struct G(int Pos) : IComponent, ITile<int> { int IIndexed<int>.Key => Pos; static int ITileShape.Width => 4; static int ITileShape.Depth => 4; static int ITile<int>.Beside(int k, TileFaces f) => k + 1; }",
        "[ForEach] static void A(Tile<byte, G> t, ref P p) => p.V = t.Cells.Length;",
        "public global::EasyEcs.TileNeighbors n0;", "(k0, entity, out n0);", "new global::EasyEcs.Tile<byte, global::G>(entity, in n0)")]
    public void AccessIsDeclaredFromParametersBeside(string declarations, string members, params string[] expected)
        => AccessOf(declarations, members, expected);

    private void AccessOf(string declarations, string members, string[] expected)
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, $"{declarations} [EcsSystem] public ref struct S {{ {members} }}"),
            out var output, out _);
        string generated = output.SyntaxTrees.Single(t => IsCode(t) && t.ToString().Contains("struct __S_SystemCode")).ToString();
        foreach (var line in expected) Assert.That(generated, Does.Contain(line), generated);
        if (!expected.Contains("access.RunAlone();")) Assert.That(generated, Does.Not.Contain("RunAlone"));
    }

    private const string Lanes = """
        public struct F : IComponent { public float V; }
        public struct H : IComponent { public float V; }
        public struct Two : IComponent { public float A, B; }
        public record struct Duo(float A, float B) : IComponent;
        public struct Five : IComponent { public float A, B, C, D, E; }
        public struct Mixed : IComponent { public float A; public int B; }
        public struct D : IComponent { public double V; }
        public struct L64 : IComponent { public long V; }
        public struct B8 : IComponent { public byte V; }
        public struct C8 : IComponent { public byte V; }
        public struct S8 : IComponent { public sbyte V; }
        public struct Px : IComponent { public byte R, G; }
        public record struct Pos(System.Numerics.Vector2 Value) : IComponent;
        public record struct Vel(System.Numerics.Vector2 Value) : IComponent;
        public struct Odd : IComponent { public float X, Y; public Odd(float x, float y) { X = x; Y = x + y; } }
        public partial struct Split : IComponent { public float A; }
        public partial struct Split { public float B; }
        public partial struct Keyed : IComponent { public float V; }      // a second part without fields keeps the loop
        public partial struct Keyed { public override string ToString() => V.ToString(); }
        public struct Unit : IComponent { public float Speed; public bool Alive; public byte Team; public int Hp; }
        public struct Sixteen : IComponent { public float F0, F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12, F13, F14, F15; }
        public record struct G(float Pull) : ISingletonComponent;
        public struct Purse : ISingletonComponent { public long Sum; }
        public struct Count : ISingletonComponent, IReduction<Count> { public int N, Low; public static Count Identity => new() { Low = int.MaxValue }; public static Count Combine(Count a, Count b) => new() { N = a.N + b.N, Low = System.Math.Min(a.Low, b.Low) }; }
        public struct Total : ISingletonComponent, IReduction<Total> { public float V; public static Total Identity => default; public static Total Combine(Total a, Total b) => new() { V = a.V + b.V }; }
        public struct Ends : ISingletonComponent, IReduction<Ends> { public int First, Last; public static Ends Identity => default; public static Ends Combine(Ends a, Ends b) => new() { First = a.First, Last = b.Last }; }
        public struct Wide : ISingletonComponent, IReduction<Wide> { public int N; public long L; public static Wide Identity => default; public static Wide Combine(Wide a, Wide b) => new() { N = a.N + b.N, L = a.L + b.L }; }
        """;

    // Kernel.cs: a method on one lane type with vector counterparts for its operations gets a Vector<T> loop.
    [TestCase("[ForEach] static void A(ref F f, in H h) => f.V += h.V * 2;", true)]
    [TestCase("[ForEach] static void A(ref F f, in H h) { if (h.V > 0) return; f.V = h.V > 1 ? 1 : System.MathF.Sqrt(h.V); }", true)]
    [TestCase("[ForEach] static void A(ref F f, in G g) => f.V -= g.Pull;", true)]
    [TestCase("[ForEach] static void A(ref Keyed k) => k.V++;", true)]                                  // parts without fields                     // a singleton: uniform
    [TestCase("[ForEach] static void A(ref P p) => p.V = (p.V << 2) | 1;", true)]
    [TestCase("[ForEach] static void A(ref F f, in P p) => f.V = p.V;", true)]                         // int to float: one size
    [TestCase("[ForEach] static void A(ref F f, in D d) => f.V = (float)d.V;", true)]                  // a pair of double vectors, narrowed
    [TestCase("[ForEach] static void A(in F f, ref D d) { if (d.V > 1) d.V += f.V; }", true)]           // float to double, compared in halves
    [TestCase("[ForEach] static void A(ref P p, ref L64 l) { long x = (long)p.V * p.V + l.V; l.V = x; p.V = (int)(x >> 3); }", true)] // a long local
    [TestCase("[ForEach] static void A(in P p, ref Reduce<Wide> w) { w.Value.N += p.V; w.Value.L += p.V; }", true)] // an int and a long sum
    [TestCase("[ForEach(Parallel = true)] static void A(in P p, ref Reduce<Wide> w) => w.Value.L += (long)p.V * p.V;", true)]
    [TestCase("[ForEach] static void A(in B8 b, ref F f) => f.V += b.V;", false)]                     // four times as wide
    [TestCase("[ForEach] static void A(ref F f, in L64 l) => f.V = l.V;", false)]                     // long to float rounds twice
    [TestCase("[ForEach] static void A(ref F f, in G g) => f.V = System.Math.Clamp(f.V, -g.Pull, g.Pull);", true)]
    [TestCase("[ForEach] static void A(ref F f) => f.V = System.Math.Clamp(f.V, 2, 1);", false)]       // throws
    [TestCase("[ForEach] static void A(ref F f, in H h) => f.V = float.Lerp(f.V, h.V, 0.5f);", true)]
    [TestCase("[ForEach] static void A(ref F f, ref P p) { if (f.V is > 0 and < 1 || !(p.V == 3)) p.V = (int)f.V; }", true)]
    [TestCase("[ForEach] static void A(ref P p) => p.V = System.Math.Abs(p.V);", true)]                // Math.Abs(int.MinValue) runs the method
    [TestCase("[ForEach] static void A(ref P p) => p.V /= 3;", true)]                                  // integer division through doubles
    [TestCase("[ForEach] static void A(ref P p) => p.V = p.V / 8 + p.V % -16;", true)]                 // by powers of two: shifts
    [TestCase("[ForEach] static void A(ref Mixed m) => m.B = m.B / m.B + (m.B != 0 && m.B % 3 > 1 ? 1 : 0);", true)] // a divisor per row
    [TestCase("[ForEach] static void A(ref L64 l) => l.V = l.V / 8 + l.V % 4;", true)]                 // long lanes by powers of two
    [TestCase("[ForEach] static void A(ref L64 l) => l.V /= 3;", false)]                               // long lanes by 3
    [TestCase("[ForEach] static void A(ref F f) => f.V = f.V / 4 + System.MathF.Pow(f.V, 1);", true)]  // a product, the value
    // The user's own calls, inlined (Kernel.Inlining.cs): the loop of the method written without them.
    [TestCase("[ForEach] static void A(ref F f) => f.V = M(f.V); static float M(float x) => x;", true)]           // a static helper
    [TestCase("[ForEach] static void A(Entity e, ref P p, in Q q) { Hurt(ref p.V, q.V); if (IsDead(p.V)) e.SetH(1); } static bool IsDead(int hp) => hp <= 0; static void Hurt(ref int hp, int dmg) { if (dmg <= 0) return; hp -= dmg; }", true)]
    [TestCase("[ForEach] static void A(ref P p, in Q q) => p.V -= Damage(p.V, q.V); static int Damage(int a, int d) { int x = a - d; if (x < 1) return 1; return x; }", true)] // a value through statements
    [TestCase("[ForEach] static void A(ref P p, in Q q) => p.V = Pick(p.V, q.V); static int Pick(int a, int b) { if (a > b) return a - b; else if (a == b) return 0; return b; }", true)] // returns as ?:
    [TestCase("[ForEach] static void A(ref P p) { if (TryHalf(p.V, out var h)) p.V = h; } static bool TryHalf(int v, out int half) { half = v >> 1; return v > 0; }", true)] // an out parameter
    [TestCase("[ForEach] static void A(ref F f) { f.V = Twice(f.V); static float Twice(float x) => x + x; }", true)] // a local function
    [TestCase("[ForEach] static void A(ref F f, in H h) => f.V = Outer(f.V, h.V); static float Outer(float a, float b) => Inner(a) * b; static float Inner(float a) => a + 1;", true)] // nested
    [TestCase("[ForEach] static void A(ref P p) => p.V = p.Doubled(); } public static class Ext { public static int Doubled(this in P p) => p.V * 2;", true)] // an extension method
    [TestCase("[ForEach] static void A(ref P p) { if (p.Low) p.Bump(2); } } public static class Ext { extension(ref P p) { public bool Low => p.V < 3; public void Bump(int n) { p.V += n; } }", true)] // C# 14 extension members
    [TestCase("[ForEach] static void A(ref Hits h) { h.Hit(2); if (h.Dead) h.V = 0; } } public struct Hits : IComponent { public int V; public void Hit(int n) { if (n < 0) return; V -= n; } public bool Dead => V <= 0; ", true)] // a component's own members
    [TestCase("[ForEach] static void A(ref F f) => f.V = R(f.V); static float R(float x) => x <= 0 ? 0 : R(x - 1);", false)] // recursive
    [TestCase("[ForEach] static void A(ref F f) => f.V = L(f.V); static float L(float x) { for (int i = 0; i < 2; i++) x += i; return x; }", false)] // a loop
    [TestCase("[ForEach] static void A(ref Two t) => t.A = t.B;", true)]                               // two fields: Interleaved
    [TestCase("[ForEach] static void A(ref Five t) => t.A = t.B;", false)]                             // five fields
    [TestCase("[ForEach] static void A(ref Mixed m) => m.A += m.B;", true)]                             // float and int fields
    [TestCase("[ForEach(Parallel = true)] static void A(ref F f) => f.V++;", true)]                    // parallel: in each chunk
    [TestCase("[ForEach(Parallel = true)] static void A(ref Two t, in G g) => t.A -= t.B * g.Pull;", true)]
    [TestCase("[ForEach(Parallel = true)] static void A(in P p, ref Reduce<Count> c) => c.Value.N += p.V;", true)]
    [TestCase("[ForEach] static void A(Entity e, ref F f) => f.V++;", true)] // the Entity unused: the loop needs no handle
    // A structural change of the row's entity under a condition: marked, applied in bulk after the pass.
    [TestCase("[ForEach] static void A(Entity e, in F f) { if (f.V <= 0) e.Set(new H()); }", true)]
    [TestCase("[ForEach] static void A(Entity e, in F f) { if (f.V <= 0) e.SetH(2); }", true)]          // generated: new H { V = 2 }
    [TestCase("[ForEach] static void A(Entity e, ref F f) { if (f.V > 9) e.RemoveH(); else f.V++; }", true)]
    [TestCase("[ForEach] static void A(Entity e, in F f) { if (f.V > 9 || f.V < -9) e.Remove<H>(); }", true)]
    [TestCase("[ForEach] static void A(Entity e, in F f) { if (f.V < 0) return; e.Disable<H>(); }", true)]
    [TestCase("[ForEach] static void A(Entity e, in F f) { if (f.V < 0) e.Destroy(); }", true)]
    [TestCase("[ForEach] static void A(Entity e, in F f, Emit<Hit> h) { if (f.V < 0) { e.SetH(1); h.Add(new Hit(e.Id)); } } public record struct Hit(EntityId Who);", true)]
    [TestCase("[ForEach] static void A(in F f, Emit<Score> s) { if (f.V > 1) s.Add(new Score(f.V * 2)); } public record struct Score(float V);", true)]
    [TestCase("[ForEach] static void A(Entity e, in F f) { if (f.V <= 0) e.SetH(f.V); }", false)]      // a value per row
    [TestCase("[ForEach] static void A(Entity e, in F f) { if (f.V < 0) e.Set(new H()); else e.Remove<D>(); }", false)] // two changes
    [TestCase("[ForEach] static void A(Entity e, in F f) { if (f.V < 0) e.RemoveF(); }", false)]        // the component it takes
    [TestCase("[ForEach] static void A(Entity e, in F f, Emit<Hit> h) { if (f.V < 0) { e.Destroy(); h.Add(new Hit(e.Id)); } } public record struct Hit(EntityId Who);", false)]
    [TestCase("[ForEach] static void A(Entity e, in F f) { if (f.V < 0) e.SetH(1); if (f.V > 5) System.Console.WriteLine(); }", false)] // a cold branch
    [TestCase("[ForEach] static void A(in P p, ref Reduce<Count> c) { c.Value.N++; c.Value.Low = System.Math.Min(c.Value.Low, p.V); }", true)] // reductions
    [TestCase("[ForEach] static void A(in P p, ref Reduce<Count> c) => c.Combine(new Count { N = p.V, Low = p.V });", true)]
    [TestCase("[ForEach(FloatMode = FloatMode.Fast)] static void A(in F f, ref Reduce<Total> t) => t.Value.V += f.V;", true)]
    [TestCase("[ForEach] static void A(in F f, ref Reduce<Total> t) => t.Value.V += f.V;", false)]  // a float sum: Exact
    [TestCase("[ForEach] static void A(in P p, ref Reduce<Ends> e) => e.Value.Last = p.V;", false)]  // not field by field
    [TestCase("[ForEach(FloatMode = FloatMode.Fast)] static void A(ref F f, in H h) => f.V = System.MathF.Sin(f.V) * System.MathF.Exp(h.V);", true)]
    [TestCase("[ForEach(FloatMode = FloatMode.Fast)] static void A(ref D d) => d.V = System.Math.Log2(d.V) + double.Hypot(d.V, 2);", true)]
    [TestCase("[ForEach] static void A(ref F f) => f.V = System.MathF.Cos(f.V);", false)]                 // approximate: Exact
    [TestCase("[ForEach] static void A(ref F f, in G g) => f.V *= System.MathF.Sin(g.Pull);", true)]      // uniform: the scalar call
    [TestCase("[ForEach] static void A(ref F f) => f.V = float.DegreesToRadians(f.V);", true)]            // (x * Pi) / 180: exact
    [TestCase("[ForEach] static void A(ref F f) => f.V = System.MathF.Atan2(f.V, 1);", false)]            // no vector form
    [TestCase("[ForEach] static void A(ref F f, Maybe<H> h) { if (h.HasValue) f.V += h.Value.V; else f.V = 0; }", true)] // a lane mask
    [TestCase("[ForEach] static void A(ref F f, ReadOnlyMaybe<Two> t) => f.V = t.HasValue && t.Value.A > 0 ? t.Value.B : f.V;", true)]
    [TestCase("[ForEach] static void A(ref F f, Maybe<H> h) { if (!h.HasValue) return; h.Value.V = f.V; }", true)]   // written through
    [TestCase("[ForEach] static void A(ref F f, Maybe<D> d) { if (d.HasValue) f.V++; }", false)]                    // another lane size
    [TestCase("[ForEach(Parallel = true)] static void A(ref F f, Maybe<H> h) { if (h.HasValue) f.V++; }", false)]   // in parallel
    public void QualifyingMethodsGetAVectorLoop(string members, bool vectorized)
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, Lanes, $"[EcsSystem] public ref struct S {{ {members} }}"),
            out var output, out _);
        string generated = output.SyntaxTrees.Single(t => t.FilePath.EndsWith("S.ForEach.g.cs") || t.FilePath.Contains("/S.") && IsCode(t)).ToString();
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty, generated);
        Assert.That(generated.Contains("for (nint last = rows - global::System.Numerics.Vector<"), Is.EqualTo(vectorized), generated);
        // Vector512 first where the hardware runs it, then Vector<T> for the rows left.
        Assert.That(generated.Contains("for (nint last = rows - global::System.Runtime.Intrinsics.Vector512<"), Is.EqualTo(vectorized), generated);
        // (In the job's Execute: a job that marks rows also names Vector.IsHardwareAccelerated in its Vectorizes.)
        int execute = System.Math.Max(0, generated.IndexOf("public void Execute(global::EasyEcs.EntitySpan", System.StringComparison.Ordinal));
        Assert.That(generated.IndexOf("Vector512.IsHardwareAccelerated", execute, System.StringComparison.Ordinal)
            < generated.IndexOf("global::System.Numerics.Vector.IsHardwareAccelerated", execute, System.StringComparison.Ordinal), Is.EqualTo(vectorized), generated);
        // A vector loop also takes words of rows of which some pass, as a lane mask (IForEachMasked); a parallel one
        // runs each chunk's runs of rows (ForEachBatchParallel).
        bool parallel = members.Contains("Parallel = true");
        Assert.That(generated.Contains("global::EasyEcs.IForEachMasked<") && generated.Contains("ulong pass, "), Is.EqualTo(vectorized && !parallel), generated);
        if (parallel && vectorized) Assert.That(generated, Does.Contain("world.ForEachBatchParallel<"), generated);

        // Skipped means reported: the same method with SimdMode.Require is an error exactly when it got no loop.
        string required = members.Replace("[ForEach]", "[ForEach(SimdMode = SimdMode.Require)]").Replace("[ForEach(Parallel", "[ForEach(SimdMode = SimdMode.Require, Parallel").Replace("[ForEach(FloatMode", "[ForEach(SimdMode = SimdMode.Require, FloatMode");
        var errors = Diagnose(Components, Lanes, $"[EcsSystem] public ref struct S {{ {required} }}").Result;
        Assert.That(errors.Contains("EECS056"), Is.EqualTo(!vectorized), string.Join(", ", errors));
    }

    // EECS055: the first reason a method that looks meant for a vector loop stays scalar, from Kernel.Vectorize.
    [TestCase("[ForEach] static void A(ref Five t) => t.A = t.B;", "component Five has 5 fields (a vector loop splits up to 4), and gathering the 2 fields it uses takes 11 permutes per 16 rows, for no vector operation; it uses 2 of Five's 5 fields (A, B): split them into a component of their own (a component of 2 fields gets a vector loop)", true)]
    [TestCase("[ForEach] static void A(in B8 b, ref F f) => f.V += b.V;", "lane types byte and float differ in size: a vector loop takes lanes of one size and of twice that size, not 4 times")]
    [TestCase("[ForEach] static void A(ref F f, in L64 l) => f.V = l.V;", "the conversion of 'l.V' from long to float is not supported")]
    [TestCase("[ForEach] static void A(ref P p, in D d) => p.V = (int)d.V;", "the conversion of 'd.V' from double to int is not supported")]
    [TestCase("[ForEach] static void A(ref L64 l, in F f) => l.V = (long)f.V;", "the conversion of 'f.V' from float to long is not supported")]
    [TestCase("[ForEach] static void A(ref F f, in D d) => f.V = (float)System.Math.Floor(d.V / 3) + (float)(d.V % 2);", "'%' of floats is not supported (no vector remainder)")] // the reason of a half
    [TestCase("[ForEach] static void A(ref P p, in L64 l) => p.V = (int)(l.V / 3);", "integer division of lanes of two sizes is not supported")]
    [TestCase("[ForEach] static void A(ref F f) => f.V = System.Math.Clamp(f.V, 2, 1);", "Math.Clamp's min is above its max, so the call throws")]
    [TestCase("[ForEach] static void A(ref F f) => f.V = System.MathF.Round(f.V, 2);", "MathF.Round is not supported")]
    [TestCase("[ForEach] static void A(ref F f) => f.V %= 2;", "'%' of floats is not supported (no vector remainder)")]
    [TestCase("[ForEach] static void A(ref Split s) => s.A++;", "component Split declares fields in several parts, so the order of its fields is not defined: declare them in one part", true)]
    [TestCase("[ForEach] static void A(ref L64 l) => l.V %= 10;", "'%' of long lanes by 10, not a power of two, is not supported (no exact vector form for lanes of 8 bytes)")]
    [TestCase("[ForEach] static void A(ref F f) => f.V = M(f.V); static float M(float x) => x > 0 ? M(x - 1) : x;", "it calls S.M, which is recursive")]
    [TestCase("[ForEach] static void A(ref F f) => f.V = M(f.V); static float M(float x) { for (int i = 0; i < 2; i++) x++; return x; }", "it calls S.M, which has a loop")]
    [TestCase("[ForEach] static void A(ref F f) => f.V = M0(f.V); static float M0(float x) => M1(x); static float M1(float x) => M2(x); static float M2(float x) => M3(x); static float M3(float x) => M4(x); static float M4(float x) => M5(x); static float M5(float x) => M6(x); static float M6(float x) => M7(x); static float M7(float x) => M8(x); static float M8(float x) => x;", "it calls S.M8, more than 8 helpers deep")]
    [TestCase("[ForEach] static void A(ref F f) => f.V = Fn(f.V); static readonly System.Func<float, float> Fn = static x => x;", "it calls Func.Invoke, a delegate, whose target the vector loop cannot see")]
    [TestCase("[ForEach] static void A(ref F f) => f.V = B.K(f.V); static readonly Base B = new Base(); } public class Base { public virtual float K(float x) => x;", "it calls Base.K, which is virtual: the vector loop cannot see which override runs")]
    [TestCase("[ForEach] static void A(ref F f) => f.V = Take(ref f) + 1; static float Take(ref F f) { f.V++; return f.V; }", "it calls S.Take, which changes its arguments or its entity, inside an expression: call it in a statement of its own (var r = ...;)", true)] // and none for Take: EECS055 is for [ForEach] methods only
    [TestCase("[ForEach] static void A(ref F f) => f.V = R.Scaled(f.V); } public static class R { static readonly float Scale = 2; public static float Scaled(float x) => x * Scale;", "it calls R.Scaled, whose body names private field 'float R.Scale' of R, which code outside its type (or file) cannot reach: make it internal", true)]
    [TestCase("[ForEach] static void A(ref F f) => f.V = Log(f.V) * 2; static float Log(float x) { System.Console.WriteLine(x); return x; }", "it calls S.Log, which calls Console.WriteLine")]
    [TestCase("[ForEach] static void A(ref F f) { for (int i = 0; i < 2; i++) f.V++; }", "'for (int i = 0; i < 2; i++) f.V++;' is not supported")]
    [TestCase("[ForEach] static void A(ref F f, in H h) => h.V.ToString();", "it calls Single.ToString, which is defined in another assembly")]
    [TestCase("[ForEach] static void A(in B8 b, ref C8 c) { float d = b.V; c.V = (byte)d; }", "local 'd' is a float, not a lane type of 1 or 2 bytes")]
    [TestCase("[ForEach] static void A(in P p, ref Purse u) => u.Sum = p.V + 1L;", "it writes 'u.Sum', which is neither a local nor the lane of a component taken by ref")] // the write, not the conversion of its value
    [TestCase("[ForEach] static void A(ref F f, ref int n) => n++;", "it takes 'n' by ref: a vector loop reduces only into a Reduce<T>")]
    [TestCase("[ForEach] static void A(in F f, ref Reduce<Total> t) => t.Value.V += f.V;", "the float sum Total.V would add its rows in another order (one sum per lane): set FloatMode = FloatMode.Fast to accept a sum that is not bit for bit the method's")]
    [TestCase("[ForEach] static void A(in P p, ref Reduce<Ends> e) => e.Value.Last = p.V;", "Ends.Combine sets First to 'a.First', not the sum, Min or Max of both sides' First")]
    [TestCase("[ForEach] static void A(in B8 b, ref Reduce<Wide> w) => w.Value.N += b.V;", "field Wide.N of the reduction is a int, not a lane type of 1 or 2 bytes")]
    [TestCase("[ForEach] static void A(in P p, ref Reduce<Count> c) { long x = p.V; c.Value.N += x; }", "it writes 'c.Value.N += x', where Count.Combine takes the sum of N")]
    [TestCase("[ForEach] static void A(ref P p, ref Reduce<Count> c) { c.Value.N += p.V; p.V = c.Value.N; }", "it reads 'c.Value.N', a partial result the lanes of a vector loop hold in parts")]
    [TestCase("[ForEach(Parallel = true)] static void A(ref Five t) => t.A = t.B;", "component Five has 5 fields (a vector loop splits up to 4), and gathering the 2 fields it uses takes 11 permutes per 16 rows, for no vector operation; it uses 2 of Five's 5 fields (A, B): split them into a component of their own (a component of 2 fields gets a vector loop)", true)] // parallel too
    [TestCase("[ForEach] static void A(ref F f, Maybe<D> d) { if (d.HasValue) f.V++; }", "lane types float and double differ in size")]
    [TestCase("[ForEach(Parallel = true)] static void A(ref F f, Maybe<H> h) { if (h.HasValue) f.V++; }", "it takes a Maybe<H> and runs in parallel: an optional column has a vector loop on one thread")]
    [TestCase("[ForEach] static void A(ref F f) => f.V = System.MathF.Sin(f.V);", "MathF.Sin is not bit-exact in vector form: set FloatMode = FloatMode.Fast")]
    [TestCase("[ForEach] static void A(ref D d) => d.V = double.Hypot(d.V, 1);", "Double.Hypot is not bit-exact in vector form: set FloatMode = FloatMode.Fast")]
    [TestCase("[ForEach(FloatMode = FloatMode.Fast)] static void A(ref F f) => f.V = System.MathF.Atan2(f.V, 1);", "MathF.Atan2 has no vector form in .NET 10")]
    [TestCase("[ForEach(FloatMode = FloatMode.Fast)] static void A(ref F f) => f.V = System.MathF.Pow(f.V, 5);", "MathF.Pow has no vector form in .NET 10 but by a constant 0, 1, 2, 3, 4, -1, -2 or 0.5")]
    [TestCase("[ForEach] static void A(ref F f) => f.V = System.MathF.Pow(f.V, 2);", "MathF.Pow is not bit-exact in vector form except by 0 and 1: set FloatMode = FloatMode.Fast")]
    [TestCase("[ForEach] static void A(ref F f) => f.V = System.MathF.Tanh(f.V);", "MathF.Tanh is not bit-exact in vector form: set FloatMode = FloatMode.Fast")]
    [TestCase("[ForEach(FloatMode = FloatMode.Fast)] static void A(ref D d) => d.V = System.Math.Tanh(d.V);", "Math.Tanh has no vector form in .NET 10")]
    [TestCase("[ForEach] static void A(ref B8 b, in C8 c) => b.V = (byte)System.Math.Min(b.V - c.V, 10);", "'(byte)System.Math.Min(b.V - c.V, 10)' keeps the low bits of an int the vector loop cannot compute in 8-bit lanes (it may leave [0, 255] and [-128, 127])")]
    [TestCase("[ForEach] static void A(ref B8 b, in C8 c) => b.V = (byte)((b.V * c.V) >> 8);", "the shift '(b.V * c.V) >> 8' needs bits of an int that 8-bit lanes do not hold")]
    [TestCase("[ForEach] static void A(ref B8 b, in C8 c) => b.V = (byte)(b.V << c.V);", "the shift 'b.V << c.V' is by a count that is not a constant")]
    [TestCase("[ForEach] static void A(ref B8 b, in C8 c) => b.V = (byte)(b.V / (c.V + 1));", "integer division is not supported (it throws on 0)")]
    [TestCase("[ForEach] static void A(ref B8 b, in S8 s) { if (b.V > s.V) b.V = 0; }", "'b.V > s.V' compares ints that 8-bit lanes do not hold")]
    [TestCase("[ForEach] static void A(ref B8 b, in C8 c) { if (b.V + c.V > 255) b.V = 0; }", "'b.V + c.V > 255' compares ints that 8-bit lanes do not hold")]
    [TestCase("[ForEach] static void A(ref B8 b, Maybe<C8> c) { if (c.HasValue) b.V++; }", "it takes a Maybe<C8> of byte lanes: optional columns have vector loops of 4- and 8-byte lanes")]
    [TestCase("[ForEach] static void A(ref Sixteen r, in G g) { r.F0 += r.F2 * g.Pull; r.F1 += r.F3 * g.Pull; }", "component Sixteen has 16 fields (a vector loop splits up to 4), and gathering the 4 fields it uses takes 42 permutes per 16 rows, more than its 4 vector operations gain; it uses 4 of Sixteen's 16 fields (F0, F1, F2, F3): split them into a component of their own (a component of 4 fields gets a vector loop)", true)]
    [TestCase("[ForEach] static void A(ref Unit u) { if (u.Alive) u.Hp--; }", "field Unit.Alive is a bool, not a lane type (float, double, int, uint, long, ulong, byte, sbyte, short or ushort)")]
    [TestCase("[ForEach] static void A(ref Px p, in B8 b) => p.R = (byte)(p.R + b.V);", "component Px has 2 fields of byte: a vector loop splits fields of 4 or 8 bytes, and takes narrower ones only when the method does the same to each field; it uses 1 of Px's 2 fields (R): move it into a component of its own (a component of one field gets a vector loop)", true)]
    [TestCase("[ForEach] static void A(ref Px p) => p.R = (byte)(p.R + p.G);", "component Px has 2 fields of byte: a vector loop splits fields of 4 or 8 bytes, and takes narrower ones only when the method does the same to each field")]
    [TestCase("[ForEach] static void A(ref F f, in Pos p) => f.V = System.Numerics.Vector2.Dot(p.Value, p.Value);", "Vector2.Dot adds the fields of a vector together, in an order not proven to be the scalar code's")]
    [TestCase("[ForEach] static void A(ref F f, in Pos p) => f.V = p.Value.Length();", "Vector2.Length adds the fields of a vector together, in an order not proven to be the scalar code's")]
    [TestCase("[ForEach] static void A(ref Odd o) => o = new Odd(o.Y, o.X);", "Odd's constructor does more than set fields from its parameters ('Y = x + y;')")]
    [TestCase("[ForEach] static void A(ref F f, in H h) { ref var v = ref f.V; v += h.V; }", "ref local 'v' is not supported: name the parameter's fields instead", true)]
    [TestCase("[ForEach] static void A(ref F f, in H h) { float v; v = h.V; f.V = v; }", "local 'v' has no initializer: give it its value where it is declared", true)]
    [TestCase("[ForEach] static void A(Entity e, ref F f) => f.V = e.Id.Index;", "it uses the Entity in 'e.Id.Index': a vector loop takes the entity's id in a record and its own Set, Remove, Disable, Enable or Destroy; read components as parameters (a tag as [With<T>] / [Without<T>], or a Maybe<T>)", true)]
    public void ScalarLoopsNameTheirReason(string members, string reason, bool fixable = false)
    {
        var diagnostics = Analyze([Components, Lanes, $"public static partial class S {{ {members} }}"]).Result.Where(d => d.Id == "EECS055").ToArray();
        Assert.That(diagnostics.Select(d => d.GetMessage()), Is.EqualTo(new[] { $"[ForEach] method 'A' runs one row at a time, without a vector loop: {reason}" }));
        // A rewrite the message names is a warning; what the method computes (a call, a conversion, a division) a suggestion.
        Assert.That(diagnostics[0].Severity, Is.EqualTo(fixable ? DiagnosticSeverity.Warning : DiagnosticSeverity.Info));
    }

    // Not meant for a vector loop (or vectorized, or opted out): no EECS055.
    [TestCase("[ForEach] static void A(ref F f, in H h) => f.V += h.V;")]                              // vectorized
    [TestCase("[ForEach(SimdMode = SimdMode.Off)] static void A(ref Five t) => t.A = t.B;")]              // opted out
    [TestCase("[ForEach] static void A(ref Holder h) { }")]                                             // not numeric fields
    [TestCase("[ForEach] static void A(System.Span<F> f) { }")]                                          // a batch method
    [TestCase("[ForEach(FloatMode = FloatMode.Fast)] static void A(in F f, ref Reduce<Total> t) => t.Value.V += f.V;")] // vectorized
    [TestCase("[ForEntity] static void A(ref F f, in H h) => f.V = h.V.GetHashCode();")]                // one entity: no loop
    [TestCase("public struct Acc : ISingletonComponent { public float Sum; } [ForEach] static void A(in F f, ref Acc a) => a.Sum += f.V;")] // accumulated by hand: EECS086 when a system resets it, a running total otherwise
    public void MethodsNotMeantForAVectorLoopGetNoMessage(string members)
    {
        var ids = Analyze([Components, Lanes, $"public static partial class S {{ {members} }}"]).Result.Select(d => d.Id);
        Assert.That(ids, Has.None.EqualTo("EECS055"));
    }

    [TestCase("[ForEach(SimdMode = SimdMode.Require)] static void A(Entity e, ref F f) => f.V = e.Id.Index;", "it uses the Entity in 'e.Id.Index': a vector loop takes the entity's id in a record and its own Set, Remove, Disable, Enable or Destroy; read components as parameters (a tag as [With<T>] / [Without<T>], or a Maybe<T>)")]
    [TestCase("[ForEach(SimdMode = SimdMode.Require)] static void A(Entity e, in F f) { if (f.V <= 0) e.SetH(f.V); }", "'e.SetH(f.V)' sets H to a value that differs per row: a vector loop gives the rows it marks one value; set the tag here and the value in a [ForEach] method over the tag")]
    [TestCase("[ForEach(SimdMode = SimdMode.Require)] static void A(Entity e, in F f) { if (f.V < 0) e.Set(new H()); else e.Remove<D>(); }", "it makes two structural changes ('e.Set(new H())' and 'e.Remove<D>()'): a vector loop applies one; split the method into one [ForEach] method per change")]
    [TestCase("[ForEach(SimdMode = SimdMode.Require)] static void A(Entity e, in F f) { if (f.V < 0) e.RemoveF(); }", "it changes F, which it also takes as 'f': the change would be seen at once by the method and after the pass by a vector loop; change it in a [ForEach] method of its own")]
    [TestCase("[ForEach(SimdMode = SimdMode.Require)] static void A(Entity e, in F f) { if (f.V < 0) e.Clone(); }", "it calls 'e.Clone()': a vector loop applies one Set, Remove, Disable, Enable or Destroy of one component to the rows it marks")]
    [TestCase("[ForEach(SimdMode = SimdMode.Require)] static void A(Entity e, in F f) { if (f.V < 0) e.SetH(1); if (f.V > 5) System.Console.WriteLine(); }", "it calls Console.WriteLine, which is defined in another assembly, in a branch of a method that changes structure: a vector loop sends such a branch's rows to the method, which cannot change structure during the pass; move that branch into a [ForEach] method of its own")]
    [TestCase("[ForEach(SimdMode = SimdMode.Require, Parallel = true)] static void A(ref F f, in B8 b) => f.V++;", "lane types byte and float differ in size: a vector loop takes lanes of one size and of twice that size, not 4 times")]
    [TestCase("[ForEach(SimdMode = SimdMode.Require)] static void A(ref Five t) => t.A++;", "component Five has 5 fields (a vector loop splits up to 4), and gathering the 1 field it uses takes 9 permutes per 16 rows, more than its 1 vector operation gains; it uses 1 of Five's 5 fields (A): move it into a component of its own (a component of one field gets a vector loop)")]
    [TestCase("[ForEach(SimdMode = SimdMode.Require)] static void A(ref F f, Scratch<int> s) => f.V += s.Value;", "it takes a Scratch<Int32>: working memory a row at a time, not one per lane")]
    [TestCase("public struct Aim : IComponent { public EntityId Target; } [ForEach(SimdMode = SimdMode.Require)] static void A(ref F f, in Aim a, Lookup<D> d) { if (d.Has(a.Target)) f.V = 1; }", "it reads Lookup<D>, whose fields are not all lanes of 4 bytes (float, int, uint): a vector loop gathers only those")]
    public void ARequiredVectorLoopThatDoesNotQualifyIsAnError(string members, string reason)
    {
        var diagnostics = Analyze([Components, Lanes, $"[EcsSystem] public ref struct S {{ {members} }}"]).Result.Where(d => d.Id.StartsWith("EECS05")).ToArray();
        Assert.That(diagnostics.Select(d => d.Id + " " + d.GetMessage()),
            Is.EqualTo(new[] { $"EECS056 [ForEach(SimdMode = SimdMode.Require)] method 'A' gets no vector loop: {reason}" }));
        Assert.That(diagnostics[0].Severity, Is.EqualTo(DiagnosticSeverity.Error));
    }

    [Test]
    public void SimdOffKeepsAQualifyingMethodScalar()
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, Lanes,
            "[EcsSystem] public ref struct S { [ForEach(SimdMode = SimdMode.Off)] static void A(ref F f, in H h) => f.V += h.V; [ForEach(SimdMode = SimdMode.Require)] static void B(ref F f) => f.V++; }"),
            out var output, out _);
        string generated = output.SyntaxTrees.Single(t => IsCode(t)).ToString();
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty, generated);
        Assert.That(generated, Does.Contain("internal ref struct AJob0 : global::EasyEcs.IForEach<"), generated);  // per row
        Assert.That(generated, Does.Contain("internal ref struct BJob1 : global::EasyEcs.IForEachMasked<"), generated);
        Assert.That(System.Text.RegularExpressions.Regex.Matches(generated, "Vector.LoadUnsafe").Count, Is.EqualTo(2), generated); // B only: its batch and masked forms (and their Vector512.LoadUnsafe)
    }

    [Test]
    public void AVectorLoopLoadsOnlyTheColumnsItUses()
    {
        // H filters the rows (a component of the query) but the method never reads it: its column is not loaded.
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, Lanes,
            "[EcsSystem] public ref struct S { [ForEach(SimdMode = SimdMode.Require)] static void A(ref F f, in H h) => f.V++; }"),
            out var output, out _);
        string generated = output.SyntaxTrees.Single(t => IsCode(t)).ToString();
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty, generated);
        Assert.That(generated, Does.Contain("var __c1_0 = global::System.Numerics.Vector.LoadUnsafe(ref e1, (nuint)i);"), generated);
        Assert.That(generated, Does.Not.Contain("__c2_0"), generated);
    }

    // Each field alike (field i of every component to field i, the same operations): the columns as flat lanes.
    [TestCase("[ForEach] static void A(ref Two t, in Two u) { t.A += u.A * 2; t.B += u.B * 2; }", true)]
    [TestCase("[ForEach] static void A(ref Two t, in G g) { if (t.A > 1) t.A = 0; if (t.B > 1) t.B = 0; t.A -= g.Pull; t.B -= g.Pull; }", true)]
    [TestCase("[ForEach] static void A(ref Two t, in Two u) { t.A += u.A * 2; t.B += u.B * 3; }", false)]   // other operations
    [TestCase("[ForEach] static void A(ref Two t, in Two u) { t.A += u.B; t.B += u.A; }", false)]          // field A from B
    [TestCase("[ForEach] static void A(ref Two t, in Two u) => t.A += u.A;", false)]                      // B left alone
    [TestCase("[ForEach] static void A(ref Two t, in Two u) { float d = u.A; t.A += d; t.B += u.B; }", false)] // a local
    [TestCase("[ForEach] static void A(ref Two t, in F f) { t.A += f.V; t.B += f.V; }", false)]            // one field and two
    [TestCase("[ForEach] static void A(ref Mixed m) { m.A += 1; m.B += 1; }", false)]                       // float and int
    [TestCase("[ForEach] static void A(ref Two t, in Two u) => t = new Two { A = t.A + u.A, B = t.B + u.B };", true)] // whole, field by field
    [TestCase("[ForEach] static void A(ref Duo t, in Duo u) => t = new Duo(t.A + u.A, t.B + u.B);", true)]               // a record's constructor
    [TestCase("[ForEach] static void A(ref Two t, in Two u) => t = new Two { A = t.B + u.A, B = t.A + u.B };", false)] // whole, fields swapped
    [TestCase("[ForEach] static void A(ref Two t, in Two u) => t = new Two { A = t.A + u.A, B = t.B * u.B };", false)] // whole, other operations
    [TestCase("[ForEach] static void A(ref Two t, in Two u) => t = new Two { A = t.A + u.B, B = t.B + u.B };", false)] // whole, one field twice
    public void ComponentsWhoseFieldsMatchAreFlatLanes(string members, bool flat)
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, Lanes, $"[EcsSystem] public ref struct S {{ {members} }}"),
            out var output, out _);
        string generated = output.SyntaxTrees.Single(t => IsCode(t) && t.ToString().Contains("Name = \"A\"")).ToString();
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty, generated);
        Assert.That(generated.Contains("lanes = rows * 2;"), Is.EqualTo(flat), generated);
        Assert.That(generated.Contains("for (nint last = rows - global::System.Numerics.Vector<"), Is.True, generated); // masked words, or the loop
        if (flat) Assert.That(generated, Does.Contain("i /= 2;").And.Contain("Interleaved.Load2("), generated);   // the masked Execute deinterleaves
    }

    // A parallel method's vector loop runs in each chunk's batch job: its singletons are the job's SingletonRefs, its
    // reductions the run's locals, combined per chunk.
    [Test]
    public void AParallelVectorLoopRunsInEachChunk()
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, Lanes,
            "[EcsSystem] public ref struct S { [ForEach(Parallel = true, SimdMode = SimdMode.Require)] static void A(ref F f, in G g, ref Reduce<Count> c) { f.V -= g.Pull; c.Value.N++; } }"),
            out var output, out _);
        string generated = output.SyntaxTrees.Single(t => IsCode(t) && t.ToString().Contains("Name = \"A\"")).ToString();
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty, generated);
        Assert.That(generated, Does.Contain("internal struct AJob0 : global::EasyEcs.IForEachBatch<global::F>, global::EasyEcs.INoStructuralChanges")
            .And.Contain("ref readonly global::G __p1 = ref s1.Value;")
            .And.Contain("var l2 = a2;")
            .And.Contain("l2.Combine(__v);")
            .And.Contain("static bool global::EasyEcs.IForEachBatch<global::F>.Merges => true;")
            .And.Contain("world.ForEachBatchParallel<global::F, __ForEach.AJob0>(ref job0, in __ForEach.AFilter0);")
            .And.Not.Contain("ulong pass"), generated);
    }

    [Test]
    public void MakingAVectorLoopParallelReemitsOnlyItsSystem()
    {
        const string other = "[EcsSystem] public ref struct O { [ForEach] static void B(ref F f) => f.V++; }";
        var tree = CSharpSyntaxTree.ParseText("using EasyEcs;\n" + Components + Lanes + "[EcsSystem] public ref struct S { [ForEach] static void A(ref F f) => f.V++; }", Parse);
        var compilation = Compile().AddSyntaxTrees(tree, CSharpSyntaxTree.ParseText("using EasyEcs;\n" + other, Parse));
        var driver = Driver().RunGenerators(compilation);
        driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(tree, CSharpSyntaxTree.ParseText(
            tree.ToString().Replace("[ForEach] static void A", "[ForEach(Parallel = true)] static void A"), Parse)));

        var reasons = Steps(driver)["SystemCode"].SelectMany(s => s.Outputs).Select(o => o.Reason).ToArray();
        Assert.That(reasons.Count(r => r is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged), Is.EqualTo(1)); // O
        Assert.That(reasons.Count(r => r == IncrementalStepRunReason.Modified), Is.EqualTo(1));                                      // S
        string generated = driver.GetRunResult().GeneratedTrees.Select(t => t.ToString()).Single(t => t.Contains("Name = \"A\""));
        Assert.That(generated, Does.Contain("ForEachBatchParallel<").And.Contain("Vector.LoadUnsafe"));
    }

    // Maybe<T>: HasValue is a lane mask of the presence bits, Value's fields are lanes of the run's optional column; a
    // read of Value where lanes are not proven to have it checks them (a lane without it sends the vector's rows to the
    // method, which throws), one proven by HasValue does not.
    [Test]
    public void AMaybeIsALaneMask()
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, Lanes,
            "[EcsSystem] public ref struct S { [ForEach(SimdMode = SimdMode.Require)] static void A(ref F f, Maybe<H> h) { if (h.HasValue) f.V += h.Value.V; f.V -= h.Value.V; } }"),
            out var output, out _);
        string generated = output.SyntaxTrees.Single(t => IsCode(t) && t.ToString().Contains("Name = \"A\"")).ToString();
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty, generated);
        Assert.That(generated, Does.Contain("ref float __r1 = ref global::System.Runtime.CompilerServices.Unsafe.As<global::H, float>(ref m1.Lanes);")
            .And.Contain("var __has1 = global::EasyEcs.Interleaved.Lanes32(m1.Present(i), 0);")
            .And.Contain("var __has1 = global::EasyEcs.Interleaved.Lanes32In512(m1.Present(i), 0);")
            .And.Contain("var __q1_0 = global::System.Numerics.Vector.LoadUnsafe(ref __r1, (nuint)i & __z1);")
            .And.Not.Contain("__live"), generated);
        // One check, for the read outside the branch; none for the one under h.HasValue.
        Assert.That(System.Text.RegularExpressions.Regex.Matches(generated, @"__bail \|= \(global::System.Numerics.Vector<int>.AllBitsSet & ~__has1\) != global::System.Numerics.Vector<int>.Zero;").Count, Is.EqualTo(1), generated);
        Assert.That(System.Text.RegularExpressions.Regex.Matches(generated, @"__bail \|= \(__pass & ~__has1\) != global::System.Numerics.Vector<int>.Zero;").Count, Is.EqualTo(1), generated);
        Assert.That(generated, Does.Not.Contain("__bail |= (__pass & __m"), generated);
    }

    // A static readonly field a helper reads is a uniform value of the loop: read once per run, in the setup before the
    // loop, never per vector (Native AOT checks the class's initialization at each read).
    [Test]
    public void AStaticReadonlyFieldAHelperReadsIsReadOncePerRun()
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, Lanes,
            "[EcsSystem] public ref struct S { [ForEach(SimdMode = SimdMode.Require)] static void A(ref F f) => f.V = Rates.Scaled(f.V); } " +
            "public static class Rates { internal static readonly float Scale = 2.5f; public static float Scaled(float x) => x * Scale; }"),
            out var output, out _);
        string generated = output.SyntaxTrees.Single(t => IsCode(t) && t.ToString().Contains("Name = \"A\"")).ToString();
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty, generated);
        var reads = generated.Split('\n').Where(l => l.Contains("Rates.Scale")).ToArray();
        Assert.That(reads, Is.Not.Empty, generated);
        Assert.That(reads, Has.All.Match(@"^\s*var __\w+ = "), generated); // setup locals only
    }

    // A call under an if the loop computes: a cold branch, whose lanes send the vector's rows to the method.
    [Test]
    public void ACallUnderAConditionIsAColdBranch()
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, Lanes,
            "[EcsSystem] public ref struct S { static void Log(float v) => System.Console.WriteLine(v); [ForEach(SimdMode = SimdMode.Require)] static void A(ref F f) { f.V -= 1; if (f.V < 0) { Log(f.V); f.V = 0; } } }"),
            out var output, out _);
        string generated = output.SyntaxTrees.Single(t => IsCode(t) && t.ToString().Contains("Name = \"A\"")).ToString();
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty, generated);
        Assert.That(generated, Does.Contain("__bail |= __m1 != global::System.Numerics.Vector<int>.Zero; // a cold branch: the method runs these rows")
            .And.Contain("__bail |= (__pass & __m1) != global::System.Numerics.Vector<int>.Zero; // a cold branch: the method runs these rows")
            .And.Not.Contain("Log("), generated);
    }

    // A component of a Vector2 assigned whole: one statement per lane, and as each lane is computed alike, the flat loop.
    [Test]
    public void AWholeComponentOfAVector2IsOneStatementPerLane()
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, Lanes,
            "[EcsSystem] public ref struct S { [ForEach(SimdMode = SimdMode.Require)] static void A(ref Pos p, in Vel v, in G g) => p = new Pos(p.Value + v.Value * g.Pull); }"),
            out var output, out _);
        string generated = output.SyntaxTrees.Single(t => IsCode(t) && t.ToString().Contains("Name = \"A\"")).ToString();
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty, generated);
        Assert.That(generated, Does.Contain("lanes = rows * 2;").And.Contain("var __t1 = (__c1_0 + (__c2_0 * __u0));")
            .And.Contain("var __t2 = (__c1_1 + (__c2_1 * __u0));"), generated); // the flat loop, then the masked one per lane
    }

    // A wide component (a float, a bool, a byte and an int a row): the float lane gathered from 3 words per row by
    // permutes on AVX-512 and by table lookups on ARM64 (4 rows of 3 words: 3 vectors), each behind its hardware and the
    // job's check of the layout; the masked words run the method.
    [Test]
    public void AWideComponentsLanesAreGatheredByPermutesAndTableLookups()
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, Lanes,
            "[EcsSystem] public ref struct S { [ForEach(SimdMode = SimdMode.Require)] static void A(ref Unit u) => u.Speed = System.MathF.Max(u.Speed * 0.5f - 1, 0); }"),
            out var output, out _);
        string generated = output.SyntaxTrees.Single(t => IsCode(t) && t.ToString().Contains("Name = \"A\"")).ToString();
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty, generated);
        Assert.That(generated, Does.Contain("private static readonly bool __layout1 = __Layout1();")
            .And.Contain("t = t with { Speed = global::System.BitConverter.Int32BitsToSingle(1) };")
            .And.Contain("global::System.Runtime.Intrinsics.X86.Avx512F.IsSupported && __layout1 && rows - i >=")
            .And.Contain("global::System.Runtime.Intrinsics.Vector512.LoadUnsafe(ref e1, (nuint)i * 3 + 32);")
            .And.Contain("global::System.Runtime.Intrinsics.X86.Avx512F.PermuteVar16x32x2(")
            .And.Contain("global::System.Numerics.Vector.IsHardwareAccelerated && global::EasyEcs.Interleaved.IsWideAccelerated && __layout1 && rows - i >=")
            .And.Contain("var __c1_0 = global::EasyEcs.Interleaved.Pick(__v1_0, __v1_1, __v1_2, __y0);")
            .And.Contain("__v1_1 = global::EasyEcs.Interleaved.PickInto(__v1_1, __c1_0, __y2);")
            .And.Contain("global::System.Runtime.Intrinsics.Vector128.Create((byte)0, 1, 2, 3, 12, 13, 14, 15, 24, 25, 26, 27, 36, 37, 38, 39);")
            .And.Contain("A: vector (Vector512 or Vector<float> of 128 bits), wide component (permutes, table lookups), masked words scalar"), generated);
    }

    // A form that does not pay is not written: 16 words a row put each field of a 128-bit vector's 4 rows in 4 vectors,
    // and the table lookups cost more than the method's operations gain at 4 lanes, while AVX-512's permutes pay.
    [Test]
    public void AWideFormThatDoesNotPayIsLeftOut()
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, Lanes,
            "[EcsSystem] public ref struct S { [ForEach(SimdMode = SimdMode.Require)] static void A(ref Sixteen r, in G g) { r.F0 = r.F0 - r.F7 * g.Pull; r.F15 = System.MathF.Min(r.F15 + r.F0, 10) * 0.5f + g.Pull * g.Pull; } }"),
            out var output, out _);
        string generated = output.SyntaxTrees.Single(t => IsCode(t) && t.ToString().Contains("Name = \"A\"")).ToString();
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty, generated);
        Assert.That(generated, Does.Contain("global::System.Runtime.Intrinsics.X86.Avx512F.PermuteVar16x32x2(")
            .And.Contain("A: vector (Vector512 only), wide component (permutes), masked words scalar")
            .And.Not.Contain("Interleaved.Pick").And.Not.Contain("Vector128<byte>").And.Not.Contain("IsWideAccelerated"), generated);
    }

    // Narrow lanes: a byte sum brought back into range by Math.Min is AddSaturate, a difference raised to 0 by Math.Max is
    // SubtractSaturate, a wrapping sum stays the lanes' own +, and the masked words run the method on their passing rows.
    [Test]
    public void ByteSaturationIdiomsAreTheSaturatingForms()
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, Lanes,
            "[EcsSystem] public ref struct S { [ForEach(SimdMode = SimdMode.Require)] static void A(ref B8 b, in C8 c) { b.V = (byte)System.Math.Max(b.V - c.V, 0); b.V = (byte)System.Math.Min(b.V + c.V, 255); b.V += c.V; } }"),
            out var output, out _);
        string generated = output.SyntaxTrees.Single(t => IsCode(t) && t.ToString().Contains("Name = \"A\"")).ToString();
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty, generated);
        Assert.That(generated, Does.Contain("global::System.Numerics.Vector.SubtractSaturate(__c1_0, __c2_0)")
            .And.Contain("global::System.Runtime.Intrinsics.Vector512.AddSaturate(")
            .And.Contain("(__c1_0 + __c2_0)")
            .And.Not.Contain("Interleaved.Lanes8"), generated);
        // the masked Execute: no vector loop, the passing rows run the method
        string masked = generated.Substring(generated.IndexOf("ulong pass,", System.StringComparison.Ordinal));
        Assert.That(masked.Substring(0, masked.IndexOf("WriteMask", System.StringComparison.Ordinal)), Does.Not.Contain("Vector"), generated);
    }

    // FloatMode.Fast: the vector approximations of .NET (Vector.Sin, Vector512.Sin) in both loops; degrees to radians as
    // the scalar's two operations; a uniform call computed once by the scalar function.
    [Test]
    public void FastMathCallsTheVectorForms()
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, Lanes,
            "[EcsSystem] public ref struct S { [ForEach(FloatMode = FloatMode.Fast, SimdMode = SimdMode.Require)] static void A(ref F f, in G g) => f.V = System.MathF.Sin(f.V) + System.MathF.Cos(g.Pull) * float.DegreesToRadians(f.V); }"),
            out var output, out _);
        string generated = output.SyntaxTrees.Single(t => IsCode(t) && t.ToString().Contains("Name = \"A\"")).ToString();
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty, generated);
        Assert.That(generated, Does.Contain("global::System.Numerics.Vector.Sin(__c1_0)").And.Contain("global::System.Runtime.Intrinsics.Vector512.Sin(__c1_0)")
            .And.Contain("global::System.Numerics.Vector.Create<float>(global::System.MathF.Cos(__p1.Pull))")
            .And.Contain("Vector.Create<float>(float.Pi)").And.Contain("Vector.Create<float>(180f)"), generated);
    }

    // The Vector512 loop is the Vector<T> loop's text with the vector type's token replaced: user types named
    // Vector-something in the setup (a context value, a singleton's field) stay as written.
    [TestCase("public static partial class R { [ForEach(SimdMode = SimdMode.Require)] public static void A(ref F f, in System.Numerics.Vector3 wind) => f.V += wind.X * wind.Y; }")]
    [TestCase("public static partial class R { [ForEach(SimdMode = SimdMode.Require)] public static void A(ref F f, in System.Numerics.Vector<float> bias, float gain) => f.V *= gain; }")]
    [TestCase("public struct Wind : ISingletonComponent { public System.Numerics.Vector3 V; } [EcsSystem] public ref struct S { [ForEach(SimdMode = SimdMode.Require)] static void A(ref F f, in Wind w) => f.V += w.V.X; }")]
    public void UserTypesNamedVectorStayAsWritten(string source)
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, Lanes, source), out var output, out _);
        string generated = string.Join("\n", output.SyntaxTrees.Where(t => IsCode(t)).Select(t => t.ToString()));
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty, generated);
        Assert.That(generated, Does.Contain("for (nint last = rows - global::System.Runtime.Intrinsics.Vector512<float>.Count;"), generated);
        Assert.That(generated, Does.Not.Contain("Vector5123").And.Not.Contain("Intrinsics.Vector512<float> __p").And.Not.Contain("Intrinsics.Vector512<float> x"), generated);
        Assert.That(generated.IndexOf('\u0001'), Is.EqualTo(-1), "a token left in the loop"); // ordinal: culture comparison ignores U+0001
    }

    // A reduction: one accumulator per lane and field from T.Identity, folded into the run's partial result in lane order.
    [Test]
    public void AReductionFoldsItsLanesInLaneOrder()
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, Lanes,
            "[EcsSystem] public ref struct S { [ForEach] static void A(in P p, ref Reduce<Count> c) { if (p.V > 0) c.Value.N += p.V; c.Value.Low = System.Math.Min(c.Value.Low, p.V); } }"),
            out var output, out _);
        string generated = output.SyntaxTrees.Single(t => IsCode(t) && t.ToString().Contains("Name = \"A\"")).ToString();
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty, generated);
        Assert.That(generated, Does.Contain("var __id1 = global::Count.Identity;")
            .And.Contain("var __a1_0 = global::System.Numerics.Vector.Create<int>(__id1.N);")
            .And.Contain("var __a1_1 = global::System.Numerics.Vector.Create<int>(__id1.Low);")
            .And.Contain("__a1_0 = global::System.Numerics.Vector.ConditionalSelect(__m0, (__a1_0 + __c1_0), __a1_0);")
            .And.Contain("__a1_1 = global::System.Numerics.Vector.Min(__a1_1, __c1_0);")
            .And.Contain("for (int __j = 0; __j < global::System.Numerics.Vector<int>.Count; __j++)")
            .And.Contain("__v.N = __a1_0[__j];")
            .And.Contain("l1.Combine(__v);"), generated);
        // In a masked word each write of an accumulator leaves the lanes that do not pass as they were: no select after.
        Assert.That(generated, Does.Contain("__a1_0 = global::System.Numerics.Vector.ConditionalSelect((__m0 & __pass), (__a1_0 + __c1_0), __a1_0);")
            .And.Contain("__a1_1 = global::System.Numerics.Vector.ConditionalSelect(__pass, global::System.Numerics.Vector.Min(__a1_1, __c1_0), __a1_1);")
            .And.Not.Contain("var __h1_0 = __a1_0;"), generated);
    }

    // T.Combine is part of the loop: an edit of it in another file re-emits the method (here: no longer field by field).
    [Test]
    public void AnEditOfCombineReemitsTheLoopsThatFoldWithIt()
    {
        var reduction = CSharpSyntaxTree.ParseText("using EasyEcs;\npublic struct P : IComponent { public int V; } " +
            "public struct Sum : ISingletonComponent, IReduction<Sum> { public int N; public static Sum Identity => default; public static Sum Combine(Sum a, Sum b) => new() { N = a.N + b.N }; }", Parse);
        var systems = CSharpSyntaxTree.ParseText("using EasyEcs;\n[EcsSystem] public ref struct S { [ForEach] static void A(in P p, ref Reduce<Sum> s) => s.Value.N += p.V; }", Parse);
        var compilation = Compile().AddSyntaxTrees(reduction, systems);
        var driver = Driver().RunGenerators(compilation);
        string Generated() => driver.GetRunResult().GeneratedTrees.Select(t => t.ToString()).Single(t => t.Contains("Name = \"A\""));
        Assert.That(Generated(), Does.Contain("l1.Combine(__v);"));
        driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(reduction, CSharpSyntaxTree.ParseText(
            reduction.ToString().Replace("N = a.N + b.N", "N = a.N"), Parse)));
        Assert.That(Steps(driver)["SystemCode"].SelectMany(s => s.Outputs).Select(o => o.Reason), Has.Some.EqualTo(IncrementalStepRunReason.Modified));
        Assert.That(Generated(), Does.Not.Contain("l1.Combine(__v);").And.Not.Contain("Vector.IsHardwareAccelerated"));
    }

    [Test]
    public void AFieldAddedToAComponentReemitsTheLoopsThatSplitIt()
    {
        var components = CSharpSyntaxTree.ParseText("using EasyEcs;\npublic struct Pos : IComponent { public float X, Y; } public struct Other : IComponent { public float V; }", Parse);
        var systems = CSharpSyntaxTree.ParseText("using EasyEcs;\n[EcsSystem] public ref struct S { [ForEach] static void A(ref Pos p) => p.X += p.Y; } " +
            "[EcsSystem] public ref struct O { [ForEach] static void B(ref Other o) => o.V++; }", Parse);
        var compilation = Compile().AddSyntaxTrees(components, systems);
        var driver = Driver().RunGenerators(compilation);
        Assert.That(driver.GetRunResult().GeneratedTrees.Select(t => t.ToString()).Single(t => t.Contains("Name = \"A\"")), Does.Contain("Interleaved.Load2("));
        driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(components, CSharpSyntaxTree.ParseText(
            components.ToString().Replace("public float X, Y;", "public float X, Y, Z;"), Parse)));

        var reasons = Steps(driver)["SystemCode"].SelectMany(s => s.Outputs).Select(o => o.Reason).ToArray();
        Assert.That(reasons.Count(r => r is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged), Is.EqualTo(1)); // O
        Assert.That(reasons.Count(r => r == IncrementalStepRunReason.Modified), Is.EqualTo(1));                                      // S
        string generated = driver.GetRunResult().GeneratedTrees.Select(t => t.ToString()).Single(t => t.Contains("Name = \"A\""));
        Assert.That(generated, Does.Contain("Interleaved.Load3(").And.Contain("Interleaved.Store3("), generated);
    }

    [Test]
    public void TurningSimdOffReemitsOnlyItsSystem()
    {
        const string other = "[EcsSystem] public ref struct O { [ForEach] static void B(ref F f) => f.V++; }";
        var tree = CSharpSyntaxTree.ParseText("using EasyEcs;\n" + Components + Lanes + "[EcsSystem] public ref struct S { [ForEach] static void A(ref F f) => f.V++; }", Parse);
        var compilation = Compile().AddSyntaxTrees(tree, CSharpSyntaxTree.ParseText("using EasyEcs;\n" + other, Parse));
        var driver = Driver().RunGenerators(compilation);
        driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(tree, CSharpSyntaxTree.ParseText(
            tree.ToString().Replace("[ForEach] static void A", "[ForEach(SimdMode = SimdMode.Off)] static void A"), Parse)));

        var reasons = Steps(driver)["SystemCode"].SelectMany(s => s.Outputs).Select(o => o.Reason).ToArray();
        Assert.That(reasons.Count(r => r is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged), Is.EqualTo(1)); // O
        Assert.That(reasons.Count(r => r == IncrementalStepRunReason.Modified), Is.EqualTo(1));                                      // S
        // Not the walk of O and S (.Fusion.g.cs): two trees without a path read as one file, S declared after O.
        string generated = driver.GetRunResult().GeneratedTrees.Where(t => !t.FilePath.EndsWith(".Fusion.g.cs"))
            .Select(t => t.ToString()).Single(t => t.Contains("Name = \"A\""));
        Assert.That(generated, Does.Not.Contain("Vector.LoadUnsafe"));
    }

    [TestCase("[EcsSystem] public ref struct G<T> where T : struct, IComponent { [ForEach] static void A(ref T t, in P p) { } } "
              + "public static class U { public static void F(World w) { w.AddSystem<G<P>>(); w.HasSystem<G<Q>>(); } }",
        "file static class __ForEach<T> where T : struct, global::EasyEcs.IComponent", "(global::G<T> owner, ref T t, in global::P p)",
        "public readonly struct __G_1_SystemCode<T> : global::EasyEcs.ISystemCode where T : struct, global::EasyEcs.IComponent",
        "access.Write<T>();", "[assembly: global::EasyEcs.GenericSystemCode(typeof(global::G<>), typeof(global::__G_1_SystemCode<>))]",
        "systems.Add<global::G<global::P>, global::__G_1_SystemCode<global::P>>();", "systems.Add<global::G<global::Q>, global::__G_1_SystemCode<global::Q>>();")]
    // Every API that names a system closes it: the set registers the closed type's code (no reflection, AOT-safe).
    [TestCase("[EcsSystem] public ref struct G<T> where T : struct, IComponent { [ForEach] static void A(ref T t) { } } "
              + "[UpdateAfter<G<P>>] [EcsSystem] public ref struct After { } "
              + "public static class U { public static void F(World w) { w.System<G<Q>>(); w.TryGetSystem<G<Settings>>(out _); w.RemoveSystem<global::G<T>>(); } }",
        "systems.Add<global::G<global::P>, global::__G_1_SystemCode<global::P>>();", "systems.Add<global::G<global::Q>, global::__G_1_SystemCode<global::Q>>();",
        "systems.Add<global::G<global::Settings>, global::__G_1_SystemCode<global::Settings>>();", "systems.Add<global::G<global::T>, global::__G_1_SystemCode<global::T>>();",
        "systems.Add<global::After, global::__After_SystemCode>();")]
    [TestCase("public static class R { [ForEach] static void A<T>(in T t, ref int n) where T : struct, IComponent { } }",
        "public static void A<T>(global::EasyEcs.World world, ref int n) where T : struct, global::EasyEcs.IComponent",
        "internal ref struct AJob0<T> : global::EasyEcs.IForEachBatch<T> where T : struct, global::EasyEcs.IComponent",
        "internal static extern void A_0<T>([global::System.Runtime.CompilerServices.UnsafeAccessorType(\"R\")] object owner, in T t, ref int n) where T : struct, global::EasyEcs.IComponent;")]
    [TestCase("[Without<Q>, WithAny<T, P>] public struct G : ISystemGroup; [UpdateInGroup<G>] [EcsSystem] public ref struct S { [ForEach, Without<Q>] static void A(ref P p) { } [ForEach, With<P>] static void B() { } }",
        "AFilter0 = global::EasyEcs.QueryFilter.Create().Without<global::Q>().WithAny<global::T, global::P>();", // once, though both name it
        "BFilter1 = global::EasyEcs.QueryFilter.Create().Without<global::Q>().WithAny<global::T, global::P>();", 
        "world.ForEachMasked<global::P, __ForEach.A_B_Fused0>(ref fused0, in __ForEach.A_B_Fused0Filter)")] // the same rows: one walk
    [TestCase("public record struct In<T>(int X) : IComponent; public interface IActor<TSelf> : ISystemGroupWith<In<TSelf>>, ISystemGroupWithout<Q> where TSelf : struct; "
        + "public struct Map : IActor<Map>; [UpdateInGroup<Map>] [EcsSystem] public ref struct S { [ForEach] static void A(ref P p) { } }",
        ".With<global::In<global::Map>>()", ".Without<global::Q>()")] // the group's filters through a generic interface
    [TestCase("public static class R<T> where T : struct, IComponent { [ForEach] static void A(in T t, ref int n) { } }",
        "extension<T>(global::R<T>) where T : struct, global::EasyEcs.IComponent", "new __ForEach<T>.AJob0 { x1 = ref n }", "public static class R_1ForEach")]
    public void GenericSystemsAndRunnersAreGenerated(string source, params string[] expected)
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, source), out var output, out _);
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
        string generated = string.Concat(output.SyntaxTrees.Where(t => IsCode(t) || t.FilePath.EndsWith("SystemCode.g.cs") || t.FilePath.EndsWith("__EasyEcsSystems.g.cs"))
            .Select(t => t.ToString()));
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
                public interface IActor<TSelf> : IComponent, IIndexed<TSelf>, ISystemGroup, ISystemGroupWith<In<TSelf>>
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
            [EcsSystem] public ref struct Grow { [ForEach] static void A(ref Tiles t) => t.Count++; }
            public static class Spawn
            {
                public static void Add(World world) => world.AddSystem<Grow>();

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
        string generated = string.Concat(output.SyntaxTrees.Where(t => IsCode(t)).Select(t => t.ToString()));
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
            spawn.GetMethod("Add")!.Invoke(null, [world]);
            Assert.That(spawn.GetMethod("Activate")!.Invoke(null, [world]), Is.EqualTo(82L));   // key 8, Tiles grown once
        }
        finally { context.Unload(); }
    }

    [Test]
    public void AGenericSystemOfAnotherAssemblyIsRegisteredWhereItIsClosed()
    {
        // A library's public generic system, closed over a component of the user's assembly: the library cannot name the
        // closed type, the user's generator registers its code (no MakeGenericMethod at run time: Native AOT, WebAssembly).
        // The user compiles against the reference assembly, where the private [ForEach] method is gone.
        var library = CSharpCompilation.Create("Library",
            [CSharpSyntaxTree.ParseText("""
                using EasyEcs;
                namespace Library;
                public interface ILife : IComponent { int Value { get; set; } }
                [UpdateInterval(2)]
                [EcsSystem] public ref struct Regen<T> where T : struct, ILife { [ForEach] static void Heal(ref T t) => t.Value++; }
                """, Parse)],
            Basic.References.Append(MetadataReference.CreateFromFile(typeof(World).Assembly.Location)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Driver().RunGeneratorsAndUpdateCompilation(library, out var built, out _);
        Assert.That(built.GetDiagnostics().Where(d => d.Severity >= DiagnosticSeverity.Warning), Is.Empty);
        using var image = new System.IO.MemoryStream();
        using var reference = new System.IO.MemoryStream();
        Assert.That(built.Emit(image, metadataPEStream: reference).Success, Is.True);

        var user = Compile("""
            using Library;
            public record struct Hp(int Value) : ILife { public int Value { get; set; } = Value; }
            public static class Game
            {
                public static int Run(World world)
                {
                    world.AddSystem<Regen<Hp>>();
                    var e = world.CreateEntity(new Hp(0));
                    for (int i = 0; i < 4; i++) world.Update();
                    return e.Read<Hp>().Value;
                }
            }
            """).AddReferences(MetadataReference.CreateFromImage(reference.ToArray()));
        Driver().RunGeneratorsAndUpdateCompilation(user, out var output, out _);
        Assert.That(output.GetDiagnostics().Where(d => d.Severity >= DiagnosticSeverity.Warning), Is.Empty);
        string registration = string.Concat(output.SyntaxTrees.Where(t => t.FilePath.EndsWith("__EasyEcsSystems.g.cs")).Select(t => t.ToString()));
        Assert.That(registration, Does.Contain("systems.Add<global::Library.Regen<global::Hp>, global::Library.__Regen_1_SystemCode<global::Hp>>();"), registration);

        using var userImage = new System.IO.MemoryStream();
        Assert.That(output.Emit(userImage).Success, Is.True);
        var context = new System.Runtime.Loader.AssemblyLoadContext("library", isCollectible: true);
        try
        {
            context.LoadFromStream(new System.IO.MemoryStream(image.ToArray()));
            var game = context.LoadFromStream(new System.IO.MemoryStream(userImage.ToArray())).GetType("Game")!;
            using var world = new World();
            Assert.That(game.GetMethod("Run")!.Invoke(null, [world]), Is.EqualTo(2));   // every 2nd of 4 updates
        }
        finally { context.Unload(); }
    }

    [TestCase("[ForEach] static void A(ref P p, ref int n, in int floor, int add) { }",
        "public static void A(global::EasyEcs.World world, ref int n, in int floor, int add)", "x1 = ref n, x2 = ref floor, x3 = add")]
    [TestCase("[ForEach, Changed<T>] static void A(ref P p, ref Clock c, int world) { }",
        "public static void A(global::EasyEcs.World _world, ulong since, int world)", "__ForEach.AFilter0.Since(since)", "s1 = ref _world.SingletonRef")]
    [TestCase("[ForEach(Parallel = true)] static void A(ref P p, in int k) { }", "public int x1;", "x1 = k }", "ForEachBatchParallel<")]
    [TestCase("[ForEach(Slices = 3), Without<Q>] static void A(ref P p, int slice) { }",
        "public static void A(global::EasyEcs.World world, int _slice, int slice)", "__ForEach.AFilter0.Slice(_slice, 3)")]
    public void RunnersGetAnEntryPoint(string members, params string[] expected)
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, $"public static partial class R {{ {members} }}"),
            out var output, out _);
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
        string generated = output.SyntaxTrees.Single(t => IsCode(t)).ToString();
        foreach (var line in expected) Assert.That(generated, Does.Contain(line), generated);
        Assert.That(generated, Does.Not.Contain("GeneratedUpdate").And.Not.Contain("GeneratedAccess"));
    }

    // Skipped by the generator == reported: a method the analyzer rejects gets no job.
    [TestCase("[ForEach(Slices = 0)] static void A(ref P p) { }")]                                    // EECS035
    [TestCase("[ForEach(ChunkRows = 64)] static void A(ref P p) { }")]                                // EECS054: not parallel
    [TestCase("[ForEach(Parallel = true, ChunkRows = -1)] static void A(ref P p) { }")]               // EECS054: below 0
    public void RejectedMethodsGetNoJob(string members)
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, $"[EcsSystem] public ref struct S {{ {members} }}"), out var output, out _);
        Assert.That(string.Concat(output.SyntaxTrees.Where(t => IsCode(t)).Select(t => t.ToString())), Does.Not.Contain("Job0"));
    }

    [TestCase("[ForEach] static void A(Entity e, in P p) { if (e.Has<Q>()) _ = e.Id; }", true, "")]
    [TestCase("[ForEach] static void A(Entity e, ref P p, in Q q) => e.Remove<Q>();", false, "WriteMask => 1;")]
    [TestCase("[ForEach] static void A(Entity e, in P p) => Use(e); static void Use(Entity e) { }", false, "WriteMask => 0;")]
    [TestCase("[ForEach] static void A(in P p, World w) { }", false, "WriteMask => 0;")]
    public void StructuralChangesAreDetectedFromTheMethodBody(string members, bool noStructuralChanges, string writes)
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, $"[EcsSystem] public ref struct S {{ {members} }}"),
            out var output, out _);
        string generated = output.SyntaxTrees.Single(t => IsCode(t)).ToString();
        Assert.That(generated.Contains("INoStructuralChanges"), Is.EqualTo(noStructuralChanges));
        Assert.That(generated, Does.Contain(writes));
    }

    [Test]
    public void TheModuleTellsWorldsHowToIndexItsUniqueComponents()
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, """
            public record struct Account(int Id) : IComponent, IUniqueIndexed<int> { public int Key => Id; }
            public record struct Mount(EntityId Rider) : IComponent, IUniqueIndexed<EntityId> { public EntityId Key => Rider; }
            """), out var output, out _);
        var source = output.SyntaxTrees.Single(t => t.FilePath.EndsWith("EasyEcs.TypeRegistry.g.cs")).ToString();
        Assert.That(source, Does.Contain("global::EasyEcs.TypeRegistry.Component<global::Account>()"));
        Assert.That(source, Does.Contain("global::EasyEcs.TypeRegistry.UniqueIndex<global::Account, int>()"));
        Assert.That(source, Does.Not.Contain("UniqueIndex<global::Mount"));            // an EntityId key cannot be unique (EECS036)
        Assert.That(source, Does.Not.Contain("UniqueIndex<global::Owned"));            // IIndexed only
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
    }

    [Test]
    public void TheModuleRegistersTheWorldSetupItsTypesDeclare()
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, """
            public partial record struct Zone([GroupBy] int Id) : IComponent;
            public record struct Room(int Id) : IComponent, IGroup { [GroupBy] public int Group => Id; }
            public partial struct Hall : IComponent { [GroupBy] public int Floor; }
            public partial record struct Match([Group] int Id) : IComponent;
            [WhenTargetDestroyed(TargetPolicy.Destroy)] public partial record struct Summon([LookupKey] EntityId Owner) : IComponent;
            [WhenTargetDestroyed(TargetPolicy.Remove)] public record struct Ally(EntityId Target) : IRelation;
            [NotSaved] public record struct Link2(object Socket) : IComponent;
            [NotSaved] public record struct Pool(object Handle) : ISingletonComponent;
            """), out var output, out _);
        var source = output.SyntaxTrees.Single(t => t.FilePath.EndsWith("EasyEcs.TypeRegistry.g.cs")).ToString();
        foreach (var call in new[] { "GroupBy<global::Zone>()", "GroupBy<global::Room>()", "GroupBy<global::Hall>()", "WhenTargetDestroyed<global::Summon>((global::EasyEcs.TargetPolicy)2)",
                     "RelationWhenTargetDestroyed<global::Ally>((global::EasyEcs.TargetPolicy)1)", "NotSaved<global::Link2>()", "NotSaved<global::Pool>()" })
            Assert.That(source, Does.Contain("global::EasyEcs.TypeRegistry." + call + ";"), call);
        Assert.That(source, Does.Not.Contain("GroupBy<global::Match>"));                // [Group]: only worlds that call GroupBy
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
    }

    [Test]
    public void TheModuleRegistersItsComponentsSingletonsAndBuffersForLoading()
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components,
            "[EcsSystem] public ref struct S { [ForEach] static void A(in DynamicBuffer<P> b) { } }"), out var output, out _);
        var source = output.SyntaxTrees.Single(t => t.FilePath.EndsWith("EasyEcs.TypeRegistry.g.cs")).ToString();
        Assert.That(source, Does.Contain("[global::System.Runtime.CompilerServices.ModuleInitializer]"));
        foreach (var call in new[] { "Component<global::P>()", "Component<global::T>()", "Singleton<global::Clock>()",
                     "Component<global::EasyEcs.DynamicBuffer<global::P>>()" })
            Assert.That(source, Does.Contain("global::EasyEcs.TypeRegistry." + call), call);
        // Link's id is a public field, Owned's an auto-property's backing field: both are reached through UnsafeAccessor.
        Assert.That(source, Does.Contain("global::EasyEcs.TypeRegistry.EntityIds<global::Link>(static (ref value) => ref Field"));
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
            var source = output.SyntaxTrees.Single(t => t.FilePath.EndsWith("EasyEcs.TypeRegistry.g.cs")).ToString();
            return source.Split('\n').Single(l => l.Contains("TypeRegistry.Raw<global::R>")).Trim();
        }

        var original = Registration("public int A; public float B;");
        Assert.That(Registration("public int A; public float B;"), Is.EqualTo(original));
        Assert.That(Registration("public float A; public int B;"), Is.Not.EqualTo(original));   // same size, other fields
        Assert.That(Registration("public int B; public float A;"), Is.Not.EqualTo(original));   // renamed
        Driver().RunGeneratorsAndUpdateCompilation(Compile("public struct M : IComponent { public string S; }"), out var managed, out _);
        Assert.That(managed.SyntaxTrees.Single(t => t.FilePath.EndsWith("EasyEcs.TypeRegistry.g.cs")).ToString(), Does.Not.Contain("Raw<"));
    }

    // Every tracked stage of ComponentGenerator and SystemGenerator: a new stage is added here (the test fails until it is).
    private static readonly string[] TrackedStages =
    [
        "ForEachMethods", "Systems", "SystemUpdates", "ClosedSystems", "SystemSet", "Components", "ExtensionMembers", "BufferUses",
        "BufferElements", "QueryShapes", "SavedComponents", "SavedBuffers", "ModuleInitializers", "Parts",
        "GroupBy", "TargetPolicies", "NotSaved", "ForEntityMethods", "ForEntityTypes", "LambdaPasses", "LambdaFiles", "Views", "Prefabs",
        "SystemPlaces", "SystemFusions", "SetFusions", "SystemLoops", "SystemCode",
    ];

    // An edit that changes no model, as the IDE sees one per keystroke: every stage's outputs come from the cache (or
    // equal it), so no file is emitted again.
    [TestCase("public class Unrelated { }", "public class Unrelated { int x; }")]                  // another file
    [TestCase("i.P.V++;", "i.P.V += 2;")]                                                          // a body in the systems' file
    [TestCase("{ [ForEach] static void A(ref P p, in DynamicBuffer<L> b) { }", "{ [ForEach] static void A(ref P p, in DynamicBuffer<L> b) { p.V++; }")] // a [ForEach] body
    [TestCase("for (int i = 0; i < 2; i++) q.V += ++s.Value;", "for (int i = 0; i < 3; i++) q.V += ++s.Value;")] // a looping Scratch method's body
    [TestCase("static void U(World w) { _ = w.EntityCount; }", "static void U(World w) { _ = w.EntityCount + 1; }")] // a hook's body: the set stays
    [TestCase("public P P = new() { V = 3 };", "public P P = new() { V = 3 }; ")] // a prefab declaration's trivia
    [TestCase("q.V += 2 + e.Id.Index;", "q.V += 3 + e.Id.Index;")]                       // a fused (scalar) method's body: its pair stays
    public void EditsThatChangeNoModelHitTheCache(string before, string after)
    {
        string code = "using EasyEcs;\n" + Components +
            "public struct L; [EcsSystem] public ref struct S { [ForEach] static void A(ref P p, in DynamicBuffer<L> b) { } [ForEach(Slices = 4)] static void D(ref Q q) { } " +
            "[ForEach] static void E(ref Q q, Scratch<int> s) { for (int i = 0; i < 2; i++) q.V += ++s.Value; } }" +
            "[EcsSystem] public ref struct G<T> where T : struct, IComponent { [ForEach] static void B(ref T t) { } } public static class H { static void F(World w) => w.AddSystem<G<P>>(); }" +
            "public static class U { static void C(World w) { foreach (var i in w.Query<P>()) i.P.V++; } }" +
            "public partial struct Census : ISingletonComponent { [Sum] public int N; [Min] public float L; }" +
            "public partial record struct Zn([GroupBy] int Id) : IComponent; [WhenTargetDestroyed(TargetPolicy.Remove)] public record struct Tether(EntityId Target) : IRelation;" +
            "[NotSaved] public record struct Wire(object O) : IComponent;" +
            "[UpdateInterval(3, Phase = 1)] [EcsSystem] public ref struct Sch { static void OnUpdate(World w) { _ = w.EntityCount; } } public static class FE { [ForEntity, Without<Q>] static void E(ref P p, int n) { } }" +
            "[View] public ref struct Vw { public ref P P; public ref readonly Q Q; public void Add(int n) { P.V += Q.V + n; } }" +
            "[EcsSystem] public ref struct VS { [ForEach] static void M(Vw v) => v.Add(1); }" +
            "[Prefab] public partial struct Kind { public P P = new() { V = 3 }; public Q Q; }" +
            "[EcsSystem] public ref struct F1 { [ForEach] static void Fa(ref Q q, Entity e) => q.V += e.Id.Index; [ForEach] static void Fb(ref Q q, Entity e) => q.V += 2 + e.Id.Index; } [EcsSystem] public ref struct F2 { [ForEach] static void Fc(ref Q q) => q.V++; }";
        var system = CSharpSyntaxTree.ParseText(code, Parse);
        var other = CSharpSyntaxTree.ParseText("public class Unrelated { }", Parse);
        // A lambda pass in a file of its own: its interceptor names the file's checksum, so an edit there is a new model.
        var lambdas = CSharpSyntaxTree.ParseText("using EasyEcs;\npublic static class Lp { static void F(World w, int n) => w.ForEach(n, static (ref P p, int n) => p.V += n); }", Parse);
        var edited = code.Contains(before) ? system : other;
        var compilation = Compile().AddSyntaxTrees(system, other, lambdas);

        var driver = Driver().RunGenerators(compilation);
        driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(edited,
            CSharpSyntaxTree.ParseText(edited.ToString().Replace(before, after), Parse)));

        var steps = Steps(driver);
        // Roslyn's own steps: its providers, FAWMN's internals (name_ForAttribute...) and the two outputs.
        string[] roslyn = ["Compilation", "CompilationOptions", "ParseOptions", "SourceOutput", "ImplementationSourceOutput"];
        Assert.That(steps.Keys.Where(k => !k.Contains('_') && !roslyn.Contains(k)), Is.EquivalentTo(TrackedStages), "every tracked stage is asserted");
        foreach (var name in TrackedStages.Append("SourceOutput").Append("ImplementationSourceOutput")) // and nothing is emitted again
            Assert.That(steps[name].SelectMany(s => s.Outputs).Select(o => o.Reason),
                Is.All.AnyOf(IncrementalStepRunReason.Cached, IncrementalStepRunReason.Unchanged), name);
    }

    // Realistic keystrokes, in a build (every output) and as the IDE runs the generators while typing (implementation
    // outputs skipped: the steps only they read never run). A tracked step either takes every output from the cache
    // or is one the edit really changes. Unchanged is not a cache hit: the step ran again and returned an equal value,
    // so a step the edit does not change must not report it. Allowed: in a step the edit changes, the other outputs of a
    // Collect it regroups; IsEasyEcsCore, a narrow Select of the compilation, which reruns per compilation by design (a
    // string compare) so that its consumers stay cached; SetFusions, which regroups on every compilation
    // (SystemGenerator.Fusion.cs, the declaration/body split's to fix).
    private const string IncrementalComponents = "using EasyEcs;\npublic struct P : IComponent { public int V; }\npublic struct Q : IComponent { public int V; }\n" +
        "public struct L;\npublic struct Clock : ISingletonComponent { public int Ticks; }\n";
    private const string IncrementalSystems = "using EasyEcs;\n" +
        "[EcsSystem] public ref struct Move { [ForEach] static void A(ref P p, in Q q) { p.V += q.V; } }\n" +
        "[EcsSystem] public ref struct Turn { [ForEach] static void C(ref Q q) { q.V--; } }\n" +
        "public static class Free { [ForEach] static void B(ref Q q) { q.V++; } }\n" +
        "public static class Report { public static int Sum(World w) { int s = 0; foreach (var i in w.Read<P, Q>()) s += i.P.V; return s; } static int N(DynamicBuffer<L> b) => b.Count; }\n";

    private static readonly string[] ImplementationOnlyStages =
    [
        "LambdaPasses", "LambdaFiles", "ClosedSystems", "SystemSet", "SystemPlaces", "SystemFusions", "SetFusions",
        "SavedComponents", "SavedBuffers", "GroupBy", "TargetPolicies", "NotSaved", "ModuleInitializers", "SystemLoops", "SystemCode",
    ];

    [TestCase("a [ForEach] body", "p.V += q.V;", "p.V += 2 * q.V;", "SystemLoops SystemCode")]
    [TestCase("an unrelated file", "public class Unrelated { }", "public class Unrelated { int x; }", "")]
    [TestCase("whitespace", "\npublic static class Free", "\n\npublic static class Free", "")]
    [TestCase("a new component field", "P : IComponent { public int V; }", "P : IComponent { public int V; public int W; }",
        "Components SavedComponents SystemLoops SystemCode LambdaPasses LambdaFiles")]
    [TestCase("a new query shape", "return s;", "foreach (var i in w.Query<Q>()) i.Q.V++; return s;", "QueryShapes")]
    public void AnEditRerunsOnlyTheStepsItChanges(string edit, string before, string after, string changes)
    {
        var components = CSharpSyntaxTree.ParseText(IncrementalComponents, Parse, path: "Components.cs");
        var systems = CSharpSyntaxTree.ParseText(IncrementalSystems, Parse, path: "Systems.cs");
        var lambdas = CSharpSyntaxTree.ParseText("using EasyEcs;\npublic static class Lp { static void F(World w, int n) => w.ForEach(n, static (ref P p, int n) => p.V += n); }", Parse, path: "Lambdas.cs");
        var other = CSharpSyntaxTree.ParseText("public class Unrelated { }", Parse, path: "Unrelated.cs");
        var compilation = Compile().AddSyntaxTrees(components, systems, lambdas, other);
        var edited = new[] { components, systems, other }.Single(t => t.ToString().Contains(before));
        var next = compilation.ReplaceSyntaxTree(edited, CSharpSyntaxTree.ParseText(edited.ToString().Replace(before, after), Parse, path: edited.FilePath));
        var changed = changes.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        string[] tolerated = ["IsEasyEcsCore", "SetFusions"];

        foreach (bool ide in new[] { false, true })
        {
            var driver = CSharpGeneratorDriver.Create(
                [new SystemGenerator().AsSourceGenerator(), new ComponentGenerator().AsSourceGenerator(), new PartGenerator().AsSourceGenerator(),
                    new ViewGenerator().AsSourceGenerator(), new PrefabGenerator().AsSourceGenerator(), new ArityGenerator().AsSourceGenerator()],
                parseOptions: Parse, driverOptions: new GeneratorDriverOptions(
                    ide ? IncrementalGeneratorOutputKind.Implementation : IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true))
                .RunGenerators(compilation).RunGenerators(next);
            var steps = Steps(driver);
            string mode = ide ? " (IDE)" : " (build)";
            foreach (var (name, runs) in steps)
            {
                if (name.Contains('_') || name is "Compilation" or "CompilationOptions" or "ParseOptions") continue; // Roslyn's own
                bool output = name is "SourceOutput" or "ImplementationSourceOutput";
                if (changed.Contains(name) || output && changed.Length > 0 || tolerated.Contains(name)) continue; // asserted below
                Assert.That(runs.SelectMany(s => s.Outputs).Select(o => o.Reason), Is.All.EqualTo(IncrementalStepRunReason.Cached),
                    $"{name} after {edit}{mode}");
            }
            foreach (var name in changed.Where(n => !ide || !ImplementationOnlyStages.Contains(n)))
                Assert.That(steps[name].SelectMany(s => s.Outputs).Select(o => o.Reason),
                    Has.Some.AnyOf(IncrementalStepRunReason.New, IncrementalStepRunReason.Modified), $"{edit} changes {name}{mode}");
            if (ide) // implementation outputs skipped: the steps only they read do not run at all
                Assert.That(steps.Keys.Intersect(ImplementationOnlyStages), Is.Empty, edit);
        }
    }

    // While typing, a generator transform runs again on every compilation for every node it matches, in every file.
    // One that binds a member body to read a call (GetSymbolInfo on a node inside it) binds the whole body and runs its
    // nullable analysis on every keystroke anywhere. The body here takes seconds to bind (nested lambdas, each bound
    // for both overloads of M): an edit elsewhere must not pay for it. Its Read of one component is the call
    // QueryShapes once bound to tell an entity's from a World's.
    [Test]
    public void AnEditElsewhereBindsNoBody()
    {
        string nested = "x17.GetHashCode()";
        for (int i = 17; i >= 0; i--) nested = $"M(x{i} => {nested})";
        var slow = CSharpSyntaxTree.ParseText("using EasyEcs;\npublic static class Slow { static int M(System.Func<int, int> f) => 0; static int M(System.Func<string, int> f) => 0; " +
            "static int F(Entity e) { int v = e.Read<P>().V; return v + " + nested + "; } }", Parse, path: "Slow.cs");
        var other = CSharpSyntaxTree.ParseText("public class Unrelated { }", Parse, path: "Unrelated.cs");
        // Nullable on, as projects set it: a node's binding then runs the nullable analysis of its whole member.
        var compilation = Compile(Components).AddSyntaxTrees(slow, other);
        compilation = compilation.WithOptions(compilation.Options.WithNullableContextOptions(NullableContextOptions.Enable));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new SystemGenerator().AsSourceGenerator(), new ComponentGenerator().AsSourceGenerator(), new PartGenerator().AsSourceGenerator(),
                new ViewGenerator().AsSourceGenerator(), new PrefabGenerator().AsSourceGenerator()],
            parseOptions: Parse, driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.Implementation));
        driver = driver.RunGenerators(compilation);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        driver.RunGenerators(compilation.ReplaceSyntaxTree(other, CSharpSyntaxTree.ParseText("public class Unrelated { int x; }", Parse, path: "Unrelated.cs")));
        Assert.That(watch.Elapsed.TotalSeconds, Is.LessThan(3), "binding the body takes over 10 s; the generators alone take milliseconds");
    }

    // The assembly's set: one Add per system (generic ones per closed type), a switch per hook over the systems'
    // numbers, which follow the order of their names (stable between builds), and a module initializer.
    [Test]
    public void TheSetCallsEverySystemOfTheAssembly()
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, """
            [EcsSystem] public ref struct Zeta { static void OnCreate(SystemHandle s, World w) { } }
            namespace N { [EcsSystem] public ref struct Alpha { [ForEach] static void A(ref P p) => p.V++; } }
            [EcsSystem] public ref struct G<X> where X : struct, IComponent { [ForEach] static void B(ref X x) { } }
            public static class Use { public static void F(World w) => w.AddSystem<G<Q>>(); }
            """), out var output, out _);
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
        string set = output.SyntaxTrees.Single(t => t.FilePath.EndsWith("__EasyEcsSystems.g.cs")).ToString();
        Assert.That(set, Does.Contain("file readonly struct __EasyEcsSystems : global::EasyEcs.ISystemSet")
            .And.Contain("[global::System.Runtime.CompilerServices.ModuleInitializer]")
            .And.Contain("systems.Add<global::G<global::Q>, global::__G_1_SystemCode<global::Q>>();")
            .And.Contain("systems.Add<global::N.Alpha, global::N.__Alpha_SystemCode>();")
            .And.Contain("systems.Add<global::Zeta, global::__Zeta_SystemCode>();"), set);
        Assert.That(set.IndexOf("Add<global::G<"), Is.LessThan(set.IndexOf("Add<global::N.Alpha")).And.LessThan(set.IndexOf("Add<global::Zeta")), set);
        Assert.That(set, Does.Contain("case 2: global::__Zeta_SystemCode.Create(world, handle); return;"), set);
        string zeta = output.SyntaxTrees.Single(t => t.FilePath.Contains("Zeta.") && t.FilePath.EndsWith(".System.g.cs")).ToString();
        Assert.That(zeta, Does.Contain("__ForEach.OnCreate_Create(default, system, world);"), zeta);   // the hook takes its arguments in its own order
    }

    [Test]
    public void AnEditedScheduleReemitsOnlyItsSystem()
    {
        var tree = CSharpSyntaxTree.ParseText("using EasyEcs;\n" + Components + "[UpdateInterval(3)] [EcsSystem] public ref struct S { }", Parse);
        var other = CSharpSyntaxTree.ParseText("using EasyEcs;\n[EcsSystem] public ref struct O { [ForEach] static void B(ref Q q) => q.V++; } public static class E { [ForEntity] static void C(ref P p) { } }", Parse);
        var compilation = Compile().AddSyntaxTrees(tree, other);
        var driver = Driver().RunGenerators(compilation);
        driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(tree, CSharpSyntaxTree.ParseText(tree.ToString().Replace("UpdateInterval(3)", "UpdateInterval(5, Phase = 2)"), Parse)));

        var reasons = Steps(driver)["SystemCode"].SelectMany(s => s.Outputs).Select(o => o.Reason).ToArray();
        Assert.That(reasons.Count(r => r is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged), Is.EqualTo(1)); // O
        Assert.That(reasons.Count(r => r == IncrementalStepRunReason.Modified), Is.EqualTo(1));                                      // S
        Assert.That(Steps(driver)["ForEntityTypes"].SelectMany(s => s.Outputs).Select(o => o.Reason),
            Is.All.AnyOf(IncrementalStepRunReason.Cached, IncrementalStepRunReason.Unchanged));
        Assert.That(driver.GetRunResult().GeneratedTrees.Select(t => t.ToString()).Single(t => t.Contains("struct __S_SystemCode")), Does.Contain("public static int Interval => 5;\n\n    public static int Phase => 2;"));
    }

    [Test]
    public void ConsumeForEntityAndSchedulesAreGenerated()
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, """
            [EcsSystem] public ref struct S
            {
                [ForEach, Consume, Without<Q>] static void A(in P p) { }               // stable: in bulk after the pass
                [ForEach, Consume] static void B(Entity e, ref P p) => e.Set(new Q()); // may change structure: per row
            }
            [UpdateInterval(4, Phase = 3)] [EcsSystem] public ref struct Slow { static void OnUpdate() { } }
            [UpdateInterval(2)] [EcsSystem] public ref struct G<X> where X : struct, IComponent { }
            public static class H { static void F(World w) => w.AddSystem<G<P>>(); }
            public static class O { [ForEntity, Changed<T>] static void C(Entity e, ref P p, in Clock c, int n) { } }
            public static class R { [ForEach, Consume] static void D(in Q q, ref int n) => n++; }
            """), out var output, out _);
        string all = string.Join("\n", output.SyntaxTrees.Skip(2).Select(t => t.ToString()));
        Assert.That(all, Does.Contain("world.DestroyAll<global::P>(in __ForEach.AFilter0); // [Consume]"));
        Assert.That(all, Does.Contain("__ForEach.B_1(default, entity, ref c1);\n            entity.Destroy();"));
        Assert.That(all, Does.Contain("world.DestroyAll<global::Q>(in __ForEach.DFilter0); // [Consume]"));          // a runner's
        Assert.That(all, Does.Contain("public static int Interval => 4;\n\n    public static int Phase => 3;").And.Contain("public static int Interval => 2;\n\n    public static int Phase => 0;"));
        Assert.That(all, Does.Contain("systems.Add<global::G<global::P>, global::__G_1_SystemCode<global::P>>();"));
        Assert.That(all, Does.Contain("public static bool C(global::EasyEcs.Entity e, ulong since, int n)"));
        Assert.That(all, Does.Contain("if (e.World is not { } world) return false;"));
        Assert.That(all, Does.Contain("return e.Run<global::P, __ForEach.CJob0>(ref job, in __ForEach.CFilter0, since);"));
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty, all);
    }

    [Test]
    public void GeneratedNamesNeverCollide()
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, """
            [EcsSystem] public ref struct S
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
            [EcsSystem] public ref struct S
            {
                [ForEach] static ref int Bump(ref P p) => ref p.V;                                  // a ref return
                [ForEach] static void @default(ref Q q) { }
                [ForEach] static void Owned(Entity owner, ref P p) { }                              // the accessor's instance name
                [ForEach] static void Mine(ref P p, in Q owner, in Q _owner) { }
            }
            public static class U
            {
                public static void Use(Entity e, World w)
                {
                    e.SetDefaults(); e.SetDefaults(2f, Color.Red); e.SetThis(1); e.SetSelf(2); e.SetSets(3); _ = e.@class; _ = w.@event;
                    e.Set(new O1.State()); e.Set(new O2.State());
                    e.SetBuffer(1).SetProps(hp: 1, speed: 2f).LinkAlly(e, 3).LinkBag(e, "bag").Set(new Locked());
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
            .And.Contain("LinkAlly(global::EasyEcs.EntityId target, int weight) => @this.Link(new global::Ally { Target = target, Weight = weight })")
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
            "[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, Maybe<Q> q) { if (q.HasValue) (q.Value.V, p.V) = (1, 2); } }"),
            out var output, out _);
        string generated = string.Join("\n", output.SyntaxTrees.Where(t => IsCode(t)).Select(t => t.ToString()));
        Assert.That(generated, Does.Contain("access.Write<global::Q>();"));
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

    [EcsSystem] public ref struct RefReturning
    {
        [ForEach] private static ref int Bump(ref Counted c)
        {
            c.V++;
            return ref c.V;
        }
    }

    [Test]
    public void AnotherSliceCountReemitsOnlyItsSystem()
    {
        var sliced = CSharpSyntaxTree.ParseText("using EasyEcs;\n" + Components + "[EcsSystem] public ref struct S { [ForEach(Slices = 2)] static void A(ref P p) { } }", Parse);
        var other = CSharpSyntaxTree.ParseText("using EasyEcs;\n[EcsSystem] public ref struct O { [ForEach(Slices = 2)] static void B(ref Q q) { } }", Parse);
        var compilation = Compile().AddSyntaxTrees(sliced, other);
        var driver = Driver().RunGenerators(compilation);
        driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(sliced, CSharpSyntaxTree.ParseText(
            "using EasyEcs;\n" + Components + "[EcsSystem] public ref struct S { [ForEach(Slices = 3)] static void A(ref P p) { } }", Parse)));

        var reasons = Steps(driver)["SystemCode"].SelectMany(s => s.Outputs).Select(o => o.Reason).ToArray();
        Assert.That(reasons.Count(r => r is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged), Is.EqualTo(1)); // O
        Assert.That(reasons.Count(r => r == IncrementalStepRunReason.Modified), Is.EqualTo(1));                                      // S
        string generated = driver.GetRunResult().GeneratedTrees.Select(t => t.ToString()).Single(t => t.Contains("Name = \"A\""));
        Assert.That(generated, Does.Contain("Slice((int)(system.RunCount % 3), 3)"));
    }

    [Test]
    public void AScratchMethodThatLoopsOrCallsRunsOutOfLine()
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components,
            "[EcsSystem] public ref struct S { " +
            "[ForEach(Parallel = true)] static void Loops(ref P p, Scratch<int[]> s) { s.Value ??= new int[4]; for (int i = 0; i < 4; i++) p.V += s.Value[i]; } " +
            "[ForEach] static void Calls(ref Q q, ReadOnlyMaybe<T> t, Scratch<System.Collections.Generic.List<int>> s) { (s.Value ??= new()).Clear(); q.V = s.Value.Count; } " +
            "[ForEach] static void Counts(ref P p, Scratch<int> s) => p.V = ++s.Value; " +                    // small: inlined
            "[ForEach] static void Plain(ref Q q) { for (int i = 0; i < 4; i++) q.V++; } }"),                // no Scratch: inlined
            out var output, out _);
        string generated = output.SyntaxTrees.Single(t => IsCode(t)).ToString();
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty, generated);
        const string noInlining = "global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]";
        Assert.That(generated, Does.Contain($"Name = \"Loops\"), {noInlining}"), generated);
        Assert.That(generated, Does.Contain($"Name = \"Calls\"), {noInlining}"), generated);   // a run of rows calls it too
        Assert.That(generated, Does.Contain("internal ref struct CallsJob1 : global::EasyEcs.IForEachBatch<"), generated);
        Assert.That(generated, Does.Contain("Name = \"Counts\")]"), generated);
        Assert.That(generated, Does.Contain("Name = \"Plain\")]"), generated);
    }

    [Test]
    public void ALoopAddedToAScratchMethodReemitsOnlyItsSystem()
    {
        var tree = CSharpSyntaxTree.ParseText("using EasyEcs;\n" + Components + "[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, Scratch<int> s) => p.V = ++s.Value; }", Parse);
        var other = CSharpSyntaxTree.ParseText("using EasyEcs;\n[EcsSystem] public ref struct O { [ForEach] static void B(ref Q q, Scratch<int> s) { for (int i = 0; i < 2; i++) q.V += ++s.Value; } }", Parse);
        var compilation = Compile().AddSyntaxTrees(tree, other);
        var driver = Driver().RunGenerators(compilation);
        driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(tree, CSharpSyntaxTree.ParseText(
            tree.ToString().Replace("=> p.V = ++s.Value;", "{ for (int i = 0; i < 2; i++) p.V += ++s.Value; }"), Parse)));

        var reasons = Steps(driver)["SystemCode"].SelectMany(s => s.Outputs).Select(o => o.Reason).ToArray();
        Assert.That(reasons.Count(r => r is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged), Is.EqualTo(1)); // O
        Assert.That(reasons.Count(r => r == IncrementalStepRunReason.Modified), Is.EqualTo(1));                                      // S
        string generated = driver.GetRunResult().GeneratedTrees.Select(t => t.ToString()).Single(t => t.Contains("Name = \"A\""));
        Assert.That(generated, Does.Contain("MethodImplOptions.NoInlining"), generated);
    }

    [Test]
    public void AnEditAboveAMethodKeepsItsCode()
    {
        const string system = "[EcsSystem] public ref struct S { [ForEach] static void A(ref P p) { } [ForEach] static void B(in P p) { } }";
        var tree = CSharpSyntaxTree.ParseText("using EasyEcs;\n" + Components + system, Parse);
        var compilation = Compile().AddSyntaxTrees(tree);
        var driver = Driver().RunGenerators(compilation);
        driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(tree,
            CSharpSyntaxTree.ParseText("using EasyEcs;\n// a comment moves every method\n" + Components + system, Parse)));

        var steps = Steps(driver);
        foreach (var name in new[] { "Systems", "SystemUpdates", "SystemSet" })
            Assert.That(steps[name].SelectMany(s => s.Outputs).Select(o => o.Reason),
                Is.All.AnyOf(IncrementalStepRunReason.Cached, IncrementalStepRunReason.Unchanged), name);
    }

    // An edit in a file of lambda passes changes its checksum, so its interceptors are written again; another file's stay.
    [Test]
    public void AnEditInALambdasFileLeavesTheOtherFilesCached()
    {
        var first = CSharpSyntaxTree.ParseText("using EasyEcs;\npublic static class A { static void F(World w) => w.ForEach(static (ref P p) => p.V++); }", Parse, path: "A.cs");
        var second = CSharpSyntaxTree.ParseText("using EasyEcs;\npublic static class B { static void F(World w) => w.ForEach(static (ref Q q) => q.V++); }", Parse, path: "B.cs");
        var compilation = Compile(Components).AddSyntaxTrees(first, second);
        var driver = Driver().RunGenerators(compilation);
        driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(first,
            CSharpSyntaxTree.ParseText(first.ToString().Replace("p.V++", "p.V += 2"), Parse, path: "A.cs")));

        var reasons = Steps(driver)["LambdaFiles"].SelectMany(s => s.Outputs).Select(o => o.Reason).ToArray();
        Assert.That(reasons.Count(r => r is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged), Is.EqualTo(1));
        Assert.That(reasons.Count(r => r is IncrementalStepRunReason.Modified or IncrementalStepRunReason.New), Is.EqualTo(1));
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

    internal static System.Collections.Generic.Dictionary<string, System.Collections.Generic.IEnumerable<IncrementalGeneratorRunStep>> Steps(GeneratorDriver driver) =>
        driver.GetRunResult().Results.SelectMany(r => r.TrackedSteps).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.SelectMany(p => p.Value));

    [Test]
    public void AMaybeOfAStructuralMethodIsLookedUpPerEntity()
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components,
            "[EcsSystem] public ref struct S { [ForEach] static void A(Entity e, ref P p, ReadOnlyMaybe<Q> q) { if (q.HasValue) e.Destroy(); } }"),
            out var output, out _);
        string generated = string.Join("\n", output.SyntaxTrees.Where(t => IsCode(t)).Select(t => t.ToString()));
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
        string generated = string.Join("\n", output.SyntaxTrees.Where(t => IsCode(t)).Select(t => t.ToString()));
        Assert.That(generated, Does.Contain("SumJob0 : global::EasyEcs.IForEachBatch<global::P>")
            .And.Contain("var l1 = x1;").And.Contain("var l2 = x2;").And.Contain("ref l1, ref l2")
            .And.Contain("x1 = l1;").And.Contain("x2 = l2;"));
        Assert.That(generated, Does.Contain("KillJob1 : global::EasyEcs.IForEachEntity<global::P>"));   // a structural method keeps the ref
    }

    [Test]
    public void GeneratedReadsKeepMethodsStable()
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, """
            [EcsSystem] public ref struct S
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
        string generated = string.Join("\n", output.SyntaxTrees.Where(t => IsCode(t)).Select(t => t.ToString()));
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
                     "HasLoot", "LinksLoot", "LinkLoot", "RemoveLoot", "LootIndex", "global::N.O5.Mark Mark", "SetMark(int" })
            Assert.That(generated, Does.Not.Contain(member), member);
        Assert.That(generated, Does.Contain("public bool HasMark"));                        // the top-level element keeps its own
    }

    [Test]
    public void ANamesakeNoOtherFileCanNameTakesNoMembers()
    {
        // A private or file-local namesake gets no members of its own: the nested type it shares a name with keeps them.
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, """
            namespace N
            {
                public class Tests { private struct Census : ISingletonComponent { public int Players; } }
                file struct Health { public int V; }
                public class Server
                {
                    public struct Census : ISingletonComponent { public int Players; }
                    public struct Health : IComponent { public int V; }
                    public static int Use(World w, Entity e) => w.Census.Players + e.Health.V;
                }
            }
            """), out var output, out var diagnostics);
        Assert.That(diagnostics, Is.Empty);
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()), Is.Empty);
    }

    [Test]
    public void ARelationNamedLikeAnEntityMemberKeepsItsOtherMembers()
    {
        // Entity.Id takes Id: LinksId is another name, still generated.
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, """
            public record struct Id(EntityId Target) : IRelation;
            public static class U { static DynamicBuffer<Id> r; public static bool Use(Entity e, Entity f) => e.LinkId(f).LinksId(f); }
            """), out var output, out var diagnostics);
        Assert.That(diagnostics, Is.Empty);
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()), Is.Empty);
        string generated = string.Join("\n", output.SyntaxTrees.Where(t => t.FilePath.EndsWith(".Buffer.g.cs")).Select(t => t.ToString()));
        Assert.That(generated, Does.Contain("public bool LinksId(global::EasyEcs.EntityId target)").And.Contain("public bool HasId"));
        Assert.That(generated, Does.Not.Contain("DynamicBuffer<global::Id> Id"));
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
                    if (e.SetChildOfIfChanged(parent) || e.IsRetired) n++;
                    var recipe = new Prefab().SetChildOf(parent);
                    return recipe.HasChildOf && recipe.ChildOf.Parent == parent.Id && e.RemoveChildOf().IsAlive ? n : -1;
                }
            }
            """), out var output, out var diagnostics);
        Assert.That(diagnostics, Is.Empty);
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()), Is.Empty);
        Assert.That(output.SyntaxTrees.Where(t => t.FilePath.Contains("ChildOf") || t.FilePath.Contains("Retired")), Is.Empty);

        // What the world refuses is not offered: ChildOf is never disabled, Retired is the world's to add and remove.
        var extensions = typeof(World).Assembly.GetExportedTypes().Where(t => t.Name is "ChildOfEcsExtensions" or "RetiredEcsExtensions"
            or "ChildOfEcsPrefabExtensions" or "RetiredEcsPrefabExtensions").ToArray();
        Assert.That(extensions.Select(t => t.Name), Is.EquivalentTo(new[] { "ChildOfEcsExtensions", "ChildOfEcsPrefabExtensions", "RetiredEcsExtensions" }));
        var names = extensions.SelectMany(t => t.GetMethods()).Select(m => m.Name).ToArray();
        Assert.That(names, Has.None.Contains("DisableChildOf").And.None.Contains("EnableChildOf").And.None.Contains("IsChildOfDisabled")
            .And.None.Contains("SetRetired").And.None.Contains("RemoveRetired"));
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
            public struct Q2 : IComponent, ITracked { public int V; }
            public record struct Loot(int Item);
            internal struct Note { public int V; }
            public enum Block : byte { Air, Stone }                          // an enum element: the same members
            public record struct Friend(EntityId Target) : IRelation;
            public static class U
            {
                public static Block Dig(Entity e)
                {
                    e.GetOrAddBlock().Add(Block.Stone);
                    var was = e.Block[0];
                    e.WriteBlock().AsSpan()[0] = Block.Air;
                    return e.HasBlock && e.RemoveBlock().IsAlive ? was : Block.Air;
                }
                public static Prefab Column() => new Prefab().SetBlock(Block.Stone, Block.Air);
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
                    e.LinkFriend(f).LinksFriend(f) ? w.FriendIndex.Count(f) + e.UnlinkFriend(f) : 0;
                static void Links(in DynamicBuffer<Friend> friends, in DynamicBuffer<Block> blocks) { }
                static void Notes(ref DynamicBuffer<Note> notes, in DynamicBuffer<int> ints, in DynamicBuffer<P> components) { }
            }
            """;
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Components, source), out var output, out var diagnostics);
        Assert.That(diagnostics, Is.Empty);
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
        string generated = string.Join("\n", output.SyntaxTrees.Where(t => t.FilePath.EndsWith(".Buffer.g.cs")).Select(t => t.ToString()));
        Assert.That(generated, Does.Contain("public ref readonly global::EasyEcs.DynamicBuffer<global::Game.Loot> Loot"));
        Assert.That(generated, Does.Contain("public ref global::EasyEcs.DynamicBuffer<global::Game.Block> WriteBlock()"));
        Assert.That(generated, Does.Contain("internal static class NoteEcsBufferExtensions").And.Contain("public ref global::EasyEcs.DynamicBuffer<global::Game.Note> WriteNote()"));
        Assert.That(generated, Does.Contain("public global::EasyEcs.Entity LinkFriend(global::EasyEcs.EntityId Target) => @this.Link(new global::Game.Friend(Target));")
            .And.Contain("public int UnlinkFriend(global::EasyEcs.EntityId target)").And.Contain("public bool LinksFriend(global::EasyEcs.EntityId target)")
            .And.Contain("public global::EasyEcs.RelationIndex<global::Game.Friend> FriendIndex"));
        Assert.That(output.SyntaxTrees.Count(t => t.FilePath.EndsWith(".Buffer.g.cs")), Is.EqualTo(4)); // Loot, Note, Block, Friend: not int, not a component
        Assert.That(generated, Does.Not.Contain("WriteFriend").And.Not.Contain("int RemoveFriend(")); // links: LinkFriend / UnlinkFriend
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

    /// <summary>Framework references of the running test host (enough for the generated code to compile).</summary>
    internal static class Basic
    {
        public static readonly ImmutableArray<MetadataReference> References = ((string)System.AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(System.IO.Path.PathSeparator)
            .Where(p => System.IO.Path.GetFileName(p).StartsWith("System.") || System.IO.Path.GetFileName(p) is "netstandard.dll" or "mscorlib.dll")
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToImmutableArray();
    }

    // The JIT inlines no method of more than 32 IL locals (MAX_INL_LCLS, "too many locals"), AggressiveInlining or not:
    // a job's Execute past it is called by the runner per run, and per 64-row word in a masked table, instead of being
    // part of its loop. The generator moves the loops of an Execute past it into methods of the job, one per width
    // (SystemGenerator.Loops.cs); the deinterleaved loads name each field once (a copy per field put Move3's masked job
    // at 36 locals).
    [Test]
    public void LargeJobsStayWithinTheInlinersLocals()
    {
        var compilation = Compile("""
            public struct P3 : IComponent { public float X, Y, Z; }
            public struct V3 : IComponent { public float X, Y, Z; }
            public struct P4 : IComponent { public float X, Y, Z, W; }
            public struct V4 : IComponent { public float X, Y, Z, W; }
            public struct Q4 : IComponent { public float X, Y, Z, W; }
            public struct R4 : IComponent { public float X, Y, Z, W; }
            public struct Mixed : IComponent { public int Count; public float Weight; }
            public struct Heat : IComponent { public float Value; }
            public struct Speed : IComponent { public float Value; }
            public struct Mass : IComponent { public float Value; }
            public struct Pull : IComponent { public float Value; }
            public struct Ticks : IComponent { public int Value; }
            public struct Den : IComponent { public int Value; }
            public struct Dead : IComponent { }
            public partial struct Tally : ISingletonComponent { [Sum] public int Count; [Sum] public int Sum; [Min] public int Low; [Max] public int High; }
            [EcsSystem] public ref struct Moves
            {
                [ForEach] static void Move3(ref P3 p, in V3 v) => p = new P3 { X = p.X + v.X, Y = p.Y + v.Y, Z = p.Z + v.Z };
                [ForEach] static void Move4(ref P4 p, in V4 v) { p.X += v.X; p.Y += v.Y; p.Z += v.Z; p.W += v.W; }
                [ForEach] static void Weigh(ref Mixed m) => m.Weight += m.Count;
                [ForEach] static void Blend(ref P4 p, ref V4 v, in Q4 q, in R4 r) // 16 fields per width: past 32 locals
                {
                    p.X += v.Y * q.Z - r.W; p.Y += v.Z * q.W - r.X; p.Z += v.W * q.X - r.Y; p.W += v.X * q.Y - r.Z;
                    v.X = p.W; v.Y = p.Z; v.Z = p.Y; v.W = p.X;
                }
                [ForEach] static void Seek(ref P3 p, ref V3 v)
                {
                    float dx = 1 - p.X, dy = 2 - p.Y, dz = 3 - p.Z;
                    float scale = 4 / System.MathF.Max(System.MathF.Sqrt(dx * dx + dy * dy + dz * dz), 1e-6f);
                    v.X = dx * scale; v.Y = dy * scale; v.Z = dz * scale;
                    p.X += v.X; p.Y += v.Y; p.Z += v.Z;
                }
            }
            [EcsSystem] public ref struct Heats
            {
                [ForEach] static void Step(ref Heat heat, ref Speed speed, in Mass mass, in Pull pull)
                {
                    float force = heat.Value * 0.5f - mass.Value * pull.Value;
                    speed.Value = System.MathF.Max(System.MathF.Min(speed.Value + force - 0.1f, 4f), -4f);
                    if (heat.Value > 100f) heat.Value = 100f;
                    else heat.Value += speed.Value * 0.125f;
                }
                [ForEach] static void Cycle(ref Heat heat, in Mass mass, ref Reduce<Tally> tally)
                {
                    if (heat.Value > 50f) { heat.Value -= 100f + mass.Value; tally.Value.Count++; }
                    else heat.Value += 30f;
                }
                [ForEach] static void Gate(ref Heat heat, in Speed speed, in Mass mass)
                {
                    bool fast = speed.Value > 2 || speed.Value < -2;
                    bool heavy = mass.Value > 2.5f;
                    if (fast && !heavy) heat.Value = System.Math.Clamp(heat.Value, -5f, 5f);
                    else if (!fast || heat.Value is > 50 and < 100) heat.Value = System.MathF.Max(heat.Value - 1, -50);
                }
                [ForEach] static void Share(ref Ticks ticks, in Den den) => ticks.Value = ticks.Value / den.Value + ticks.Value % den.Value * 1000 + 7919;
                [ForEach, Without<Dead>] static void Die(Entity entity, in Heat heat) { if (heat.Value > 1000f) entity.Set(new Dead()); }
                [ForEach(Parallel = true)] static void StepParallel(ref Heat heat, ref Speed speed, in Mass mass, in Pull pull)
                {
                    float force = heat.Value * 0.5f - mass.Value * pull.Value;
                    speed.Value = System.MathF.Max(System.MathF.Min(speed.Value + force - 0.1f, 4f), -4f);
                    if (heat.Value > 100f) heat.Value = 100f;
                    else heat.Value += speed.Value * 0.125f;
                }
            }
            """).WithOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
                optimizationLevel: OptimizationLevel.Release));
        Driver().RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        using var image = new System.IO.MemoryStream();
        var emitted = output.Emit(image);
        Assert.That(emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()), Is.Empty);
        image.Position = 0;
        using var pe = new System.Reflection.PortableExecutable.PEReader(image);
        var locals = InlinedLocals(pe);
        // each method's whole and masked forms (Blend: also its loops, split out of Execute)
        Assert.That(locals.Count(l => l.Method.StartsWith("Move") && l.Method.Contains("::Execute")), Is.EqualTo(4));
        Assert.That(locals.Any(l => l.Method.StartsWith("Blend") && l.Method.Contains("::__MaskedLoop512")), "Blend's masked Execute is split");
        Assert.That(locals.Where(l => l.Locals > 32), Is.Empty);
    }

    // The same over every job this assembly's generators wrote: the corpora of KernelTests, KernelLookupTests,
    // FusionTests, LambdaPassTests, StructuralLoopTests, HelperLoopTests, ReduceTests, ViewTests and the battle server.
    [Test]
    public void GeneratedJobsOfTheTestsStayWithinTheInlinersLocals()
    {
        using var file = System.IO.File.OpenRead(typeof(GeneratorTests).Assembly.Location);
        using var pe = new System.Reflection.PortableExecutable.PEReader(file);
        var locals = InlinedLocals(pe);
        Assert.That(locals.Count, Is.GreaterThan(500));
        Assert.That(locals.Where(l => l.Locals > 32), Is.Empty);
    }

    /// <summary>
    /// The IL locals of every AggressiveInlining method of the generated jobs in <paramref name="pe"/> (the types of
    /// the generated <c>__ForEach</c> classes): what a runner inlines.
    /// </summary>
    private static System.Collections.Generic.List<(string Method, int Locals)> InlinedLocals(System.Reflection.PortableExecutable.PEReader pe)
    {
        var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        var locals = new System.Collections.Generic.List<(string Method, int Locals)>();
        foreach (var handle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(handle);
            var owner = type.GetDeclaringType();
            if (owner.IsNil || !metadata.GetString(metadata.GetTypeDefinition(owner).Name).EndsWith("__ForEach")) continue;
            foreach (var m in type.GetMethods())
            {
                var method = metadata.GetMethodDefinition(m);
                if ((method.ImplAttributes & System.Reflection.MethodImplAttributes.AggressiveInlining) == 0 || method.RelativeVirtualAddress == 0) continue;
                var body = System.Reflection.Metadata.PEReaderExtensions.GetMethodBody(pe, method.RelativeVirtualAddress);
                int count = 0;
                if (!body.LocalSignature.IsNil)
                {
                    var signature = metadata.GetBlobReader(metadata.GetStandaloneSignature(body.LocalSignature).Signature);
                    signature.ReadSignatureHeader();
                    count = signature.ReadCompressedInteger();
                }
                locals.Add(($"{metadata.GetString(type.Name)}::{metadata.GetString(method.Name)}", count));
            }
        }
        return locals;
    }
}
