using System.Collections.Generic;
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

        if (Symbols.Named(starter.ContainingType, "System.Threading.Tasks.Parallel")) return true; // runs at once
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

    /// <summary>
    /// A write made by code that runs on several threads at once (a parallel [ForEach] method, the <c>Execute</c> of a
    /// job run in parallel): what it writes (<see cref="Symbol"/>: the static field or property, the context parameter,
    /// or the job's own field), whether every thread shares it (<see cref="Shared"/>; else the location is the job's
    /// own field, a copy per chunk), and whether the value written is a constant (every thread writes the same).
    /// </summary>
    public sealed record Write(IOperation Operation, ISymbol Symbol, bool Shared, bool Constant, string What);

    /// <summary>
    /// The writes in <paramref name="body"/> that reach memory outside the row being visited: a static field or
    /// auto-property (not <c>[ThreadStatic]</c>), an object a context parameter refers to (a [ForEach] method outside
    /// a system), and with <paramref name="job"/> the job's own fields and the objects they refer to. Assignments,
    /// <c>++</c> / <c>--</c>, <c>ref</c> / <c>out</c> arguments and the mutating calls of collections
    /// (<see cref="Symbols.MutatedReceiver"/>) count. Left out: writes inside a <c>lock</c>, the calls of
    /// <c>System.Threading</c> (<c>Interlocked</c>, <c>Volatile</c>), concurrent collections, and elements of arrays
    /// and lists (rows may own different elements). Conservative: a location reached through a local or a call's
    /// result is not followed.
    /// </summary>
    public static IEnumerable<Write> Writes(IOperation body, bool job)
    {
        foreach (var operation in body.Descendants())
        {
            IOperation? target;
            bool contents = false, constant = false;
            switch (operation)
            {
                case ISimpleAssignmentOperation { IsRef: false } assignment:
                    target = assignment.Target;
                    constant = assignment.Value.ConstantValue.HasValue;
                    break;
                case ICompoundAssignmentOperation or ICoalesceAssignmentOperation:
                    target = ((IAssignmentOperation)operation).Target;
                    break;
                case IIncrementOrDecrementOperation step:
                    target = step.Target;
                    break;
                case IInvocationOperation call when Symbols.MutatedReceiver(call) is { } receiver && !ThreadSafe(receiver.Type):
                    target = receiver;
                    contents = true;
                    break;
                case IArgumentOperation { Parameter.RefKind: RefKind.Ref or RefKind.Out, Parent: IInvocationOperation callee } argument
                    when !Symbols.Named(callee.TargetMethod.ContainingNamespace, "System.Threading"):
                    target = argument.Value;
                    break;
                default:
                    continue;
            }

            if (Locked(operation, body) || Reached(target, contents, job) is not var (symbol, shared, what)) continue;
            yield return new Write(operation, symbol, shared, constant, what);
        }
    }

    /// <summary>
    /// The location a write's target is in (see <see cref="Writes"/>), or null: walked from the written member to its
    /// root, noting whether the way passes through an object (a reference held by a field, property or parameter).
    /// </summary>
    private static (ISymbol Symbol, bool Shared, string What)? Reached(IOperation? target, bool contents, bool job)
    {
        bool reference = contents, first = !contents; // the receiver of a call is read, not written
        IFieldSymbol? own = null;
        while (true)
        {
            switch (target)
            {
                case IFieldReferenceOperation { Field: var field } access:
                    if (field.IsStatic)
                        return field.IsConst || field.GetAttributes().Any(a => Symbols.Named(a.AttributeClass, "System.ThreadStaticAttribute"))
                            ? null
                            : (field, true, (reference ? "an object static field '" : "static field '") + field.ContainingType.Name + "." + field.Name + "'" + (reference ? " holds" : ""));
                    own = field;
                    target = access.Instance;
                    break;
                case IPropertyReferenceOperation { Property: { IsIndexer: false } property } access:
                    // a property written is only known to be storage when it is an auto-property declared in source
                    if (first && !IsAutoProperty(property)) return null;
                    if (property.IsStatic)
                        return reference || first
                            ? (property, true, (reference ? "an object static property '" : "static property '") + property.ContainingType.Name + "." + property.Name + "'" + (reference ? " holds" : ""))
                            : null;
                    own = null;
                    target = access.Instance;
                    break;
                case IConversionOperation conversion:
                    target = conversion.Operand;
                    continue;
                case IInstanceReferenceOperation when job && own != null:
                    return reference
                        ? (own, true, $"the object field '{own.Name}' holds, which every chunk's copy of the job shares")
                        : (own, false, $"its field '{own.Name}'");
                case IParameterReferenceOperation { Parameter: var parameter }
                    when reference && parameter.Type.IsReferenceType && parameter.ContainingSymbol is IMethodSymbol method
                         && Symbols.IsForEach(method) && Symbols.Classify(parameter, Symbols.IsRunner(method)) == Symbols.ParamKind.Context:
                    return (parameter, true, $"the object parameter '{parameter.Name}' refers to, which every chunk is given");
                default:
                    return null; // a local, a row's component, an element, a call's result
            }

            first = false;
            if (target?.Type is { IsReferenceType: true }) reference = true; // the member written lives in that object
        }
    }

    /// <summary>An auto-property declared in source: its backing field is the storage a write reaches.</summary>
    private static bool IsAutoProperty(IPropertySymbol property) =>
        property.SetMethod != null && property.ContainingType.GetMembers().OfType<IFieldSymbol>()
            .Any(f => SymbolEqualityComparer.Default.Equals(f.AssociatedSymbol, property));

    /// <summary>A collection made for several threads (<c>System.Collections.Concurrent</c>, channels).</summary>
    private static bool ThreadSafe(ITypeSymbol? type) =>
        type?.ContainingNamespace is { } ns && (Symbols.Named(ns, "System.Collections.Concurrent") || Symbols.Named(ns, "System.Threading.Channels"));

    /// <summary>
    /// Inside a <c>lock</c> statement of <paramref name="body"/>, or a <c>using</c> of a
    /// <c>System.Threading.Lock</c>'s <c>EnterScope()</c> (what <c>lock</c> on a Lock compiles to).
    /// </summary>
    private static bool Locked(IOperation operation, IOperation body)
    {
        for (var node = operation.Parent; node != null && node != body; node = node.Parent)
            if (node is ILockOperation || node is IUsingOperation { Resources: var resource } && EntersLock(resource)) return true;
        return false;
    }

    private static bool EntersLock(IOperation resource) => resource switch
    {
        IInvocationOperation { TargetMethod: { Name: "EnterScope" } method } => Symbols.Named(method.ContainingType, "System.Threading.Lock"),
        IVariableDeclarationGroupOperation group => group.Declarations.Any(d => d.Declarators.Any(v => v.Initializer?.Value is { } value && EntersLock(value))),
        IConversionOperation conversion => EntersLock(conversion.Operand),
        _ => false,
    };
}
