using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>A miner indexed by the blocks it dug ([LookupKey] on a field).</summary>
public partial struct Miner : IComponent
{
    [LookupKey] public int BlocksDug;
}

/// <summary>A subscriber, one entity per id ([UniqueKey] on a record parameter).</summary>
public partial record struct Subscriber([UniqueKey] int Id) : IComponent;

/// <summary>A plot indexed by its whole value ([LookupKey] on the type).</summary>
[LookupKey]
public readonly partial record struct Plot(int X, int Y) : IComponent;

public enum Biome { Plains, Desert, Snow }

/// <summary>The biome an entity's rows are grouped by ([Group] on an enum).</summary>
public partial record struct Terrain([Group] Biome Biome) : IComponent;

/// <summary>
/// PartGenerator and the rules of the declarations it writes parts for: the part's place (nested, generic, every kind
/// of containing type), EECS062-EECS066, and the caching of its stage.
/// </summary>
public class PartTests
{
    /// <summary>The generated part files and the compilation with them.</summary>
    private static (string Parts, Compilation Output) Generate(string source)
    {
        GeneratorTests.Driver().RunGeneratorsAndUpdateCompilation(GeneratorTests.Compile(source), out var output, out _);
        return (string.Join("\n", output.SyntaxTrees.Where(t => t.FilePath.EndsWith(".Part.g.cs")).Select(t => t.ToString())), output);
    }

    private static string[] Errors(Compilation output) =>
        output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()).ToArray();

    private static async Task<string[]> Ids(string source) =>
        (await GeneratorTests.Analyze([source])).Where(d => d.Id.StartsWith("EECS") && d.Id != "EECS069" || d.Severity == DiagnosticSeverity.Error)
        .Select(d => d.Id).Distinct().OrderBy(x => x).ToArray();

    [TestCase("public partial struct Census : ISingletonComponent { [Sum] public int Alive; }",
        "public partial struct Census : global::EasyEcs.IReduction<Census>")]
    [TestCase("namespace Game.Rules { public static partial class Outer { internal partial struct Census : ISingletonComponent { [Sum] public int Alive; } } }",
        "namespace Game.Rules;\n\npublic partial class Outer\n{\n    internal partial struct Census : global::EasyEcs.IReduction<Census>")]
    [TestCase("public partial class Outer<T> where T : struct { public partial record struct Inner<U> : ISingletonComponent { [Sum] public int Alive; } }",
        "public partial class Outer<T>\n{\n    public partial record struct Inner<U> : global::EasyEcs.IReduction<Inner<U>>")]
    [TestCase("public partial record Outer { private protected partial class Mid { protected internal readonly partial struct Census { [Sum] public int Alive { get; init; } } } }",
        "public partial record Outer\n{\n    private protected partial class Mid\n    {\n        protected internal readonly partial struct Census : global::EasyEcs.IReduction<Census>")]
    [TestCase("public partial interface IHolder { public partial struct Census : ISingletonComponent { [Sum] public int Alive; } }",
        "public partial interface IHolder\n{\n    public partial struct Census")]
    [TestCase("public partial record struct Census([Sum] int Alive, [Max] float Oldest) : ISingletonComponent;",
        "public static Census Combine(Census left, Census right) => new Census { Alive = left.Alive + right.Alive, Oldest = global::System.Math.Max(left.Oldest, right.Oldest) };")]
    [TestCase("[Sum] public partial record struct Stats(long Kills, int Deaths, [Max] long Longest) : ISingletonComponent;",   // the struct's attribute: the default
        "public static Stats Combine(Stats left, Stats right) => new Stats { Kills = left.Kills + right.Kills, Deaths = left.Deaths + right.Deaths, Longest = global::System.Math.Max(left.Longest, right.Longest) };")]
    [TestCase("[Accumulate, Min] public partial struct Low : ISingletonComponent { public int A; public static int Shared; }",
        "public static Low Identity => new Low { A = int.MaxValue };")]
    public void PartsGoInsideEveryContainingType(string source, string expected)
    {
        var (parts, output) = Generate(source);
        Assert.That(parts.Replace("\r", ""), Does.Contain(expected));
        Assert.That(Errors(output), Is.Empty, parts);
    }

    [Test]
    public void EveryAttributeHasItsIdentityAndCombine()
    {
        var (parts, output) = Generate("""
            public partial struct Totals : ISingletonComponent
            {
                [Sum] public int Count;
                [Product] public double Odds;
                [Min] public float Lowest;
                [Max] public long Highest;
                [Min] public byte Least;
                [Sum] public short Small;
                [All] public bool Every;
                [Any] public bool Some;
                [Max] public decimal Price;
            }
            """);
        Assert.That(Errors(output), Is.Empty, parts);
        Assert.That(parts, Does.Contain("public static Totals Identity => new Totals { Count = 0, Odds = 1, Lowest = float.PositiveInfinity, Highest = long.MinValue, "
                                        + "Least = byte.MaxValue, Small = 0, Every = true, Some = false, Price = decimal.MinValue };"));
        Assert.That(parts, Does.Contain("Count = left.Count + right.Count, Odds = left.Odds * right.Odds, Lowest = global::System.Math.Min(left.Lowest, right.Lowest), "
                                        + "Highest = global::System.Math.Max(left.Highest, right.Highest), Least = global::System.Math.Min(left.Least, right.Least), "
                                        + "Small = (short)(left.Small + right.Small), Every = left.Every & right.Every, Some = left.Some | right.Some, "
                                        + "Price = global::System.Math.Max(left.Price, right.Price) };"));
        Assert.That(parts, Does.Contain("[global::System.CodeDom.Compiler.GeneratedCode(\"EasyEcs.Generator\""));
    }

    // EECS062 at each level of the chain, and for a file type; EECS063 and EECS064.
    [TestCase("public struct Census : ISingletonComponent { [Sum] public int Alive; }", "EECS062")]
    [TestCase("public class Outer { public partial struct Census : ISingletonComponent { [Sum] public int Alive; } }", "EECS062")]
    [TestCase("public class Outer<T> { public partial class Mid { public partial struct Census : ISingletonComponent { [Sum] public int Alive; } } }", "EECS062")]
    [TestCase("public partial class Outer { public class Mid { public partial struct Census : ISingletonComponent { [Sum] public int Alive; } } }", "EECS062")]
    [TestCase("file partial struct Census : ISingletonComponent { [Sum] public int Alive; }", "EECS062")]
    [TestCase("public partial struct Census : ISingletonComponent { [Sum] public int Alive; public static Census Identity => default; }", "EECS063")]
    [TestCase("public partial struct Census : ISingletonComponent { [Sum] public int Alive; public static Census Combine(Census a, Census b) => a; }", "EECS063")]
    [TestCase("public partial struct Census : ISingletonComponent { [Sum] public int Alive; public int Dead; }", "EECS064")]               // no attribute
    [TestCase("public partial struct Census : ISingletonComponent { [Sum] public bool Alive; }", "EECS064")]                            // a bool summed
    [TestCase("public partial struct Census : ISingletonComponent { [Any] public int Alive; }", "EECS064")]                             // an int or-ed
    [TestCase("public partial struct Census : ISingletonComponent { [Sum, Max] public int Alive; }", "EECS064")]                        // two
    [TestCase("public partial struct Census : ISingletonComponent { [Sum] public readonly int Alive; }", "EECS064")]                     // not settable
    [TestCase("public partial struct Census : ISingletonComponent { [Sum] public int Alive { get; } }", "EECS064")]                      // no setter
    [TestCase("public partial struct Census : ISingletonComponent { public int A; [Sum] public int Alive => A; }", "EECS064")]          // computed
    [TestCase("public partial struct Census : ISingletonComponent { [Sum] public string Name; }", "EECS064")]
    [TestCase("[Sum, Max] public partial struct Census : ISingletonComponent { public int Alive; }", "EECS064")]                 // two on the struct
    [TestCase("[Sum] public partial struct Census : ISingletonComponent { public int Alive; public string Name; }", "EECS064")]    // a field it cannot sum
    public async Task DeclarationsThePartCannotHoldAreReported(string source, string id)
    {
        Assert.That(await Ids(source), Is.EqualTo(new[] { id }), "one diagnostic, and no error of the generated code");
        Assert.That(Generate(source).Parts, Is.Empty, "the generator skips what the analyzer reports");
    }

    [TestCase("public partial struct Census : ISingletonComponent { [Sum] public int Alive; [Min] public float Lowest; }")]
    [TestCase("public partial record struct Census([Sum] int Alive, [property: Max] long Oldest) : ISingletonComponent;")]
    [TestCase("public partial struct Census : ISingletonComponent { [Sum] public int Alive { get; set; } public const int Cap = 4; public static int Shared; }")]
    [TestCase("public partial struct Census : ISingletonComponent, IReduction<Census> { [Sum] public int Alive; }")]  // the interface written too
    [TestCase("[Accumulate, Sum] public partial struct Census : ISingletonComponent { public int Alive; [Max] public float Oldest; }")]
    public async Task ValidDeclarationsCompileWithoutDiagnostics(string source) =>
        Assert.That(await Ids(source), Is.Empty);

    // A hand-written reduction the attributes declare (EECS065), and one whose Identity does not fit (EECS066).
    [TestCase("public struct C : ISingletonComponent, IReduction<C> { public int N; public float L; public static C Identity => new C { L = float.MaxValue }; public static C Combine(C a, C b) => new C { N = a.N + b.N, L = System.Math.Min(a.L, b.L) }; }", "EECS065")]
    [TestCase("public struct C : ISingletonComponent, IReduction<C> { public int N; public static C Identity => default; public static C Combine(C a, C b) { a.N += b.N; return a; } }", "EECS065")]
    [TestCase("public struct C : ISingletonComponent, IReduction<C> { public float H; public static C Identity { get; } = new() { H = float.NegativeInfinity }; public static C Combine(C a, C b) => new() { H = a.H > b.H ? a.H : b.H }; }", "EECS065")]
    [TestCase("public struct C : ISingletonComponent, IReduction<C> { public bool Any; public static C Identity => new C(); public static C Combine(C a, C b) => new C { Any = a.Any || b.Any }; }", "EECS065")]
    [TestCase("public struct C : ISingletonComponent, IReduction<C> { public float L; public static C Identity => default; public static C Combine(C a, C b) => new C { L = System.MathF.Min(a.L, b.L) }; }", "EECS066")]
    [TestCase("public struct C : ISingletonComponent, IReduction<C> { public int Lo, Hi; public static C Identity => new C { Lo = int.MaxValue }; public static C Combine(C a, C b) => new C { Lo = System.Math.Min(a.Lo, b.Lo), Hi = System.Math.Max(a.Hi, b.Hi) }; }", "EECS066")]
    [TestCase("public struct C : ISingletonComponent, IReduction<C> { public int N; public static C Identity => new C { N = 1 }; public static C Combine(C a, C b) => new C { N = a.N + b.N }; }", "EECS066")]
    [TestCase("public struct C : ISingletonComponent, IReduction<C> { public int N, First; public static C Identity => new C { N = 5, First = -1 }; public static C Combine(C a, C b) => new C { N = a.N * b.N, First = a.First < 0 ? b.First : a.First }; }", "EECS066")] // the other field is the user's
    public async Task HandWrittenReductionsAreRead(string source, string id) =>
        Assert.That(await Ids(source), Is.EqualTo(new[] { id }));

    [TestCase("public struct C : ISingletonComponent, IReduction<C> { public int N, First; public static C Identity => new C { First = -1 }; public static C Combine(C a, C b) => new C { N = a.N + b.N, First = a.First < 0 ? b.First : a.First }; }")] // a first value: not field-wise
    [TestCase("public struct C : ISingletonComponent, IReduction<C> { public int N; static readonly C Start = new C { N = 9 }; public static C Identity => Start; public static C Combine(C a, C b) => new C { N = a.N + b.N }; }")]  // not a constant
    [TestCase("public struct C : ISingletonComponent, IReduction<C> { public int N; public static C Identity => default; public static C Combine(C a, C b) => new C { N = a.N - b.N }; }")]   // no attribute says it
    [TestCase("public struct C : ISingletonComponent, IReduction<C> { public float L; public static C Identity => new C { L = float.MaxValue }; public static C Combine(C a, C b) => new C { L = System.Math.Min(a.L, b.L) }; }", "EECS065")] // MaxValue is an identity of a float minimum
    public async Task OtherReductionsAreLeftAlone(string source, string only = null) =>
        Assert.That(await Ids(source), only == null ? Is.Empty : Is.EqualTo(new[] { only }));

    [TestCase("public partial struct M : IComponent { [LookupKey] public int N; }",
        "public partial struct M : global::EasyEcs.IIndexed<int>", "readonly int global::EasyEcs.IIndexed<int>.Key => N;")]
    [TestCase("public partial record struct A([UniqueKey] string Name) : IComponent;",
        "public partial record struct A : global::EasyEcs.IUniqueIndexed<string>", "string global::EasyEcs.IIndexed<string>.Key => Name;")]
    [TestCase("[LookupKey] public readonly partial record struct T(int X, int Y) : IComponent;",
        "public readonly partial record struct T : global::EasyEcs.IIndexed<T>", "T global::EasyEcs.IIndexed<T>.Key => this;")]
    [TestCase("public partial struct M : IComponent { [LookupKey] public int N { get; set; } public int Computed => N * 2; }",
        "public partial struct M : global::EasyEcs.IIndexed<int>", "int global::EasyEcs.IIndexed<int>.Key => N;")]
    [TestCase("public partial struct M : IComponent { int n; [LookupKey] public int Doubled => n * 2; }",
        "public partial struct M : global::EasyEcs.IIndexed<int>", "int global::EasyEcs.IIndexed<int>.Key => Doubled;")]  // a computed member, named
    [TestCase("public partial struct R : IComponent, IIndexed<EntityId> { [LookupKey] public EntityId Target; }",
        "public partial struct R\n", "EntityId global::EasyEcs.IIndexed<global::EasyEcs.EntityId>.Key")]                        // the user's interface brings it
    [TestCase("public partial struct U : IComponent, IIndexed<int> { [UniqueKey] public int N; }",
        "public partial struct U : global::EasyEcs.IUniqueIndexed<int>", "IIndexed<int>.Key => N;")]                              // the part makes it unique
    [TestCase("public partial struct D : IComponent, IDirectMapped<int> { [LookupKey] public int Cell; public static int Slots => 8; public static int SlotOf(int key) => key; }",
        "public partial struct D\n", "IIndexed<int>.Key => Cell;")]                                                               // IDirectMapped keeps Slots and SlotOf
    [TestCase("public partial record struct Z([Group] int Zone) : IComponent;",
        "public partial record struct Z : global::EasyEcs.IGroup", "int global::EasyEcs.IGroup.Group => Zone;")]
    [TestCase("public enum B { P, D } public partial struct Z : IComponent { [Group] public B Biome; }",
        "public partial struct Z : global::EasyEcs.IGroup", "int global::EasyEcs.IGroup.Group => (int)Biome;")]
    [TestCase("public partial struct Z : IComponent, IGroup { [Group] public int Zone; }",
        "public partial struct Z\n", "int global::EasyEcs.IGroup.Group => Zone;")]
    [TestCase("public partial struct Z : IComponent { [LookupKey] public int Player; [Group] public int Zone; }",
        "public partial struct Z : global::EasyEcs.IIndexed<int>, global::EasyEcs.IGroup", "IGroup.Group => Zone;")]             // both
    public void KeysAndGroupsAreExplicitImplementations(string source, string head, string member)
    {
        var (parts, output) = Generate(source);
        Assert.That(parts.Replace("\r", ""), Does.Contain(head).And.Contain(member));
        Assert.That(Errors(output), Is.Empty, parts);
    }

    [TestCase("public partial record struct W(float X, float Y) : IComponent { [OrderBy(nameof(X), nameof(Y))] public readonly uint Cell => (uint)X; }",
        "public partial record struct W : global::EasyEcs.IOrderKey<uint>",
        "readonly global::System.Numerics.Vector3 global::EasyEcs.IOrderKey<uint>.BoxPoint => new global::System.Numerics.Vector3(X, Y, 0f);")]
    [TestCase("public partial struct S : IComponent { public System.Numerics.Vector3 At; [OrderBy(nameof(At))] public readonly ulong Cell => 0; }",
        "public partial struct S : global::EasyEcs.IOrderKey<ulong>", "global::System.Numerics.Vector3 global::EasyEcs.IOrderKey<ulong>.BoxPoint => At;")]
    [TestCase("public partial struct S : IComponent { public System.Numerics.Vector2 At; [OrderBy(nameof(At))] public int Key; }",
        "IOrderKey<int>.OrderKey => Key;", "BoxPoint => new global::System.Numerics.Vector3(At, 0f);")]               // a Vector2; a field key: not readonly
    [TestCase("public enum T : short { A } public partial record struct R([OrderBy] T Tier) : IComponent;",
        "public partial record struct R : global::EasyEcs.IOrderKey<global::T>", "IOrderKey<global::T>.BoxPoint => default;")]  // no box
    public void AnOrderIsAnExplicitImplementation(string source, string head, string member)
    {
        var (parts, output) = Generate(source);
        Assert.That(parts.Replace("\r", ""), Does.Contain(head).And.Contain(member));
        Assert.That(Errors(output), Is.Empty, parts);
    }

    [TestCase("public partial struct O : IComponent { [OrderBy] public int A; [OrderBy] public int B; }")]                                  // two keys
    [TestCase("public partial struct O : ISingletonComponent { [OrderBy] public int A; }")]                                               // a singleton
    [TestCase("public partial struct O : IComponent { [OrderBy] public string A; }")]                                                    // not a radix key
    [TestCase("public partial struct O : IComponent { [OrderBy] public decimal A; }")]                                                   // decimal: no bits that order
    [TestCase("public partial struct O : IComponent { public int X; [OrderBy(nameof(X))] public int A; }")]                               // an int box member
    [TestCase("public partial struct O : IComponent { [OrderBy(\"Nope\")] public int A; }")]                                             // no such member
    [TestCase("public partial struct O : IComponent { public float X; public System.Numerics.Vector2 V; [OrderBy(nameof(X), nameof(V))] public int A; }")]  // a vector beside others
    [TestCase("public partial struct O : IComponent { public float X, Y, Z, W; [OrderBy(nameof(X), nameof(Y), nameof(Z), nameof(W))] public int A; }")] // 4 axes
    [TestCase("public partial struct O : IComponent { [OrderBy(Interval = 0)] public int A; }")]                                         // interval
    [TestCase("public partial struct O : IComponent { [OrderBy(Margin = -1)] public int A; }")]                                          // margin
    [TestCase("public partial struct O : IComponent { [OrderBy(Margin = float.PositiveInfinity)] public int A; }")]
    [TestCase("public partial struct O : IComponent, IOrderKey<int> { [OrderBy] public int A; readonly int IOrderKey<int>.OrderKey => A; readonly System.Numerics.Vector3 IOrderKey<int>.BoxPoint => default; }")] // written by hand
    public async Task OrdersThePartCannotKeepAreReported(string source)
    {
        Assert.That(await Ids(source), Is.EqualTo(new[] { "EECS104" }), "one diagnostic, and no error of the generated code");
        Assert.That(Generate(source).Parts, Is.Empty, "the generator skips what the analyzer reports");
    }

    [TestCase("public partial struct M : IComponent { [LookupKey] public int A; [LookupKey] public int B; }")]                         // two keys
    [TestCase("[LookupKey] public partial struct M : IComponent { [UniqueKey] public int A; }")]                                 // the type and a member
    [TestCase("public partial struct M : IComponent { [LookupKey] public object A; }")]                                         // not IEquatable
    [TestCase("public partial struct M : IComponent { [UniqueKey] public EntityId Target; }")]                            // a unique relation
    [TestCase("public partial struct M : IComponent, IIndexed<long> { [LookupKey] public int A; long IIndexed<long>.Key => A; }")] // disagrees with the interface
    [TestCase("public partial struct M : IComponent, IIndexed<int> { [LookupKey] public int A; public int Key => A; }")]        // Key written by hand
    [TestCase("public partial struct Z : IComponent { [Group] public int A; [Group] public int B; }")]                     // two groups
    [TestCase("public partial struct Z : IComponent { [Group] public long A; }")]                                         // not an int
    [TestCase("public enum E : byte { A } public partial struct Z : IComponent { [Group] public E A; }")]                 // an enum of byte
    [TestCase("public partial struct Z : IComponent, IGroup { [Group] public int A; public int Group => A; }")]           // Group written by hand
    public async Task KeysAndGroupsThePartCannotHoldAreReported(string source)
    {
        Assert.That(await Ids(source), Is.EqualTo(new[] { "EECS036" }), "one diagnostic, and no error of the generated code");
        Assert.That(Generate(source).Parts, Is.Empty, "the generator skips what the analyzer reports");
    }

    [TestCase("public struct M : IComponent, IIndexed<int> { public int N; public int Key => N; }", "EECS067")]
    [TestCase("public struct M : IComponent, IIndexed<int> { public int N; readonly int IIndexed<int>.Key => this.N; }", "EECS067")]
    [TestCase("public struct M : IComponent, IIndexed<int> { int n; public int N => n; public int Key { get => N; } }", "EECS067")]   // a computed member, named
    [TestCase("public record struct A(string Name) : IComponent, IUniqueIndexed<string> { readonly string IIndexed<string>.Key => Name; }", "EECS067")]
    [TestCase("public readonly record struct T(int X) : IComponent, IIndexed<T> { T IIndexed<T>.Key => this; }", "EECS067")]
    [TestCase("public readonly record struct T(int X) : IComponent, IIndexed<T> { public T Key => this; }", "EECS043")]                 // EECS043's
    [TestCase("public struct M : IComponent, IIndexed<int> { public int N; public int Key => N * 2; }")]                                // computed
    [TestCase("public struct M : IComponent, IIndexed<long> { public int N; public long Key => N; }")]                                  // converted
    [TestCase("public struct M : IComponent, IIndexed<int>, IIndexed<string> { public int N; public string S; int IIndexed<int>.Key => N; string IIndexed<string>.Key => S; }", "EECS036")]  // two keys: EECS036's
    [TestCase("public struct M : IComponent, IIndexed<int> { public static int Shared; public int Key => Shared; }")]                    // not the value's
    [TestCase("file struct M : IComponent, IIndexed<int> { public int N; public int Key => N; }")]                                     // no part for a file type
    public async Task AKeyTheAttributesCanDeclareIsReported(string source, string id = null) =>
        Assert.That(await Ids(source), id == null ? Is.Empty : Is.EqualTo(new[] { id }));

    [TestCase("public struct Z : IComponent, IGroup { public int Zone; public int Group => Zone; }", "EECS067")]
    [TestCase("public struct Z : IComponent, IGroup { public int Zone; readonly int IGroup.Group => this.Zone; }", "EECS067")]
    [TestCase("public struct Z : IComponent, IGroup { int zone; public int Zone => zone; public int Group { get => Zone; } }", "EECS067")]  // a computed member, named
    [TestCase("public enum E { A, B } public record struct Z(E Biome) : IComponent, IGroup { readonly int IGroup.Group => (int)Biome; }", "EECS067")]
    [TestCase("public struct Z : IComponent, IGroup { public int X, Y; public int Group => X * 16 + Y; }")]                 // computed from several fields
    [TestCase("public struct Z : IComponent, IGroup { public short Level; public int Group => Level; }")]                   // converted: [Group] takes an int
    [TestCase("public struct Z : IComponent, IGroup { public long Id; public int Group => (int)Id; }")]                     // a cast of a long
    [TestCase("public enum E : byte { A } public struct Z : IComponent, IGroup { public E B; public int Group => (int)B; }")]   // an enum of byte
    [TestCase("public enum E { A } public struct Z : IComponent, IGroup { public E B; public int Group => (short)B; }")]   // not a cast to int
    [TestCase("public struct Z : IComponent, IGroup { public static int Shared; public int Group => Shared; }")]            // not the value's
    [TestCase("file struct Z : IComponent, IGroup { public int Zone; public int Group => Zone; }")]                         // no part for a file type
    [TestCase("public struct Z : IComponent, IGroup { public int Zone; public int Group { get { return Zone; } } }")]       // a block body stays
    public async Task AGroupTheAttributeCanDeclareIsReported(string source, string id = null)
    {
        var ids = await Ids(source);
        Assert.That(ids, id == null ? Is.Empty : Is.EqualTo(new[] { id }));
        if (id != null)
            Assert.That((await GeneratorTests.Analyze([source])).Single(d => d.Id == id).GetMessage(), Does.Contain(".Group' returns").And.Contain("[Group]"));
    }

    [Test]
    public void KeysDeclaredByAttributesIndexTheirEntities()
    {
        using var world = new World();
        var a = world.CreateEntity(new Miner { BlocksDug = 3 }, new Subscriber(1), new Plot(1, 2));
        var b = world.CreateEntity(new Miner { BlocksDug = 3 }, new Subscriber(2), new Plot(2, 1));
        Assert.That(world.Index<Miner, int>().Count(3), Is.EqualTo(2));
        Assert.That((world.SubscriberIndex.Get(1), world.SubscriberIndex.Get(2)), Is.EqualTo((a, b)));
        Assert.That(world.PlotIndex.TryFind(new Plot(2, 1), out var found) && found == b, Is.True);

        b.Set(new Miner { BlocksDug = 4 });                                          // a write moves the entity to its new key
        Assert.That((world.Index<Miner, int>().Count(3), world.Index<Miner, int>().Count(4)), Is.EqualTo((1, 1)));
        Assert.That(() => world.CreateEntity(new Subscriber(1)), Throws.InvalidOperationException);  // unique
    }

    [Test]
    public void AGroupDeclaredByAttributesSplitsTables()
    {
        using var world = new World();
        world.GroupBy<Terrain>();
        world.CreateEntity(new Terrain(Biome.Desert), new Health(1));
        world.CreateEntity(new Terrain(Biome.Snow), new Health(10));
        world.CreateEntity(new Terrain(Biome.Desert), new Health(100));
        Assert.That(world.Count<Health>(QueryFilter.Create().InGroup<Terrain>((int)Biome.Desert)), Is.EqualTo(2));
        Assert.That(world.Count<Health>(QueryFilter.Create().InGroup<Terrain>((int)Biome.Snow)), Is.EqualTo(1));
    }

    public struct Dug : IComponent { public int Total; }

    /// <summary>
    /// A vector loop over a component with an [LookupKey] part: the generated part declares no field, so the field order is
    /// defined (with SimdMode.Require, a method left scalar would not compile: EECS056).
    /// </summary>
    [EcsSystem]
    public ref struct AddDug
    {
        [ForEach(SimdMode = SimdMode.Require)]
        static void Add(in Miner miner, ref Dug dug) => dug.Total += miner.BlocksDug;
    }

    [Test]
    public void AKeyedComponentKeepsItsVectorLoop()
    {
        using var world = new World();
        for (int i = 0; i < 100; i++) world.CreateEntity(new Miner { BlocksDug = i }, new Dug { Total = 1 });
        world.AddSystem<AddDug>();
        world.Update();
        int sum = 0;
        foreach (var item in world.Query<Miner, Dug>()) sum += item.Dug.Total - item.Miner.BlocksDug;
        Assert.That(sum, Is.EqualTo(100));
        Assert.That(world.Index<Miner, int>().Count(7), Is.EqualTo(1));
    }

    /// <summary>A unique index declared by [UniqueKey], found by key from a parallel pass (the system's access and lookup see the key).</summary>
    [EcsSystem]
    public ref struct FindSubscriber
    {
        [ForEach(Parallel = true, ChunkSize = 64)]
        static void Find(in LookupTests.Wants wants, ref LookupTests.Found found, Lookup<Subscriber> accounts) =>
            found.Id = accounts.TryFind(wants.Login, out var entity) ? entity : default;
    }

    [Test]
    public void ALookupFindsAnAttributeKey()
    {
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4 });
        var accounts = new Entity[300];
        for (int i = 0; i < accounts.Length; i++) accounts[i] = world.CreateEntity(new Subscriber(i));
        var seekers = new Entity[2000];
        for (int i = 0; i < seekers.Length; i++) seekers[i] = world.CreateEntity(new LookupTests.Wants(i % 400), new LookupTests.Found());
        world.AddSystem<FindSubscriber>();
        world.Update();
        for (int i = 0; i < seekers.Length; i++)
            Assert.That(seekers[i].Read<LookupTests.Found>().Id, Is.EqualTo(i % 400 < 300 ? accounts[i % 400].Id : default(EntityId)));
    }

    [Test]
    public void AChangedDeclarationWritesItsPartAlone()
    {
        var first = CSharpSyntaxTree.ParseText("using EasyEcs;\npublic partial struct A : ISingletonComponent { [Sum] public int N; }", GeneratorTests.Parse);
        var second = CSharpSyntaxTree.ParseText("using EasyEcs;\npublic partial struct B : ISingletonComponent { [Sum] public int N; }", GeneratorTests.Parse);
        var compilation = GeneratorTests.Compile().AddSyntaxTrees(first, second);
        var driver = GeneratorTests.Driver().RunGenerators(compilation);
        driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(second,
            CSharpSyntaxTree.ParseText("using EasyEcs;\npublic partial struct B : ISingletonComponent { [Max] public int N; }", GeneratorTests.Parse)));

        var reasons = GeneratorTests.Steps(driver)["Parts"].SelectMany(s => s.Outputs).Select(o => o.Reason).ToArray();
        Assert.That(reasons.Count(r => r is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged), Is.EqualTo(1)); // A
        Assert.That(reasons.Count(r => r is IncrementalStepRunReason.Modified or IncrementalStepRunReason.New), Is.EqualTo(1));  // B
    }
}
