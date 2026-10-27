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

/// <summary>
/// EasyEcsAnalyzer's rules, one file per family of the analyzer's source (AnalyzerTests.Systems.cs for
/// EasyEcsAnalyzer.Systems.cs, ...): what each rule reports, what it leaves alone, and the edges its logic branches on.
/// </summary>
public partial class AnalyzerTests
{
    /// <summary>
    /// The case compiles and reports <paramref name="id"/> and no other EasyEcs diagnostic of
    /// <see cref="Diagnose"/>, every message of it filled in (no placeholder left).
    /// </summary>
    internal static async Task AssertReports(string id, params string[] sources)
    {
        var found = await Diagnose(sources);
        Assert.That(found.Where(e => !e.StartsWith("CS")), Is.EqualTo(new[] { id }), "exactly one EasyEcs diagnostic: " + string.Join(", ", found));
        Assert.That(found.Where(e => e.StartsWith("CS")), Is.Empty, "the case compiles");
        foreach (var diagnostic in (await GeneratorTests.Analyze(sources)).Where(d => d.Id == id))
            Assert.That(diagnostic.GetMessage(), Does.Not.Match(@"\{\d+\}"), "a placeholder left in " + id);
    }

    /// <summary>The case reports no EasyEcs diagnostic of <see cref="Diagnose"/> and no compiler error.</summary>
    internal static async Task AssertClean(params string[] sources) =>
        Assert.That(await Diagnose(sources), Is.Empty);

    /// <summary>How often the case reports <paramref name="id"/>: one mistake, one diagnostic.</summary>
    internal static async Task<int> Count(string id, params string[] sources) =>
        (await GeneratorTests.Analyze(sources)).Count(d => d.Id == id);

    /// <summary>
    /// Errors and the diagnostics of the correctness rules (EECS002-024). The performance rules (EECS025-033) are left
    /// out: the cases here write the shortest code that shows a rule, which often also costs; <see cref="Costs"/> checks them.
    /// So are the suggestion to declare a hand-written key with [LookupKey] (EECS067, PartTests) and the formatter a managed type
    /// lacks (EECS069): the shared components write keys by hand and hold references. So are the suggestions to write
    /// a hand-written one-entity job or consuming pass with attributes (EECS080, EECS084; FormTests), and the passes
    /// that almost share one walk (EECS093; FusionTests): the cases often write two methods over the same rows.
    /// </summary>
    internal static async Task<string[]> Diagnose(params string[] sources) =>
        (await Analyze(sources)).Where(d => d.Severity == DiagnosticSeverity.Error || d.Id.StartsWith("EECS") && !IsPerformance(d.Id) && d.Id != "EECS055" && d.Id is not ("EECS067" or "EECS069" or "EECS080" or "EECS084" or "EECS093"))
            .Select(d => d.Id).Distinct().OrderBy(x => x).ToArray();

    /// <summary>The diagnostics of the performance rules (EECS025-034, EECS039, EECS046, EECS052, EECS053), with their properties for the code fixes.</summary>
    internal static async Task<Diagnostic[]> Costs(params string[] sources) =>
        (await Analyze(sources)).Where(d => (IsPerformance(d.Id) || d.Id == "EECS034") && !(d.Id == "EECS086" && d.Severity == DiagnosticSeverity.Info))
        .OrderBy(d => d.Location.SourceSpan.Start).ToArray();

    // The performance rules, and the suggestions to declare with attributes what a hand-written reduction does (EECS065)
    // and to take the parameter that does what the method does by hand (EECS086, EECS087, EECS089): the shortest code
    // that shows a rule often writes one by hand. EECS086's suggestion for a total (Info: a counter like Clock.Ticks++)
    // is left to FormTests.
    private static bool IsPerformance(string id) => string.CompareOrdinal(id, "EECS025") >= 0 && string.CompareOrdinal(id, "EECS033") <= 0
        || id is "EECS039" or "EECS046" or "EECS052" or "EECS053" or "EECS065" or "EECS086" or "EECS087" or "EECS089";

    /// <summary>Every EasyEcs diagnostic but EECS055 (the vector loop's message, asserted by <see cref="GeneratorTests.ScalarLoopsNameTheirReason"/>).</summary>
    internal static async Task<Diagnostic[]> All(string source) => (await Analyze([Components, Extra, source])).Where(d => d.Id.StartsWith("EECS") && d.Id != "EECS055" && d.Id is not ("EECS067" or "EECS069" or "EECS093") || d.Severity == DiagnosticSeverity.Error).OrderBy(d => d.Location.SourceSpan.Start).ToArray();

    internal const string Extra = """
        public struct Counter : IComponent { public int V; public void Bump() => V++; public readonly int Peek() => V; }
        public struct Boss : IComponent;
        public struct Loot { public int Item; }
        public record struct Zone(int Id) : IComponent, IGroup { public readonly int Group => Id; }
        public record struct Area(int Id) : IComponent, IGroup { public readonly int Group => Id; }
        public record struct Rel(EntityId Target) : IRelation;
        public struct Lease : ICleanupComponent { public int V; }
        public static class Helpers { public static void Inc(ref int v) => v++; public static int Look(in P p) => p.V; }
        """;
}
