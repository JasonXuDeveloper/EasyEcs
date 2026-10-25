using System.Threading.Tasks;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

// EasyEcsAnalyzer.Parallel.cs: code on several threads (EECS060, EECS061) and random draws in passes (EECS071,
// EECS088), in generic systems, runners, helpers the pass calls, and the code each rule leaves alone.
public partial class AnalyzerTests
{
    [TestCase("[EcsSystem] public ref struct S<X> { [ForEach(Parallel = true)] static void A(in P p) => Counters.Hits += p.V; }", "EECS060")]   // a generic system
    [TestCase("public static class R { [ForEach(Parallel = true)] public static void A(in P p) => Counters.Seen.Clear(); }", "EECS060")]       // a runner
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(in P p) { Counters.Max = p.V; } [ForEach] static void B(in P p) => Counters.Max = p.V; }", "EECS060")] // the parallel one only
    [TestCase("public struct J : IForEachEntity<P>, INoStructuralChanges { public void Execute(Entity e, ref P p) => Counters.Hits++; } public static class U { public static void F(World w) { var j = new J(); w.ForEachEntityParallel<P, J>(ref j); } }", "EECS060")]
    [TestCase("[EcsSystem] public ref struct S<X> { [ForEach(Parallel = true)] static void A(ref P p) => p.V = System.Random.Shared.Next(); }", "EECS071")]
    [TestCase("public static class R { [ForEntity] static void A(ref P p, Entity e, in EntityRandom r) => p.V = r.Range(e.Id, 5, 0, 6) - r.Range(e.Id, 5, 0, 6); }", "EECS088")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(ref P p, Entity e, in EntityRandom r) => p.V = r.Bits(e.Id, 3) == r.Bits(e.Id, 3) ? 1 : 0; }", "EECS088")]
    public Task ParallelRulesReport(string source, string id) => AssertReports(id, GeneratorTests.Components, GeneratorTests.Extra, GeneratorTests.Shared, source);

    [TestCase("[EcsSystem] public ref struct S { static void OnUpdate(World w) => w.Clock.Ticks = System.Random.Shared.Next(); }")]          // a hook: once per update, one thread
    [TestCase("public static class U { public static int F() => System.Random.Shared.Next(); }")]                                          // no pass
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(ref P p) { int local = 0; local += p.V; p.V = local; } }")] // a local per row
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(in P p, Scratch<System.Collections.Generic.List<int>> s) { var l = s.Value ??= new(); l.Clear(); l.Add(p.V); } }")] // a list per thread
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, Entity e, in EntityRandom r) => p.V = r.Range(e.Id, 0, 0, 6) + r.Range(e.Id, 1, 0, 6) + r.Range(e.Id, 2, 0, 6); }")] // a stream per draw
    public Task ParallelRulesLeaveAlone(string source) => AssertClean(GeneratorTests.Components, GeneratorTests.Extra, GeneratorTests.Shared, source);
}
