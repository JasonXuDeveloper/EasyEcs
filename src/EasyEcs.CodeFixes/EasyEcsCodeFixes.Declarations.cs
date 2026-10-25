using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EasyEcs.CodeFixes;

/// <summary>
/// The migrations of declarations that changed shape, each from the compiler's error on the old form (the old form no
/// longer binds, so these are syntax only, offered in a project that references EasyEcs): single-type
/// <c>[WithAny&lt;T&gt;]</c> attributes and <c>.WithAny&lt;T&gt;()</c> calls (CS0305) merged into one
/// <c>WithAny&lt;T1, T2, ...&gt;</c> (a lone one is <c>With&lt;T&gt;</c>), and the removed hook attributes (CS0246:
/// the name makes a method a hook), <c>[GroupBy]</c> on a struct (CS0592: it goes on the grouped member), and
/// <c>EntityRandom</c>'s tick written by hand (CS0200, or CS0272 in source, on <c>Tick++</c> or <c>Tick = ...</c>:
/// the statement goes; CS1729, CS1501, CS1739 on <c>new EntityRandom(seed, tick)</c> or
/// <c>SetEntityRandom(seed, tick)</c>: the tick goes; the world advances it on every update), and renamed attributes
/// (CS0246, or CS0234 when qualified: <c>[IndexKey]</c> is <c>[LookupKey]</c>). EECS090's rename of a method named
/// almost like a hook lives here too.
/// </summary>
public sealed partial class EasyEcsCodeFixes
{
    /// <summary>A <c>WithAny&lt;T&gt;</c> of one type, the form the 2..9-type family replaced.</summary>
    private static bool IsSingleWithAny(SimpleNameSyntax? name) =>
        name is GenericNameSyntax { Identifier.ValueText: "WithAny", TypeArgumentList.Arguments.Count: 1 };

    /// <summary>The generic name an attribute is spelled with (<c>WithAny&lt;T&gt;</c>, <c>EasyEcs.WithAny&lt;T&gt;</c>).</summary>
    private static SimpleNameSyntax? LastName(NameSyntax name) => name switch
    {
        QualifiedNameSyntax q => q.Right,
        AliasQualifiedNameSyntax a => a.Name,
        SimpleNameSyntax s => s,
        _ => null,
    };

    /// <summary>
    /// CS0305 on a single-type <c>WithAny&lt;T&gt;</c>: every such attribute of the same declaration, or every such call
    /// of the same filter chain, merged into one <c>WithAny&lt;T1, T2, ...&gt;</c> in their order (the first keeps its
    /// place, the others go); a lone one becomes <c>With&lt;T&gt;</c>, which is what it meant. Returns the node to
    /// replace and its replacement, or null when the name is in no attribute or call chain.
    /// </summary>
    private static (string Title, SyntaxNode Old, SyntaxNode New)? MergedWithAny(GenericNameSyntax any)
    {
        SyntaxNode? at = any.Parent is QualifiedNameSyntax or AliasQualifiedNameSyntax ? any.Parent : any;
        if (at?.Parent is AttributeSyntax { Parent: AttributeListSyntax { Parent: { } owner } })
        {
            var attributes = owner.ChildNodes().OfType<AttributeListSyntax>().SelectMany(l => l.Attributes)
                .Where(a => IsSingleWithAny(LastName(a.Name))).ToList();
            var types = Merged(attributes.Select(a => (GenericNameSyntax)LastName(a.Name)!));
            var first = attributes[0];
            var named = (GenericNameSyntax)LastName(first.Name)!;
            var replacement = Renamed(named, types);
            // The first takes the merged list; the others go, with the lists they leave empty.
            var updated = owner.TrackNodes(attributes);
            updated = updated.ReplaceNode(updated.GetCurrentNode(first)!, first.ReplaceNode(named, replacement));
            var rest = attributes.Skip(1).ToList();
            var emptied = rest.Where(a => ((AttributeListSyntax)a.Parent!).Attributes.All(rest.Contains)).ToList();
            updated = updated.RemoveNodes(rest.Except(emptied).Select(a => updated.GetCurrentNode(a)!), SyntaxRemoveOptions.KeepNoTrivia)!;
            updated = updated.RemoveNodes(emptied.Select(a => updated.GetCurrentNode(a)?.Parent).Where(l => l != null).Distinct()!, SyntaxRemoveOptions.KeepNoTrivia)!;
            return (Title(replacement), owner, updated);
        }

        if (any.Parent is not MemberAccessExpressionSyntax { Parent: InvocationExpressionSyntax call } access || access.Name != any) return null;
        // The whole chain: up to the outermost call, then each WithAny call of one type from the inside out.
        ExpressionSyntax top = call;
        while (top.Parent is MemberAccessExpressionSyntax { Parent: InvocationExpressionSyntax outer } link && link.Expression == top) top = outer;
        var calls = new List<InvocationExpressionSyntax>();
        for (var current = top as InvocationExpressionSyntax; current is { Expression: MemberAccessExpressionSyntax m };
             current = m.Expression as InvocationExpressionSyntax)
            if (IsSingleWithAny(m.Name) && current.ArgumentList.Arguments.Count == 0) calls.Add(current);
        calls.Reverse(); // source order: the innermost call comes first
        var all = Merged(calls.Select(c => (GenericNameSyntax)((MemberAccessExpressionSyntax)c.Expression).Name));
        var name = Renamed((GenericNameSyntax)((MemberAccessExpressionSyntax)calls[0].Expression).Name, all);
        var chain = top.ReplaceNodes(calls, (original, current) => original == calls[0]
            ? current.WithExpression(((MemberAccessExpressionSyntax)current.Expression).WithName(name))
            : ((MemberAccessExpressionSyntax)current.Expression).Expression.WithTrailingTrivia(current.GetTrailingTrivia()));
        return (Title(name), top, chain);

        static string Title(GenericNameSyntax name) => $"Write it as {name.Identifier.ValueText}<{name.TypeArgumentList.Arguments}>";
    }

    /// <summary>
    /// CS0246 on a removed hook attribute (<c>[OnCreate]</c>, <c>[OnUpdate]</c>, <c>[OnDestroy]</c>) of a method of a
    /// system: the attribute goes (its list too when it was alone) and the method takes the hook's name, the name alone
    /// making it the hook; the system's uses of the old name follow. Null outside an [EcsSystem] struct.
    /// </summary>
    private static (string Title, SyntaxNode Old, SyntaxNode New)? FromHookAttribute(AttributeSyntax attribute)
    {
        if (LastName(attribute.Name) is not IdentifierNameSyntax { Identifier.ValueText: var spelled }
            || HookNames.FirstOrDefault(h => spelled == h || spelled == h + "Attribute") is not { } hook
            || attribute.Parent is not AttributeListSyntax { Parent: MethodDeclarationSyntax method } list
            || method.Parent is not StructDeclarationSyntax system
            || !system.AttributeLists.SelectMany(l => l.Attributes).Any(a => LastName(a.Name)?.Identifier.ValueText is "EcsSystem" or "EcsSystemAttribute"))
            return null;

        string old = method.Identifier.ValueText;
        var updated = system.ReplaceNodes(system.DescendantNodes().Where(n => n == list || n == method
                || n is IdentifierNameSyntax id && id.Identifier.ValueText == old && old != hook),
            (original, current) => original switch
            {
                AttributeListSyntax => current, // removed below, once the method is rewritten
                MethodDeclarationSyntax m => Unmarked((MethodDeclarationSyntax)current, list, old == hook ? null : hook),
                _ => SyntaxFactory.IdentifierName(hook).WithTriviaFrom(current),
            });
        return (old == hook ? $"Remove [{hook}]: the name makes it the hook" : $"Rename it to {hook}: the name makes it the hook", system, updated);

        static MethodDeclarationSyntax Unmarked(MethodDeclarationSyntax method, AttributeListSyntax list, string? rename)
        {
            var current = method.AttributeLists.First(l => l.IsEquivalentTo(list));
            var attribute = current.Attributes.First(a => LastName(a.Name)?.Identifier.ValueText is { } n && HookNames.Any(h => n == h || n == h + "Attribute"));
            if (current.Attributes.Count > 1)
                method = method.ReplaceNode(current, current.WithAttributes(current.Attributes.Remove(attribute)));
            else
            {
                // The list's leading trivia (the method's indentation) moves to what now comes first.
                var leading = current.GetLeadingTrivia();
                method = method.RemoveNode(current, SyntaxRemoveOptions.KeepNoTrivia)!;
                method = method.WithLeadingTrivia(leading);
            }

            return rename == null ? method : method.WithIdentifier(SyntaxFactory.Identifier(rename).WithTriviaFrom(method.Identifier));
        }
    }

    /// <summary>
    /// CS0592 on <c>[GroupBy]</c> on a struct (it now goes on the grouped member): the member marked <c>[Group]</c> becomes
    /// <c>[GroupBy]</c>, or, for a hand-written <c>IGroup</c>, its <c>Group</c> property takes <c>[GroupBy]</c>; the
    /// struct's attribute goes. Null when the struct has neither.
    /// </summary>
    private static (string Title, SyntaxNode Old, SyntaxNode New)? GroupByOnMember(AttributeSyntax attribute)
    {
        if (LastName(attribute.Name) is not IdentifierNameSyntax { Identifier.ValueText: "GroupBy" or "GroupByAttribute" } name
            || attribute.Parent is not AttributeListSyntax { Parent: TypeDeclarationSyntax type } list)
            return null;

        var marked = type.DescendantNodes().OfType<AttributeSyntax>().FirstOrDefault(a =>
            a.Parent?.Parent is ParameterSyntax or PropertyDeclarationSyntax or FieldDeclarationSyntax
            && a.Parent.Parent.Parent is ParameterListSyntax { Parent: TypeDeclarationSyntax } or TypeDeclarationSyntax
            && a.FirstAncestorOrSelf<TypeDeclarationSyntax>() == type
            && LastName(a.Name)?.Identifier.ValueText is "Group" or "GroupAttribute");
        var own = type.Members.OfType<PropertyDeclarationSyntax>().FirstOrDefault(p => p.Identifier.ValueText == "Group");
        if (marked == null && own == null) return null;

        var tracked = type.TrackNodes(new SyntaxNode?[] { list, attribute, marked, own }.Where(n => n != null)!);
        if (marked != null)
        {
            var current = tracked.GetCurrentNode(marked)!;
            var markedName = LastName(current.Name)!;
            tracked = tracked.ReplaceNode(markedName, SyntaxFactory.IdentifierName("GroupBy").WithTriviaFrom(markedName));
        }
        else
        {
            var property = tracked.GetCurrentNode(own!)!;
            var mark = SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Attribute(name.WithoutTrivia())))
                .WithTrailingTrivia(SyntaxFactory.Space);
            tracked = tracked.ReplaceNode(property, property.WithoutLeadingTrivia().WithAttributeLists(property.AttributeLists.Insert(0, mark))
                .WithLeadingTrivia(property.GetLeadingTrivia()));
        }

        var currentList = tracked.GetCurrentNode(list)!;
        if (currentList.Attributes.Count > 1)
            tracked = tracked.ReplaceNode(currentList, currentList.WithAttributes(currentList.Attributes.Remove(tracked.GetCurrentNode(attribute)!)));
        else
        {
            var leading = currentList.GetLeadingTrivia();
            bool first = tracked.AttributeLists.IndexOf(currentList) == 0;
            tracked = tracked.RemoveNode(currentList, SyntaxRemoveOptions.KeepNoTrivia)!;
            if (first) tracked = tracked.WithLeadingTrivia(leading);
        }

        return (marked != null ? "Move [GroupBy] onto the [Group] member" : "Move [GroupBy] onto the Group property", type, tracked);
    }

    /// <summary>The attributes renamed before 1.0, old name to new: the old name no longer binds.</summary>
    private static readonly (string Old, string New)[] RenamedAttributes = [("IndexKey", "LookupKey")];

    /// <summary>
    /// CS0246 / CS0234 on an attribute of a renamed name (<see cref="RenamedAttributes"/>, spelled with or without the
    /// <c>Attribute</c> suffix, qualified or not): the name takes the new one, its arguments and qualification kept.
    /// Null for any other name.
    /// </summary>
    private static (string Title, SyntaxNode Old, SyntaxNode New)? RenamedAttribute(AttributeSyntax attribute)
    {
        if (LastName(attribute.Name) is not IdentifierNameSyntax { Identifier.ValueText: var spelled } name) return null;
        foreach (var (old, renamed) in RenamedAttributes)
        {
            if (spelled != old && spelled != old + "Attribute") continue;
            string now = spelled == old ? renamed : renamed + "Attribute";
            return ($"Rename it to [{renamed}]", name, SyntaxFactory.IdentifierName(now).WithTriviaFrom(name));
        }

        return null;
    }

    /// <summary>EECS090: a method named almost like a hook takes the hook's name (the analyzer checked it has no caller).</summary>
    private static MethodDeclarationSyntax RenamedHook(MethodDeclarationSyntax method, string hook) =>
        method.WithIdentifier(SyntaxFactory.Identifier(hook).WithTriviaFrom(method.Identifier));

    /// <summary>Whether the statement writes a <c>.Tick</c> (<c>++</c>, <c>--</c>, an assignment).</summary>
    private static bool IsTickWrite(ExpressionSyntax expression) => expression switch
    {
        PostfixUnaryExpressionSyntax { Operand: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Tick" } } => true,
        PrefixUnaryExpressionSyntax { Operand: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Tick" } } => true,
        AssignmentExpressionSyntax { Left: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Tick" } } => true,
        _ => false,
    };

    /// <summary>Whether the <c>.Tick</c> the statement writes is <c>EasyEcs.EntityRandom.Tick</c> (CS0200 / CS0272 bind it).</summary>
    private static async Task<bool> IsEntityRandomTick(Document document, ExpressionStatementSyntax statement, CancellationToken token)
    {
        var model = await document.GetSemanticModelAsync(token).ConfigureAwait(false);
        var access = statement.DescendantNodes().OfType<MemberAccessExpressionSyntax>().First(m => m.Name.Identifier.ValueText == "Tick");
        return model?.GetSymbolInfo(access, token) is { } info
               && (info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) is IPropertySymbol
               {
                   ContainingType: { Name: "EntityRandom", ContainingNamespace: { Name: "EasyEcs", ContainingNamespace.IsGlobalNamespace: true } }
               };
    }

    /// <summary>The argument list an error on a call or a creation points into (its name, its type or an argument).</summary>
    private static ArgumentListSyntax? ArgumentsAt(SyntaxNode node)
    {
        foreach (var at in node.AncestorsAndSelf())
            switch (at)
            {
                case ArgumentListSyntax list: return list;
                case BaseObjectCreationExpressionSyntax creation: return creation.ArgumentList;
                case InvocationExpressionSyntax call: return call.ArgumentList;
            }

        return null;
    }

    /// <summary>
    /// The tick of <c>new EntityRandom(seed, tick)</c> or <c>world.SetEntityRandom(seed, tick)</c> (the second argument,
    /// or the one named <c>Tick:</c>), the form whose tick the world now moves; null for any other argument list.
    /// </summary>
    private static ArgumentSyntax? TickArgument(ArgumentListSyntax arguments)
    {
        bool random = arguments.Parent switch
        {
            ObjectCreationExpressionSyntax { Type: var type } => type is NameSyntax name && LastName(name) is { Identifier.ValueText: "EntityRandom" },
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "SetEntityRandom" } } => true,
            InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "SetEntityRandom" } } => true,
            _ => false,
        };
        if (!random || arguments.Arguments.Count != 2) return null;
        foreach (var argument in arguments.Arguments)
            if (argument.NameColon?.Name.Identifier.ValueText == "Tick") return argument;
        return arguments.Arguments.Any(a => a.NameColon != null) ? null : arguments.Arguments[1];
    }

    /// <summary>The type arguments of the merged names in order, each once.</summary>
    private static List<TypeSyntax> Merged(IEnumerable<GenericNameSyntax> names)
    {
        var types = new List<TypeSyntax>();
        foreach (var name in names)
        {
            var type = name.TypeArgumentList.Arguments[0];
            if (types.All(t => !t.IsEquivalentTo(type))) types.Add(type.WithoutTrivia());
        }

        return types;
    }

    /// <summary><c>WithAny&lt;T1, T2, ...&gt;</c> of <paramref name="types"/>, or <c>With&lt;T&gt;</c> for one.</summary>
    private static GenericNameSyntax Renamed(GenericNameSyntax name, List<TypeSyntax> types) =>
        types.Count == 1
            ? name.WithIdentifier(SyntaxFactory.Identifier("With").WithTriviaFrom(name.Identifier))
            : name.WithTypeArgumentList(name.TypeArgumentList.WithArguments(SyntaxFactory.SeparatedList(
                types.Select((t, i) => i == 0 ? t : t.WithLeadingTrivia(SyntaxFactory.Space)))));
}
