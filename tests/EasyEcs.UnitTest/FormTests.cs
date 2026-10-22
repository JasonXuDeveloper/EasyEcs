using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>
/// The suggestions to declare with attributes what code writes by hand: a one-entity job and its entity.Run calls
/// ([ForEntity], EECS080) and a [ForEach] method that destroys every entity it visits ([Consume], EECS084). Their fixes
/// are in CodeFixTests.
/// </summary>
public class FormTests
{
    private const string Data = """
        public struct P : IComponent { public int V; }
        public struct Q : IComponent { public int V; }
        public struct Hit : IComponent { public int Amount; }
        public struct Closed : IComponent;

        """;

    private static async Task<string[]> Ids(string source) =>
        (await GeneratorTests.Analyze([Data + source])).Where(d => d.Id is "EECS080" or "EECS084").Select(d => d.Id).ToArray();

    private const string Job = "public struct J : IForEachEntity<P> { public int N; public static int WriteMask => 1; public void Execute(Entity e, ref P p) => p.V += N; }\n";

    [TestCase(Job + "public static class U { public static bool F(Entity e) { var j = new J { N = 2 }; return e.Run<P, J>(ref j); } }")]
    [TestCase(Job + "public static class U { static readonly QueryFilter Open = QueryFilter.Create().Without<Closed>().With<Q>(); public static void F(Entity e, int n) { var j = new J { N = n }; e.Run<P, J>(ref j, Open); } }")]
    [TestCase(Job + "public static class U { public static void F(Entity a, Entity b) { var j = new J(); a.Run<P, J>(ref j); var k = new J { N = 1 }; if (b.Run<P, J>(ref k)) { } } }")]
    [TestCase("public struct J : IForEachEntity<P, Q>, INoStructuralChanges { public int N; public void Execute(Entity e, ref P p, ref Q q) => p.V = q.V + N; }\n"
              + "public static class U { public static void F(Entity e) { var j = new J { N = 1 }; e.Run<P, Q, J>(ref j); } }")]   // WriteMask: the default
    public async Task AHandWrittenOneEntityJobIsReported(string source) =>
        Assert.That(await Ids(source), Is.EqualTo(new[] { "EECS080" }));

    [TestCase(Job + "public static class U { public static int F(Entity e) { var j = new J { N = 2 }; e.Run<P, J>(ref j); return j.N; } }")]           // read after
    [TestCase(Job + "public static class U { public static void F(World w) { var j = new J(); w.ForEachEntity<P, J>(ref j); } }")]                      // a pass
    [TestCase(Job + "public static class U { public static void F(Entity e) { var j = new J(); e.Run<P, J>(ref j); } static J Make() => default; }")]   // another use
    [TestCase(Job + "public static class U { public static void F(Entity e) { var j = new J { N = Next() }; e.Run<P, J>(ref j); } static int Next() => 1; }")] // a call moved
    [TestCase(Job + "public static class U { public static void F(Entity e) { var j = new J(); e.Run<P, J>(ref j, QueryFilter.Create().Without<Closed>()); } }")] // built per call
    [TestCase(Job + "public static class U { static readonly QueryFilter A = QueryFilter.Create().Without<Closed>(); public static void F(Entity e, Entity f) { var j = new J(); e.Run<P, J>(ref j, A); var k = new J(); f.Run<P, J>(ref k); } }")] // two filters
    [TestCase(Job + "public static class U { public static void F(Entity e) { var j = new J(); System.Console.WriteLine(); e.Run<P, J>(ref j); } }")]   // not next
    [TestCase("public struct J : IForEachEntity<P> { public int N; public void Execute(Entity e, ref P p) => N = p.V; }\n"
              + "public static class U { public static void F(Entity e) { var j = new J(); e.Run<P, J>(ref j); } }")]                                     // a result
    [TestCase("public struct J : IForEachEntity<P> { public int N; public static int WriteMask => 0; public void Execute(Entity e, ref P p) => p.V = N; }\n"
              + "public static class U { public static void F(Entity e) { var j = new J(); e.Run<P, J>(ref j); } }")]                                     // EECS037's
    [TestCase("public struct J : IForEachEntity<P> { public int N; public void Execute(Entity e, ref P p) => Help(ref p); void Help(ref P p) { } }\n"
              + "public static class U { public static void F(Entity e) { var j = new J(); e.Run<P, J>(ref j); } }")]                                     // another member
    [TestCase("public struct J : IForEachEntity<P> { public int N; public void Execute(Entity e, ref P p) => Use(this); static void Use(J j) { } }\n"
              + "public static class U { public static void F(Entity e) { var j = new J(); e.Run<P, J>(ref j); } }")]                                     // 'this'
    public async Task JobsTheFixCannotConvertAreLeftAlone(string source) =>
        Assert.That(await Ids(source), Is.Empty);

    [TestCase("public class S : EcsSystem { [ForEach] static void A(Entity e, in Hit h, ref P p) { p.V -= h.Amount; e.Destroy(); } }")]
    [TestCase("public class S : EcsSystem { [ForEach] void A(Entity e, in Hit h, ref P p) { p.V -= h.Amount; World.CreateEntity(new Q()); e.Destroy(); } }")]
    [TestCase("public static class R { [ForEach] static void A(Entity e, in Hit h, ref int total) { total += h.Amount; e.Destroy(); } }")]
    public async Task AFinalDestroyIsReported(string source) =>
        Assert.That(await Ids(source), Is.EqualTo(new[] { "EECS084" }));

    [TestCase("public class S : EcsSystem { [ForEach] static void A(Entity e, in Hit h, ref P p) { p.V -= h.Amount; if (p.V < 0) e.Destroy(); } }")]   // conditional
    [TestCase("public class S : EcsSystem { [ForEach] static void A(Entity e, in Hit h, ref P p) { if (p.V < 0) return; p.V -= h.Amount; e.Destroy(); } }")] // a return
    [TestCase("public class S : EcsSystem { [ForEach, With<Hit>] static void A(Entity e) { e.Destroy(); } }")]                                     // EECS030's
    [TestCase("public struct Link : IComponent { public EntityId To; } public class S : EcsSystem { [ForEach] static void A(Entity e, in Link l, ref P p) { p.V++; e.World.GetEntity(l.To).Destroy(); } }")] // another entity
    [TestCase("public class S : EcsSystem { [ForEach, Consume] static void A(Entity e, in Hit h, ref P p) { p.V -= h.Amount; e.Destroy(); } }")]  // already
    [TestCase("public class S : EcsSystem { [ForEach(Slices = 2)] static void A(Entity e, in Hit h, ref P p) { p.V -= h.Amount; e.Destroy(); } }")] // sliced
    public async Task OtherDestroysAreLeftAlone(string source) =>
        Assert.That(await Ids(source), Is.Empty);
}
