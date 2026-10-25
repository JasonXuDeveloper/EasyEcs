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
    public Task ParallelRulesReport(string source, string id) => AssertReports(id, GeneratorTests.Components, Extra, Shared, source);

    [TestCase("[EcsSystem] public ref struct S { static void OnUpdate(World w) => w.Clock.Ticks = System.Random.Shared.Next(); }")]          // a hook: once per update, one thread
    [TestCase("public static class U { public static int F() => System.Random.Shared.Next(); }")]                                          // no pass
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(ref P p) { int local = 0; local += p.V; p.V = local; } }")] // a local per row
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(in P p, Scratch<System.Collections.Generic.List<int>> s) { var l = s.Value ??= new(); l.Clear(); l.Add(p.V); } }")] // a list per thread
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, Entity e, in EntityRandom r) => p.V = r.Range(e.Id, 0, 0, 6) + r.Range(e.Id, 1, 0, 6) + r.Range(e.Id, 2, 0, 6); }")] // a stream per draw
    public Task ParallelRulesLeaveAlone(string source) => AssertClean(GeneratorTests.Components, Extra, Shared, source);

    internal const string Shared = """
        public static class Counters
        {
            public static int Hits; public static bool Any; public static int Max { get; set; }
            public static readonly System.Threading.Lock Gate = new();
            public static readonly int[] Counts = new int[8];
            public static readonly System.Collections.Generic.List<int> Seen = new();
            public static readonly System.Collections.Concurrent.ConcurrentBag<int> Bag = new();
            [System.ThreadStatic] public static int Mine;
        }
        public class Tally { public int N; }
        """;

    // EECS060: code on several threads writes memory they share; EECS061: a parallel job's field read after the call.
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(in P p) => Counters.Hits += p.V; }", "EECS060")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(in P p) { if (p.V > 0) Counters.Hits++; } }", "EECS060")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(in P p) => Counters.Max = p.V; }", "EECS060")]          // last writer wins
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(in P p) => Counters.Seen.Add(p.V); }", "EECS060")]      // a list's Add
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(in P p) => Helpers.Inc(ref Counters.Hits); }", "EECS060")]
    [TestCase("[EcsSystem] public ref struct S { static readonly System.Collections.Generic.List<int> Scratch = new(); [ForEach(Parallel = true)] static void A(in P p) => Scratch.Add(p.V); }", "EECS060")] // readonly scratch: shared
    [TestCase("public static class R { [ForEach(Parallel = true)] public static void A(in P p, Tally t) => t.N += p.V; }", "EECS060")]   // a context object
    [TestCase("public struct J : IForEach<P>, INoStructuralChanges { public void Execute(ref P p) => Counters.Hits += p.V; } public static class U { public static void F(World w) { var j = new J(); w.ForEachParallel<P, J>(ref j); } }", "EECS060")]
    [TestCase("public struct J : IForEachEntity<P>, INoStructuralChanges { public System.Collections.Generic.List<EntityId> Hits; public void Execute(Entity e, ref P p) { if (p.V > 0) Hits.Add(e.Id); } } public static class U { public static void F(World w) { var j = new J { Hits = new() }; w.ForEachEntityParallel<P, J>(ref j); } }", "EECS060")] // copies share the list
    [TestCase("public struct Sum : IForEach<P>, INoStructuralChanges { public int Total; public void Execute(ref P p) => Total += p.V; } public static class U { public static int F(World w) { var s = new Sum(); w.ForEachParallel<P, Sum>(ref s); return s.Total; } }", "EECS061")]
    [TestCase("public struct Seen : IForEach<P>, INoStructuralChanges { public bool Any; public void Execute(ref P p) { if (p.V > 0) Any = true; } } public static class U { public static bool F(World w) { var s = new Seen(); w.ForEachParallel<P, Seen>(ref s, default, 64); return s.Any; } }", "EECS061")]
    [TestCase("public struct J : IForRange { public void Execute(int start, int end) => Counters.Hits += end - start; } public static class U { public static void F(World w) { var j = new J(); w.ForParallel(100, ref j); } }", "EECS060")]
    [TestCase("public struct Sum : IForRange { public int Total; public void Execute(int start, int end) => Total += end - start; } public static class U { public static int F(World w) { var s = new Sum(); w.ForParallel(100, ref s); return s.Total; } }", "EECS061")]
    [TestCase("public struct Sum : IForSpan<int> { public int Total; public void Execute(int start, System.Span<int> items) => Total += items.Length; } public static class U { public static int F(World w, ref DynamicBuffer<int> b) { var s = new Sum(); w.ForParallel(ref b, ref s); return s.Total; } }", "EECS061")]
    [TestCase("public struct J : IForEachBatch<P>, INoStructuralChanges { public void Execute(EntitySpan e, System.Span<P> p) => Counters.Hits += p.Length; } public static class U { public static void F(World w) { var j = new J(); w.ForEachBatchParallel<P, J>(ref j); } }", "EECS060")]
    [TestCase("public struct Sum : IForEachBatch<P>, INoStructuralChanges { public int Total; public void Execute(EntitySpan e, System.Span<P> p) { for (int i = 0; i < p.Length; i++) Total += p[i].V; } } public static class U { public static int F(World w) { var s = new Sum(); w.ForEachBatchParallel<P, Sum>(ref s); return s.Total; } }", "EECS061")]
    [TestCase("public struct Sum : IForEach<P>, INoStructuralChanges { public int Total; public void Execute(ref P p) => Total += p.V; public static bool Merges => false; public static void Merge<TSelf>(ref TSelf job, in TSelf chunk) where TSelf : struct { } } public static class U { public static int F(World w) { var s = new Sum(); w.ForEachParallel<P, Sum>(ref s); return s.Total; } }", "EECS061")] // merges nothing: a constant false
    [TestCase("public struct Sum : IForEachBatch<P>, INoStructuralChanges { public int Total; public void Execute(EntitySpan e, System.Span<P> p) => Total += p.Length; static bool IForEachBatch<P>.Merges => false; static void IForEachBatch<P>.Merge<TSelf>(ref TSelf job, in TSelf chunk) { } } public static class U { public static int F(World w) { var s = new Sum(); w.ForEachBatchParallel<P, Sum>(ref s); return s.Total; } }", "EECS061")] // explicitly
    [TestCase("public struct Sum : IForEach<P>, INoStructuralChanges { public int Total; public void Execute(ref P p) => Total += p.V; public static bool Merges { get { return false; } } public static void Merge<TSelf>(ref TSelf job, in TSelf chunk) where TSelf : struct { } } public static class U { public static int F(World w) { var s = new Sum(); w.ForEachParallel<P, Sum>(ref s); return s.Total; } }", "EECS061")] // a getter's constant
    [TestCase("public struct Sum : IForEach<P>, INoStructuralChanges { public int Total; public void Execute(ref P p) => Total += p.V; const bool Never = false; public static bool Merges { get => Never; } public static void Merge<TSelf>(ref TSelf job, in TSelf chunk) where TSelf : struct { } } public static class U { public static int F(World w) { var s = new Sum(); w.ForEachParallel<P, Sum>(ref s); return s.Total; } }", "EECS061")] // a const
    public async Task ParallelWritesAreReported(string source, string id)
    {
        var errors = await Diagnose(Components, Extra, Shared, source);
        Assert.That(errors, Does.Contain(id));
        Assert.That(errors.Where(e => !e.StartsWith("CS")), Is.EqualTo(new[] { id }), "exactly one EasyEcs diagnostic");
        Assert.That(errors.Where(e => e.StartsWith("CS")), Is.Empty, "the case compiles");
    }

    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(in P p) => Counters.Hits += p.V; }")]                                   // one thread
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(in P p) => System.Threading.Interlocked.Add(ref Counters.Hits, p.V); }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(in P p) { lock (Counters.Gate) Counters.Hits += p.V; } }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(in P p) { using (Counters.Gate.EnterScope()) Counters.Hits += p.V; } }")] // the Lock's scope
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(in P p) { if (p.V > 0) Counters.Any = true; } }")]      // the same constant
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(in P p) => Counters.Bag.Add(p.V); }")]                  // concurrent
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(in P p) => Counters.Mine += p.V; }")]                   // [ThreadStatic]
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(in P p) => Counters.Counts[p.V & 7]++; }")]             // an element: rows may own it
    [TestCase("[EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void A(ref P p, in Q q) { var l = new System.Collections.Generic.List<int>(); l.Add(q.V); p.V = l.Count; } }")] // its own list
    [TestCase("public static class R { [ForEach(Parallel = true)] public static void A(ref P p, int n) { n++; p.V += n; } }")]           // a copy
    [TestCase("public struct J : IForEach<P>, INoStructuralChanges { public void Execute(ref P p) => Counters.Hits += p.V; } public static class U { public static void F(World w) { var j = new J(); w.ForEach<P, J>(ref j); } }")]
    [TestCase("public struct Sum : IForEach<P>, INoStructuralChanges { public int Total; public void Execute(ref P p) => Total += p.V; } public static class U { public static int F(World w) { var s = new Sum(); w.ForEach<P, Sum>(ref s); return s.Total; } }")]
    [TestCase("public struct Sum : IForEach<P>, INoStructuralChanges { public int Total; public void Execute(ref P p) { Total = p.V; p.V = Total * 2; } } public static class U { public static void F(World w) { var s = new Sum(); w.ForEachParallel<P, Sum>(ref s); } }")] // scratch per chunk
    [TestCase("public struct Sum : IForEach<P>, INoStructuralChanges { public int Total, Limit; public void Execute(ref P p) => Total += p.V; } public static class U { public static int F(World w) { var s = new Sum { Limit = 3 }; w.ForEachParallel<P, Sum>(ref s); return s.Limit; } }")] // not written
    [TestCase("public struct Sum : IForEach<P>, INoStructuralChanges { public int Total; public void Execute(ref P p) => Total += p.V; } public static class U { public static int F(World w) { var s = new Sum(); w.ForEachParallel<P, Sum>(ref s); s.Total = 0; return s.Total; } }")] // set first
    [TestCase("public struct Sum : IForEach<P>, INoStructuralChanges { public int Total; public void Execute(ref P p) => Total += p.V; public static bool Merges => true; public static void Merge<TSelf>(ref TSelf job, in TSelf chunk) where TSelf : struct => System.Runtime.CompilerServices.Unsafe.As<TSelf, Sum>(ref job).Total += System.Runtime.CompilerServices.Unsafe.As<TSelf, Sum>(ref System.Runtime.CompilerServices.Unsafe.AsRef(in chunk)).Total; } public static class U { public static int F(World w) { var s = new Sum(); w.ForEachParallel<P, Sum>(ref s); return s.Total; } }")] // merges its copies back
    [TestCase("public struct Double : IForSpan<int> { public void Execute(int start, System.Span<int> items) { for (int i = 0; i < items.Length; i++) items[i] *= 2; } } public static class U { public static void F(World w, ref DynamicBuffer<int> b) { var j = new Double(); w.ForParallel(ref b, ref j); } }")] // its own elements
    [TestCase("public struct Fill : IForRange { public int[] Cells; public void Execute(int start, int end) { for (int i = start; i < end; i++) Cells[i] = i; } } public static class U { public static void F(World w, int[] cells) { var j = new Fill { Cells = cells }; w.ForParallel(cells.Length, ref j); } }")]
    [TestCase("public struct Sum : IForEachBatch<P>, INoStructuralChanges { public int Total; public void Execute(EntitySpan e, System.Span<P> p) => Total += p.Length; } public static class U { public static int F(World w) { var s = new Sum(); w.ForEachBatch<P, Sum>(ref s); return s.Total; } }")] // one thread
    [TestCase("public struct Double : IForEachBatch<P>, INoStructuralChanges { public void Execute(EntitySpan e, System.Span<P> p) { for (int i = 0; i < p.Length; i++) p[i].V *= 2; } } public static class U { public static void F(World w) { var j = new Double(); w.ForEachBatchParallel<P, Double>(ref j); } }")] // its own rows
    [TestCase("public struct Sum : IForEachBatch<P>, INoStructuralChanges { public int Total; public void Execute(EntitySpan e, System.Span<P> p) => Total += p.Length; static bool IForEachBatch<P>.Merges => true; static void IForEachBatch<P>.Merge<TSelf>(ref TSelf job, in TSelf chunk) => System.Runtime.CompilerServices.Unsafe.As<TSelf, Sum>(ref job).Total += System.Runtime.CompilerServices.Unsafe.As<TSelf, Sum>(ref System.Runtime.CompilerServices.Unsafe.AsRef(in chunk)).Total; } public static class U { public static int F(World w) { var s = new Sum(); w.ForEachBatchParallel<P, Sum>(ref s); return s.Total; } }")] // merges, explicitly
    [TestCase("public struct Sum : IForEach<P>, INoStructuralChanges { public int Total; public void Execute(ref P p) => Total += p.V; public static bool Merges { get => true; } public static void Merge<TSelf>(ref TSelf job, in TSelf chunk) where TSelf : struct { } } public static class U { public static int F(World w) { var s = new Sum(); w.ForEachParallel<P, Sum>(ref s); return s.Total; } }")] // a getter's constant true
    [TestCase("public struct Sum : IForEach<P>, INoStructuralChanges { public int Total; public void Execute(ref P p) => Total += p.V; public static bool On = true; public static bool Merges => On; public static void Merge<TSelf>(ref TSelf job, in TSelf chunk) where TSelf : struct { } } public static class U { public static int F(World w) { var s = new Sum(); w.ForEachParallel<P, Sum>(ref s); return s.Total; } }")] // not a constant: may merge
    [TestCase("public struct Sum : IForEach<P>, INoStructuralChanges { public int Total; public void Execute(ref P p) => Total += p.V; public static bool Merges { get; } = false; public static void Merge<TSelf>(ref TSelf job, in TSelf chunk) where TSelf : struct { } } public static class U { public static int F(World w) { var s = new Sum(); w.ForEachParallel<P, Sum>(ref s); return s.Total; } }")] // an auto-property: not read, may merge
    public async Task ParallelWritesThatDoNotRaceAreNotReported(string source)
    {
        var errors = await Diagnose(Components, Extra, Shared, source);
        Assert.That(errors, Is.Empty);
    }
}
