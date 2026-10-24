using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EasyEcs.CodeFixes;

/// <summary>
/// The fixes of system declarations: a type marked [EcsSystem] that is no ref struct (EECS074), an instance member of a
/// system (EECS075), and the migration of a system written against the removed <c>EcsSystem</c> base class (the
/// compiler's CS0246 on the base) into an [EcsSystem] ref struct. The migration is syntax only (the old base no longer
/// binds) and is offered only when nothing but the class itself changes: no constructor arguments, no abstract base.
/// </summary>
public sealed partial class EasyEcsCodeFixes
{
    private static readonly string[] HookNames = ["OnCreate", "OnUpdate", "OnDestroy"];

    /// <summary>The members of the old base class a system's code read, now read through its SystemHandle.</summary>
    private static readonly HashSet<string> HandleMembers = ["RunCount", "LastRunTick", "Enabled", "Error", "Stats"];

    /// <summary>EECS074: a class or struct becomes a ref struct (<c>ref</c> right before <c>partial</c>, where C# wants it).</summary>
    private static TypeDeclarationSyntax? ToRefStruct(TypeDeclarationSyntax type)
    {
        if (type is not (ClassDeclarationSyntax or StructDeclarationSyntax) || type.Modifiers.Any(SyntaxKind.RefKeyword) && type is StructDeclarationSyntax)
            return null;
        return AsRefStruct(type, type.Modifiers, type.BaseList);
    }

    /// <summary>Whether <paramref name="type"/> is spelled as the removed base class.</summary>
    private static bool IsOldSystemBase(TypeSyntax type) =>
        type.ToString() is "EcsSystem" or "EasyEcs.EcsSystem" or "global::EasyEcs.EcsSystem";

    /// <summary>Whether the project references EasyEcs (the migration names its [EcsSystem]).</summary>
    private static async Task<bool> ReferencesEasyEcs(Document document, CancellationToken token)
    {
        var compilation = await document.Project.GetCompilationAsync(token).ConfigureAwait(false);
        return compilation?.GetTypeByMetadataName("EasyEcs.EcsSystemAttribute") != null;
    }

    /// <summary>
    /// CS0246 on <c>class X : EcsSystem</c>: <c>[EcsSystem] ref struct X</c> with every member static. OnCreate,
    /// OnUpdate and OnDestroy overrides become static hooks of the same name; the World property becomes a
    /// <c>World world</c> parameter and RunCount, LastRunTick, Enabled, Error and Stats are read through a
    /// <c>SystemHandle system</c> parameter, each added to the methods that use it (or call a method of the class that
    /// does). Null (no fix) for a class with constructor arguments, an abstract base, or a [ForEach] method that reads
    /// the handle.
    /// </summary>
    private static TypeDeclarationSyntax? ToSystem(ClassDeclarationSyntax type, SimpleBaseTypeSyntax old)
    {
        if (type.ParameterList is { Parameters.Count: > 0 } || type.Modifiers.Any(SyntaxKind.AbstractKeyword)
            || type.BaseList!.Types.IndexOf(old) != 0) return null;
        foreach (var constructor in type.Members.OfType<ConstructorDeclarationSyntax>())
            if (!constructor.Modifiers.Any(SyntaxKind.StaticKeyword)
                && (constructor.ParameterList.Parameters.Count > 0 || constructor.Initializer != null
                    || constructor.Body is { Statements.Count: > 0 } || constructor.ExpressionBody != null))
                return null;

        string prefix = old.Type.ToString().EndsWith("EasyEcs.EcsSystem") ? old.Type.ToString().Substring(0, old.Type.ToString().Length - "EcsSystem".Length) : "";
        var methods = type.Members.OfType<MethodDeclarationSyntax>().ToList();

        // What each method needs: the world, the handle; closed over calls to methods of the class by simple name.
        var world = new HashSet<string>();
        var handle = new HashSet<string>();
        foreach (var method in methods)
        {
            if (Uses(method, n => n == "World")) world.Add(method.Identifier.ValueText);
            if (Uses(method, HandleMembers.Contains)) handle.Add(method.Identifier.ValueText);
        }

        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (var method in methods)
                foreach (var called in Calls(method))
                {
                    if (world.Contains(called)) changed |= world.Add(method.Identifier.ValueText);
                    if (handle.Contains(called)) changed |= handle.Add(method.Identifier.ValueText);
                }
        }

        var members = new List<MemberDeclarationSyntax>();
        foreach (var member in type.Members)
        {
            switch (member)
            {
                case ConstructorDeclarationSyntax { Modifiers: var m } when !m.Any(SyntaxKind.StaticKeyword):
                    continue; // empty and parameterless: nothing to keep
                case MethodDeclarationSyntax method:
                    string name = method.Identifier.ValueText;
                    bool hook = method.Modifiers.Any(SyntaxKind.OverrideKeyword) && HookNames.Contains(name) && method.ParameterList.Parameters.Count == 0;
                    bool forEach = method.AttributeLists.SelectMany(l => l.Attributes).Any(IsForEach);
                    bool needsWorld = world.Contains(name), needsHandle = handle.Contains(name);
                    if (forEach && needsHandle) return null;
                    members.Add(Migrated(method, hook, forEach, needsWorld, needsHandle, world, handle, prefix));
                    continue;
                case BaseTypeDeclarationSyntax or DelegateDeclarationSyntax:
                    members.Add(member);
                    continue;
                default:
                    members.Add(Static(member));
                    continue;
            }
        }

        var bases = type.BaseList.Types.RemoveAt(0);
        var list = bases.Count == 0 ? null : type.BaseList.WithTypes(bases);
        var modifiers = SyntaxFactory.TokenList(type.Modifiers.Select(m => m.Kind() is SyntaxKind.PrivateKeyword or SyntaxKind.ProtectedKeyword
            ? SyntaxFactory.Token(SyntaxKind.InternalKeyword).WithTriviaFrom(m) : m));
        // "private protected" / "protected internal" would now read "internal internal".
        modifiers = SyntaxFactory.TokenList(modifiers.Where((m, i) => !m.IsKind(SyntaxKind.InternalKeyword)
                                                                     || modifiers.Take(i).All(p => !p.IsKind(SyntaxKind.InternalKeyword))));
        var system = AsRefStruct(type.WithMembers(SyntaxFactory.List(members)), modifiers, list);
        if (system == null) return null;
        if (list == null && type.BaseList.GetTrailingTrivia().Any(t => t.IsKind(SyntaxKind.EndOfLineTrivia)))
            system = system.WithIdentifier(system.Identifier.WithTrailingTrivia(type.BaseList.GetTrailingTrivia()));
        // Into the type's last attribute list when it has one ([UpdateInterval(2), EcsSystem]), on a line of its own otherwise.
        var mark = SyntaxFactory.Attribute(SyntaxFactory.ParseName(prefix + "EcsSystem"));
        if (system.AttributeLists.LastOrDefault() is { } last)
            return WithoutElastic(system.ReplaceNode(last, last.WithAttributes(last.Attributes.Add(mark.WithLeadingTrivia(SyntaxFactory.Space)))));
        return WithoutElastic(WithAttribute(system, prefix + "EcsSystem", type.SyntaxTree.GetRoot()));
    }

    /// <summary>
    /// <paramref name="node"/> without the elastic markers SyntaxFactory tokens carry: the code action would format
    /// around them (an attribute moved to a line of its own), and the trivia written here is already what it should be.
    /// </summary>
    private static T WithoutElastic<T>(T node) where T : SyntaxNode =>
        node.ReplaceTrivia(node.DescendantTrivia().Where(t => t.IsKind(SyntaxKind.WhitespaceTrivia) && t.Span.Length == 0), static (_, _) => default);

    /// <summary>A class or struct as <c>ref struct</c> with <paramref name="modifiers"/> (minus those a struct cannot carry) and <paramref name="bases"/>.</summary>
    private static TypeDeclarationSyntax? AsRefStruct(TypeDeclarationSyntax type, SyntaxTokenList modifiers, BaseListSyntax? bases)
    {
        var kept = modifiers.Where(m => m.Kind() is not (SyntaxKind.SealedKeyword or SyntaxKind.AbstractKeyword or SyntaxKind.StaticKeyword or SyntaxKind.RefKeyword)).ToList();
        int at = kept.FindIndex(m => m.IsKind(SyntaxKind.PartialKeyword));
        kept.Insert(at < 0 ? kept.Count : at, SyntaxFactory.Token(SyntaxKind.RefKeyword).WithTrailingTrivia(SyntaxFactory.Space));
        var keyword = type.Keyword;
        // The type's leading trivia sits on its first token: keep it there whichever token now comes first.
        var leading = type.GetLeadingTrivia();
        var stripped = type.WithoutLeadingTrivia();
        if (stripped is ClassDeclarationSyntax cls)
            stripped = SyntaxFactory.StructDeclaration(cls.AttributeLists, default, SyntaxFactory.Token(SyntaxKind.StructKeyword).WithTriviaFrom(keyword),
                cls.Identifier, cls.TypeParameterList, cls.ParameterList, bases, cls.ConstraintClauses, cls.OpenBraceToken, cls.Members,
                cls.CloseBraceToken, cls.SemicolonToken);
        else if (stripped is StructDeclarationSyntax str)
            stripped = str.WithBaseList(bases);
        else
            return null;
        if (kept.Count > 0 && stripped.AttributeLists.Count == 0 && modifiers.Count > 0) kept[0] = kept[0].WithLeadingTrivia(modifiers[0].LeadingTrivia);
        return WithoutElastic(((TypeDeclarationSyntax)stripped).WithModifiers(SyntaxFactory.TokenList(kept)).WithLeadingTrivia(leading));
    }

    /// <summary>Whether the method's body names an identifier <paramref name="match"/> takes, as the old base's member.</summary>
    private static bool Uses(MethodDeclarationSyntax method, System.Func<string, bool> match) =>
        Body(method).SelectMany(b => b.DescendantNodesAndSelf()).OfType<IdentifierNameSyntax>().Any(n => match(n.Identifier.ValueText) && IsBaseMember(n));

    private static IEnumerable<SyntaxNode> Body(MethodDeclarationSyntax method) =>
        new SyntaxNode?[] { method.Body, method.ExpressionBody }.Where(n => n != null)!;

    /// <summary>The methods of the class the method calls by simple name.</summary>
    private static IEnumerable<string> Calls(MethodDeclarationSyntax method) =>
        Body(method).SelectMany(b => b.DescendantNodes()).OfType<InvocationExpressionSyntax>()
            .Select(c => c.Expression switch
            {
                IdentifierNameSyntax id => id.Identifier.ValueText,
                MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: IdentifierNameSyntax id } => id.Identifier.ValueText,
                _ => null,
            }).Where(n => n != null)!;

    /// <summary>
    /// Whether an identifier is a use of the old base's member: an expression of its own (not the name after a dot, not a
    /// type, not a named argument), or <c>this.X</c>.
    /// </summary>
    private static bool IsBaseMember(IdentifierNameSyntax name) => name.Parent switch
    {
        MemberAccessExpressionSyntax access when access.Name == name => access.Expression is ThisExpressionSyntax,
        CastExpressionSyntax cast => cast.Expression == name, // (int)RunCount: the value, not the type
        QualifiedNameSyntax or NameColonSyntax or NameEqualsSyntax or TypeArgumentListSyntax or VariableDeclarationSyntax
            or ParameterSyntax or ObjectCreationExpressionSyntax or TypeOfExpressionSyntax
            or DeclarationExpressionSyntax or DeclarationPatternSyntax or ArrayTypeSyntax or NullableTypeSyntax => false,
        _ => true,
    };

    /// <summary>One method of the old class as a static member of the system.</summary>
    private static MethodDeclarationSyntax Migrated(MethodDeclarationSyntax method, bool hook, bool forEach, bool needsWorld, bool needsHandle,
        HashSet<string> world, HashSet<string> handle, string prefix)
    {
        // The body first: World -> world, RunCount -> system.RunCount, this.X -> X, calls pass what their callee takes.
        var rewritten = method.ReplaceNodes(Body(method).SelectMany(b => b.DescendantNodesAndSelf())
                .Where(n => n is IdentifierNameSyntax or MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } or InvocationExpressionSyntax),
            (original, current) =>
            {
                switch (current)
                {
                    case MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } self:
                        return Rename(self.Name.Identifier.ValueText, self.Name).WithTriviaFrom(self);
                    case IdentifierNameSyntax id when IsBaseMember((IdentifierNameSyntax)original):
                        return Rename(id.Identifier.ValueText, id);
                    case InvocationExpressionSyntax call when original is InvocationExpressionSyntax { Expression: var callee } && CalleeName(callee) is { } calleeName:
                        var added = new List<ArgumentSyntax>();
                        if (world.Contains(calleeName)) added.Add(SyntaxFactory.Argument(SyntaxFactory.IdentifierName("world")));
                        if (handle.Contains(calleeName)) added.Add(SyntaxFactory.Argument(SyntaxFactory.IdentifierName("system")));
                        return added.Count == 0 ? call : call.WithArgumentList(Prepend(call.ArgumentList, added));
                    default:
                        return current;
                }
            });

        var modifiers = new List<SyntaxToken>();
        foreach (var m in rewritten.Modifiers)
        {
            if (m.Kind() is SyntaxKind.OverrideKeyword or SyntaxKind.VirtualKeyword or SyntaxKind.SealedKeyword or SyntaxKind.NewKeyword
                or SyntaxKind.AbstractKeyword or SyntaxKind.StaticKeyword) continue;
            if (hook && m.Kind() is SyntaxKind.PublicKeyword or SyntaxKind.ProtectedKeyword or SyntaxKind.InternalKeyword or SyntaxKind.PrivateKeyword) continue;
            modifiers.Add(m.IsKind(SyntaxKind.ProtectedKeyword)
                ? SyntaxFactory.Token(SyntaxKind.PrivateKeyword).WithTriviaFrom(m)
                : m);
        }

        var leading = rewritten.GetLeadingTrivia();
        var stat = SyntaxFactory.Token(SyntaxKind.StaticKeyword).WithTrailingTrivia(SyntaxFactory.Space);
        modifiers.Add(stat);
        var result = rewritten.WithoutLeadingTrivia().WithModifiers(SyntaxFactory.TokenList(modifiers.Select((m, i) => i == 0 ? m.WithLeadingTrivia() : m)));

        var parameters = new List<ParameterSyntax>();
        if (needsWorld) parameters.Add(SyntaxFactory.Parameter(SyntaxFactory.Identifier("world")).WithType(SyntaxFactory.ParseTypeName(prefix + "World ")));
        if (needsHandle) parameters.Add(SyntaxFactory.Parameter(SyntaxFactory.Identifier("system")).WithType(SyntaxFactory.ParseTypeName(prefix + "SystemHandle ")));
        if (parameters.Count > 0)
            result = result.WithParameterList(forEach ? Append(result.ParameterList, parameters) : Prepend(result.ParameterList, parameters));

        return result.WithLeadingTrivia(leading);

        ExpressionSyntax Rename(string name, SimpleNameSyntax node) =>
            name == "World" ? SyntaxFactory.IdentifierName("world").WithTriviaFrom(node)
            : HandleMembers.Contains(name) ? SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName("system"), SyntaxFactory.IdentifierName(name)).WithTriviaFrom(node)
            : node;
    }

    private static string? CalleeName(ExpressionSyntax callee) => callee switch
    {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: IdentifierNameSyntax id } => id.Identifier.ValueText,
        _ => null,
    };

    private static ArgumentListSyntax Prepend(ArgumentListSyntax list, List<ArgumentSyntax> added)
    {
        var items = new List<ArgumentSyntax>(added);
        items.AddRange(list.Arguments);
        return list.WithArguments(Separated(items));
    }

    private static ParameterListSyntax Prepend(ParameterListSyntax list, List<ParameterSyntax> added)
    {
        var items = new List<ParameterSyntax>(added);
        items.AddRange(list.Parameters);
        return list.WithParameters(Separated(items));
    }

    private static ParameterListSyntax Append(ParameterListSyntax list, List<ParameterSyntax> added)
    {
        var items = new List<ParameterSyntax>(list.Parameters);
        items.AddRange(added);
        return list.WithParameters(Separated(items));
    }

    private static SeparatedSyntaxList<T> Separated<T>(List<T> items) where T : SyntaxNode =>
        SyntaxFactory.SeparatedList(items.Select((n, i) => i == 0 ? n.WithLeadingTrivia() : n.WithLeadingTrivia(SyntaxFactory.Space)),
            Enumerable.Repeat(SyntaxFactory.Token(SyntaxKind.CommaToken), System.Math.Max(0, items.Count - 1)));

    /// <summary>A field, property or event of the old class as a static member (<c>protected</c> becomes <c>private</c>: a struct has no subclass).</summary>
    private static MemberDeclarationSyntax Static(MemberDeclarationSyntax member)
    {
        var modifiers = new List<SyntaxToken>();
        foreach (var m in member.Modifiers)
        {
            if (m.Kind() is SyntaxKind.OverrideKeyword or SyntaxKind.VirtualKeyword or SyntaxKind.SealedKeyword or SyntaxKind.NewKeyword
                or SyntaxKind.AbstractKeyword) continue;
            modifiers.Add(m.IsKind(SyntaxKind.ProtectedKeyword) ? SyntaxFactory.Token(SyntaxKind.PrivateKeyword).WithTriviaFrom(m) : m);
        }

        if (modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword) || m.IsKind(SyntaxKind.ConstKeyword)))
            return member.WithModifiers(SyntaxFactory.TokenList(modifiers));
        var leading = member.GetLeadingTrivia();
        int at = modifiers.FindIndex(m => m.IsKind(SyntaxKind.ReadOnlyKeyword));
        modifiers.Insert(at < 0 ? modifiers.Count : at, SyntaxFactory.Token(SyntaxKind.StaticKeyword).WithTrailingTrivia(SyntaxFactory.Space));
        return member.WithoutLeadingTrivia().WithModifiers(SyntaxFactory.TokenList(modifiers.Select((m, i) => i == 0 ? m.WithLeadingTrivia() : m)))
            .WithLeadingTrivia(leading);
    }
}
