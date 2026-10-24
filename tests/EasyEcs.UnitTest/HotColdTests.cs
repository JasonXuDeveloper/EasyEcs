using System.Linq;
using Microsoft.CodeAnalysis;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>
/// EECS091: a component whose frequent passes all leave the same wide set of its fields untouched (the hot/cold split
/// of a row), and the cases it leaves alone.
/// </summary>
public class HotColdTests
{
    // A row of 32 bytes: 8 a movement pass uses, a name, a birth tick and a kind it never reads. The string keeps the
    // method away from a vector loop, so EECS055 says nothing about it.
    private const string Body = "public struct Body : IComponent { public float X, VX; public string Name; public long Born; public int Kind; }";

    private static Diagnostic[] Report(params string[] sources) =>
        GeneratorTests.Analyze(sources).Result.Where(d => d.Id is "EECS091" or "EECS055").ToArray();

    [Test]
    public void AFrequentPassThatSkipsHalfARowIsReported()
    {
        var found = Report(Body, """
            [EcsSystem] public ref struct Movement { [ForEach] static void Move(ref Body b) => b.X += b.VX; }
            [EcsSystem, UpdateInterval(60)] public ref struct Census { [ForEach] static void Count(in Body b, ref Tally t) => t.N += b.Kind + b.Name.Length; }
            public struct Tally : ISingletonComponent { public long N; }
            """);
        Assert.That(found.Select(d => d.Id), Is.EqualTo(new[] { "EECS091" }));
        Assert.That(found[0].Severity, Is.EqualTo(DiagnosticSeverity.Warning));
        Assert.That(found[0].GetMessage(), Is.EqualTo("Component 'Body' is 32 bytes, and pass Movement.Move touches only X and VX (8 bytes) of each "
            + "row every update: move Name, Born and Kind (24 bytes) into a component of their own, so the pass reads 8 bytes per row instead of 32"));
        Assert.That(found[0].Location.GetLineSpan().StartLinePosition.Line, Is.EqualTo(1)); // the component's declaration
    }

    [TestCase("[ForEach] static void Move(Mut<Body> b) { if (b.Value.VX != 0) b.Write().X += b.Value.VX; }")]
    [TestCase("[ForEach] static void Move(System.Span<Body> b) { for (int i = 0; i < b.Length; i++) b[i].X += b[i].VX; }")]
    [TestCase("[ForEach] static void Move(ref Speed s, Maybe<Body> b) { if (b.HasValue) b.Value.X += b.Value.VX * s.V; }")]
    public void FieldsAreFoundThroughEveryParameterForm(string method)
    {
        var found = Report(Body, "public struct Speed : IComponent { public float V; }", $"[EcsSystem] public ref struct Movement {{ {method} }}");
        Assert.That(found.Select(d => d.Id), Is.EqualTo(new[] { "EECS091" }), string.Join("\n", found.Select(d => d.GetMessage())));
    }

    [Test]
    public void SeveralPassesAreNamedAndTheirFieldsJoined()
    {
        var found = Report(Body, """
            [EcsSystem] public ref struct Movement { [ForEach] static void Move(ref Body b) => b.X += b.VX; }
            public static class Physics { [ForEach] public static void Drag(ref Body b) => b.VX *= 0.5f; }
            """);
        Assert.That(found.Select(d => d.GetMessage()), Is.EqualTo(new[] { "Component 'Body' is 32 bytes, and passes Movement.Move and Physics.Drag touch "
            + "only X and VX (8 bytes) of each row every update: move Name, Born and Kind (24 bytes) into a component of their own, so those passes read "
            + "8 bytes per row instead of 32" }));
    }

    // False-positive guards: what is not a frequent pass over a wide row with cold fields.
    [TestCase("public struct Small : IComponent { public float X, VX; public long Born; }", // 16 bytes: the column costs about the same
        "[EcsSystem] public ref struct M { [ForEach] static void Move(ref Small s) => s.X += s.VX; }")]
    [TestCase(Body, "[EcsSystem] public ref struct M { [ForEach] static void Move(ref Body b) => b.X += b.VX; [ForEach] static void Age(ref Body b) => b.Born += b.Kind + b.Name.Length; }")] // every field hot
    [TestCase(Body, "[EcsSystem, UpdateInterval(10)] public ref struct M { [ForEach] static void Move(ref Body b) => b.X += b.VX; }")] // no frequent pass
    [TestCase(Body, "[EcsSystem] public ref struct M { [ForEach, Changed<Body>] static void Move(ref Body b) => b.X += b.VX; }")] // changed rows only
    [TestCase(Body, "[EcsSystem] public ref struct M { [ForEach(Slices = 4)] static void Move(ref Body b) => b.X += b.VX; }")] // a slice per update
    [TestCase(Body, "[EcsSystem] public ref struct M { [ForEach] static void Move(ref Body b) { b.X += b.VX; Log(in b); } static void Log(in Body b) { } }")] // used whole
    [TestCase(Body, "[EcsSystem] public ref struct M { [ForEach] static void Move(ref Body b) { ref var r = ref b; r.X += r.VX; } }")] // a ref local: whole
    [TestCase(Body, "public static class M { [ForEntity] public static void Move(ref Body b) => b.X += b.VX; }")] // one entity: no column walked
    [TestCase("public struct Hot : IComponent { public float X, VX; public string Name; public int Born; public long Kind; public float Y; }", // 24 of 40 bytes hot
        "[EcsSystem] public ref struct M { [ForEach] static void Move(ref Hot h) { h.X += h.VX; h.Y = h.X; h.Born++; h.Kind = h.Born; } }")] // cold under half
    [TestCase("public struct Owned : IComponent, IIndexed<EntityId> { public EntityId Owner; public float X, VX; public string Name; public long Born; EntityId IIndexed<EntityId>.Key => Owner; }",
        "[EcsSystem] public ref struct M { [ForEach] static void Move(ref Owned o) => o.X += o.VX; }")] // a key stays with its row
    [TestCase("[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)] public struct Blob : IComponent { [System.Runtime.InteropServices.FieldOffset(0)] public float X; [System.Runtime.InteropServices.FieldOffset(4)] public float VX; [System.Runtime.InteropServices.FieldOffset(8)] public string Name; [System.Runtime.InteropServices.FieldOffset(16)] public long Born; [System.Runtime.InteropServices.FieldOffset(24)] public long Kind; }",
        "[EcsSystem] public ref struct M { [ForEach] static void Move(ref Blob b) => b.X += b.VX; }")] // a layout of its own: size unknown
    public void ComponentsNotWorthSplittingAreNotReported(string component, string system)
    {
        var found = Report(component, system).Where(d => d.Id == "EECS091");
        Assert.That(found.Select(d => d.GetMessage()), Is.Empty);
    }

    // One mistake, one diagnostic: when EECS055 already advises to split the fields a pass uses out of the component,
    // EECS091 leaves the component to it.
    [Test]
    public void TheVectorLoopsAdviceToSplitWins()
    {
        var found = Report("public struct Ship : IComponent { public float X, VX, A, B, C, D, E, F; }",
            "[EcsSystem] public ref struct M { [ForEach] static void Move(ref Ship s) => s.X += s.VX; }");
        Assert.That(found.Select(d => d.Id), Is.EqualTo(new[] { "EECS055" }), string.Join("\n", found.Select(d => d.GetMessage())));
        Assert.That(found[0].GetMessage(), Does.Contain("split them into a component of their own"));
        Assert.That(found[0].Severity, Is.EqualTo(DiagnosticSeverity.Warning));
    }

    // With the method kept scalar on purpose (SimdMode.Off) EECS055 says nothing, so EECS091 reports the row.
    [Test]
    public void AScalarMethodByChoiceStillGetsTheLayoutAdvice()
    {
        var found = Report("public struct Ship : IComponent { public float X, VX, A, B, C, D, E, F; }",
            "[EcsSystem] public ref struct M { [ForEach(SimdMode = SimdMode.Off)] static void Move(ref Ship s) => s.X += s.VX; }");
        Assert.That(found.Select(d => d.Id), Is.EqualTo(new[] { "EECS091" }));
    }
}
