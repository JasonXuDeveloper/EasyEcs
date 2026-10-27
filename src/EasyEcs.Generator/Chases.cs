using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace EasyEcs.Generator;

/// <summary>What a chase reads at the value a row holds (<see cref="Chase"/>).</summary>
internal enum ChaseKind
{
    /// <summary>Another entity at an id of an <c>in</c> component: <c>lookup[x.F]</c>, <c>Read</c>, <c>Has</c>.</summary>
    Entity,

    /// <summary>
    /// An index probe at a key of an <c>in</c> component: <c>lookup.TryFind(x.F, ...)</c>, <c>Find</c>, <c>Count</c> of an
    /// indexed component (its value index's slot, bucket, entry and list, by representation).
    /// </summary>
    Probe,

    /// <summary>
    /// Other entities at the ids of a buffer's elements (relation targets): <c>lookup[link.Target]</c> with <c>link</c> an
    /// element of a <c>DynamicBuffer</c> the method takes by <c>in</c> (<c>foreach</c>, the indexer, <c>AsReadOnlySpan</c>).
    /// </summary>
    Buffer,
}

/// <summary>
/// A read the row form's twin job prefetches some rows ahead (SystemGenerator.EmitChases): the value it is read at is
/// in an <c>in</c> component, so a later row's is known before that row runs.
/// </summary>
/// <param name="Lookup">The Lookup parameter's index.</param>
/// <param name="Component">The component parameter's index (the buffer's, for <see cref="ChaseKind.Buffer"/>).</param>
/// <param name="Id">The members from the component (from the buffer's element) to the id or key (<c>.Target</c>, <c>.Hunt.Prey</c>): fields or auto-properties.</param>
/// <param name="Row">An entity chase reads T's row (an indexer or Read), not only whether the entity has it (Has): both stages.</param>
/// <param name="Kind">What it reads; each kind prefetches two dependent loads, <c>ChaseDistance</c> rows apart (record, row; slot or
/// bucket, entry).</param>
internal readonly record struct Chase(int Lookup, int Component, string Id, bool Row, ChaseKind Kind = ChaseKind.Entity);

/// <summary>The chases of a <c>[ForEach]</c> method or lambda pass, read from its body once (the row form's prefetches).</summary>
internal static class Chases
{
    /// <summary>
    /// The distinct chases of <paramref name="method"/>'s body, in the order they appear; empty when it takes no
    /// Lookup (no body is bound then).
    /// </summary>
    public static EquatableArray<Chase> Of(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model, System.Threading.CancellationToken ct)
    {
        bool outside = Symbols.IsRunner(method);
        if (!method.Parameters.Any(p => Symbols.Classify(p, outside) == Symbols.ParamKind.Lookup)) return new([]);
        (syntax, model) = Symbols.WithBody(method, syntax, model);
        return model.GetOperation(syntax, ct) is IMethodBodyOperation body ? Of(body, outside) : new([]);
    }

    /// <summary>The distinct chases of a lambda pass's body (<paramref name="function"/>), as a [ForEach] method's.</summary>
    public static EquatableArray<Chase> Of(IAnonymousFunctionOperation function) =>
        function.Symbol.Parameters.Any(p => Symbols.Classify(p, true) == Symbols.ParamKind.Lookup) ? Of(function.Body, true) : new([]);

    private static EquatableArray<Chase> Of(IOperation body, bool outside)
    {
        var found = new List<Chase>();
        foreach (var op in body.Descendants())
        {
            var (lookup, at, row, probe) = op switch
            {
                IPropertyReferenceOperation { Property.IsIndexer: true, Arguments.Length: 1, Instance: IParameterReferenceOperation { Parameter: var p } } x
                    => (p, x.Arguments[0].Value, true, false),
                IInvocationOperation { TargetMethod.Name: ("Read" or "Has") and var name, Arguments.Length: 1, Instance: IParameterReferenceOperation { Parameter: var p } } call
                    => (p, call.Arguments[0].Value, name == "Read", false),
                IInvocationOperation { TargetMethod: { Name: "TryFind" or "Find" or "Count" } } call
                    when IsKeyLookup(call.TargetMethod) && Receiver(call) is IParameterReferenceOperation { Parameter: var p } && call.Arguments.FirstOrDefault(a => a.Parameter?.Name == "key") is { } key
                    => (p, key.Value, true, true),
                _ => (null, null, false, false),
            };
            if (lookup == null || Symbols.Classify(lookup, outside) != Symbols.ParamKind.Lookup || Path(at!, probe) is not ({ } root, { } members)) continue;
            if (Root(root, members, body, outside, probe) is not ({ } component, var kind)) continue;
            var chase = new Chase(lookup.Ordinal, component.Ordinal, members, row, kind);
            int same = found.FindIndex(f => f with { Row = false } == chase with { Row = false });
            if (same < 0) found.Add(chase);
            else if (row) found[same] = chase; // read somewhere: both stages
        }
        return new(found.ToArray());
    }

    /// <summary>
    /// Where the value of a chase comes from, and so its kind: an <c>in</c> component parameter (an entity chase, or a
    /// probe for <paramref name="probe"/>), or, for an entity chase, an element of a <c>DynamicBuffer</c> the method takes
    /// by <c>in</c> (<see cref="ChaseKind.Buffer"/>); else null.
    /// </summary>
    private static (IParameterSymbol, ChaseKind)? Root(IOperation root, string members, IOperation body, bool outside, bool probe)
    {
        switch (root)
        {
            case IParameterReferenceOperation { Parameter: var p } when (members.Length > 0 || probe) && InComponent(p, outside):
                return (p, probe ? ChaseKind.Probe : ChaseKind.Entity);
            case { } when probe: // a probe's key comes from a component, not from a buffer's element
                return null;
            // links[j]: the buffer's indexer.
            case IPropertyReferenceOperation { Property.IsIndexer: true, Instance: IParameterReferenceOperation { Parameter: var p } } when Buffer(p, outside):
                return (p, ChaseKind.Buffer);
            // foreach (var link in links) / foreach (ref readonly var link in links.AsReadOnlySpan()).
            case ILocalReferenceOperation { Local: var local }:
                foreach (var loop in body.Descendants().OfType<IForEachLoopOperation>())
                {
                    if (!loop.Locals.Contains(local, SymbolEqualityComparer.Default)
                        && !(loop.LoopControlVariable is IVariableDeclaratorOperation { Symbol: var declared } && SymbolEqualityComparer.Default.Equals(declared, local))) continue;
                    var collection = Unwrapped(loop.Collection);
                    if (collection is IInvocationOperation { TargetMethod.Name: "AsReadOnlySpan", Arguments.Length: 0, Instance: { } instance }) collection = Unwrapped(instance);
                    return collection is IParameterReferenceOperation { Parameter: var p } && Buffer(p, outside) ? (p, ChaseKind.Buffer) : null;
                }
                return null;
            default:
                return null;
        }
    }

    private static IOperation Unwrapped(IOperation op)
    {
        while (op is IConversionOperation c) op = c.Operand;
        return op;
    }

    private static bool InComponent(IParameterSymbol p, bool outside) => p.RefKind == RefKind.In && Symbols.Classify(p, outside) == Symbols.ParamKind.Component;

    /// <summary>A <c>DynamicBuffer&lt;E&gt;</c> component the method takes by <c>in</c>: its elements hold still during the pass.</summary>
    private static bool Buffer(IParameterSymbol p, bool outside) =>
        InComponent(p, outside) && p.Type is INamedTypeSymbol { IsGenericType: true } type && Symbols.Named(type.OriginalDefinition, "EasyEcs.DynamicBuffer<T>");

    /// <summary>
    /// A key lookup of <c>LookupExtensions</c>: a member of its extension block (C# 14: the block is a type nested in the
    /// class), or the static method the block lowers to.
    /// </summary>
    private static bool IsKeyLookup(IMethodSymbol method) =>
        method.ContainingType is { } type && (type.Name == "LookupExtensions" || type.ContainingType?.Name == "LookupExtensions")
        && (type.ContainingType ?? type).ContainingNamespace is { Name: "EasyEcs", ContainingNamespace.IsGlobalNamespace: true };

    /// <summary>The receiver of a call of an extension member (the first argument of the static form).</summary>
    private static IOperation? Receiver(IInvocationOperation call) =>
        call.Instance ?? (call.TargetMethod.IsExtensionMethod || call.TargetMethod.IsStatic && call.Arguments.Length > 0 ? call.Arguments[0].Value : null);

    /// <summary>
    /// <paramref name="value"/>'s root (a parameter, a local, a buffer's element) and the members from it to the value
    /// (fields, or the properties of fields they back: reading another row's runs no user code), or null. An entity
    /// chase's value is an <c>EntityId</c>; a probe's key may be of any type.
    /// </summary>
    private static (IOperation, string)? Path(IOperation value, bool key)
    {
        while (value is IConversionOperation { Conversion.IsIdentity: true } c) value = c.Operand;
        if (!key && value.Type is not { Name: "EntityId", ContainingNamespace.Name: "EasyEcs" }) return null;
        var members = new List<string>();
        for (var at = value; ; )
        {
            switch (at)
            {
                case IFieldReferenceOperation { Field: { IsStatic: false } f, Instance: { } instance }:
                    members.Add(f.Name);
                    at = instance;
                    break;
                case IPropertyReferenceOperation { Property: { IsStatic: false, IsIndexer: false } prop, Instance: { } instance }
                    when Symbols.BackingField(prop) != null:
                    members.Add(prop.Name);
                    at = instance;
                    break;
                case IParameterReferenceOperation or ILocalReferenceOperation or IPropertyReferenceOperation { Property.IsIndexer: true }:
                    members.Reverse();
                    return (at, string.Concat(members.Select(m => "." + m)));
                default:
                    return null;
            }
        }
    }
}
