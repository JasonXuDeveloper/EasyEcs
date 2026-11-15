using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using NUnit.Framework;
using static EasyEcs.UnitTest.GeneratorTests;

namespace EasyEcs.UnitTest;

/// <summary>
/// The file-level aliases of generated files (Code.Aliased, Tidy.Types) where type names meet: two types of one simple
/// name (each qualified by the outer names that tell it apart, in its own alias and in a closed generic's), nested types
/// of one name, user types named like the BCL aliases, and names starting with <c>__</c> the user's code declares,
/// which an alias must not take (a type of an enclosing namespace wins over a file's alias). Each case compiles, and its
/// aliases read apart.
/// </summary>
public class AliasTests
{
    private const string TwoPositions = """
        namespace Game.A { public struct Position : IComponent { public float X; } }
        namespace Game.B { public struct Position : IComponent { public float X; } }
        namespace Game
        {
            [EcsSystem] public ref struct Move
            {
                [ForEach] static void Follow(ref A.Position a, in B.Position b) => a.X += b.X;
                [ForEach, With<B.Position>] static void Step(ref A.Position a) => a.X += 1f;
            }
        }
        """;

    private const string Nested = """
        namespace Game
        {
            public struct Ship { public struct Pos : IComponent { public float X; } }
            public struct Rock { public struct Pos : IComponent { public float X; } }
            [EcsSystem] public ref struct Drift
            {
                [ForEach] static void Follow(ref Ship.Pos a, in Rock.Pos b) => a.X += b.X;
                [ForEach] static void Push(ref Rock.Pos b) => b.X += 1f;
            }
        }
        """;

    private const string LikeAliases = """
        namespace Game
        {
            public struct Vector : IComponent { public float X; }
            public struct Unsafe : IComponent { public float X; }
            public struct Span : IComponent { public float X; }
            public struct Interleaved : IComponent { public float X, Y; }
            [EcsSystem] public ref struct Mix
            {
                [ForEach] static void A(ref Vector v, in Unsafe u, in Span s) => v.X += u.X * s.X;
                [ForEach] static void B(ref Interleaved i, in Vector v) => i.X += v.X + i.Y;
            }
        }
        """;

    private const string Underscored = """
        namespace Game
        {
            public struct __Heat : IComponent { public float V; }
            public struct __Vec : IComponent { public float V; }
            public struct Heat : IComponent { public float V; }
            [EcsSystem] public ref struct Warm
            {
                [ForEach] static void A(ref __Heat h, in Heat g) => h.V += g.V;
                [ForEach] static void B(ref Heat g, in __Vec v) => g.V += v.V;
            }
        }
        """;

    // Game.__Heat is not named by the system's file: an alias __Heat for Game.Heat would bind to it
    private const string Shadowed = """
        namespace Game
        {
            public struct __Heat : IComponent { public float V; }
            public struct Heat : IComponent { public float V; }
            public struct Cold : IComponent { public float V; }
            [EcsSystem] public ref struct Warm
            {
                [ForEach] static void A(ref Heat h) => h.V += 1f;
                [ForEach] static void B(ref Heat h, in Cold c) => h.V -= c.V;
            }
        }
        """;

    [TestCase(TwoPositions,
        "using __A_Position = global::Game.A.Position;", "using __B_Position = global::Game.B.Position;",
        "using __Span_A_Position = global::System.Span<global::Game.A.Position>;", "using __Span_B_Position = global::System.Span<global::Game.B.Position>;",
        "using __IForEachBatch_A_Position_B_Position = global::EasyEcs.IForEachBatch<global::Game.A.Position, global::Game.B.Position>;")]
    [TestCase(Nested,
        "using __Ship_Pos = global::Game.Ship.Pos;", "using __Rock_Pos = global::Game.Rock.Pos;",
        "using __Span_Ship_Pos = global::System.Span<global::Game.Ship.Pos>;", "using __Span_Rock_Pos = global::System.Span<global::Game.Rock.Pos>;")]
    [TestCase(LikeAliases,
        "using __Game_Vector = global::Game.Vector;", "using __Game_Unsafe = global::Game.Unsafe;", "using __Game_Span = global::Game.Span;",
        "using __Game_Interleaved = global::Game.Interleaved;", "using __Vec = global::System.Numerics.Vector;",
        "using __Unsafe = global::System.Runtime.CompilerServices.Unsafe;", "using __Interleaved = global::EasyEcs.Interleaved;",
        "using __System_Span_Game_Span = global::System.Span<global::Game.Span>;")]
    [TestCase(Underscored, "using ____Heat = global::Game.__Heat;", "using ____Vec = global::Game.__Vec;", "using __Game_Heat = global::Game.Heat;")]
    [TestCase(Shadowed, "using __Game_Heat = global::Game.Heat;")]
    public void AliasesOfTypesWhoseNamesMeetCompileAndReadApart(string source, params string[] expected)
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(source), out var output, out _);
        var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()).ToArray();
        Assert.That(errors, Is.Empty, string.Join("\n", errors));
        var aliases = output.SyntaxTrees.Where(IsCode).SelectMany(t => Regex.Matches(t.ToString(), @"(?m)^using __\w+ = .+;$").Select(m => m.Value)).ToHashSet();
        foreach (string line in expected) Assert.That(aliases, Does.Contain(line), string.Join("\n", aliases.OrderBy(a => a)));
        // no alias names two types in one file, and none takes a name the user's code declares
        var declared = Regex.Matches(source, @"struct (__\w+)").Select(m => m.Groups[1].Value).ToArray();
        foreach (var tree in output.SyntaxTrees.Where(IsCode))
        {
            var names = Regex.Matches(tree.ToString(), @"(?m)^using (\w+) = ").Select(m => m.Groups[1].Value).ToArray();
            Assert.That(names, Is.Unique, tree.FilePath);
            Assert.That(names.Intersect(declared), Is.Empty, tree.FilePath);
        }
    }
}
