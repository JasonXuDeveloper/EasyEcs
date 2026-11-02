using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using EasyEcs.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace EasyEcs.Benchmark;

/// <summary>
/// The code <see cref="GeneratorBenchmark"/> runs the generators and the analyzer over: a game server's worth of code,
/// the battle server (BattleServer.cs, an embedded resource) and <see cref="Modules"/> more files, each with 10
/// components (plain, tracked, indexed, a tag), a buffer element, 5 systems of 5 [ForEach] methods (one calls helpers),
/// a system whose method takes a [View] (helpers and view members inlined into the vector loops), a query loop and
/// gameplay code that changes entities through the generated members, then a file with no EasyEcs code. Shared with
/// the unit tests (GeneratorTests.TheBenchmarkCorpusIsClean), which check once that it compiles and that the analyzer
/// has nothing to say about it, so the benchmark's processes do not pay for that check.
/// </summary>
public static class GeneratorCorpus
{
    /// <summary>The number of generated modules.</summary>
    public const int Modules = 40;

    /// <summary>The language version the corpus and the generated code parse with.</summary>
    public static readonly CSharpParseOptions Parse = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);

    /// <summary>The corpus's files: the battle server, the modules, then the file with no EasyEcs code.</summary>
    /// <param name="oneNamespace">Every module in one namespace (a game's <c>Game</c>), as many projects lay out their
    /// components, instead of one namespace per module.</param>
    public static string[] Sources(bool oneNamespace)
    {
        using var stream = typeof(GeneratorCorpus).Assembly.GetManifestResourceStream("BattleServer.cs")!;
        string battle = new StreamReader(stream).ReadToEnd();
        return Enumerable.Range(0, Modules).Select(m => Module(m, oneNamespace)).Prepend(battle).Append(Unrelated(0)).ToArray();
    }

    /// <summary>The framework's assemblies and EasyEcs.Core.</summary>
    public static ImmutableArray<MetadataReference> References() =>
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p).StartsWith("System.") || Path.GetFileName(p) is "netstandard.dll" or "mscorlib.dll")
            .Append(typeof(World).Assembly.Location)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
    ];

    /// <summary>A compilation of <paramref name="trees"/> as a game's project builds it.</summary>
    public static CSharpCompilation Compilation(ImmutableArray<SyntaxTree> trees, ImmutableArray<MetadataReference> references) =>
        CSharpCompilation.Create("Game", trees, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable));

    /// <summary>The five generators in one driver, with <paramref name="disabled"/> outputs skipped (an IDE's driver skips implementation outputs).</summary>
    public static GeneratorDriver Driver(IncrementalGeneratorOutputKind disabled = IncrementalGeneratorOutputKind.None) => CSharpGeneratorDriver.Create(
        [new ArityGenerator().AsSourceGenerator(), new ComponentGenerator().AsSourceGenerator(), new SystemGenerator().AsSourceGenerator(), new PartGenerator().AsSourceGenerator(),
            new ViewGenerator().AsSourceGenerator()], parseOptions: Parse,
        driverOptions: new GeneratorDriverOptions(disabled));

    /// <summary>
    /// The analyzer's diagnostics of the generated compilation that the corpus must not have (none: it is written as users
    /// should), but EECS055: the methods over components of several fields stay scalar, and the message says so; and
    /// EECS086 until BattleServer.cs takes its Battle totals as an [Accumulate] reduction (docs/plans/docs-todos.md,
    /// feat-generator-gaps).
    /// </summary>
    public static Diagnostic[] Findings(Compilation generated) => generated.WithAnalyzers(EasyEcsAnalyzer.All).GetAnalyzerDiagnosticsAsync().Result
        .Where(d => d.Severity >= DiagnosticSeverity.Info && d.Id is not ("EECS055" or "EECS086")).ToArray();

    /// <summary>One module of a game: components, a buffer element, systems with [ForEach] methods and a query loop.</summary>
    public static string Module(int m, bool oneNamespace)
    {
        var sb = new StringBuilder("using EasyEcs;\n\nnamespace Game").Append(oneNamespace ? "" : ".Module" + m).Append(";\n\n");
        for (int c = 0; c < 10; c++)
            sb.Append(c switch
            {
                0 => $"public struct Tag{m}_{c} : IComponent;\n",
                1 => $"public partial record struct Score{m}_{c}([LookupKey] int Value) : IComponent;\n",
                2 or 3 => $"public record struct Tracked{m}_{c}(int V, float W) : IComponent, ITracked;\n",
                _ => $"public struct Data{m}_{c} : IComponent {{ public int V; public float X, Y; }}\n",
            });
        sb.Append($"public record struct Item{m}(int Id);\n");
        sb.Append($"public record struct Clock{m}(long Tick) : ISingletonComponent;\n\n");
        for (int s = 0; s < 5; s++)
        {
            string a = $"Data{m}_{4 + s}", b = $"Data{m}_{5 + s % 5}", t = $"Tracked{m}_{2 + s % 2}";
            sb.Append($$"""
                [EcsSystem]
                public ref struct System{{m}}_{{s}}
                {
                    [ForEach, Without<Tag{{m}}_0>]
                    static void Move(ref {{a}} p, in {{b}} q)
                    {
                        p.V += 1;
                        p.X += q.X;
                    }

                    [ForEach]
                    static void Watch(Mut<{{t}}> t, in Clock{{m}} clock)
                    {
                        if (clock.Tick % 2 == 0) t.SetIfChanged(new {{t}}(t.Value.V + 1, t.Value.W));
                    }

                    [ForEach]
                    static void Pick(Mut<DynamicBuffer<Item{{m}}>> items, in {{b}} q)
                    {
                        if (q.V > 100 && items.Value.Count < 8) items.Write().Add(new Item{{m}}(q.V));
                    }

                    [ForEach]
                    static void Expire(Entity e, in {{a}} p)
                    {
                        if (p.V > 1000) e.Destroy();
                    }

                    [ForEach]
                    static void Tune(ref {{b}} q)
                    {
                        q.X = Rules{{m}}.Scaled(q.X, q.V);
                        if (Rules{{m}}.Over(q.Y)) Rules{{m}}.Settle(ref q.Y);
                    }
                }


                """);
        }

        sb.Append($$"""
            public static class Report{{m}}
            {
                public static int Sum(World world)
                {
                    int sum = 0;
                    foreach (var item in world.Read<Data{{m}}_4, Tracked{{m}}_2>()) sum += item.Data{{m}}_4.V + item.Tracked{{m}}_2.V;
                    return sum;
                }

                public static void Reset(World world)
                {
                    foreach (var item in world.Query<Data{{m}}_5, Data{{m}}_6>()) item.Data{{m}}_5.V = item.Data{{m}}_6.V;
                }
            }

            public static class Rules{{m}}
            {
                public static float Scaled(float x, int v)
                {
                    if (v < 0) return x;
                    return x * 0.5f + v;
                }

                public static bool Over(float y) => y > 100;

                public static void Settle(ref float y) => y = 100;
            }

            [View]
            public ref struct Body{{m}}
            {
                public ref Data{{m}}_6 P;
                public ref readonly Data{{m}}_7 Q;

                public void Step(float by) => P.Y += Q.Y * by;
            }

            [EcsSystem]
            public ref struct Bodies{{m}}
            {
                [ForEach]
                static void Walk(Body{{m}} body) => body.Step(2);
            }

            public static class Gameplay{{m}}
            {
                public static Entity Spawn(World world, int v) => world.CreateEntity(new Data{{m}}_4 { V = v }, new Tracked{{m}}_2(v, 0f));

                public static void Hit(Entity e, int damage)
                {
                    e.Data{{m}}_4.V -= damage;
                    if (e.Data{{m}}_4.V <= 0) e.Destroy();
                    else if (e.HasData{{m}}_7) e.RemoveData{{m}}_7();
                }

                public static void Heal(Entity e, int amount) => e.SetTracked{{m}}_2(e.Tracked{{m}}_2.V + amount, e.Tracked{{m}}_2.W);
            }

            """);
        return sb.ToString();
    }

    /// <summary>A file with no EasyEcs code, in two versions (a keystroke changes one into the other).</summary>
    public static string Unrelated(int version) => $$"""
        namespace Game.Util;

        public static class Text
        {
            public static string Greeting(string name) => "Hello " + name + {{version}};
        }

        """;
}
