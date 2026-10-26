using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace EasyEcs.Generator;

/// <summary>
/// A lookup of another entity at an id the row holds: <c>lookup[x.F]</c>, <c>lookup.Read(x.F)</c> or
/// <c>lookup.Has(x.F)</c>, with <c>x</c> a component the method takes by <c>in</c> (so a row's id is known before its row
/// runs). The row form's job prefetches it some rows ahead (SystemGenerator.EmitChases).
/// </summary>
/// <param name="Lookup">The Lookup parameter's index.</param>
/// <param name="Component">The component parameter's index.</param>
/// <param name="Id">The members from the component to the id (<c>.Target</c>, <c>.Hunt.Prey</c>): fields or auto-properties.</param>
/// <param name="Row">The method reads T's row (an indexer or Read), not only whether the entity has it (Has): both stages.</param>
internal readonly record struct Chase(int Lookup, int Component, string Id, bool Row);

/// <summary>The chases of a <c>[ForEach]</c> method, read from its body once (the row form's prefetches).</summary>
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
        if (model.GetOperation(syntax, ct) is not IMethodBodyOperation body) return new([]);
        var found = new List<Chase>();
        foreach (var op in body.Descendants())
        {
            var (lookup, id, row) = op switch
            {
                IPropertyReferenceOperation { Property.IsIndexer: true, Arguments.Length: 1, Instance: IParameterReferenceOperation { Parameter: var p } } x
                    => (p, x.Arguments[0].Value, true),
                IInvocationOperation { TargetMethod.Name: ("Read" or "Has") and var name, Arguments.Length: 1, Instance: IParameterReferenceOperation { Parameter: var p } } call
                    => (p, call.Arguments[0].Value, name == "Read"),
                _ => (null, null, false),
            };
            if (lookup == null || Symbols.Classify(lookup, outside) != Symbols.ParamKind.Lookup || Path(id!) is not ({ } component, { } members)) continue;
            if (component.RefKind != RefKind.In || Symbols.Classify(component, outside) != Symbols.ParamKind.Component) continue;
            var chase = new Chase(lookup.Ordinal, component.Ordinal, members, row);
            int same = found.FindIndex(c => c with { Row = false } == chase with { Row = false });
            if (same < 0) found.Add(chase);
            else if (row) found[same] = chase; // read somewhere: both stages
        }
        return new(found.ToArray());
    }

    /// <summary>
    /// <paramref name="id"/> as a parameter and the members from it to an EntityId (fields, or the properties of fields
    /// they back: reading another row's runs no user code), or null.
    /// </summary>
    private static (IParameterSymbol, string)? Path(IOperation id)
    {
        while (id is IConversionOperation { Conversion.IsIdentity: true } c) id = c.Operand;
        if (id.Type is not { Name: "EntityId", ContainingNamespace.Name: "EasyEcs" }) return null;
        var members = new List<string>();
        for (var at = id; ; )
        {
            switch (at)
            {
                case IParameterReferenceOperation { Parameter: var p }:
                    members.Reverse();
                    return members.Count == 0 ? null : (p, string.Concat(members.Select(m => "." + m)));
                case IFieldReferenceOperation { Field: { IsStatic: false } f, Instance: { } instance }:
                    members.Add(f.Name);
                    at = instance;
                    break;
                case IPropertyReferenceOperation { Property: { IsStatic: false, IsIndexer: false } prop, Instance: { } instance }
                    when prop.ContainingType.GetMembers().OfType<IFieldSymbol>().Any(f => SymbolEqualityComparer.Default.Equals(f.AssociatedSymbol, prop)):
                    members.Add(prop.Name);
                    at = instance;
                    break;
                default:
                    return null;
            }
        }
    }
}
