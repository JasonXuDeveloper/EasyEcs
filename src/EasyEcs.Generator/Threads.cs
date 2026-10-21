using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace EasyEcs.Generator;

/// <summary>
/// Whether a structural call in a delegate handed to another thread (EECS014) can run at the same time as another
/// change of the same world. Handing a world to a thread that then owns it is fine (a world per thread); what races is
/// a world or entity the delegate shares: the delegate runs several times at once (Parallel.*, or it is handed off in a
/// loop), or the code that hands it off changes the same world's structure too. Syntax-local and conservative: a
/// receiver it cannot follow (a call's result) is not reported.
/// </summary>
internal static class Threads
{
    /// <param name="function">The code the thread runs: a lambda's or anonymous method's syntax, or the declaration of
    /// the method a method group hands off.</param>
    public static bool Concurrent(IInvocationOperation call, SyntaxNode function, IOperation handoff, IMethodSymbol starter,
        CancellationToken token)
    {
        var (root, throughElement) = Root(Symbols.Receiver(call));
        if (root == null || !Captured(root, function)) return false;
        bool world = Symbols.OnWorld(call);
        if (world && throughElement) return false; // worlds[i]: a world per item, each with one owner

        if (starter.ContainingType.ToDisplayString() == "System.Threading.Tasks.Parallel") return true; // runs at once
        if (Awaited(handoff.Syntax)) return false;                    // the caller waits for it: one change after the other
        if (InLoop(handoff.Syntax, root)) return true;                                                 // handed off again
        return ChangedOutside(root, function, handoff, token);
    }

    /// <summary>The variable, parameter, field or property a receiver comes from, and whether an element of it is used.</summary>
    private static (ISymbol? Root, bool ThroughElement) Root(IOperation? receiver)
    {
        bool element = false;
        while (true)
        {
            switch (receiver)
            {
                case ILocalReferenceOperation local: return (local.Local, element);
                case IParameterReferenceOperation parameter: return (parameter.Parameter, element);
                case IFieldReferenceOperation { Instance: null or IInstanceReferenceOperation } field: return (field.Field, element);
                case IPropertyReferenceOperation { Instance: null or IInstanceReferenceOperation, Arguments.Length: 0 } property:
                    return (property.Property, element);
                case IFieldReferenceOperation field: receiver = field.Instance; break; // link.Target: the link's root
                case IPropertyReferenceOperation property:
                    element |= property.Arguments.Length > 0;                              // list[i]
                    receiver = property.Instance;
                    break;
                case IArrayElementReferenceOperation array: element = true; receiver = array.ArrayReference; break;
                case IConversionOperation conversion: receiver = conversion.Operand; break;
                default: return (null, element);
            }
        }
    }

    /// <summary>
    /// The hand-off's task is waited for at once: <c>await Task.Run(…)</c> (also with <c>ConfigureAwait</c>),
    /// <c>.Wait()</c>, <c>.Result</c> or <c>.GetAwaiter().GetResult()</c>.
    /// </summary>
    private static bool Awaited(SyntaxNode handoff)
    {
        SyntaxNode node = handoff;
        while (true)
        {
            switch (node.Parent)
            {
                case AwaitExpressionSyntax:
                    return true;
                case ParenthesizedExpressionSyntax parenthesized:
                    node = parenthesized;
                    continue;
                case MemberAccessExpressionSyntax access when access.Expression == node:
                    string name = access.Name.Identifier.ValueText;
                    if (name is "Result") return true;
                    if (access.Parent is not InvocationExpressionSyntax invocation) return false;
                    if (name is "Wait" or "GetResult") return true;
                    if (name is not ("ConfigureAwait" or "GetAwaiter")) return false;
                    node = invocation;
                    continue;
                default:
                    return false;
            }
        }
    }

    /// <summary>Declared outside the delegate (fields and properties always are).</summary>
    private static bool Captured(ISymbol root, SyntaxNode function) =>
        root is not (ILocalSymbol or IParameterSymbol)
        || root.DeclaringSyntaxReferences.FirstOrDefault() is not { } declaration
        || declaration.SyntaxTree != function.SyntaxTree || !function.Span.Contains(declaration.Span);

    /// <summary>The hand-off sits in a loop whose iterations share <paramref name="root"/> (declared outside the loop).</summary>
    private static bool InLoop(SyntaxNode handoff, ISymbol root)
    {
        var declaration = root.DeclaringSyntaxReferences.FirstOrDefault();
        for (var node = handoff.Parent; node != null && node is not MemberDeclarationSyntax; node = node.Parent)
        {
            if (node is not (ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax)) continue;
            if (declaration == null || declaration.SyntaxTree != node.SyntaxTree || !node.Span.Contains(declaration.Span))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The code around the hand-off (the method for a local or parameter, the type for a field or property) also changes
    /// <paramref name="root"/>'s structure outside the delegate.
    /// </summary>
    private static bool ChangedOutside(ISymbol root, SyntaxNode function, IOperation handoff, CancellationToken token)
    {
        var model = handoff.SemanticModel;
        if (model == null) return false;
        SyntaxNode? scope = handoff.Syntax;
        while (scope != null && !(root is ILocalSymbol or IParameterSymbol
                   ? scope is MemberDeclarationSyntax
                   : scope is TypeDeclarationSyntax))
            scope = scope.Parent;
        if (scope == null) return false;

        foreach (var node in scope.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (function.Span.Contains(node.Span)) continue;
            if (model.GetOperation(node, token) is IInvocationOperation other && Symbols.IsStructural(other.TargetMethod)
                && SymbolEqualityComparer.Default.Equals(Root(Symbols.Receiver(other)).Root, root))
                return true;
        }

        return false;
    }
}
