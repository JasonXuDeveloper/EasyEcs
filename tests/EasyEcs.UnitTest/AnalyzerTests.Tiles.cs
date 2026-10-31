using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

// EasyEcsAnalyzer.Tiles.cs: a tile step the generator cannot write (EECS115), a second buffer and its commit written by
// hand (EECS116), a relaxation stepped one cell per pass (EECS117), a load the generator cannot prove (EECS120), a buffer
// no method writes copied over a tile's cells (EECS121); and the tile edges of EECS059 and EECS073.
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
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref Tile<byte, G> t) { t.ExchangeHalo(); t.Restart(); foreach (var row in t.Rows()) t.Write(row).Clear(); } }")]   // Restart in a Jacobi step
    [TestCase("[EcsSystem] public ref struct S { [ForEach, GaussSeidel] static void A(ref Tile<byte, G> t, Tile<int, G> u) { u.ExchangeHalo(); u.Restart(); t.ExchangeHalo(); foreach (var row in t.Rows()) t.Write(row).Clear(); } }")] // Restart by value
    [TestCase("[EcsSystem] public ref struct S { [ForEach, GaussSeidel] static void A(ref Tile<byte, G> t, Tile<int, G> u, ref P p) { p.V = u.Sweep; t.ExchangeHalo(); } }")] // Sweep by value
    public Task TileStepsTheGeneratorCannotWriteReport(string source) => ReportsOnTiles("EECS115", source);

    [Test]
    public async Task AStepOfOneEntityReports()
    {
        string source = "public static class R { [EntityMethod] public static void A(ref Tile<byte, G> t) { } }";
        Assert.That(await Count("EECS115", GeneratorTests.Components, TileParts, source), Is.EqualTo(1));
    }

    [TestCase("[EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(ref Tile<byte, G> t) { t.ExchangeHalo(); foreach (var row in t.Rows()) t.Write(row).Clear(); } }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Parallel = true), GaussSeidel(MaxSweeps = 4)] static void A(ref Tile<byte, G> t) { t.ExchangeHalo(); foreach (var row in t.Rows()) t.Write(row).Clear(); } }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Parallel = true), GaussSeidel] static void A(ref Tile<byte, G> t) { t.ExchangeHalo(); t.Restart(); foreach (var row in t.Rows()) t.Write(row).Clear(); } }")] // a restarted sweep
    [TestCase("[EcsSystem] public ref struct S { [ForEach, GaussSeidel] static void A(ref Tile<byte, G> t) { t.ExchangeHalo(); for (int k = 0; k < 4; k++) t.Write(new TileRow<G>(0, (t.Sweep & 1) == 0 ? k : 3 - k)).Clear(); } }")] // sweeps in turns of direction
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

    private static string Loading(string loop) =>
        "[EcsSystem] public ref struct S { [ForEach] static void A(Tile<byte, G> t, ref P p) { t.ExchangeHalo(); foreach (var row in t.Rows()) { var c = t.At(row); " + loop + " } } }";

    [TestCase("for (int x = 0; x < 4; x += 4) { p.V += c.Load128<byte>(x).ToScalar(); x += 0; }", "writes 'x'")]                             // written in the body
    [TestCase("for (int x = 0; x < 4; x++) { ref int y = ref x; p.V += c.Load128<byte>(x).ToScalar(); }", "takes a reference")]          // a reference to it
    [TestCase("for (int x = 0; x < p.V; x += 4) p.V += c.Load128<byte>(x).ToScalar();", "condition")]                                       // a bound it cannot read
    [TestCase("for (int x = 0; 4 > x; x += 4) p.V += c.Load128<byte>(x).ToScalar();", "condition")]                                         // the bound first
    [TestCase("for (int x = 0; x < 4; x += p.V) p.V += c.Load128<byte>(x).ToScalar();", "positive constant")]                              // a step that is not a constant
    [TestCase("for (int x = 3; x >= 0; x--) p.V += c.Load128<byte>(x - 3).ToScalar();", "condition")]                                      // walked backwards
    [TestCase("for (int x = p.V; x < 4; x += 4) p.V += c.Load128<byte>(x).ToScalar();", "starts from a constant")]                          // a start that is not a constant
    [TestCase("int x = row.Z; p.V += c.Load128<byte>(x).ToScalar();", "not declared by a for loop")]                                         // no loop
    [TestCase("int x = 0; while (x < 4) { p.V += c.Load128<byte>(x).ToScalar(); x += 4; }", "not declared by a for loop")]                   // a while loop
    [TestCase("for (int x = 0; x < 4; x += 4) p.V += c.Load128<byte>(x * 2).ToScalar();", "not the variable of a for loop")]                // a column that is not x + k
    [TestCase("for (int x = 0; x < 4; x += 4) { int L(int k) => k; p.V += c.Load256<byte>(L(x)).ToScalar(); }", "not the variable")]          // a call around the column
    public async Task ATileLoadTheGeneratorCannotFollowReports(string loop, string reason)
    {
        var found = (await GeneratorTests.Analyze([GeneratorTests.Components, TileParts, Loading(loop)])).Where(d => d.Id == "EECS120").ToArray();
        Assert.That(found, Has.Length.EqualTo(1));
        Assert.That(found[0].Severity, Is.EqualTo(Microsoft.CodeAnalysis.DiagnosticSeverity.Info));
        Assert.That(found[0].GetMessage(), Does.Contain(reason));
    }

    [TestCase("for (int x = 0; x < 4; x += 4) p.V += c.Load128<byte>(x).ToScalar();")]                                     // proven: the interceptor's
    [TestCase("for (int x = 0; x < 4; x += 4) p.V += c.Load128<byte>(1 + x).ToScalar();")]
    [TestCase("for (int x = 0; x <= 3; ++x) p.V += c.Load128<byte>(x - 1).ToScalar();")]
    [TestCase("p.V += c.Load128<byte>(1).ToScalar() + c.Load128<byte>().ToScalar() + c.Load128<byte>(dy: 0).ToScalar();")] // constants: the JIT checks them
    [TestCase("const int k = 2; p.V += c.Load128<byte>(k).ToScalar();")]
    [TestCase("foreach (var col in c.Columns(4)) p.V += col.Load128<byte>(-1).ToScalar();")]                              // a column walk
    [TestCase("int x = row.Z; p.V += c[x, 0, 0];")]                                                                        // the indexer: not a load
    public async Task TileLoadsTheGeneratorFollowsAreClean(string loop) =>
        Assert.That((await GeneratorTests.Analyze([GeneratorTests.Components, TileParts, Loading(loop)])).Where(d => d.Id == "EECS120"), Is.Empty);

    // A cursor is a ref struct, so a lambda or local function takes the tile as a parameter and captures only the column.
    [TestCase("int L(Tile<byte, G> u) => u.At(new TileRow<G>(0, 0)).Load128<byte>(x).ToScalar(); p.V += L(t);", "local function")]
    [TestCase("Ld f = u => u.At(new TileRow<G>(0, 0)).Load128<byte>(x).ToScalar(); p.V += f(t);", "lambda")]
    public async Task ALoadInAFunctionInsideTheLoopReports(string body, string reason)
    {
        string source = "public delegate int Ld(Tile<byte, G> u); [EcsSystem] public ref struct S { [ForEach] static void A(Tile<byte, G> t, ref P p) { t.ExchangeHalo(); for (int x = 0; x < 4; x += 4) { " + body + " } } }";
        var found = (await GeneratorTests.Analyze([GeneratorTests.Components, TileParts, source])).Where(d => d.Id == "EECS120").ToArray();
        Assert.That(found, Has.Length.EqualTo(1));
        Assert.That(found[0].GetMessage(), Does.Contain(reason));
    }

    // EECS121: what a commit leaves once its step takes the tile by ref (EECS116's fix when it cannot delete the commit,
    // or a step rewritten by hand): a copy of a buffer no method writes over the cells of a tile.
    private const string SteppedByRef = "[EcsSystem] public ref struct Step { [ForEach] static void A(ref Tile<byte, G> t) { t.ExchangeHalo(); foreach (var row in t.Rows()) t.Write(row).Clear(); } }";
    private const string Commit = " [EcsSystem] public ref struct Commit { [ForEach] static void A(in DynamicBuffer<NB> next, Mut<DynamicBuffer<byte>> cells) => next.AsReadOnlySpan().CopyTo(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, NB>(cells.Write().AsSpan())); }";

    [TestCase(Commit)]                                                                                                          // the plain commit
    [TestCase(Commit + " public static class Made { public static void Size(Entity e) { e.GetOrAdd<DynamicBuffer<NB>>().SetCount(16); e.Get<DynamicBuffer<NB>>().Clear(); } }")] // sized when made: no write
    [TestCase(" [EcsSystem] public ref struct Commit { [ForEach] static void A(in DynamicBuffer<NB> next, Mut<DynamicBuffer<byte>> cells, ref P p) { if (p.V > 0) next.AsReadOnlySpan().CopyTo(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, NB>(cells.Write().AsSpan())); p.V++; } }")] // more than the copy
    [TestCase(" [EcsSystem] public ref struct Commit { [ForEach] static void A(in DynamicBuffer<NB> next, ref DynamicBuffer<byte> cells) => next.AsReadOnlySpan().CopyTo(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, NB>(cells.AsSpan())); }")] // into a ref buffer
    public Task ACopyOfABufferNoMethodWritesOverATilesCellsReports(string commit) => ReportsOnTiles("EECS121", SteppedByRef + commit);

    [TestCase(" [EcsSystem] public ref struct Fill { [ForEach] static void A(Mut<DynamicBuffer<NB>> next) => next.Write().AsSpan().Fill(new NB(1)); }")]   // a pass writes it
    [TestCase(" public static class Fill { public static void Set(Entity e) => e.Get<DynamicBuffer<NB>>().Add(new NB(2)); }")]                             // code outside a pass writes it
    [TestCase(" public static class Fill { public static void Set(Entity e) => e.Get<DynamicBuffer<NB>>().SetCount(4)[0] = new NB(3); }")]                 // the span SetCount hands out
    [TestCase(" public static class Fill { public static void Set(ref DynamicBuffer<NB> b) { } }")]                                                         // a method that may write it
    public Task ACopyOfABufferSomeMethodWritesIsClean(string writer) => AssertClean(GeneratorTests.Components, TileParts, SteppedByRef + Commit + writer);

    [TestCase(" [EcsSystem] public ref struct Commit { [ForEach] static void A(in DynamicBuffer<NB> next, Mut<DynamicBuffer<int>> cells) => next.AsReadOnlySpan().CopyTo(System.Runtime.InteropServices.MemoryMarshal.Cast<int, NB>(cells.Write().AsSpan())); }")] // no tile of int cells
    [TestCase(" [EcsSystem] public ref struct Commit { [ForEach] static void A(in DynamicBuffer<NB> next, Mut<DynamicBuffer<byte>> cells) { } }")]           // no copy
    public Task OtherCopiesAreClean(string commit) => AssertClean(GeneratorTests.Components, TileParts, SteppedByRef + commit);

    [Test]
    public async Task AHandWrittenCommitIsReportedOnce()
    {
        // The step writes NB by hand: EECS116, never EECS121 as well.
        Assert.That(await Count("EECS116", GeneratorTests.Components, TileParts, Stepper + Commit), Is.EqualTo(1));
        Assert.That(await Count("EECS121", GeneratorTests.Components, TileParts, Stepper + Commit), Is.Zero);
    }

    [Test]
    public async Task OneBadStepIsReportedOnce()
    {
        string source = "[EcsSystem] public ref struct S { [ForEach, GaussSeidel] static void A(ref Tile<byte, G> t, ref Tile<int, G> u) { } }";
        Assert.That(await Count("EECS115", GeneratorTests.Components, TileParts, source), Is.EqualTo(1));
    }
}
