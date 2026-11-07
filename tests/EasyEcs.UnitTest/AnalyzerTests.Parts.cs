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

// EasyEcsAnalyzer.Parts.cs: generated parts and the world setup a type declares (EECS062-069, EECS102).
public partial class AnalyzerTests
{
    [TestCase("public partial struct Z { [GroupBy] public int Id; }")]                                                               // not a component
    [TestCase("public partial record struct Z([GroupBy] int Id) : ISingletonComponent;")]                                    // not a component
    [TestCase("[WhenTargetDestroyed(TargetPolicy.Remove)] public record struct W(int Id) : IComponent;")]                   // no link
    [TestCase("[WhenTargetDestroyed(TargetPolicy.Remove)] public partial record struct W([LookupKey] int Id) : IComponent;")]     // not an EntityId key
    [TestCase("[NotSaved] public record struct N(object O);")]                                                              // no component
    [TestCase("[Accumulate] public record struct A(int N) : IComponent;")]                                                  // not a singleton
    [TestCase("[Accumulate] public record struct A(int N) : ISingletonComponent;")]                                         // does not combine
    public async Task AWorldSetupOnATypeItDoesNotFitIsReported(string source)
    {
        Assert.That((await Analyze([source])).Where(d => d.Id.StartsWith("EECS") || d.Severity == DiagnosticSeverity.Error).Select(d => d.Id).Distinct(),
            Is.EqualTo(new[] { "EECS068" }));
        Driver().RunGeneratorsAndUpdateCompilation(Compile(source), out var output, out _);
        var saved = output.SyntaxTrees.SingleOrDefault(t => t.FilePath.EndsWith("EasyEcs.TypeRegistry.g.cs"))?.ToString() ?? "";
        Assert.That(saved, Does.Not.Contain("GroupBy<").And.Not.Contain("WhenTargetDestroyed<").And.Not.Contain("NotSaved<"));
    }

    [TestCase("public record struct S(object O) : IComponent;", DiagnosticSeverity.Info)]
    [TestCase("public record struct S(string Name) : ISingletonComponent; public static class U { public static void F(World w, System.Buffers.IBufferWriter<byte> o) => w.Save(o); }", DiagnosticSeverity.Warning)]
    [TestCase("public record struct S(object O) : IComponent; public static class U { public static void F(World w, System.Buffers.IBufferWriter<byte> o) => w.SaveEntities(o); }", DiagnosticSeverity.Warning)]
    [TestCase("[NotSaved] public record struct S(object O) : IComponent; public static class U { public static void F(World w, System.Buffers.IBufferWriter<byte> o) => w.Save(o); }", null)]
    [TestCase("namespace Nino.Core { public sealed class NinoTypeAttribute : System.Attribute; } [Nino.Core.NinoType] public partial record struct S(string Name) : IComponent; public static class U { public static void F(World w, System.Buffers.IBufferWriter<byte> o) => w.Save(o); }", null)]
    [TestCase("public record struct S(int N) : IComponent; public static class U { public static void F(World w, System.Buffers.IBufferWriter<byte> o) => w.Save(o); }", null)] // unmanaged: raw rows
    [TestCase("public record struct S(object O); public static class U { public static void F(World w, System.Buffers.IBufferWriter<byte> o) => w.Save(o); }", null)]           // not a component
    [TestCase("public record struct S(object O) : IComponent; public static class U { public static void F(World w, System.Buffers.IBufferWriter<byte> o) => w.SaveWithout(o, ComponentSet.Create().With<S>()); }", null)] // left out by the code
    [TestCase("public record struct S(string Name) : ISingletonComponent; public static class U { static readonly ComponentSet Keep = ComponentSet.Create().WithSingleton<S>(); }", null)]  // named in a set
    [TestCase("public record struct S(string Name) : ISingletonComponent; public static class U { public static void F(World w, System.Buffers.IBufferWriter<byte> o) => w.SaveEntities(o); }", DiagnosticSeverity.Info)] // entity saves write no singletons
    public async Task AManagedTypeASaveCannotWriteIsReported(string source, DiagnosticSeverity? severity)
    {
        var found = (await Analyze([source])).Where(d => d.Id == "EECS069").ToArray();
        Assert.That(found.Select(d => (DiagnosticSeverity?)d.Severity), Is.EqualTo(severity == null ? [] : new[] { severity }));
    }

    private const string TileKey = "public record struct Tp(int X, int Z); public record struct Tq(int X, int Y, int Z); ";

    private const string Steps = "static Tp ITile<Tp>.Beside(Tp k, TileFaces f) => f switch { TileFaces.North => k with { Z = k.Z - 1 }, TileFaces.South => k with { Z = k.Z + 1 }, "
                                 + "TileFaces.West => k with { X = k.X - 1 }, TileFaces.East => k with { X = k.X + 1 }, _ => k };";

    // EECS125: a tile's hand-written shape (on a type whose key an attribute declares) and a Beside the generator infers.
    [TestCase("public partial record struct T([LookupKey] Tp Pos) : IComponent, ITile<Tp> { static int ITileShape.Width => 16; static int ITileShape.Depth => 8; " + Steps + " }",
        "its shape and Beside", "Width = 16, Depth = 8", "1")]
    [TestCase("public partial record struct T([LookupKey] Tp Pos) : IComponent, ITile<Tp> { static int ITileShape.Width => 4; static int ITileShape.Depth => 4; static int ITileShape.Halo => 2; static Tp ITile<Tp>.Beside(Tp k, TileFaces f) => new((k.X + 1) % 4, k.Z); }",
        "its shape", "Width = 4, Depth = 4, Halo = 2", "")]                                                                  // a torus keeps Beside
    [TestCase("public partial record struct T(Tp Pos) : IComponent, ITile<Tp> { readonly Tp IIndexed<Tp>.Key => Pos; static int ITileShape.Width => 4; static int ITileShape.Depth => 4; " + Steps + " }",
        "Beside", "", "1")]                                                                                                 // a key written by hand: Beside alone
    [TestCase("[Tile(Width = 4, Depth = 4)] public partial record struct T([LookupKey] Tp Pos) : IComponent { " + Steps + " }",
        "Beside", "", "1")]
    [TestCase("public partial record struct T([LookupKey] int Pos) : IComponent, ITile<int> { static int ITileShape.Width => 4; static int ITileShape.Depth => 1; static TileFaces ITileShape.Faces => TileFaces.West | TileFaces.East; static int ITile<int>.Beside(int k, TileFaces f) => f == TileFaces.West ? k - 1 : k + 1; }",
        "its shape and Beside", "Width = 4, Depth = 1, Faces = TileFaces.West | TileFaces.East", "1")]
    public async Task AHandWrittenTileIsReported(string source, string what, string arguments, string beside)
    {
        var found = (await GeneratorTests.Analyze([TileKey + source])).Where(d => d.Id == "EECS125").ToArray();
        Assert.That(found, Has.Length.EqualTo(1));
        Assert.That(found[0].Severity, Is.EqualTo(DiagnosticSeverity.Info));
        Assert.That(found[0].GetMessage(), Does.StartWith($"'T' writes {what} by hand: "));
        Assert.That((found[0].Properties["Tile"], found[0].Properties["Beside"]), Is.EqualTo((arguments, beside)));
    }

    [TestCase("public partial record struct T(Tp Pos) : IComponent, ITile<Tp> { readonly Tp IIndexed<Tp>.Key => Pos; static int ITileShape.Width => 4; static int ITileShape.Depth => 4; static Tp ITile<Tp>.Beside(Tp k, TileFaces f) => k; }")]   // a Beside of its own
    [TestCase("public partial record struct T(Tp Pos) : IComponent, ITile<Tp> { readonly Tp IIndexed<Tp>.Key => Pos; static int ITileShape.Width => 4; static int ITileShape.Depth => 4; static Tp ITile<Tp>.Beside(Tp k, TileFaces f) => f switch { TileFaces.North => k with { Z = k.Z + 1 }, _ => k with { X = k.X + 1 } }; }")] // another direction
    [TestCase("public partial record struct T([LookupKey] Tp Pos) : IComponent, ITile<Tp> { static int ITileShape.Width => System.Environment.ProcessorCount; static int ITileShape.Depth => 4; static Tp ITile<Tp>.Beside(Tp k, TileFaces f) => k; }")] // not a constant
    [TestCase("[Tile(Width = 4, Depth = 4)] public partial record struct T([LookupKey] Tp Pos) : IComponent;")]
    public async Task OtherTilesAreLeftAlone(string source) =>
        Assert.That((await GeneratorTests.Analyze([TileKey + source])).Where(d => d.Id is "EECS124" or "EECS125" || d.Severity == DiagnosticSeverity.Error), Is.Empty);

    // EECS127: a window SlotsPerAxis declares, written by hand.
    [TestCase("public partial record struct C([UniqueKey] Tp Pos) : IComponent, IDirectMapped<Tp> { public static int Slots => 4096; public static int SlotOf(Tp p) => (p.Z & 63) * 64 + (p.X & 63); }", "64")]
    [TestCase("public partial record struct C([UniqueKey] int Cell) : IComponent, IDirectMapped<int> { static int IDirectMapped<int>.Slots => 16; static int IDirectMapped<int>.SlotOf(int key) => key & 15; }", "16")]
    [TestCase("public partial record struct C([UniqueKey] Tq Pos) : IComponent, IDirectMapped<Tq> { public static int Slots => 8; public static int SlotOf(Tq k) => ((k.Y & 1) * 2 + (k.Z & 1)) * 2 + (k.X & 1); }", "2")]
    public async Task AHandWrittenWindowIsReported(string source, string side)
    {
        var found = (await GeneratorTests.Analyze([TileKey + source])).Where(d => d.Id == "EECS127").ToArray();
        Assert.That(found.Select(d => d.Properties["Side"]), Is.EqualTo(new[] { side }));
        Assert.That(found[0].GetMessage(), Is.EqualTo($"'C' maps its keys onto a window of {side} per axis by hand: [UniqueKey(SlotsPerAxis = {side})] declares Slots and SlotOf"));
    }

    [TestCase("public partial record struct C([UniqueKey] Tp Pos) : IComponent, IDirectMapped<Tp> { public static int Slots => 4096; public static int SlotOf(Tp p) => (p.X & 63) * 64 + (p.Z & 63); }")]   // z fastest
    [TestCase("public partial record struct C([UniqueKey] Tp Pos) : IComponent, IDirectMapped<Tp> { public static int Slots => 4096; public static int SlotOf(Tp p) => (p.Z & 63) * 64 + (p.X & 31); }")]   // another mask
    [TestCase("public partial record struct C([UniqueKey] Tp Pos) : IComponent, IDirectMapped<Tp> { public static int Slots => 4000; public static int SlotOf(Tp p) => (p.Z & 63) * 64 + (p.X & 63); }")]   // not the slots
    [TestCase("public record struct C(Tp Pos) : IComponent, IDirectMapped<Tp> { readonly Tp IIndexed<Tp>.Key => Pos; public static int Slots => 4096; public static int SlotOf(Tp p) => (p.Z & 63) * 64 + (p.X & 63); }")] // no [UniqueKey]
    public async Task OtherWindowsAreLeftAlone(string source) =>
        Assert.That((await GeneratorTests.Analyze([TileKey + source])).Where(d => d.Id is "EECS126" or "EECS127"), Is.Empty);

    [TestCase("[Tile(Width = 4, Depth = 4, Halo = 5)] public partial record struct T([LookupKey] Tp Pos) : IComponent;")]      // past the tile beside
    [TestCase("[Tile(Width = 4, Depth = 65)] public partial record struct T([LookupKey] Tp Pos) : IComponent;")]                // a layer of 65 rows
    public async Task ATileAttributesShapeIsChecked(string source) =>
        Assert.That((await GeneratorTests.Analyze([TileKey + source])).Where(d => d.Id.StartsWith("EECS")).Select(d => d.Id).Distinct(), Is.EqualTo(new[] { "EECS072" }));

    [Test]
    public async Task AnAccumulatedKeptRowFitsItsSetup() =>
        Assert.That((await GeneratorTests.Analyze(["[Accumulate] public partial record struct Rec(EntityId Who, [MinBy] int Score) : ISingletonComponent;"]))
            .Where(d => d.Id.StartsWith("EECS") || d.Severity == DiagnosticSeverity.Error), Is.Empty);
}
