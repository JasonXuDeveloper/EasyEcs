using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace EasyEcs.Generator;

/// <summary>
/// What the generator of an assembly recorded for the generators of the assemblies that reference it (Recorder.cs
/// writes it), read back here: metadata holds no method body, and a ProjectReference compiles against a reference
/// assembly whose bodies are <c>throw null</c>, so without it a [ForEach] method calling <c>new Position(x, y)</c> or
/// <c>Terrain.Cell(p)</c> of a contracts assembly would stay out of its vector loop. The facts are hidden assembly
/// attributes, which reference assemblies keep: per public constructor of a public component, the member each
/// parameter sets (<c>[assembly: ConstructorFields]</c>); per small public helper, its body with every name qualified
/// (<c>[assembly: InlineBody]</c>), whose fields the referencing assembly cannot see stand as placeholders its
/// generated code reaches through <c>[UnsafeAccessor]</c>. Read once per referenced assembly (cached on its symbol, which
/// Roslyn shares between the compilations of one build and of an IDE's keystrokes). An assembly built without them
/// keeps the reasons it had: "declared in another assembly".
/// </summary>
internal sealed class Recorded
{
    public const string ConstructorFieldsAttribute = "EasyEcs.ConstructorFieldsAttribute";
    public const string InlineBodyAttribute = "EasyEcs.InlineBodyAttribute";

    /// <summary>The local function of a recorded body that is the member itself.</summary>
    public const string Body = "__EasyEcsBody";

    /// <summary>The prefix of the local functions of a recorded body that are the internal helpers it calls.</summary>
    public const string Helper = "__EasyEcsHelper";

    /// <summary>The prefix of a recorded body's placeholders, one per field the referencing assembly cannot see.</summary>
    public const string Accessor = "__EasyEcsAccessor";

    /// <summary>The annotation that carries a placeholder's field (<see cref="Access"/>) on its declaration.</summary>
    private const string AccessAnnotation = "EasyEcs.Access";

    /// <param name="Members">Per parameter, the field or property it sets ("" for none).</param>
    /// <param name="Reason">Why they are not known (its body computes them), or null.</param>
    public sealed record ConstructorFact(string[] Members, string? Reason);

    /// <param name="Body">The body as static local functions (<see cref="Recorded"/> remarks), or "".</param>
    /// <param name="Accessors">Per placeholder, its field: <c>kind|type|name</c>.</param>
    /// <param name="Reason">Why the body was not recorded, or null.</param>
    /// <param name="Assembly">The assembly that recorded it (the placeholders' types live there).</param>
    public sealed record BodyFact(string Body, string[] Accessors, string? Reason, string Assembly);

    /// <summary>
    /// A field a placeholder stands for: <paramref name="Kind"/> <c>field</c>, <c>static field</c> or <c>static readonly
    /// field</c>, the metadata name of its type (<c>Game.Physics</c>, a nested type <c>Game.Outer+Inner</c>), its name and
    /// its assembly's name, which <c>[UnsafeAccessorType]</c> needs.
    /// </summary>
    public sealed record Access(string Kind, string Type, string Name, string Assembly)
    {
        public bool IsStatic => Kind != "field";
        public bool IsReadOnly => Kind == "static readonly field";
    }

    private static readonly ConditionalWeakTable<IAssemblySymbol, Recorded> Table = new();
    private readonly Dictionary<string, ConstructorFact> _constructors = new(System.StringComparer.Ordinal);
    private readonly Dictionary<string, BodyFact> _bodies = new(System.StringComparer.Ordinal);

    private Recorded(IAssemblySymbol assembly)
    {
        foreach (var attribute in assembly.GetAttributes())
        {
            var arguments = attribute.ConstructorArguments;
            if (arguments.Length != 2 && arguments.Length != 3 || arguments[0].Value is not string id) continue;
            string? reason = attribute.NamedArguments.FirstOrDefault(a => a.Key == "Reason").Value.Value as string;
            if (Symbols.Named(attribute.AttributeClass, ConstructorFieldsAttribute) && arguments.Length == 2)
                _constructors[id] = new ConstructorFact(Strings(arguments[1]), reason);
            else if (Symbols.Named(attribute.AttributeClass, InlineBodyAttribute) && arguments.Length == 3 && arguments[1].Value is string body)
                _bodies[id] = new BodyFact(body, Strings(arguments[2]), reason, assembly.Identity.Name);
        }
    }

    private static string[] Strings(TypedConstant array) =>
        array.Kind == TypedConstantKind.Array ? array.Values.Select(v => v.Value as string ?? "").ToArray() : [];

    private static Recorded Of(IAssemblySymbol assembly) => Table.GetValue(assembly, static a => new Recorded(a));

    /// <summary>What a constructor of a referenced assembly sets, as its assembly recorded it; null: nothing recorded.</summary>
    public static ConstructorFact? Constructor(IMethodSymbol constructor) =>
        constructor.ContainingAssembly is { } assembly && constructor.OriginalDefinition.GetDocumentationCommentId() is { } id
        && Of(assembly)._constructors.TryGetValue(id, out var fact) ? fact : null;

    /// <summary>The body of a member of a referenced assembly, as its assembly recorded it; null: nothing recorded.</summary>
    public static BodyFact? BodyOf(IMethodSymbol method) =>
        method.ContainingAssembly is { } assembly && method.OriginalDefinition.GetDocumentationCommentId() is { } id
        && Of(assembly)._bodies.TryGetValue(id, out var fact) ? fact : null;

    /// <summary>
    /// A recorded body as a block of its local functions, each placeholder annotated with the field it stands for; null
    /// when the text does not parse.
    /// </summary>
    public static BlockSyntax? Parse(BodyFact fact)
    {
        if (SyntaxFactory.ParseStatement("{" + fact.Body + "}") is not BlockSyntax block || block.ContainsDiagnostics) return null;
        return block.ReplaceNodes(block.Statements.OfType<LocalFunctionStatementSyntax>().Where(f => f.Identifier.ValueText.StartsWith(Accessor, System.StringComparison.Ordinal)),
            (original, _) => int.TryParse(original.Identifier.ValueText.Substring(Accessor.Length), out int i) && i < fact.Accessors.Length
                ? original.WithAdditionalAnnotations(new SyntaxAnnotation(AccessAnnotation, fact.Accessors[i] + "|" + fact.Assembly))
                : original);
    }

    /// <summary>The field a placeholder (a local function of a recorded body, or its copy in a rewritten method) stands for, or null.</summary>
    public static Access? AccessOf(IMethodSymbol? method)
    {
        if (method is not { MethodKind: MethodKind.LocalFunction } || !method.Name.StartsWith("__", System.StringComparison.Ordinal)) return null;
        if (method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax().GetAnnotations(AccessAnnotation).FirstOrDefault()?.Data is not { } data) return null;
        var parts = data.Split('|');
        return parts.Length == 4 ? new Access(parts[0], parts[1], parts[2], parts[3]) : null;
    }

    /// <summary>The instance field a placeholder call reads or writes (its argument's), or null for any other call.</summary>
    public static IFieldSymbol? FieldOf(IInvocationOperation call) =>
        AccessOf(call.TargetMethod) is { IsStatic: false } access && call.TargetMethod.Parameters.Length == 1 && call.TargetMethod.Parameters[0].Type is INamedTypeSymbol type
            ? type.GetMembers(access.Name).OfType<IFieldSymbol>().FirstOrDefault(f => !f.IsStatic)
            : null;

    // ---------------- constructors ----------------

    /// <summary>
    /// The member each parameter of a constructor declared in this compilation sets (null where it sets none), or the
    /// reason it is not known: a record's positional parameters set their properties; a constructor whose body only
    /// assigns parameters to fields or auto-properties sets those. A field or property initializer the constructor also
    /// runs is a value of its own, so the members are not known then either. Read once for the vector loop (Kernel.Struct.cs)
    /// and for what the generator records for other assemblies (Recorder.cs), so both say the same.
    /// </summary>
    public static (ISymbol?[]? Members, string? Reason) Assignments(IMethodSymbol constructor, Compilation compilation, System.Threading.CancellationToken ct)
    {
        var type = constructor.ContainingType;
        var syntax = constructor.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(ct);
        var assigned = new ISymbol?[constructor.Parameters.Length];
        if (syntax is RecordDeclarationSyntax)
        {
            for (int i = 0; i < assigned.Length; i++)
            {
                assigned[i] = type.GetMembers(constructor.Parameters[i].Name).OfType<IPropertySymbol>()
                    .FirstOrDefault(p => p.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(ct) is ParameterSyntax);
                if (assigned[i] == null) return (null, $"the parameter '{constructor.Parameters[i].Name}' of {type.Name}'s constructor sets no positional property");
            }
            return Initialized(type, ct) is { } initialized ? (null, initialized) : (assigned, null);
        }

        if (syntax is not ConstructorDeclarationSyntax { Initializer: null } declaration
            || Kernel.ModelOf(compilation, declaration.SyntaxTree).GetOperation(declaration, ct) is not IConstructorBodyOperation body)
            return (null, $"{type.Name}'s constructor is not declared in this compilation, so the fields it sets are not known");
        // => _x = x: the block of its one statement
        var statements = body.BlockBody?.Operations ?? body.ExpressionBody?.Operations ?? [];
        foreach (var statement in statements)
        {
            var e = statement is IExpressionStatementOperation { Operation: var inner } ? inner : statement;
            if (e is not ISimpleAssignmentOperation { Target: var target, Value: var value }
                || target is not IMemberReferenceOperation { Instance: IInstanceReferenceOperation, Member: var member and (IFieldSymbol or IPropertySymbol) }
                || Unwrap(value) is not IParameterReferenceOperation { Parameter: var p }
                || !SymbolEqualityComparer.Default.Equals(p.ContainingSymbol, constructor) || assigned[p.Ordinal] != null
                || member is IPropertySymbol property && Symbols.BackingField(property) == null)
                return (null, $"{type.Name}'s constructor does more than set fields from its parameters ('{Text(statement.Syntax)}')");
            assigned[p.Ordinal] = member;
        }
        return Initialized(type, ct) is { } reason ? (null, reason) : (assigned, null);
    }

    /// <summary>The reason a constructor of <paramref name="type"/> sets more than its parameters: a field or property initializer it runs.</summary>
    private static string? Initialized(INamedTypeSymbol type, System.Threading.CancellationToken ct)
    {
        foreach (var member in type.GetMembers())
        {
            if (member.IsStatic) continue;
            var initializer = member.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(ct) switch
            {
                VariableDeclaratorSyntax { Initializer: { } value } when member is IFieldSymbol => value,
                PropertyDeclarationSyntax { Initializer: { } value } when member is IPropertySymbol => value,
                _ => null,
            };
            if (initializer != null)
                return $"{type.Name}'s constructor also gives '{member.Name}' its initial value ('{Text(initializer.Value)}')";
        }
        return null;
    }

    private static IOperation Unwrap(IOperation op)
    {
        while (op is IConversionOperation { Conversion.IsIdentity: true } c) op = c.Operand;
        return op;
    }

    /// <summary>The source of <paramref name="node"/> on one line, cut short.</summary>
    private static string Text(SyntaxNode node)
    {
        string text = string.Join(" ", node.ToString().Split(['\r', '\n', '\t', ' '], System.StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= 60 ? text : text.Substring(0, 57) + "...";
    }
}
