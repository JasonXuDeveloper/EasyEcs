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

// EasyEcsAnalyzer.Parts.cs: generated parts and the world setup a type declares (EECS062-069, EECS102).
public partial class AnalyzerTests
{
    [TestCase("public partial struct Z { [GroupBy] public int Id; }")]                                                               // not a component
    [TestCase("public partial record struct Z([GroupBy] int Id) : ISingletonComponent;")]                                    // not a component
    [TestCase("[WhenTargetDestroyed(TargetPolicy.Remove)] public record struct W(int Id) : IComponent;")]                   // no link
    [TestCase("[WhenTargetDestroyed(TargetPolicy.Remove)] public partial record struct W([LookupKey] int Id) : IComponent;")]     // not an EntityId key
    [TestCase("[NotSaved] public record struct N(object O);")]                                                              // no component
    [TestCase("[Accumulate] public record struct A(int N) : IComponent;")]                                                  // not a singleton
    [TestCase("[Accumulate] public record struct A(int N) : ISingletonComponent;")]                                         // does not combine
    public async Task AWorldSetupOnATypeItDoesNotFitIsReported(string source)
    {
        Assert.That((await Analyze([source])).Where(d => d.Id.StartsWith("EECS") || d.Severity == DiagnosticSeverity.Error).Select(d => d.Id).Distinct(),
            Is.EqualTo(new[] { "EECS068" }));
        Driver().RunGeneratorsAndUpdateCompilation(Compile(source), out var output, out _);
        var saved = output.SyntaxTrees.SingleOrDefault(t => t.FilePath.EndsWith("EasyEcs.TypeRegistry.g.cs"))?.ToString() ?? "";
        Assert.That(saved, Does.Not.Contain("GroupBy<").And.Not.Contain("WhenTargetDestroyed<").And.Not.Contain("NotSaved<"));
    }

    [TestCase("public record struct S(object O) : IComponent;", DiagnosticSeverity.Info)]
    [TestCase("public record struct S(string Name) : ISingletonComponent; public static class U { public static void F(World w, System.Buffers.IBufferWriter<byte> o) => w.Save(o); }", DiagnosticSeverity.Warning)]
    [TestCase("public record struct S(object O) : IComponent; public static class U { public static void F(World w, System.Buffers.IBufferWriter<byte> o) => w.SaveEntities(o); }", DiagnosticSeverity.Warning)]
    [TestCase("[NotSaved] public record struct S(object O) : IComponent; public static class U { public static void F(World w, System.Buffers.IBufferWriter<byte> o) => w.Save(o); }", null)]
    [TestCase("namespace Nino.Core { public sealed class NinoTypeAttribute : System.Attribute; } [Nino.Core.NinoType] public partial record struct S(string Name) : IComponent; public static class U { public static void F(World w, System.Buffers.IBufferWriter<byte> o) => w.Save(o); }", null)]
    [TestCase("public record struct S(int N) : IComponent; public static class U { public static void F(World w, System.Buffers.IBufferWriter<byte> o) => w.Save(o); }", null)] // unmanaged: raw rows
    [TestCase("public record struct S(object O); public static class U { public static void F(World w, System.Buffers.IBufferWriter<byte> o) => w.Save(o); }", null)]           // not a component
    [TestCase("public record struct S(object O) : IComponent; public static class U { public static void F(World w, System.Buffers.IBufferWriter<byte> o) => w.SaveWithout(o, ComponentSet.Create().With<S>()); }", null)] // left out by the code
    [TestCase("public record struct S(string Name) : ISingletonComponent; public static class U { static readonly ComponentSet Keep = ComponentSet.Create().WithSingleton<S>(); }", null)]  // named in a set
    [TestCase("public record struct S(string Name) : ISingletonComponent; public static class U { public static void F(World w, System.Buffers.IBufferWriter<byte> o) => w.SaveEntities(o); }", DiagnosticSeverity.Info)] // entity saves write no singletons
    public async Task AManagedTypeASaveCannotWriteIsReported(string source, DiagnosticSeverity? severity)
    {
        var found = (await Analyze([source])).Where(d => d.Id == "EECS069").ToArray();
        Assert.That(found.Select(d => (DiagnosticSeverity?)d.Severity), Is.EqualTo(severity == null ? [] : new[] { severity }));
    }
}
