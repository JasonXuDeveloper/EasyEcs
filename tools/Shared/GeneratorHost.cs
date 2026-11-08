using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using EasyEcs;
using EasyEcs.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

/// <summary>
/// What every host that drives the EasyEcs generators through Roslyn shares, so each compiles user code as a project
/// referencing the package does: the unit tests (GeneratorTests and the analyzer and code fix tests), the docs' fence
/// checker (tools/docs/DocCheck) and GeneratorBenchmark's corpus (linked into all three). A generator added to the package
/// is added here once.
/// </summary>
internal static class GeneratorHost
{
    /// <summary>The package's generators, as its analyzer folder ships them (ArityGenerator writes only into
    /// EasyEcs.Core and stays idle elsewhere, as in a user's build).</summary>
    public static ISourceGenerator[] Generators() =>
    [
        new ArityGenerator().AsSourceGenerator(), new ComponentGenerator().AsSourceGenerator(), new SystemGenerator().AsSourceGenerator(),
        new PartGenerator().AsSourceGenerator(), new ViewGenerator().AsSourceGenerator(), new PrefabGenerator().AsSourceGenerator(),
    ];

    /// <summary>The language version of user code, with the interceptors of lambda passes and tile loads taken from
    /// EasyEcs.Generated, as the package's build files set it (EasyEcs.props).</summary>
    public static readonly CSharpParseOptions Parse = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview)
        .WithFeatures([new("InterceptorsNamespaces", "EasyEcs.Generated")]);

    /// <summary>The framework assemblies of the running host (enough for the generated code to compile).</summary>
    public static readonly ImmutableArray<MetadataReference> Framework =
    [
        .. Trusted.Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) is "netstandard.dll" or "mscorlib.dll")
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)),
    ];

    /// <summary><see cref="Framework"/>, EasyEcs.Core and the assemblies it references (Nino.Core): a project
    /// referencing the package.</summary>
    public static readonly ImmutableArray<MetadataReference> References =
    [
        .. Framework,
        MetadataReference.CreateFromFile(typeof(World).Assembly.Location),
        .. typeof(World).Assembly.GetReferencedAssemblies()
            .Select(a => Trusted.FirstOrDefault(p => Path.GetFileNameWithoutExtension(p) == a.Name))
            .Where(p => p != null && !Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) && Path.GetFileName(p) is not ("netstandard.dll" or "mscorlib.dll"))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p!)),
    ];

    private static string[] Trusted => ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
}
