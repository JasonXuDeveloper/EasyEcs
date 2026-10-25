using System.Threading.Tasks;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

// EasyEcsAnalyzer.Forms.cs: [ForEntity], [UpdateInterval] and [Consume] where they cannot apply (EECS081, EECS083,
// EECS085) at their edges; the hand-written forms they replace (EECS080, EECS084) are FormTests'.
public partial class AnalyzerTests
{
    [TestCase("[UpdateInterval(-3)] [EcsSystem] public ref struct S { }", "EECS083")]
    [TestCase("[UpdateInterval(2, Phase = 2)] [EcsSystem] public ref struct S<X> { }", "EECS083")]                       // a phase not below the interval
    [TestCase("[UpdateInterval(2)] public struct NotASystem : IComponent { public int V; }", "EECS083")]
    public Task FormRulesReport(string source, string id) => AssertReports(id, GeneratorTests.Components, source);

    [TestCase("[UpdateInterval(1)] [EcsSystem] public ref struct S { }")]
    [TestCase("[UpdateInterval(4, Phase = 3)] [EcsSystem] public ref struct S<X> { }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach, Consume] static void A(ref P p, in Q q) => p.V += q.V; }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach, Consume, With<T>] static void A(Entity e) { } }")]
    [TestCase("public static class R { [ForEntity] static void A(Entity e, ref P p, in Q q, Maybe<T> t, int n) => p.V = q.V + n; }")]
    public Task FormRulesLeaveAlone(string source) => AssertClean(GeneratorTests.Components, source);
}
