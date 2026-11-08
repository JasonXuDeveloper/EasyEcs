using System.Linq;
using Microsoft.CodeAnalysis;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

// EasyEcsAnalyzer.Pairs.cs: a pair method that cannot run per pair (EECS105), one whose cutoff test the generator
// cannot read (EECS106), and a neighbor search written by hand (EECS107), each at the edges PairForm branches on.
public partial class AnalyzerTests
{
    internal const string PairParts = """
        public record struct Spot(float X, float Y) : IComponent;
        public record struct Drift(float X, float Y) : IComponent;
        public record struct Shove(float X, float Y) : IComponent;
        public record struct Sight(float Range) : IComponent;
        public record struct Near(int Value) : IComponent;
        public record struct Reach(float Value) : ISingletonComponent;
        public record struct At3(System.Numerics.Vector3 V) : IComponent;
        public struct Quarry : IComponent;
        public record struct Seen(int V) : IComponent, ITracked;
        [Sum] public partial struct Tally : ISingletonComponent { public int N; }
        """;

    // [EcsSystem] ref struct S { ... } around each method
    private static string InSystem(string method) => "[EcsSystem] public ref struct S { " + method + " }";

    [TestCase("[ForEach, With<Near>] static void A(Entity e, [Neighbor] in Spot q, in Reach r) { if (q.X * q.X >= r.Value) return; }")]                                                    // no component of the row's
    [TestCase("[ForEach] static void A(ref Spot p, [Neighbor] in Spot q) { float dx = p.X - q.X; if (dx * dx >= 1) return; p.X += 1; }")]                        // writes what the neighbor reads
    [TestCase("[ForEach, Consume] static void A(in Spot p, ref Near n, [Neighbor] in Spot q) { float dx = p.X - q.X; if (dx * dx >= 1) return; n.Value++; }")]
    [TestCase("[ForEach, Changed<Seen>] static void A(in Spot p, in Seen s, ref Near n, [Neighbor] in Spot q) { float dx = p.X - q.X; if (dx * dx >= 1) return; n.Value++; }")]
    [TestCase("[ForEach] static void A(in Spot p, ref Near n, [Neighbor] ref Spot q) { float dx = p.X - q.X; if (dx * dx >= 1) return; q.X += 1; }")]   // writes of the neighbor what the row reads
    [TestCase("[ForEach] static void A(in Spot p, in Near n, [Neighbor] in Spot q, [Neighbor] ref Near m) { float dx = p.X - q.X; if (dx * dx >= 1) return; m.Value++; }")] // the row's Near, read
    // Both sides written: the neighbors are the rows, every row ref written of both sides, no Mut<T>.
    [TestCase("[ForEach] static void A(in Spot p, ref Near n, [Neighbor] in Spot q, [Neighbor] ref Near m, [Neighbor] in Drift d) { float dx = p.X - q.X; if (dx * dx >= 1) return; m.Value++; n.Value++; }")]
    [TestCase("[ForEach] static void A(in Spot p, ref Near n, ref Drift w, [Neighbor] in Spot q, [Neighbor] ref Near m) { float dx = p.X - q.X; if (dx * dx >= 1) return; m.Value++; w.X++; }")]
    [TestCase("[ForEach] static void A(in Spot p, Mut<Seen> s, ref Near n, [Neighbor] in Spot q, [Neighbor] ref Near m) { float dx = p.X - q.X; if (dx * dx >= 1) return; m.Value++; s.Write().V++; }")]
    [TestCase("[ForEach] static void A(in Spot p, ref Near n, [Neighbor] in Spot q, [Neighbor] in Drift d, [Neighbor] ref Drift e) { float dx = p.X - q.X; if (dx * dx >= 1) return; e.X = d.X; }")] // twice
    [TestCase("[ForEach(Parallel = true)] static void A(in Spot p, ref Near n, [Neighbor] in Spot q, [Neighbor] ref Drift d) { float dx = p.X - q.X; if (dx * dx >= 1) return; d.X++; }")]
    [TestCase("[ForEach(Parallel = true)] static void A(in Spot p, Mut<Seen> s, [Neighbor] in Spot q) { float dx = p.X - q.X; if (dx * dx >= 1) return; s.Write().V++; }")] // a Mut<T> in parallel
    public Task BadPairMethodsReport(string method) => AssertReports("EECS105", GeneratorTests.Components, PairParts, InSystem(method));

    [TestCase("in Near n", "[Neighbor] ref Near m", "m.Value++;",
        "Pair method 'A' writes the neighbor's Near ('m'), which it reads of its row: what a row sees would depend on the order of the pairs; to write both sides of each pair once, take it by 'ref' of the row too")]
    [TestCase("ref Near n", "[Neighbor] ref Near m, [Neighbor] in Drift d", "m.Value++; n.Value++;",
        "Pair method 'A' writes both sides of its pairs (Near), so its neighbors are its rows: take Drift of the row too, or drop 'd'")]
    [TestCase("ref Near n, ref Drift w", "[Neighbor] ref Near m", "m.Value++; w.X++;",
        "Pair method 'A' writes both sides of its pairs (Near) and Drift of its row only ('w'): each pair runs once, so the row would see half of its pairs; write it of both sides ([Neighbor] ref Drift) or read it ('in')")]
    public async Task BothSidesWrittenSayWhy(string row, string neighbor, string body, string message)
    {
        var found = await GeneratorTests.Analyze([GeneratorTests.Components, PairParts,
            InSystem($"[ForEach] static void A(in Spot p, {row}, [Neighbor] in Spot q, {neighbor}) {{ float dx = p.X - q.X; if (dx * dx >= 1) return; {body} }}")]);
        Assert.That(found.Single(d => d.Id == "EECS105").GetMessage(), Is.EqualTo(message));
    }

    // A method writing both sides runs its pairs one at a time (a vector loop's sums into the neighbor lost to them):
    // EECS056 says so under SimdMode.Require; EECS055 leaves pair methods alone.
    [Test]
    public async Task BothSidesWrittenRunOnePairAtATime()
    {
        var found = await GeneratorTests.Analyze([GeneratorTests.Components, PairParts,
            InSystem("[ForEach(SimdMode = SimdMode.Require)] static void A(in Spot p, ref Near n, [Neighbor] in Spot q, [Neighbor] ref Near m) { float dx = p.X - q.X; if (dx * dx >= 1) return; n.Value++; m.Value++; }")]);
        Assert.That(found.Single(d => d.Id == "EECS056").GetMessage(),
            Does.Contain("it writes both sides of its pairs ('m', [Neighbor] ref): each unordered pair runs once, one at a time"));
    }

    [Test]
    public async Task AParallelMutOfThePairsRowSaysWhy()
    {
        var found = await GeneratorTests.Analyze([GeneratorTests.Components, PairParts,
            InSystem("[ForEach(Parallel = true)] static void A(in Spot p, Mut<Seen> s, [Neighbor] in Spot q) { float dx = p.X - q.X; if (dx * dx >= 1) return; s.Write().V++; }")]);
        Assert.That(found.Single(d => d.Id == "EECS105").GetMessage(), Is.EqualTo(
            "Pair method 'A' runs in parallel and takes 's' as a Mut<T>: its stamps per write would share the change ticks of the chunks' rows; take it by 'ref' (every row the pass runs for is stamped) or drop Parallel"));
    }

    // A neighbor's Entity by ref is no parameter a [ForEach] method takes.
    [Test]
    public Task ANeighborEntityByRefIsNoParameter() => AssertReports("EECS002", GeneratorTests.Components, PairParts,
        InSystem("[ForEach] static void A(in Spot p, ref Near n, [Neighbor] in Spot q, [Neighbor] ref Entity o) { float dx = p.X - q.X; if (dx * dx >= 1) return; n.Value++; }"));

    // A neighbor's component by ref that is only read keeps the pairs one at a time: EECS025, fixed to 'in'.
    [TestCase("n.Value += (int)d.X;", true)]
    [TestCase("d.X = 1; n.Value++;", false)]
    public async Task ANeighborByRefOnlyReadReports(string rest, bool reports)
    {
        var costs = await Costs(GeneratorTests.Components, PairParts,
            InSystem("[ForEach] static void A(in Spot p, ref Near n, [Neighbor] in Spot q, [Neighbor] ref Drift d) { float dx = p.X - q.X; if (dx * dx >= 1) return; " + rest + " }"));
        Assert.That(costs.Count(d => d.Id == "EECS025" && d.GetMessage().Contains("'d'")), Is.EqualTo(reports ? 1 : 0), string.Join("; ", costs.Select(d => d.GetMessage())));
    }

    [TestCase("[ForEach] static void A(in Spot p, ref Near n, [Neighbor] in Spot q) { float dx = p.X - q.X, dy = p.Y - q.Y; if (dx * dx + dy * dy >= 1) return; n.Value++; }")]
    [TestCase("[ForEach] static void A(in Spot p, ref Near n, [Neighbor] in Spot q, in Reach r) { float dx = q.X - p.X; float dy = q.Y - p.Y; if (dx * dx + dy * dy < r.Value * r.Value) n.Value++; }")] // the rest under the test, a singleton cutoff
    [TestCase("[ForEach, With<Quarry>] static void A(in Spot p, in Sight s, ref Near n, [Neighbor] in Spot q, [Neighbor] in Quarry _, [Neighbor] Entity o) { float dx = p.X - q.X, dy = p.Y - q.Y; if (dx * dx + dy * dy >= s.Range * s.Range || o.Id.Index < 0) return; n.Value++; }")] // a cutoff per row, the neighbor's entity
    [TestCase("[ForEach] static void A(in At3 p, ref Near n, [Neighbor] in At3 q) { if (System.Numerics.Vector3.DistanceSquared(p.V, q.V) >= 4f) return; n.Value++; }")]
    [TestCase("[ForEach] static void A(in At3 p, ref Near n, [Neighbor] in At3 q) { if ((p.V - q.V).LengthSquared() < 4f) { n.Value++; } }")]
    [TestCase("[ForEach] static void A(in Spot p, ref Near n, [Neighbor] in Spot q, [Neighbor] ref Drift d) { float dx = p.X - q.X; if (dx * dx >= 1 || d.X > 0) return; d.X = 1; n.Value++; }")] // a write of the neighbor (a pickup)
    [TestCase("[ForEach(Parallel = true)] static void A(in Spot p, ref Near n, [Neighbor] in Spot q, in Reach r) { float dx = p.X - q.X; if (dx * dx >= r.Value) return; n.Value++; }")] // on several threads
    [TestCase("[ForEach] static void A(in Spot p, [Neighbor] in Spot q, ref Reduce<Tally> t) { float dx = p.X - q.X; if (dx * dx >= 1) return; t.Value.N++; }")] // a result of the pass
    [TestCase("[ForEach(Parallel = true)] static void A(in Spot p, [Neighbor] in Spot q, ref Reduce<Tally> t) { float dx = p.X - q.X; if (dx * dx >= 1) return; t.Value.N++; }")] // each chunk's
    [TestCase("[ForEach] static void A(in Spot p, Mut<Seen> s, [Neighbor] in Spot q, [Neighbor] ref Drift d) { float dx = p.X - q.X; if (dx * dx >= 1 || d.X > 0) return; d.X = 1; s.Write().V++; }")] // a Mut<T> of the row
    [TestCase("[ForEach] static void A(in Spot p, ref Drift f, [Neighbor] in Spot q, [Neighbor] ref Drift g) { float dx = p.X - q.X; if (dx * dx >= 1) return; f.X += dx; g.X -= dx; }")] // both sides written
    [TestCase("[ForEach(Parallel = true)] static void A(in Spot p, ref Near n, [Neighbor] in Spot q, [Neighbor] ref Near m) { float dx = p.X - q.X; if (dx * dx >= 1) return; m.Value++; n.Value++; }")] // on several threads
    [TestCase("[ForEach(Parallel = true)] static void A(Entity e, in Spot p, ref Near n, [Neighbor] in Spot q, [Neighbor] ref Near m, [Neighbor] Entity o) { float dx = p.X - q.X; if (dx * dx >= 1 || n.Value == 0) return; if (e.Id.Index < o.Id.Index) (n.Value, m.Value) = (n.Value + m.Value, 0); }")] // a merge on several threads
    [TestCase("[ForEach] static void A(Entity e, in Spot p, ref Near n, [Neighbor] in Spot q, [Neighbor] ref Near m, [Neighbor] Entity o) { float dx = p.X - q.X; if (dx * dx >= 1 || n.Value == 0) return; if (e.Id.Index < o.Id.Index) (n.Value, m.Value) = (n.Value + m.Value, 0); }")] // a merge
    [TestCase("[ForEach] static void A(in Spot p, in Sight s, ref Near n, ref Seen t, [Neighbor] in Spot q, [Neighbor] ref Near m, [Neighbor] ref Seen u) { float dx = p.X - q.X; if (dx * dx >= s.Range * s.Range) return; n.Value++; m.Value++; if (dx > 0) { t.V++; u.V++; } }")] // tracked, written on some pairs: no Mut<T>
    [TestCase("[ForEach] static void A(in Spot p, ref Near n) { n.Value = (int)p.X; }")]                                                                         // no [Neighbor]: no pair rule
    public Task PairMethodsLeaveAlone(string method) => AssertClean(GeneratorTests.Components, PairParts, InSystem(method));

    [TestCase("[ForEach] static void A(in Spot p, ref Near n, [Neighbor] in Spot q) { n.Value++; }")]                                                           // no test at all
    [TestCase("[ForEach] static void A(in Spot p, ref Near n, [Neighbor] in Quarry q) { n.Value++; }")]                                                         // a neighbor tag: nothing to measure
    [TestCase("[ForEach] static void A(in Spot p, ref Near n, [Neighbor] in Spot q) { if (Far(p.X - q.X)) return; n.Value++; } static bool Far(float d) => d * d >= 1;")] // the test in a call
    [TestCase("[ForEach] static void A(in Spot p, ref Near n, [Neighbor] in Spot q) { n.Value++; float dx = p.X - q.X; if (dx * dx >= 1) return; n.Value--; }")] // a statement before the test
    [TestCase("[ForEach] static void A(in Spot p, ref Near n, [Neighbor] in Spot q) { float dx = p.X - q.Y; if (dx * dx >= 1) return; n.Value++; }")]            // two different fields
    [TestCase("[ForEach] static void A(in Spot p, ref Sight s, [Neighbor] in Spot q) { float dx = p.X - q.X; if (dx * dx >= s.Range) return; s.Range++; }")] // a cutoff of a component the row writes
    [TestCase("[ForEach] static void A(ref Spot p, [Neighbor] ref Spot q) { float dx = p.X - q.X; if (dx * dx >= 1) return; p.X += dx; q.X -= dx; }")] // both sides of what the test compares: no box holds
    public Task UnreadCutoffsReport(string method) => AssertReports("EECS106", GeneratorTests.Components, PairParts, InSystem(method));

    [Test]
    public async Task HandWrittenNeighborSearchesReport()
    {
        const string grid = """
            public struct Hood : ISingletonComponent { public EntityId[] Ids; }
            [EcsSystem] public ref struct S
            {
                [ForEach] static void A(Entity self, in Spot p, ref Near n, in Hood g, Lookup<Spot> spots)
                {
                    foreach (var id in g.Ids)
                    {
                        if (id == self.Id) continue;
                        ref readonly var q = ref spots.Read(id);
                        float dx = p.X - q.X, dy = p.Y - q.Y;
                        if (dx * dx + dy * dy >= 1) continue;
                        n.Value++;
                    }
                }
            }
            """;
        Assert.That(await Count("EECS107", GeneratorTests.Components, PairParts, grid), Is.EqualTo(1));
    }

    // EECS107 is a Warning with its fix (PairRewrite) when the search is the method's whole body and each candidate's
    // statements read nothing of it; a suggestion (Info, no fix) for any other search.
    [TestCase("foreach (var id in g.Ids) { ref readonly var q = ref spots.Read(id); float dx = p.X - q.X; if (dx * dx >= 1) continue; n.Value++; }", true)]
    [TestCase("foreach (var id in g.Ids) { var q = spots.Read(id); float dx = p.X - q.X; if (dx * dx >= 1) { continue; } n.Value += 2; if (n.Value > 9) continue; n.Value--; }", true)] // a copy, a later continue
    [TestCase("int sum = 0; foreach (var id in g.Ids) { ref readonly var q = ref spots.Read(id); float dx = p.X - q.X; if (dx * dx >= 1) continue; sum++; } n.Value = sum;", false)] // a sum written after the loop
    [TestCase("foreach (var id in g.Ids) { ref readonly var q = ref spots.Read(id); float dx = p.X - q.X; if (dx * dx >= 1) continue; n.Value++; if (n.Value > 3) break; }", false)] // a break leaves the search
    [TestCase("var ids = g.Ids; foreach (var id in ids) { ref readonly var q = ref spots.Read(id); float dx = p.X - q.X; if (dx * dx >= ids.Length) continue; n.Value++; }", false)] // a cutoff of the search
    [TestCase("foreach (var id in g.Ids) { ref readonly var q = ref spots.Read(id); n.Value++; float dx = p.X - q.X; if (dx * dx >= 1) continue; n.Value++; }", false)] // a write before the test
    [TestCase("var ids = g.Ids; foreach (var id in ids) { ref readonly var q = ref spots.Read(id); float dx = p.X - q.X; if (dx * dx >= 1) continue; n.Value += ids.Length; }", false)] // reads the search in the pair
    [TestCase("float r = 1; foreach (var id in g.Ids) { ref readonly var q = ref spots.Read(id); float dx = p.X - q.X; if (dx * dx >= r) continue; n.Value++; }", false)] // a cutoff in a local of the search
    public async Task HandWrittenSearchesWarnWithTheirFixOnlyWhenMechanical(string body, bool fixable)
    {
        string source = "public struct Hood : ISingletonComponent { public EntityId[] Ids; }\n[EcsSystem] public ref struct S { [ForEach] static void A(in Spot p, ref Near n, in Hood g, Lookup<Spot> spots) { "
                        + body + " } }";
        var found = (await GeneratorTests.Analyze([GeneratorTests.Components, PairParts, source])).Where(d => d.Id == "EECS107").ToArray();
        Assert.That(found, Has.Length.EqualTo(1));
        Assert.That(found[0].Severity, Is.EqualTo(fixable ? DiagnosticSeverity.Warning : DiagnosticSeverity.Info));
        Assert.That(found[0].Properties.ContainsKey("PairBody"), Is.EqualTo(fixable));
    }

    // A walk of a query of other entities inside a [ForEach] method: the same pairs as the pair method, so a Warning
    // with the fix when mechanical.
    [TestCase("foreach (var o in world.Read<Spot, Drift>()) { float dx = p.X - o.Component1.X; if (dx * dx >= 1) continue; n.Value += (int)o.Component2.X; }", true)]
    [TestCase("foreach (var o in world.Read<Spot, Drift>()) { float dx = p.X - o.Spot.X; if (dx * dx >= 1) continue; n.Value += (int)o.Drift.X; }", true)] // the generated item members
    [TestCase("foreach (var o in world.Read<Spot>()) { ref readonly var q = ref o.Component1; if (o.Entity.Id == self.Id) continue; float dx = p.X - q.X, dy = p.Y - q.Y; if (dx * dx + dy * dy >= 1) continue; n.Value++; }", true)] // the self skip after a bound local
    [TestCase("foreach (var o in world.Query<Spot>()) { float dx = p.X - o.Component1.X; if (dx * dx >= 1) continue; o.Component1.Y = 0; }", false)] // writes the neighbor
    [TestCase("foreach (var o in world.Read<Spot>(QueryFilter.Create().With<Near>())) { float dx = p.X - o.Component1.X; if (dx * dx >= 1) continue; n.Value++; }", false)] // a filter
    [TestCase("for (int k = 0; k < 2; k++) foreach (var o in world.Read<Spot>()) { float dx = p.X - o.Component1.X; if (dx * dx >= 1) continue; n.Value++; }", false)] // walked twice
    [TestCase("int seen = 0; foreach (var o in world.Read<Spot>()) { float dx = p.X - o.Component1.X; if (dx * dx >= 1) continue; seen++; } n.Value = seen;", false)] // a total after the walk
    public async Task QueryWalksWarnWithTheirFixOnlyWhenMechanical(string body, bool fixable)
    {
        string source = "[EcsSystem] public ref struct S { [ForEach] static void A(Entity self, in Spot p, ref Near n, World world) { " + body + " } }";
        var found = (await GeneratorTests.Analyze([GeneratorTests.Components, PairParts, source])).Where(d => d.Id == "EECS107").ToArray();
        Assert.That(found, Has.Length.EqualTo(1));
        Assert.That(found[0].Severity, Is.EqualTo(fixable ? DiagnosticSeverity.Warning : DiagnosticSeverity.Info), found[0].GetMessage());
        Assert.That(found[0].Properties.ContainsKey("PairBody"), Is.EqualTo(fixable));
    }

    // A walk of the entities after the row (by index) writing both sides: each unordered pair once, the pair method
    // writing both sides; any other walk writing the neighbor keeps no fix.
    [TestCase("if (o.Entity.Id.Index <= self.Id.Index) continue; float dx = p.X - o.Component1.X; if (dx * dx >= 1) continue; f.X += dx; o.Component2.X -= dx;", true)]
    [TestCase("if (self.Id.Index >= o.Entity.Id.Index) { continue; } float dx = p.X - o.Component1.X; if (dx * dx >= 1) continue; f.X += dx; o.Component2.X -= dx;", true)]
    [TestCase("if (o.Entity.Id == self.Id) continue; float dx = p.X - o.Component1.X; if (dx * dx >= 1) continue; f.X += dx; o.Component2.X -= dx;", false)] // every ordered pair: twice each
    [TestCase("if (o.Entity.Id.Index < self.Id.Index) continue; float dx = p.X - o.Component1.X; if (dx * dx >= 1) continue; f.X += dx; o.Component2.X -= dx;", false)] // meets itself
    [TestCase("if (o.Entity.Id.Index <= self.Id.Index) continue; float dx = p.X - o.Component1.X; if (dx * dx >= 1) continue; f.X += dx;", false)] // half its pairs, one side
    public async Task HalfWalksWarnWithTheirFixWhenTheyWriteBothSides(string body, bool fixable)
    {
        string source = "[EcsSystem] public ref struct S { [ForEach] static void A(Entity self, in Spot p, ref Drift f, World world) { foreach (var o in world.Query<Spot, Drift>()) { "
                        + body + " } } }";
        var found = (await GeneratorTests.Analyze([GeneratorTests.Components, PairParts, source])).Where(d => d.Id == "EECS107").ToArray();
        Assert.That(found, Has.Length.EqualTo(1));
        Assert.That(found[0].Properties.ContainsKey("PairBody"), Is.EqualTo(fixable), found[0].GetMessage());
    }

    [TestCase("foreach (var id in g.Ids) { ref readonly var q = ref drifts.Read(id); float dx = p.X - q.X; if (dx * dx >= 1) continue; n.Value++; }")] // another component's read
    [TestCase("ref readonly var q = ref spots.Read(g.Ids[0]); float dx = p.X - q.X; if (dx * dx >= 1) return; n.Value++;")]                              // no loop: one other entity
    [TestCase("foreach (var id in g.Ids) { ref readonly var q = ref spots.Read(id); n.Value += (int)q.X; }")]                                             // no distance test
    public async Task OtherLookupLoopsLeftAlone(string body)
    {
        string source = "public struct Hood : ISingletonComponent { public EntityId[] Ids; }\n[EcsSystem] public ref struct S { [ForEach] static void A(in Spot p, ref Near n, in Hood g, Lookup<Spot> spots, Lookup<Drift> drifts) { "
                        + body + " } }";
        Assert.That(await Count("EECS107", GeneratorTests.Components, PairParts, source), Is.Zero);
    }
}
