using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>
/// EasyEcsAnalyzer's rules, one file per family of the analyzer's source (AnalyzerTests.Systems.cs for
/// EasyEcsAnalyzer.Systems.cs, ...): what each rule reports, what it leaves alone, and the edges its logic branches on.
/// </summary>
public partial class AnalyzerTests
{
    /// <summary>
    /// The case compiles and reports <paramref name="id"/> and no other EasyEcs diagnostic of
    /// <see cref="GeneratorTests.Diagnose"/>, every message of it filled in (no placeholder left).
    /// </summary>
    internal static async Task AssertReports(string id, params string[] sources)
    {
        var found = await GeneratorTests.Diagnose(sources);
        Assert.That(found.Where(e => !e.StartsWith("CS")), Is.EqualTo(new[] { id }), "exactly one EasyEcs diagnostic: " + string.Join(", ", found));
        Assert.That(found.Where(e => e.StartsWith("CS")), Is.Empty, "the case compiles");
        foreach (var diagnostic in (await GeneratorTests.Analyze(sources)).Where(d => d.Id == id))
            Assert.That(diagnostic.GetMessage(), Does.Not.Match(@"\{\d+\}"), "a placeholder left in " + id);
    }

    /// <summary>The case reports no EasyEcs diagnostic of <see cref="GeneratorTests.Diagnose"/> and no compiler error.</summary>
    internal static async Task AssertClean(params string[] sources) =>
        Assert.That(await GeneratorTests.Diagnose(sources), Is.Empty);

    /// <summary>How often the case reports <paramref name="id"/>: one mistake, one diagnostic.</summary>
    internal static async Task<int> Count(string id, params string[] sources) =>
        (await GeneratorTests.Analyze(sources)).Count(d => d.Id == id);
}
