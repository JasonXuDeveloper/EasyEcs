using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EasyEcs.Generator;

/// <summary>
/// The facts the analyzer reads from a [ForEach] method's body (<see cref="Symbols.IsStable(IMethodSymbol, MethodDeclarationSyntax, SemanticModel)"/>,
/// <see cref="Symbols.OnlyIdentifies"/>, <see cref="Symbols.ParallelProblem"/>), computed once per method and
/// compilation: several rules ask them of the same method (EECS010, EECS013, EECS060, EECS093/094 per method of a
/// system, EECS034 per call), and each answer walks the body. Keyed by the compilation, since a body's meaning can
/// change with another file; the table lets a compilation go once no analysis holds it.
/// </summary>
internal static class BodyFacts
{
    private sealed class Facts
    {
        // Each fact is computed at most once per thread that races for it; the answer is the same either way.
        public int Stable = -1, Identifies = -1;
        public string? Parallel;
        public bool ParallelKnown;
    }

    private static readonly ConditionalWeakTable<Compilation, ConcurrentDictionary<IMethodSymbol, Facts>> PerCompilation = new();

    private static Facts Of(IMethodSymbol method, SemanticModel model) =>
        PerCompilation.GetValue(model.Compilation, static _ => new ConcurrentDictionary<IMethodSymbol, Facts>(SymbolEqualityComparer.Default))
            .GetOrAdd(method, static _ => new Facts());

    /// <summary>Whether <paramref name="syntax"/> is the declaration the facts are kept for (the method's first one).</summary>
    private static bool IsFirst(IMethodSymbol method, MethodDeclarationSyntax syntax) =>
        method.DeclaringSyntaxReferences.FirstOrDefault() is { } first && first.SyntaxTree == syntax.SyntaxTree && first.Span == syntax.Span;

    /// <summary><see cref="Symbols.IsStable(IMethodSymbol, MethodDeclarationSyntax, SemanticModel)"/>, once per method.</summary>
    public static bool IsStable(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model)
    {
        if (!method.IsStatic) return false;
        if (!IsFirst(method, syntax)) return Symbols.IsStable(method, syntax, model);
        var facts = Of(method, model);
        if (facts.Stable < 0) facts.Stable = Symbols.IsStable(method, syntax, model) ? 1 : 0;
        return facts.Stable == 1;
    }

    /// <summary><see cref="Symbols.IsStable(IMethodSymbol, SemanticModel)"/>, once per method.</summary>
    public static bool IsStable(IMethodSymbol method, SemanticModel model) =>
        method.IsStatic && method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is MethodDeclarationSyntax syntax
        && syntax.SyntaxTree == model.SyntaxTree && IsStable(method, syntax, model);

    /// <summary><see cref="Symbols.OnlyIdentifies"/>, once per method.</summary>
    public static bool OnlyIdentifies(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model)
    {
        if (!IsFirst(method, syntax)) return Symbols.OnlyIdentifies(method, syntax, model);
        var facts = Of(method, model);
        if (facts.Identifies < 0) facts.Identifies = Symbols.OnlyIdentifies(method, syntax, model) ? 1 : 0;
        return facts.Identifies == 1;
    }

    /// <summary><see cref="Symbols.ParallelProblem"/>, once per method (its stability check through this cache).</summary>
    public static string? ParallelProblem(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model)
    {
        if (!IsFirst(method, syntax)) return Symbols.ParallelProblem(method, syntax, model);
        var facts = Of(method, model);
        if (Volatile.Read(ref facts.ParallelKnown)) return facts.Parallel;
        string? problem = Symbols.ParallelProblem(method, syntax, model, IsStable(method, syntax, model));
        facts.Parallel = problem;
        Volatile.Write(ref facts.ParallelKnown, true);
        return problem;
    }
}
