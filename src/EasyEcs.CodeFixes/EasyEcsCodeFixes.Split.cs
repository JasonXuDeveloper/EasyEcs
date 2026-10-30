using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;

namespace EasyEcs.CodeFixes;

/// <summary>
/// The split of a component (EECS091: the fields its frequent passes never touch; EECS055: the few fields a method
/// uses of a wide component): the moved fields become a new component declared next to the old one, and every use of
/// them across the solution is rewritten so the program does what it did. Offered only when every use of the
/// component is a form the rewrite keeps exact; any other use (the component copied whole, a query over it, a call that
/// adds it without the new one, a saved world) leaves the split to the user, as the diagnostic's message describes.
/// </summary>
public sealed partial class EasyEcsCodeFixes
{
    /// <summary>The calls that take a component's value or type and add it (arity families of Entity, World, Prefab): the new component goes along.</summary>
    private static readonly HashSet<string> Adders = ["Set", "Remove", "CreateEntity", "CreateEntities", "Instantiate"];

    /// <summary>The calls and attributes that name a component only for its presence, which the split keeps (an entity has both or neither).</summary>
    private static readonly HashSet<string> Presence = ["Has", "Count", "Any", "With", "Without", "WithAny", "Removed"];

    /// <summary>The World calls that write or read a saved format, which the split would change.</summary>
    private static readonly HashSet<string> Saves = ["Save", "SaveWithout", "SaveEntities", "SaveEntitiesWith", "SaveEntitiesWithout", "Load", "LoadKeeping",
        "LoadEntities", "LoadEntitiesInto"];

    /// <summary>The most components one arity call takes.</summary>
    private const int MaxArity = 9;

    /// <summary>A [ForEach] / [EntityMethod] parameter over the component, and how its uses split.</summary>
    private sealed class PassParameter(ParameterSyntax syntax, IParameterSymbol symbol)
    {
        public ParameterSyntax Syntax { get; } = syntax;
        public IParameterSymbol Symbol { get; } = symbol;
        public List<IdentifierNameSyntax> Moved { get; } = [];
        public int Kept { get; set; }
    }

    /// <summary>
    /// EECS091 and EECS055: the solution with <c>Fields</c> of <c>Component</c> moved into a new component, or null when
    /// a use of the component is one the rewrite cannot keep exact (then no fix is offered).
    /// </summary>
    private static async Task<(string Title, Solution Solution)?> Split(Document document, Diagnostic diagnostic, CancellationToken token)
    {
        if (!diagnostic.Properties.TryGetValue("Component", out var id) || id == null || !diagnostic.Properties.TryGetValue("Fields", out var names) || names == null
            || await document.Project.GetCompilationAsync(token).ConfigureAwait(false) is not { } compilation
            || DocumentationCommentId.GetFirstSymbolForDeclarationId(id, compilation) is not INamedTypeSymbol component
            || component.DeclaringSyntaxReferences.Length != 1
            || await component.DeclaringSyntaxReferences[0].GetSyntaxAsync(token).ConfigureAwait(false) is not StructDeclarationSyntax declaration
            || !Declarable(component, declaration)) return null;
        var solution = document.Project.Solution;
        var moved = names.Split(',').Select(n => component.GetMembers(n).OfType<IFieldSymbol>().FirstOrDefault()).ToList();
        if (moved.Any(f => f == null || f.IsStatic || f.IsConst || f.DeclaringSyntaxReferences.Length != 1)) return null;
        var movedSet = new HashSet<IFieldSymbol>(moved!, SymbolEqualityComparer.Default);
        string newName = NewName(compilation, component, moved[0]!);
        bool saved = !component.GetAttributes().Any(a => a.AttributeClass?.Name == "NotSavedAttribute");

        // Every document that can name the component: its project and the projects that depend on it.
        var componentDocument = solution.GetDocument(declaration.SyntaxTree);
        if (componentDocument == null) return null;
        var projects = solution.GetProjectDependencyGraph().GetProjectsThatTransitivelyDependOnThisProject(componentDocument.Project.Id)
            .Append(componentDocument.Project.Id);
        var watched = new HashSet<string>(moved.Select(f => f!.Name))
        {
            component.Name, "Set" + component.Name, "GetOrAdd" + component.Name, "Remove" + component.Name, "TryGet" + component.Name,
            "Set" + component.Name + "IfChanged", "Has" + component.Name, "Is" + component.Name,
        };
        if (saved) watched.UnionWith(Saves);

        var editors = new Dictionary<DocumentId, DocumentEditor>();
        var parameters = new Dictionary<IParameterSymbol, PassParameter>(SymbolEqualityComparer.Default);
        var renames = new List<(DocumentEditor Editor, IdentifierNameSyntax Name)>();              // C becomes N at this name
        var addedTypes = new List<(DocumentEditor Editor, SyntaxNode Type, IdentifierNameSyntax Name)>(); // N added after C in a type list
        var creations = new List<(DocumentEditor Editor, ArgumentSyntax Argument, ExpressionSyntax Value)>();
        var arity = new Dictionary<SyntaxNode, int>();                                                // components added per call
        var initialized = new HashSet<SyntaxNode>();                                                  // creations rewritten: their moved initializers move along

        foreach (var projectId in projects)
            foreach (var doc in solution.GetProject(projectId)!.Documents)
            {
                var root = await doc.GetSyntaxRootAsync(token).ConfigureAwait(false);
                if (root == null) continue;
                var candidates = root.DescendantNodes().OfType<IdentifierNameSyntax>().Where(n => watched.Contains(n.Identifier.ValueText)).ToList();
                if (candidates.Count == 0) continue;
                var model = await doc.GetSemanticModelAsync(token).ConfigureAwait(false);
                if (model == null) return null;
                if (!editors.TryGetValue(doc.Id, out var editor)) editors[doc.Id] = editor = await DocumentEditor.CreateAsync(doc, token).ConfigureAwait(false);

                // Types first: the creations they find carry the moved fields' initializers, which the field pass then accepts.
                foreach (var name in candidates)
                {
                    var symbol = model.GetSymbolInfo(name, token).Symbol;
                    if (symbol is IMethodSymbol { ContainingType: { Name: "World", ContainingNamespace.Name: "EasyEcs" } } save && Saves.Contains(save.Name)) return null; // a saved format
                    if (!SymbolEqualityComparer.Default.Equals(symbol, component)) continue;
                    var type = Outer(name);
                    switch (type.Parent)
                    {
                        case ParameterSyntax p when p.Type == type && Pass(p, model, token) is { } pass:
                            if (!parameters.ContainsKey(pass.Symbol)) parameters[pass.Symbol] = pass;
                            break;
                        case TypeArgumentListSyntax { Parent: GenericNameSyntax generic } when Outer(generic).Parent is ParameterSyntax p && p.Type == Outer(generic)
                                                                                             && generic.Identifier.ValueText is "Mut" or "Maybe" or "Span" or "ReadOnlySpan"
                                                                                             && Pass(p, model, token) is { } pass:
                            if (!parameters.ContainsKey(pass.Symbol)) parameters[pass.Symbol] = pass;
                            break;
                        case TypeArgumentListSyntax { Parent: GenericNameSyntax generic } when generic.FirstAncestorOrSelf<AttributeSyntax>() is { } attribute && attribute.Name.DescendantNodesAndSelf().Contains(generic):
                            if (!Presence.Contains(generic.Identifier.ValueText)) return null;
                            break;
                        case TypeArgumentListSyntax { Parent: GenericNameSyntax generic }:
                            var call = generic.FirstAncestorOrSelf<InvocationExpressionSyntax>();
                            if (call == null || model.GetSymbolInfo(call, token).Symbol is not IMethodSymbol method || !InEasyEcs(method)) return null;
                            if (Presence.Contains(method.Name)) break;
                            if (method.Name is "Get" or "Read" && Accessed(call, model, token) is { } field)
                            {
                                if (movedSet.Contains(field)) renames.Add((editor, name)); // entity.Get<C>().X: N's X
                                break;
                            }
                            if (!Adders.Contains(method.Name) || method.ContainingType.Name is not ("Entity" or "World" or "Prefab")) return null;
                            addedTypes.Add((editor, type, name));
                            if (method.Name != "Remove") break; // a value of the component is given too: counted with its creation
                            arity[call] = arity.TryGetValue(call, out int n) ? n + 1 : 1;
                            if (method.TypeArguments.Length + arity[call] > MaxArity) return null;
                            break;
                        case ObjectCreationExpressionSyntax creation when creation.Type == type && Creation(creation, model, token) is { } at:
                            creations.Add((editor, at.Argument, creation));
                            initialized.Add(creation);
                            if ((arity[at.Call] = arity.TryGetValue(at.Call, out int c) ? c + 1 : 1) + at.Components > MaxArity) return null;
                            break;
                        case DefaultExpressionSyntax value when value.Type == type && Creation(value, model, token) is { } at:
                            creations.Add((editor, at.Argument, value));
                            if ((arity[at.Call] = arity.TryGetValue(at.Call, out int d) ? d + 1 : 1) + at.Components > MaxArity) return null;
                            break;
                        default:
                            return null; // a use the split cannot follow: a copy, a query, a member of the component itself
                    }
                }

                foreach (var name in candidates)
                {
                    var symbol = model.GetSymbolInfo(name, token).Symbol;
                    switch (symbol)
                    {
                        case IFieldSymbol field when movedSet.Contains(field):
                            if (name.Parent is AssignmentExpressionSyntax { Parent: InitializerExpressionSyntax { Parent: { } owner } } assignment && assignment.Left == name
                                && initialized.Contains(owner)) break;
                            if (name.Parent is not MemberAccessExpressionSyntax access || access.Name != name) return null;
                            if (!Receiver(access.Expression, model, token, component, parameters, out var parameterUse)) return null;
                            if (parameterUse != null) parameters[(IParameterSymbol)model.GetSymbolInfo(parameterUse, token).Symbol!].Moved.Add(parameterUse);
                            break;
                        case IPropertySymbol property when property.Name == component.Name && Generated(property):
                            // entity.C.X: N's X when X moves, C's otherwise; entity.C alone is the component whole.
                            if (name.Parent is not MemberAccessExpressionSyntax { Parent: MemberAccessExpressionSyntax outer } member || member.Name != name || outer.Expression != member
                                || model.GetSymbolInfo(outer.Name, token).Symbol is not IFieldSymbol read || !SymbolEqualityComparer.Default.Equals(read.ContainingType, component)) return null;
                            if (movedSet.Contains(read)) renames.Add((editor, name));
                            break;
                        case IMethodSymbol or IPropertySymbol when Generated(symbol) && name.Identifier.ValueText != component.Name:
                            if (name.Identifier.ValueText != "Has" + component.Name && name.Identifier.ValueText != "Is" + component.Name) return null; // presence is kept
                            break;
                    }
                }
            }

        // Each pass parameter: its uses of the moved fields go to the new component's parameter.
        var passEdits = new List<(PassParameter Pass, string? Added)>();
        foreach (var pass in parameters.Values)
        {
            if (await HasCallers(solution.GetDocument(pass.Syntax.SyntaxTree)!, pass.Syntax, token).ConfigureAwait(false)) return null;
            if (await solution.GetDocument(pass.Syntax.SyntaxTree)!.GetSemanticModelAsync(token).ConfigureAwait(false) is not { } model) return null;
            var method = (MethodDeclarationSyntax)pass.Syntax.Parent!.Parent!;
            foreach (var use in method.DescendantNodes().OfType<IdentifierNameSyntax>().Where(n => n.Identifier.ValueText == pass.Symbol.Name))
            {
                if (!SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(use, token).Symbol, pass.Symbol) || pass.Moved.Contains(use)) continue;
                switch (Use(use, model, token, component))
                {
                    case null: return null; // the component whole
                    case true: pass.Kept++; break;
                }
            }
            if (pass.Moved.Count == 0) continue;
            passEdits.Add((pass, pass.Kept == 0 ? null : FreeName(method, pass.Symbol.Name + Suffix(newName, component.Name))));
        }

        // The edits, now that every use is known to be one the split follows.
        var declarationEditor = editors.TryGetValue(componentDocument.Id, out var de) ? de : editors[componentDocument.Id] = await DocumentEditor.CreateAsync(componentDocument, token).ConfigureAwait(false);
        foreach (var (editor, name) in renames) editor.ReplaceNode(name, Renamed(name, newName));
        foreach (var (editor, type, name) in addedTypes) editor.InsertAfter(type, type.ReplaceNode(name, Renamed(name, newName)).WithoutTrivia().WithLeadingTrivia(SyntaxFactory.Space));
        foreach (var (editor, argument, value) in creations)
        {
            var (kept, added) = Divide(value, movedSet, newName, editor.SemanticModel, token);
            if (kept != value) editor.ReplaceNode(value, kept);
            editor.InsertAfter(argument, SyntaxFactory.Argument(added).WithLeadingTrivia(SyntaxFactory.Space));
        }
        foreach (var (pass, added) in passEdits)
        {
            var editor = editors[solution.GetDocument(pass.Syntax.SyntaxTree)!.Id];
            var newType = pass.Syntax.Type!.ReplaceNodes(pass.Syntax.Type!.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                .Where(n => SymbolEqualityComparer.Default.Equals(editor.SemanticModel.GetSymbolInfo(n, token).Symbol, component)), (n, _) => Renamed(n, newName));
            if (added == null) editor.ReplaceNode(pass.Syntax.Type!, newType); // every use moves: the parameter takes the new component
            else
            {
                editor.InsertAfter(pass.Syntax, pass.Syntax.WithType(newType).WithIdentifier(SyntaxFactory.Identifier(added)).WithLeadingTrivia(SyntaxFactory.Space).WithoutTrailingTrivia());
                foreach (var use in pass.Moved) editor.ReplaceNode(use, SyntaxFactory.IdentifierName(added).WithTriviaFrom(use));
            }
        }
        Declare(declarationEditor, declaration, component, moved!, newName);

        foreach (var editor in editors.Values)
            solution = solution.WithDocumentSyntaxRoot(editor.OriginalDocument.Id, editor.GetChangedRoot());
        string fields = moved.Count == 1 ? moved[0]!.Name : string.Join(", ", moved.Take(moved.Count - 1).Select(f => f!.Name)) + " and " + moved[moved.Count - 1]!.Name;
        return ($"Move {fields} into a new component {newName}", solution);
    }

    /// <summary>
    /// Whether the component's declaration is one the split can write the new component beside: a plain struct of
    /// fields, declared once, implementing IComponent only, with no constructor, no layout attribute and no attribute
    /// but [NotSaved] (copied to the new one), not nested in a generic type.
    /// </summary>
    private static bool Declarable(INamedTypeSymbol component, StructDeclarationSyntax declaration) =>
        !component.IsGenericType && component.ContainingType is not { IsGenericType: true }
        && declaration.ParameterList == null && !declaration.Modifiers.Any(SyntaxKind.PartialKeyword) && declaration.TypeParameterList == null
        && component.Interfaces.Length == 1 && component.Interfaces[0] is { Name: "IComponent", ContainingNamespace.Name: "EasyEcs" } && component.AllInterfaces.Length == 1
        && component.GetAttributes().All(a => a.AttributeClass?.Name == "NotSavedAttribute")
        && !component.InstanceConstructors.Any(c => !c.IsImplicitlyDeclared)
        && declaration.Members.All(m => m is FieldDeclarationSyntax or MethodDeclarationSyntax or PropertyDeclarationSyntax);

    /// <summary>The new component's name: the old one's and its first moved field's, numbered when taken.</summary>
    private static string NewName(Compilation compilation, INamedTypeSymbol component, IFieldSymbol first)
    {
        string field = first.Name.TrimStart('_');
        string name = component.Name + (field.Length == 0 ? "Part" : char.ToUpperInvariant(field[0]) + field.Substring(1));
        string free = name;
        for (int k = 2; compilation.GetSymbolsWithName(free, SymbolFilter.Type).Any() || component.ContainingType?.GetMembers(free).Any() == true; k++) free = name + k;
        return free;
    }

    /// <summary>The new component's part of a parameter name: the new name less the old one's (BodyX of Body: X).</summary>
    private static string Suffix(string newName, string oldName) => newName.StartsWith(oldName, System.StringComparison.Ordinal) && newName.Length > oldName.Length
        ? newName.Substring(oldName.Length) : newName;

    /// <summary><paramref name="name"/>, numbered when the method already declares it.</summary>
    private static string FreeName(MethodDeclarationSyntax method, string name)
    {
        var taken = new HashSet<string>(method.DescendantTokens().Where(t => t.IsKind(SyntaxKind.IdentifierToken)).Select(t => t.ValueText));
        string free = name;
        for (int k = 2; taken.Contains(free); k++) free = name + k;
        return free;
    }

    /// <summary>The whole type expression a name ends (<c>Ns.Body</c> for <c>Body</c>).</summary>
    private static SyntaxNode Outer(SyntaxNode name)
    {
        while (name.Parent is QualifiedNameSyntax q && q.Right == name || name.Parent is AliasQualifiedNameSyntax a && a.Name == name) name = name.Parent;
        return name;
    }

    private static IdentifierNameSyntax Renamed(IdentifierNameSyntax name, string to) => SyntaxFactory.IdentifierName(to).WithTriviaFrom(name);

    private static bool InEasyEcs(ISymbol symbol) => symbol.ContainingNamespace is { Name: "EasyEcs", ContainingNamespace.IsGlobalNamespace: true };

    private static bool Generated(ISymbol symbol) => symbol.Locations.Any(l => l.SourceTree?.FilePath.Contains("EasyEcs.Generator") == true)
        || symbol.ContainingType?.GetAttributes().Any(a => a.AttributeClass?.Name == "GeneratedCodeAttribute") == true;

    /// <summary>A parameter of a [ForEach] or [EntityMethod] method: the rows the split keeps together (every entity with the component has the new one).</summary>
    private static PassParameter? Pass(ParameterSyntax syntax, SemanticModel model, CancellationToken token) =>
        syntax.Parent?.Parent is MethodDeclarationSyntax method && model.GetDeclaredSymbol(method, token) is { } symbol
        && symbol.GetAttributes().Any(a => a.AttributeClass is { Name: "ForEachAttribute" or "EntityMethodAttribute" } attribute && InEasyEcs(attribute))
        && model.GetDeclaredSymbol(syntax, token) is { } parameter
            ? new PassParameter(syntax, parameter) : null;

    /// <summary>The field of the component a <c>Get&lt;C&gt;()</c> / <c>Read&lt;C&gt;()</c> call reads (<c>entity.Get&lt;C&gt;().X</c>), else null.</summary>
    private static IFieldSymbol? Accessed(InvocationExpressionSyntax call, SemanticModel model, CancellationToken token) =>
        call.Parent is MemberAccessExpressionSyntax access && access.Expression == call && model.GetSymbolInfo(access.Name, token).Symbol is IFieldSymbol field ? field : null;

    /// <summary>
    /// A value of the component given to an arity call that adds it (<c>CreateEntity</c>, <c>Set</c>,
    /// <c>Instantiate</c>), so the new component's value goes beside it: the argument, the call and the components it
    /// already takes; null for a value given anywhere else.
    /// </summary>
    private static (ArgumentSyntax Argument, InvocationExpressionSyntax Call, int Components)? Creation(ExpressionSyntax value, SemanticModel model, CancellationToken token) =>
        value.Parent is ArgumentSyntax { NameColon: null, RefKindKeyword.RawKind: 0 } argument && argument.Parent?.Parent is InvocationExpressionSyntax call
        && model.GetSymbolInfo(call, token).Symbol is IMethodSymbol method && InEasyEcs(method) && Adders.Contains(method.Name) && method.Name != "Remove"
        && method.ContainingType.Name is "Entity" or "World" or "Prefab"
            ? (argument, call, method.TypeArguments.Length) : null;

    /// <summary>
    /// Whether <paramref name="receiver"/>.X reads the component's row where the split can follow it: a pass
    /// parameter (<c>c</c>, <c>span[i]</c>, <c>m.Value</c>, <c>m.Write()</c>; <paramref name="parameterUse"/> is its
    /// name), <c>entity.Get&lt;C&gt;()</c> / <c>Read&lt;C&gt;()</c>, or the generated <c>entity.C</c>.
    /// </summary>
    private static bool Receiver(ExpressionSyntax receiver, SemanticModel model, CancellationToken token, INamedTypeSymbol component,
        Dictionary<IParameterSymbol, PassParameter> parameters, out IdentifierNameSyntax? parameterUse)
    {
        parameterUse = null;
        var inner = receiver switch
        {
            ElementAccessExpressionSyntax element => element.Expression,
            MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Value" } value => value.Expression,
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Write" } write, ArgumentList.Arguments.Count: 0 } => write.Expression,
            _ => receiver,
        };
        if (inner is IdentifierNameSyntax name && model.GetSymbolInfo(name, token).Symbol is IParameterSymbol p && parameters.ContainsKey(p))
        {
            parameterUse = name;
            return true;
        }
        return receiver switch
        {
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name: GenericNameSyntax { Identifier.ValueText: "Get" or "Read" } } } call
                => model.GetSymbolInfo(call, token).Symbol is IMethodSymbol { TypeArguments.Length: 1 } method && InEasyEcs(method)
                   && SymbolEqualityComparer.Default.Equals(method.TypeArguments[0], component),
            MemberAccessExpressionSyntax generated => model.GetSymbolInfo(generated.Name, token).Symbol is IPropertySymbol property && property.Name == component.Name && Generated(property),
            _ => false,
        };
    }

    /// <summary>
    /// A use of a pass parameter that is not a moved field: true when it reads a kept field, false when it reads no row
    /// (<c>span.Length</c>, <c>maybe.HasValue</c>), null when it uses the component whole.
    /// </summary>
    private static bool? Use(IdentifierNameSyntax use, SemanticModel model, CancellationToken token, INamedTypeSymbol component)
    {
        SyntaxNode at = use;
        switch (at.Parent)
        {
            case MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Length" or "IsEmpty" or "HasValue" } other when other.Expression == at:
                return false;
            case ElementAccessExpressionSyntax element when element.Expression == at:
                at = element;
                break;
            case MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Value" } value when value.Expression == at:
                at = value;
                break;
            case MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Write", Parent: InvocationExpressionSyntax write } method when method.Expression == at:
                at = write;
                break;
        }
        return at.Parent is MemberAccessExpressionSyntax field && field.Expression == at && model.GetSymbolInfo(field.Name, token).Symbol is IFieldSymbol f
               && SymbolEqualityComparer.Default.Equals(f.ContainingType, component) ? true : null;
    }

    /// <summary>
    /// A value of the component divided in two: the kept fields' initializers stay in it, the moved ones go to the new
    /// component's value (<c>new C { A = 1, X = 2 }</c>: <c>new C { A = 1 }</c> and <c>new N { X = 2 }</c>).
    /// </summary>
    private static (ExpressionSyntax Kept, ExpressionSyntax Added) Divide(ExpressionSyntax value, HashSet<IFieldSymbol> moved, string newName, SemanticModel model, CancellationToken token)
    {
        if (value is DefaultExpressionSyntax) return (value, SyntaxFactory.ParseExpression($"default({newName})"));
        var creation = (ObjectCreationExpressionSyntax)value;
        if (creation.Initializer == null) return (value, SyntaxFactory.ParseExpression($"new {newName}()"));
        var assignments = creation.Initializer.Expressions;
        bool Moves(ExpressionSyntax e) => e is AssignmentExpressionSyntax { Left: IdentifierNameSyntax left } && model.GetSymbolInfo(left, token).Symbol is IFieldSymbol f && moved.Contains(f);
        var stay = assignments.Where(e => !Moves(e)).Select(e => e.WithoutTrivia().ToString()).ToList();
        var go = assignments.Where(Moves).Select(e => e.WithoutTrivia().ToString()).ToList();
        string Of(string type, List<string> inits) => inits.Count == 0 ? $"new {type}()" : $"new {type} {{ {string.Join(", ", inits)} }}";
        var kept = go.Count == 0 ? value : SyntaxFactory.ParseExpression(Of(creation.Type.ToString(), stay)).WithTriviaFrom(value);
        return (kept, SyntaxFactory.ParseExpression(Of(newName, go)));
    }

    /// <summary>The moved fields out of the component's declaration, and the new component declared after it, in its style.</summary>
    private static void Declare(DocumentEditor editor, StructDeclarationSyntax declaration, INamedTypeSymbol component, List<IFieldSymbol> moved, string newName)
    {
        var movedNames = new HashSet<string>(moved.Select(f => f.Name));
        var members = new List<string>();
        foreach (var field in declaration.Members.OfType<FieldDeclarationSyntax>())
        {
            var going = field.Declaration.Variables.Where(v => movedNames.Contains(v.Identifier.ValueText)).ToList();
            if (going.Count == 0) continue;
            var prefix = new StringBuilder();
            foreach (var list in field.AttributeLists) prefix.Append(list.WithoutTrivia()).Append(' ');
            foreach (var modifier in field.Modifiers) prefix.Append(modifier.ValueText).Append(' ');
            members.Add($"{prefix}{field.Declaration.Type.WithoutTrivia()} {string.Join(", ", going.Select(v => v.WithoutTrivia().ToString()))};");
            if (going.Count == field.Declaration.Variables.Count) editor.RemoveNode(field, SyntaxRemoveOptions.KeepNoTrivia);
            else
            {
                var variables = field.Declaration.Variables;
                foreach (var v in going) variables = variables.Remove(variables.First(x => x.Identifier.ValueText == v.Identifier.ValueText));
                editor.ReplaceNode(field, field.WithDeclaration(field.Declaration.WithVariables(variables)));
            }
        }

        string eol = EndOfLine(declaration.SyntaxTree.GetRoot()).ToFullString();
        var lead = declaration.GetLeadingTrivia();
        string indent = lead.Count > 0 && lead.Last().IsKind(SyntaxKind.WhitespaceTrivia) ? lead.Last().ToFullString() : "";
        var lines = declaration.SyntaxTree.GetText();
        bool oneLine = lines.Lines.GetLineFromPosition(declaration.OpenBraceToken.SpanStart).LineNumber == lines.Lines.GetLineFromPosition(declaration.CloseBraceToken.SpanStart).LineNumber;
        var text = new StringBuilder();
        if (!oneLine) text.Append(eol);
        text.Append(indent);
        foreach (var list in declaration.AttributeLists)
            text.Append(list.WithoutTrivia()).Append(oneLine || !list.GetTrailingTrivia().Any(t => t.IsKind(SyntaxKind.EndOfLineTrivia)) ? " " : eol + indent);
        foreach (var modifier in declaration.Modifiers) text.Append(modifier.ValueText).Append(' ');
        text.Append("struct ").Append(newName).Append(" : ").Append(declaration.BaseList!.Types[0].WithoutTrivia());
        if (oneLine) text.Append(" { ").Append(string.Join(" ", members)).Append(" }");
        else
        {
            var first = declaration.Members.FirstOrDefault()?.GetLeadingTrivia();
            string memberIndent = first is { Count: > 0 } t && t.Last().IsKind(SyntaxKind.WhitespaceTrivia) ? t.Last().ToFullString() : indent + "    ";
            text.Append(eol).Append(indent).Append('{').Append(eol);
            foreach (var member in members) text.Append(memberIndent).Append(member).Append(eol);
            text.Append(indent).Append('}');
        }
        text.Append(eol);
        var added = SyntaxFactory.ParseMemberDeclaration(text.ToString())!;
        editor.InsertAfter(declaration, added);
    }
}
