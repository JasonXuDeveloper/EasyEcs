using System.Threading.Tasks;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

// EasyEcsAnalyzer.Tiles.cs: a tile step the generator cannot write (EECS115), a second buffer and its commit written by
// hand (EECS116), a relaxation stepped one cell per pass (EECS117); and the tile edges of EECS059 and EECS073.
public partial class AnalyzerTests
{
    internal const string TileParts = """
        public record struct G(int Pos) : IComponent, ITile<int> { int IIndexed<int>.Key => Pos; static int ITileShape.Width => 4; static int ITileShape.Depth => 4; static int ITile<int>.Beside(int k, TileFaces f) => k + 1; }
        public record struct Spot(int X, string Name);
        public record struct N(Spot Pos) : IComponent, ITile<Spot> { Spot IIndexed<Spot>.Key => Pos; static int ITileShape.Width => 4; static int ITileShape.Depth => 4; static Spot ITile<Spot>.Beside(Spot k, TileFaces f) => k; }
        public record struct NB(byte V);
        """;

    private static Task ReportsOnTiles(string id, string source) => AssertReports(id, GeneratorTests.Components, TileParts, source);

    [TestCase("[EcsSystem] public ref struct S { [ForEach, GaussSeidel] static void A(Tile<byte, G> t, Mut<DynamicBuffer<int>> b) => b.Write().SetCount(t.Cells.Length); }")]      // nothing to sweep
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref Tile<byte, G> t, ref Tile<int, G> u) { } }")]                                                           // two steps
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Slices = 2)] static void A(ref Tile<byte, G> t) { } }")]                                                                    // part of the grid
    [TestCase("[EcsSystem] public ref struct S { [ForEach, Consume] static void A(ref Tile<byte, G> t) { } }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach, GaussSeidel(MaxSweeps = -1)] static void A(ref Tile<byte, G> t) { } }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach, GaussSeidel] static void A(ref Tile<byte, N> t) { } }")]                                                                  // a key with a string
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(Tile<byte, G> t, Mut<DynamicBuffer<int>> b) { t.ExchangeHalo(); foreach (var row in t.Rows()) t.Write(row).Clear(); b.Write(); } }")] // Write by value
    public Task TileStepsTheGeneratorCannotWriteReport(string source) => ReportsOnTiles("EECS115", source);

    [Test]
    public async Task AStepOfOneEntityReports()
    {
        string source = "public static class R { [ForEntity] public static void A(ref Tile<byte, G> t) { } }";
        Assert.That(await Count("EECS115", GeneratorTests.Components, TileParts, source), Is.EqualTo(1));
    }

    [TestCase("[EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(ref Tile<byte, G> t) { t.ExchangeHalo(); foreach (var row in t.Rows()) t.Write(row).Clear(); } }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Parallel = true), GaussSeidel(MaxSweeps = 4)] static void A(ref Tile<byte, G> t) { t.ExchangeHalo(); foreach (var row in t.Rows()) t.Write(row).Clear(); } }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref Tile<byte, Columns<G>> t, Tile<int, G> blocks) { blocks.ExchangeHalo(); t.ExchangeHalo(); foreach (var row in t.Rows()) t.Write(row)[0] = (byte)blocks[0, 0, row.Z]; } }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref Tile<byte, G> t) => Step(t); static void Step(Tile<byte, G> t) { t.ExchangeHalo(); foreach (var row in t.Rows()) t.Write(row).Clear(); } }")] // a helper's copy of the stepped tile
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref Tile<byte, G> t, Lookup<DynamicBuffer<byte>> cells, in G g) { if (cells.Has(default)) t.ExchangeHalo(); } }")] // Jacobi: the cells stay as they were
    public Task TileStepsAreClean(string source) => AssertClean(GeneratorTests.Components, TileParts, source);

    [Test]
    public Task AGaussSeidelStepWithALookupOfItsCellsReports() => ReportsOnTiles("EECS059",
        "[EcsSystem] public ref struct S { [ForEach, GaussSeidel] static void A(ref Tile<byte, G> t, Lookup<DynamicBuffer<byte>> cells) { if (cells.Has(default)) t.ExchangeHalo(); } }");

    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref Tile<byte, G> t, Tile<byte, G> u) { } }")]                         // the step and a read of its cells
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(Tile<byte, G> t, Tile<byte, Columns<G>> u, Mut<DynamicBuffer<int>> b) => b.Write(); }")] // one buffer, two shapes
    public Task TwoTilesOfOneBufferReport(string source) => ReportsOnTiles("EECS073", source);

    private const string Stepper = "[EcsSystem] public ref struct Step { [ForEach(Parallel = true)] static void A(Tile<byte, G> t, Mut<DynamicBuffer<NB>> next) { t.ExchangeHalo(); var into = next.Write().AsSpan(); foreach (var row in t.Rows()) into[row.Index] = new NB(t[0, 0, row.Z]); } }";

    [Test]
    public Task ACommitOfATilesStepReports() => ReportsOnTiles("EECS116", Stepper
        + " [EcsSystem] public ref struct Commit { [ForEach] static void A(in DynamicBuffer<NB> next, Mut<DynamicBuffer<byte>> cells) => next.AsReadOnlySpan().CopyTo(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, NB>(cells.Write().AsSpan())); }");

    [TestCase(" [EcsSystem] public ref struct Commit { [ForEach] static void A(in DynamicBuffer<NB> next, Mut<DynamicBuffer<byte>> cells, ref P p) { next.AsReadOnlySpan().CopyTo(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, NB>(cells.Write().AsSpan())); p.V++; } }")] // does more than copy
    [TestCase(" [EcsSystem] public ref struct Commit { [ForEach] static void A(in DynamicBuffer<NB> next, Mut<DynamicBuffer<int>> cells) => next.AsReadOnlySpan().CopyTo(System.Runtime.InteropServices.MemoryMarshal.Cast<int, NB>(cells.Write().AsSpan())); }")] // into buffers no tile steps
    public Task CopiesThatAreNoCommitOfAStepAreClean(string rest) => AssertClean(GeneratorTests.Components, TileParts, Stepper + rest);

    [Test]
    public Task ARelaxationSteppedOneCellPerPassReports() => ReportsOnTiles("EECS117",
        "[EcsSystem] public ref struct S { [ForEach] static void A(ref Tile<byte, G> t) { t.ExchangeHalo(); foreach (var row in t.Rows()) { var c = t.At(row); var into = t.Write(row); for (int x = 0; x < 4; x++) into[x] = (byte)System.Math.Min(c[x, 0, 0], c[x - 1, 0, 0] + 1); } } }");

    [Test]
    public Task ARelaxationThatSweepsIsClean() => AssertClean(GeneratorTests.Components, TileParts,
        "[EcsSystem] public ref struct S { [ForEach, GaussSeidel] static void A(ref Tile<byte, G> t) { t.ExchangeHalo(); foreach (var row in t.Rows()) { var c = t.At(row); var into = t.Write(row); for (int x = 0; x < 4; x++) into[x] = (byte)System.Math.Min(c[x, 0, 0], c[x - 1, 0, 0] + 1); } } }");

    [Test]
    public async Task OneBadStepIsReportedOnce()
    {
        string source = "[EcsSystem] public ref struct S { [ForEach, GaussSeidel] static void A(ref Tile<byte, G> t, ref Tile<int, G> u) { } }";
        Assert.That(await Count("EECS115", GeneratorTests.Components, TileParts, source), Is.EqualTo(1));
    }
}
