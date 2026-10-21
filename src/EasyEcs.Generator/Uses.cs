using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace EasyEcs.Generator;

/// <summary>
/// Whether a use of a value held by reference (a <c>ref</c> parameter, a <c>Span&lt;T&gt;</c>, the ref an
/// <c>Entity.Get&lt;T&gt;()</c> returns) may write it: what EECS025 must rule out before it suggests a read-only access.
/// Conservative: anything it cannot prove to be a read counts as a write, so a missed read costs a missed warning,
/// never a wrong one.
/// </summary>
internal static class Uses
{
    /// <summary>
    /// Whether <paramref name="use"/> (a reference to the variable, or a call returning its ref) may write it or data
    /// it holds by reference: an assignment, <c>++</c> / <c>--</c>, a <c>ref</c> / <c>out</c> argument (a <c>this ref</c>
    /// extension too), a <c>ref</c> local (not <c>ref readonly</c>) or return, a call of a member that is not <c>readonly</c>, or a mutable
    /// reference (a class, a span, a struct holding one) that escapes, reached through any chain of members.
    /// </summary>
    public static bool MayWrite(IOperation use)
    {
        var current = use;
        while (true)
        {
            switch (current.Parent)
            {
                case null:
                    return true;
                case IConversionOperation conversion:
                    var operand = conversion.Operand.Type;
                    if (IsReadOnlyView(conversion.Type)) return false;             // Span<T> -> ReadOnlySpan<T>
                    if (operand is { IsValueType: true } && conversion.Type is { IsReferenceType: true })
                        return Mutable(operand, 0);                                // boxing: a copy
                    if (conversion.OperatorMethod != null) return Mutable(operand, 0); // a user conversion takes a copy
                    current = conversion;
                    continue;
                case IFieldReferenceOperation field when field.Instance == current:
                    current = field;
                    continue;
                case IPropertyReferenceOperation property when property.Instance == current:
                    if (current.Type is { IsValueType: true } owner && !ReadOnlyMember(property.Property.GetMethod, owner)
                        && !IsSpan(owner))
                        return true;                                               // a getter that may change the struct
                    current = property;
                    continue;
                case IArrayElementReferenceOperation element when element.ArrayReference == current:
                    current = element;
                    continue;
                case IInvocationOperation call when call.Instance == current:
                    switch (Call(call, current))
                    {
                        case Step.Read: return false;
                        case Step.Write: return true;
                        default: current = call; continue;                         // a span's Slice: still the same data
                    }
                case IArgumentOperation argument:
                    if (argument.Parameter is not { } parameter) return true;
                    if (parameter.RefKind is RefKind.Ref or RefKind.Out) return true;
                    if (parameter.RefKind is RefKind.In or RefKind.RefReadOnlyParameter) return false;
                    return Mutable(current.Type, 0);                               // by value: a copy of what it holds
                case IAssignmentOperation assignment when assignment.Target == current:
                    return true;
                case ISimpleAssignmentOperation assignment:
                    // 'x = ref value' binds a ref local: a write only when the local can write ('ref', not 'ref readonly').
                    return assignment.IsRef && assignment.Target is not ILocalReferenceOperation { Local.RefKind: RefKind.RefReadOnly }
                           || Mutable(current.Type, 0);
                case IAssignmentOperation:                                          // the value of a compound assignment
                    return Mutable(current.Type, 0);
                case IIncrementOrDecrementOperation or IAddressOfOperation:
                    return true;
                case ITupleOperation tuple:
                    if (tuple.Parent is IDeconstructionAssignmentOperation deconstruction && deconstruction.Target == tuple) return true;
                    current = tuple;
                    continue;
                case IVariableInitializerOperation { Parent: IVariableDeclaratorOperation declarator }:
                    // A 'ref' local can write it; a 'ref readonly' one reads, like 'in' (what it holds by reference aside).
                    return declarator.Symbol.RefKind == RefKind.Ref || Mutable(current.Type, 0);
                case IReturnOperation:
                    return ReturnsByRef(current) || Mutable(current.Type, 0);
                case IExpressionStatementOperation or IUnaryOperation or IBinaryOperation or ITupleBinaryOperation
                    or IIsPatternOperation or IIsTypeOperation or IInterpolationOperation or INameOfOperation
                    or IRangeOperation or ISwitchCaseOperation:
                    return false;
                case ISwitchExpressionOperation switchExpression when switchExpression.Value == current:
                    return false;
                case ISwitchOperation switchStatement when switchStatement.Value == current:
                    return false;
                case IConditionalOperation conditional:
                    if (conditional.Condition == current) return false;
                    if (conditional.IsRef) return true;
                    current = conditional;
                    continue;
                case ICoalesceOperation coalesce:
                    current = coalesce;
                    continue;
                case IWhileLoopOperation or IForLoopOperation:                       // their condition
                    return false;
                case IForEachLoopOperation loop when loop.Collection == current:
                    return Enumerates(loop, current.Type);
                case IWithOperation or IArrayInitializerOperation or ICollectionExpressionOperation or IMemberInitializerOperation
                    or IAnonymousObjectCreationOperation or ISpreadOperation or IPropertyInitializerOperation:
                    return Mutable(current.Type, 0);
                default:
                    return true;
            }
        }
    }

    private enum Step { Read, Write, Continue }

    /// <summary>A call on <paramref name="receiver"/>: a read, a write, or a span's slice of the same data.</summary>
    private static Step Call(IInvocationOperation call, IOperation receiver)
    {
        var type = receiver.Type;
        var method = call.TargetMethod;
        if (type is { IsValueType: true })
        {
            if (IsSpan(type))
                return method.Name switch
                {
                    "CopyTo" or "TryCopyTo" or "ToArray" or "ToString" => Step.Read,   // they read the span
                    "Slice" => Step.Continue,
                    _ => Step.Write,                                                    // Fill, Clear, GetPinnableReference
                };
            if (!ReadOnlyMember(method, type)) return Step.Write;
            if (method.ReturnsByRef) return Step.Continue;
            return Mutable(method.ReturnType, 0) ? Step.Write : Step.Read;
        }

        return type != null && Immutable(type) ? Step.Read : Step.Write;            // a class's method may change it
    }

    /// <summary>A <c>foreach</c> over data the variable holds: a write if it takes elements by ref or may change them.</summary>
    private static bool Enumerates(IForEachLoopOperation loop, ITypeSymbol? collection)
    {
        if (loop.LoopControlVariable is IVariableDeclaratorOperation { Symbol.RefKind: RefKind.Ref }) return true;
        if (collection is { IsValueType: true } && !IsSpan(collection) && !IsReadOnlyView(collection)
            && !collection.GetMembers("GetEnumerator").OfType<IMethodSymbol>().Any(m => m.Parameters.Length == 0 && ReadOnlyMember(m, collection)))
            return true;
        var element = (loop.LoopControlVariable as IVariableDeclaratorOperation)?.Symbol.Type;
        return element == null || Mutable(element, 0);
    }

    private static bool ReturnsByRef(IOperation operation)
    {
        for (var node = operation.Parent; node != null; node = node.Parent)
            switch (node)
            {
                case IAnonymousFunctionOperation function: return function.Symbol.ReturnsByRef;
                case ILocalFunctionOperation local: return local.Symbol.ReturnsByRef;
                case IMethodBodyOperation or IBlockOperation { Parent: null }:
                    return operation.SemanticModel?.GetEnclosingSymbol(operation.Syntax.SpanStart) is IMethodSymbol { ReturnsByRef: true };
            }

        return true;
    }

    /// <summary>A member that cannot change its struct: declared <c>readonly</c>, of a <c>readonly struct</c>, or an auto getter.</summary>
    private static bool ReadOnlyMember(IMethodSymbol? method, ITypeSymbol owner) =>
        method != null && (method.IsReadOnly || owner.IsReadOnly || method.IsStatic);

    /// <summary>
    /// Whether a copy of a <paramref name="type"/> value still reaches data the original holds: a class (except
    /// strings, immutable and frozen collections), a span or memory, or a struct with such a field (four levels deep).
    /// </summary>
    public static bool Mutable(ITypeSymbol? type, int depth)
    {
        switch (type)
        {
            case null or ITypeParameterSymbol or IPointerTypeSymbol or IFunctionPointerTypeSymbol or IErrorTypeSymbol:
                return true;
            case { IsReferenceType: true }:
                return !Immutable(type);
            case INamedTypeSymbol named when IsSpan(named) || (Symbols.Named(named.OriginalDefinition, "System.Memory<T>") || Symbols.Named(named.OriginalDefinition, "System.ArraySegment<T>")):
                return true;
            case INamedTypeSymbol named when IsReadOnlyView(named):
                return false;
            case INamedTypeSymbol { SpecialType: not SpecialType.None }:
                return false;                                                       // int, float, decimal, DateTime, ...
            case INamedTypeSymbol { TypeKind: TypeKind.Enum }:
                return false;
            case INamedTypeSymbol named:
                if (depth > 4) return true;
                if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T) return Mutable(named.TypeArguments[0], depth + 1);
                foreach (var member in named.GetMembers())
                    if (member is IFieldSymbol { IsStatic: false } field && Mutable(field.Type, depth + 1)) return true;
                return false;
            default:
                return true;
        }
    }

    private static bool Immutable(ITypeSymbol type) =>
        type.SpecialType == SpecialType.System_String
        || (Symbols.Named(type.ContainingNamespace, "System.Collections.Immutable") || Symbols.Named(type.ContainingNamespace, "System.Collections.Frozen"));

    private static bool IsSpan(ITypeSymbol? type) => Symbols.Named(type?.OriginalDefinition, "System.Span<T>");

    private static bool IsReadOnlyView(ITypeSymbol? type) =>
        (Symbols.Named(type?.OriginalDefinition, "System.ReadOnlySpan<T>") || Symbols.Named(type?.OriginalDefinition, "System.ReadOnlyMemory<T>"));
}
