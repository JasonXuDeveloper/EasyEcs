using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DocCheck;

/// <summary>
/// One fence, split into what it declares: usings, types, members (a field or method with modifiers at the top
/// level) and statements. <see cref="Emit"/> writes them back as C# with <c>#line</c> directives, so every
/// diagnostic names the page line it came from.
/// </summary>
public sealed partial class Snippet
{
    private static readonly CSharpParseOptions Regular = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);

    private readonly SyntaxTree _parsed;
    private readonly bool _namespaced;                 // a fence with a namespace is a file of its own
    private readonly HashSet<string> _without;         // types another fence replaces here
    private List<SyntaxTree> _emitted = [];

    public Snippet(Fence fence) : this(fence, []) { }

    private Snippet(Fence fence, HashSet<string> without)
    {
        Fence = fence;
        _without = without;
        _namespaced = Namespace().IsMatch(fence.Code);
        _parsed = CSharpSyntaxTree.ParseText(fence.Code, Regular);
        var root = (CompilationUnitSyntax)_parsed.GetRoot();
        var declared = _namespaced ? root.Members.OfType<BaseNamespaceDeclarationSyntax>().SelectMany(n => n.Members) : root.Members;
        TypeNames = declared.Where(IsType).Where(t => !IsPartial(t)).Select(NameOf).ToHashSet();
    }

    public Fence Fence { get; }

    /// <summary>The non-partial types the fence declares at the top level: a later fence's declaration replaces them.</summary>
    public HashSet<string> TypeNames { get; }

    private string Method => $"Fence_{Fence.Line}";

    /// <summary>This fence without the types <paramref name="names"/> (a fence compiled on its own declares them).</summary>
    public Snippet Without(HashSet<string> names) => new(Fence, [.. TypeNames.Intersect(names)]);

    public bool Owns(SyntaxTree tree) => _emitted.Contains(tree);

    /// <summary>The fence as source trees; <paramref name="keep"/> says whether a declared type is the one that stays.</summary>
    public List<SyntaxTree> Emit(string page, Func<string, bool> keep)
    {
        _emitted = [];
        var root = (CompilationUnitSyntax)_parsed.GetRoot();
        if (_namespaced)
        {
            // The fence is a file: its usings and its namespace become global, so the page's other fences see them.
            var global = SyntaxFactory.Token(SyntaxKind.GlobalKeyword).WithTrailingTrivia(SyntaxFactory.Space);
            var usings = root.Usings.Select(u => u.GlobalKeyword.IsKind(SyntaxKind.None) ? u.WithGlobalKeyword(global) : u);
            var namespaces = root.Members.OfType<BaseNamespaceDeclarationSyntax>()
                .Select(n => SyntaxFactory.UsingDirective(n.Name).WithGlobalKeyword(global).NormalizeWhitespace().WithTrailingTrivia(SyntaxFactory.LineFeed));
            var file = root.WithUsings(SyntaxFactory.List(usings.Concat(namespaces)));
            _emitted.Add(CSharpSyntaxTree.ParseText($"#line {Fence.Line} \"{page}\"\n{file.ToFullString()}", Regular, path: $"{page}#{Fence.Line}.cs"));
            return _emitted;
        }

        var types = new StringBuilder();
        foreach (var directive in root.Usings)
            Mapped(types, directive, directive.GlobalKeyword.IsKind(SyntaxKind.None) ? "global " + directive.ToString().Trim() : directive.ToString().Trim());
        foreach (var attributes in root.AttributeLists) Mapped(types, attributes, attributes.ToFullString());
        foreach (var member in root.Members)
            if (IsType(member) && (IsPartial(member) || !_without.Contains(NameOf(member)) && keep(NameOf(member))))
                Mapped(types, member, Internal(member).ToFullString());

        if (Fence.InSystem)
        {
            // A fragment of a system: everything but its types is the body of a system class.
            var system = new StringBuilder($"public class DocSystem_{Fence.Line} : EcsSystem\n{{\n");
            foreach (var member in root.Members)
                if (!IsType(member))
                    Mapped(system, member, member is GlobalStatementSyntax global ? global.Statement.ToFullString() : member.ToFullString());
            system.Append("}\n");
            _emitted.Add(CSharpSyntaxTree.ParseText(types.ToString(), Regular, path: $"{page}#{Fence.Line}.types.cs"));
            _emitted.Add(CSharpSyntaxTree.ParseText(system.ToString(), Regular, path: $"{page}#{Fence.Line}.system.cs"));
            return _emitted;
        }

        var holder = new StringBuilder("public partial class DocPage\n{\n");
        foreach (var member in root.Members)
            if (IsMember(member))
                Mapped(holder, member, Internal(member).ToFullString());

        var statements = root.Members.Where(m => !IsType(m) && !IsMember(m)).ToList();
        if (statements.Count != 0)
        {
            bool async = statements.Any(s => s.DescendantTokens().Any(t => t.IsKind(SyntaxKind.AwaitKeyword)));
            bool returns = statements.Any(s => s.DescendantNodesAndSelf(n => n is not (LambdaExpressionSyntax or AnonymousMethodExpressionSyntax or LocalFunctionStatementSyntax))
                .OfType<ReturnStatementSyntax>().Any(r => r.Expression != null));
            string type = (async, returns) switch
            {
                (true, true) => "async System.Threading.Tasks.Task<object>",
                (true, false) => "async System.Threading.Tasks.Task",
                (false, true) => "object",
                _ => "void",
            };
            holder.Append("    public ").Append(type).Append(' ').Append(Method).Append("()\n    {\n");
            foreach (var statement in statements)
                Mapped(holder, statement, statement is GlobalStatementSyntax global ? global.Statement.ToFullString() : statement.ToFullString());
            if (returns) holder.Append("        return default;\n");
            holder.Append("    }\n");
        }

        holder.Append("}\n");
        _emitted.Add(CSharpSyntaxTree.ParseText(types.ToString(), Regular, path: $"{page}#{Fence.Line}.types.cs"));
        _emitted.Add(CSharpSyntaxTree.ParseText(holder.ToString(), Regular, path: $"{page}#{Fence.Line}.code.cs"));
        return _emitted;

        void Mapped(StringBuilder text, SyntaxNode node, string code)
        {
            int line = _parsed.GetText().Lines.GetLineFromPosition(node.FullSpan.Start).LineNumber;
            text.Append("#line ").Append(Fence.Line + line).Append(" \"").Append(page).Append("\"\n");
            text.Append(code.TrimEnd()).Append("\n#line hidden\n");
        }
    }

    /// <summary>
    /// The type of the local <paramref name="name"/> this fence's statements declare (outside lambdas and local
    /// functions), as a field type; null if it declares none.
    /// </summary>
    public string? LocalType(Compilation compilation, string name)
    {
        var tree = _emitted.Count == 2 ? _emitted[1] : null;
        if (tree == null || !compilation.ContainsSyntaxTree(tree)) return null;
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.ValueText == Method);
        if (method?.Body == null) return null;
        var model = compilation.GetSemanticModel(tree);
        string? type = null;
        foreach (var node in method.Body.DescendantNodes(n => n is not (LambdaExpressionSyntax or AnonymousMethodExpressionSyntax or LocalFunctionStatementSyntax)))
        {
            var token = node switch
            {
                VariableDeclaratorSyntax v => v.Identifier,
                SingleVariableDesignationSyntax d => d.Identifier,
                _ => default,
            };
            if (token.ValueText != name || model.GetDeclaredSymbol(node) is not ILocalSymbol { Type: var t } || t.TypeKind == TypeKind.Error || t.IsAnonymousType) continue;
            type = t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }

        return type;
    }

    // A member of the holder class: a field, method or property with modifiers (`private static readonly QueryFilter
    // Alive = ...;`, `public Entity Mirror(Entity unit) => ...;`). Without modifiers it is a local (a statement).
    private static bool IsMember(MemberDeclarationSyntax member) => member switch
    {
        GlobalStatementSyntax { Statement: LocalFunctionStatementSyntax f } => f.Modifiers.Any(m => SyntaxFacts.IsAccessibilityModifier(m.Kind())),
        GlobalStatementSyntax => false,
        FieldDeclarationSyntax field => field.Modifiers.Count != 0,
        _ => !IsType(member),
    };

    private static bool IsType(MemberDeclarationSyntax member) => member is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax;

    private static bool IsPartial(MemberDeclarationSyntax member) => member.Modifiers.Any(SyntaxKind.PartialKeyword);

    private static string NameOf(MemberDeclarationSyntax member) => member switch
    {
        BaseTypeDeclarationSyntax t => t.Identifier.ValueText + Arity((t as TypeDeclarationSyntax)?.TypeParameterList),
        DelegateDeclarationSyntax d => d.Identifier.ValueText + Arity(d.TypeParameterList),
        _ => "",
    };

    private static string Arity(TypeParameterListSyntax? list) => list == null ? "" : "`" + list.Parameters.Count;

    // Members of the holder class are seen from the fences' types too (global using static): private becomes internal.
    private static SyntaxNode Internal(MemberDeclarationSyntax member)
    {
        SyntaxNode node = member is GlobalStatementSyntax global ? global.Statement : member;
        var modifiers = node is LocalFunctionStatementSyntax f ? f.Modifiers : member.Modifiers;
        int at = modifiers.IndexOf(SyntaxKind.PrivateKeyword);
        if (at < 0) return node;
        var replaced = modifiers.Replace(modifiers[at], SyntaxFactory.Token(SyntaxKind.InternalKeyword).WithTriviaFrom(modifiers[at]));
        return node is LocalFunctionStatementSyntax local ? local.WithModifiers(replaced) : member.WithModifiers(replaced);
    }

    [GeneratedRegex(@"^\s*namespace\s+[\w.]+\s*[;{]?\s*$", RegexOptions.Multiline)]
    private static partial Regex Namespace();
}
