using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace EasyEcs.Generator;

/// <summary>
/// The rules of the declarations the generator turns into code for one entity, a schedule and a consuming pass
/// (EECS080-EECS085): a hand-written one-entity job [ForEntity] would write (EECS080), a [ForEntity] method that cannot
/// be one (EECS081), an [UpdateInterval] that cannot apply (EECS083), a [ForEach] method that destroys every entity it visits by hand (EECS084), a [Consume] that
/// cannot apply (EECS085). Each computes its code fix as diagnostic properties.
/// </summary>
public sealed partial class EasyEcsAnalyzer
{
    public static readonly DiagnosticDescriptor HandWrittenEntityJob = new("EECS080", "A hand-written one-entity job",
        "'{0}' is a job written by hand and run only with entity.Run: [ForEntity] on a static method writes the job, its WriteMask and its entry point ({1})",
        Category, DiagnosticSeverity.Warning, true,
        "A struct implementing IForEachEntity<T1..Tn>, created and handed to entity.Run, is what [ForEntity] generates " +
        "from a static method: the method's 'ref' parameters make the WriteMask, its filters the QueryFilter, and its other " +
        "parameters the values the entry point takes. The fix turns the struct into a static class with a [ForEntity] " +
        "method Run and each call into Type.Run(entity, values), when every use of the struct is such a call.",
        helpLinkUri: Help("EECS080"), customTags: WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor BadForEntity = new("EECS081", "Invalid [ForEntity] method",
        "[ForEntity] method '{0}' {1}",
        Category, DiagnosticSeverity.Error, true,
        "A [ForEntity] method is static and runs on one entity through its generated entry point, which takes the " +
        "entity. What belongs to a pass over many rows (component spans, an EntitySpan, a Reduce partial result, a " +
        "Scratch) has no meaning for one entity, and a method cannot be both a pass ([ForEach]) and a run on one entity.",
        helpLinkUri: Help("EECS081"));

    public static readonly DiagnosticDescriptor BadUpdateInterval = new("EECS083", "An [UpdateInterval] that cannot apply",
        "[UpdateInterval] on '{0}' cannot apply: {1}",
        Category, DiagnosticSeverity.Error, true,
        "[UpdateInterval(n, Phase = p)] runs a system on the updates whose count modulo n is p: n is 1 or more, p is 0 to " +
        "n - 1, and the type is an [EcsSystem]. The generator leaves an invalid one out, so the system would run every update.",
        helpLinkUri: Help("EECS083"));

    public static readonly DiagnosticDescriptor HandWrittenConsume = new("EECS084", "A [ForEach] method that destroys every entity it visits",
        "[ForEach] method '{0}' ends by destroying the entity it visits, on every path: [Consume] does it ({1})",
        Category, DiagnosticSeverity.Warning, true,
        "A method that handles event or command entities and destroys each one is a consuming pass: [Consume] destroys " +
        "exactly the rows the pass visited. A method that makes no structural change has them destroyed at once when " +
        "the pass ends (one DestroyAll of its filter, an emptied table at a time), instead of a destroy and a hole per row.",
        helpLinkUri: Help("EECS084"));

    public static readonly DiagnosticDescriptor BadConsume = new("EECS085", "A [Consume] that cannot apply",
        "[Consume] on '{0}' cannot apply: {1}",
        Category, DiagnosticSeverity.Error, true,
        "[Consume] destroys the rows a [ForEach] pass visited, when it ends. A method without [ForEach] has no pass, and a " +
        "sliced one would move the rows of the other slices with every destroy.", helpLinkUri: Help("EECS085"));

    /// <summary>EECS080's properties: field=parameter pairs, the components' modifiers, the filter attributes, the calls' replacements; EECS084's entity parameter to drop.</summary>
    public const string JobParameters = "Parameters", JobModifiers = "Modifiers", JobFilter = "Filter", JobCall = "Call", DropEntity = "DropEntity";

    private static void InitializeForms(AnalysisContext context)
    {
        context.RegisterSymbolAction(AnalyzeScheduleAttribute, SymbolKind.NamedType);
        context.RegisterSyntaxNodeAction(AnalyzeHandWrittenConsume, SyntaxKind.MethodDeclaration);
        context.RegisterSymbolAction(AnalyzeConsume, SymbolKind.Method);
        context.RegisterCompilationStartAction(AnalyzeEntityJobs);
    }

    // ---------------- EECS083: [UpdateInterval] ----------------

    private static void AnalyzeScheduleAttribute(SymbolAnalysisContext context)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (Symbols.ScheduleProblem(type) is not { } problem || Symbols.Schedule(type) is not var (_, _, attribute)) return;
        context.ReportDiagnostic(Diagnostic.Create(BadUpdateInterval,
            attribute.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ?? type.Locations.FirstOrDefault(),
            type.Name, problem));
    }

    // ---------------- EECS084 / EECS085: [Consume] ----------------

    private static void AnalyzeConsume(SymbolAnalysisContext context)
    {
        var method = (IMethodSymbol)context.Symbol;
        if (Symbols.ConsumeProblem(method) is { } problem)
            context.ReportDiagnostic(Diagnostic.Create(BadConsume, method.Locations.FirstOrDefault(), method.Name, problem));
    }

    /// <summary>
    /// EECS084: a [ForEach] method (without [Consume], on one thread, not sliced) whose body's last statement destroys
    /// its Entity parameter, with no return before it, after at least one other statement (a method that only destroys
    /// is EECS030's: DestroyAll).
    /// </summary>
    private static void AnalyzeHandWrittenConsume(SyntaxNodeAnalysisContext context)
    {
        var syntax = (MethodDeclarationSyntax)context.Node;
        if (syntax.Body is not { Statements: { Count: >= 2 } statements } body
            || statements[statements.Count - 1] is not ExpressionStatementSyntax { Expression: InvocationExpressionSyntax last } destroy
            || context.SemanticModel.GetDeclaredSymbol(syntax, context.CancellationToken) is not { } method
            || !Symbols.IsForEach(method) || Symbols.IsConsume(method) || Symbols.IsParallel(method) || Symbols.Slices(method) != 1
            || Symbols.IsForEntity(method)) return;
        if (context.SemanticModel.GetOperation(last, context.CancellationToken) is not IInvocationOperation { TargetMethod: { Name: "Destroy", Parameters.Length: 0 } target } call
            || !Symbols.Named(target.ContainingType, Symbols.Entity)
            || Symbols.Unwrap(call.Instance) is not IParameterReferenceOperation { Parameter: var entity }
            || !SymbolEqualityComparer.Default.Equals(entity.ContainingSymbol, method)) return;
        // Every path reaches it: no return (or goto) before it, outside lambdas and local functions.
        if (body.DescendantNodes(n => n is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
            .Any(n => n is ReturnStatementSyntax or GotoStatementSyntax or YieldStatementSyntax)) return;

        // The Entity goes too when the destroy was its only use: the method may then make no structural change, and
        // its rows go at once after the pass.
        int uses = body.DescendantNodes().OfType<IdentifierNameSyntax>().Count(n => n.Identifier.ValueText == entity.Name
            && SymbolEqualityComparer.Default.Equals(context.SemanticModel.GetSymbolInfo(n, context.CancellationToken).Symbol, entity));
        bool drop = uses == 1;
        bool bulk = drop && method.IsStatic && !method.Parameters.Any(p => Symbols.Classify(p) == Symbols.ParamKind.World);
        var properties = ImmutableDictionary<string, string?>.Empty.Add(FixKind, "Consume");
        if (drop) properties = properties.Add(DropEntity, entity.Name);
        context.ReportDiagnostic(Diagnostic.Create(HandWrittenConsume, destroy.GetLocation(), properties, method.Name,
            bulk ? $"without '{entity.Name}', the method changes no structure, and its rows go at once when the pass ends"
                : "each entity goes after its row"));
    }

    // ---------------- EECS080: hand-written one-entity jobs ----------------

    /// <summary>A struct job whose declaration [ForEntity] can write: what the fix needs of it.</summary>
    private sealed record JobShape(INamedTypeSymbol Type, Location Location, string Parameters, string Modifiers, bool UsesEntity);

    /// <summary>A use of a candidate job: a creation and its entity.Run call (Pattern), the Run call's type argument (Covered), or anything else.</summary>
    private sealed record JobUse(INamedTypeSymbol Type, int Kind, Location? Statement, Location? Call, string? Replacement, string? Filter, ISymbol? FilterField);

    private const int UseBad = 0, UsePattern = 1, UseCovered = 2;

    private static void AnalyzeEntityJobs(CompilationStartAnalysisContext start)
    {
        var compilation = start.Compilation;
        if (compilation.GetTypeByMetadataName(Symbols.Entity) is not { } entityType) return;
        var shapes = new ConcurrentDictionary<INamedTypeSymbol, JobShape>(SymbolEqualityComparer.Default);
        var uses = new ConcurrentBag<JobUse>();
        // The names of the struct types that implement IForEachEntity: only identifiers with one of them are bound.
        var names = new HashSet<string>(compilation.GetSymbolsWithName(_ => true, SymbolFilter.Type).OfType<INamedTypeSymbol>()
            .Where(t => t.TypeKind == TypeKind.Struct && EntityJobInterface(t) != null).Select(t => t.Name));
        if (names.Count == 0) return;

        start.RegisterSyntaxNodeAction(context =>
        {
            var declaration = (StructDeclarationSyntax)context.Node;
            if (!names.Contains(declaration.Identifier.ValueText)
                || context.SemanticModel.GetDeclaredSymbol(declaration, context.CancellationToken) is not { } type
                || JobShapeOf(declaration, type, context.SemanticModel, context.CancellationToken) is not { } shape) return;
            shapes[type] = shape;
        }, SyntaxKind.StructDeclaration);

        start.RegisterSyntaxNodeAction(context =>
        {
            var name = (IdentifierNameSyntax)context.Node;
            if (!names.Contains(name.Identifier.ValueText)
                || context.SemanticModel.GetSymbolInfo(name, context.CancellationToken).Symbol is not INamedTypeSymbol { TypeKind: TypeKind.Struct } type
                || EntityJobInterface(type) == null || !type.Locations.Any(l => l.IsInSource)) return;
            uses.Add(JobUseOf(name, type, entityType, context.SemanticModel, context.CancellationToken));
        }, SyntaxKind.IdentifierName);

        start.RegisterCompilationEndAction(context =>
        {
            foreach (var group in uses.GroupBy(u => u.Type, SymbolEqualityComparer.Default))
            {
                if (!shapes.TryGetValue((INamedTypeSymbol)group.Key!, out var shape)) continue;
                var all = group.ToList();
                var patterns = all.Where(u => u.Kind == UsePattern).ToList();
                if (patterns.Count == 0 || all.Any(u => u.Kind == UseBad)
                    || all.Where(u => u.Kind == UseCovered).Any(c => !patterns.Any(p => p.Call == c.Call))
                    || patterns.Select(p => p.Filter).Distinct().Count() != 1) continue;
                var locations = new List<Location>();
                var properties = ImmutableDictionary<string, string?>.Empty.Add(FixKind, "ForEntity")
                    .Add(JobParameters, shape.Parameters).Add(JobModifiers, shape.Modifiers).Add(JobFilter, patterns[0].Filter)
                    .Add(DropEntity, shape.UsesEntity ? null : "1");
                for (int k = 0; k < patterns.Count; k++)
                {
                    locations.Add(patterns[k].Statement!);
                    locations.Add(patterns[k].Call!);
                    properties = properties.Add(JobCall + k, patterns[k].Replacement);
                }

                if (patterns[0].FilterField?.Locations.FirstOrDefault(l => l.IsInSource) is { } field) locations.Add(field);
                string example = patterns[0].Replacement!;
                context.ReportDiagnostic(Diagnostic.Create(HandWrittenEntityJob, shape.Location, locations, properties, shape.Type.Name,
                    example.Length > 60 ? example.Substring(0, 57) + "..." : example));
            }
        });
    }

    /// <summary>The one IForEachEntity&lt;T1..Tn&gt; a struct implements (directly), else null.</summary>
    private static INamedTypeSymbol? EntityJobInterface(INamedTypeSymbol type)
    {
        INamedTypeSymbol? found = null;
        foreach (var face in type.Interfaces)
        {
            if (face.Name == "IForEachEntity" && Symbols.Named(face.ContainingNamespace, "EasyEcs") && face.Arity is >= 1 and <= Symbols.MaxComponents)
            {
                if (found != null) return null;
                found = face;
            }
            else if (!Symbols.Named(face, "EasyEcs.INoStructuralChanges")) return null;
        }

        return found;
    }

    /// <summary>
    /// The shape [ForEntity] can write: a struct (not partial, not generic, no attributes) with only instance fields
    /// without initializers, a constant static WriteMask and Execute, whose body reads its fields and writes no
    /// component WriteMask leaves out. Null otherwise.
    /// </summary>
    private static JobShape? JobShapeOf(StructDeclarationSyntax declaration, INamedTypeSymbol type, SemanticModel model, System.Threading.CancellationToken token)
    {
        if (EntityJobInterface(type) is not { } face || type.IsGenericType || type.DeclaringSyntaxReferences.Length != 1
            || declaration.Modifiers.Any(SyntaxKind.PartialKeyword) || declaration.AttributeLists.Count > 0
            || type.ContainingType is { IsGenericType: true } || declaration.ParameterList != null) return null;
        MethodDeclarationSyntax? execute = null;
        PropertyDeclarationSyntax? writeMask = null;
        var fields = new List<IFieldSymbol>();
        foreach (var member in declaration.Members)
        {
            switch (member)
            {
                case FieldDeclarationSyntax field when !field.Modifiers.Any(SyntaxKind.StaticKeyword) && !field.Modifiers.Any(SyntaxKind.ConstKeyword)
                                                       && field.AttributeLists.Count == 0 && field.Declaration.Variables.All(v => v.Initializer == null):
                    fields.AddRange(field.Declaration.Variables.Select(v => model.GetDeclaredSymbol(v, token)).OfType<IFieldSymbol>());
                    break;
                case PropertyDeclarationSyntax { Identifier.ValueText: "WriteMask" } property when property.Modifiers.Any(SyntaxKind.StaticKeyword) && writeMask == null:
                    writeMask = property;
                    break;
                case MethodDeclarationSyntax { Identifier.ValueText: "Execute", TypeParameterList: null } method when execute == null && (method.Body != null || method.ExpressionBody != null):
                    execute = method;
                    break;
                default:
                    return null;
            }
        }

        int n = face.TypeArguments.Length;
        if (execute == null || model.GetDeclaredSymbol(execute, token) is not { } run || run.Parameters.Length != n + 1
            || !Symbols.Named(run.Parameters[0].Type, Symbols.Entity) || run.Parameters.Skip(1).Any(p => p.RefKind != RefKind.Ref)) return null;
        int declared = -1;
        if (writeMask != null)
        {
            if (Symbols.Returned(writeMask) is not { } value || model.GetConstantValue(value, token) is not { HasValue: true, Value: int mask }) return null;
            declared = mask;
        }

        var body = (SyntaxNode?)execute.Body ?? execute.ExpressionBody;
        if (body == null || model.GetOperation(body, token) is not { } operation) return null;
        int written = 0, used = 0;
        bool usesEntity = false;
        foreach (var o in operation.DescendantsAndSelf())
        {
            switch (o)
            {
                case IFieldReferenceOperation { Instance: IInstanceReferenceOperation, Field: var f } reference
                    when fields.Contains(f, SymbolEqualityComparer.Default):
                    if (Uses.MayWrite(reference)) return null; // a result the caller reads back: not a value it passes
                    break;
                case IInstanceReferenceOperation { Parent: not IFieldReferenceOperation }:
                    return null; // 'this' itself: the job as a value
                case IParameterReferenceOperation { Parameter: var p } when SymbolEqualityComparer.Default.Equals(p.ContainingSymbol, run):
                    if (p.Ordinal == 0) usesEntity = true;
                    else
                    {
                        used |= 1 << (p.Ordinal - 1);
                        if (Uses.MayWrite(o)) written |= 1 << (p.Ordinal - 1);
                    }
                    break;
            }
        }

        var modifiers = new List<string>();
        for (int k = 0; k < n; k++)
        {
            bool inMask = (declared & (1 << k)) != 0;
            if (!inMask && (written & (1 << k)) != 0) return null; // the WriteMask leaves out a write: EECS037's
            // A component the body never names only filters the entity: [With<T>], not an unused parameter (EECS026).
            modifiers.Add(inMask ? "ref" : (used & (1 << k)) != 0 ? "in" : "with");
        }

        // Each field becomes a parameter, named in camelCase unless that name is taken.
        var taken = new HashSet<string>(run.Parameters.Select(p => p.Name));
        foreach (var local in body.DescendantNodes().OfType<VariableDeclaratorSyntax>()) taken.Add(local.Identifier.ValueText);
        foreach (var designation in body.DescendantNodes().OfType<SingleVariableDesignationSyntax>()) taken.Add(designation.Identifier.ValueText);
        var pairs = new List<string>();
        foreach (var field in fields)
        {
            string name = char.ToLowerInvariant(field.Name[0]) + field.Name.Substring(1);
            if (taken.Contains(name) || SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None) name = field.Name;
            if (!taken.Add(name)) return null;
            pairs.Add(field.Name + "=" + name);
        }

        return new JobShape(type, declaration.Identifier.GetLocation(), string.Join(";", pairs), string.Join(",", modifiers), usesEntity);
    }

    /// <summary>
    /// What a use of candidate job <paramref name="type"/> is: <c>var job = new Job { F = value };</c> followed by a
    /// statement whose only use of <c>job</c> is <c>entity.Run&lt;..., Job&gt;(ref job[, Filter])</c> (a Pattern, with its
    /// replacement <c>Job.Run(entity, values)</c>), the type argument of such a call (Covered), or anything else (Bad).
    /// </summary>
    private static JobUse JobUseOf(IdentifierNameSyntax name, INamedTypeSymbol type, INamedTypeSymbol entityType, SemanticModel model,
        System.Threading.CancellationToken token)
    {
        var bad = new JobUse(type, UseBad, null, null, null, null, null);
        SyntaxNode node = name;
        while (node.Parent is QualifiedNameSyntax qualified && qualified.Right == node || node.Parent is AliasQualifiedNameSyntax alias && alias.Name == node)
            node = node.Parent;

        // The type argument of the Run call: covered by the creation that precedes it.
        if (node.Parent is TypeArgumentListSyntax { Parent: GenericNameSyntax { Identifier.ValueText: "Run" } generic } arguments
            && arguments.Arguments.Last() == node && generic.Parent is MemberAccessExpressionSyntax { Parent: InvocationExpressionSyntax invocation })
            return new JobUse(type, UseCovered, null, invocation.GetLocation(), null, null, null);

        if (node.Parent is not ObjectCreationExpressionSyntax creation
            || creation.ArgumentList is { Arguments.Count: > 0 }
            || creation.Parent is not EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator }
            || declarator.Parent is not VariableDeclarationSyntax { Variables.Count: 1, Type: var declared } variable
            || !(declared.IsVar || model.GetTypeInfo(declared, token).Type is { } t && SymbolEqualityComparer.Default.Equals(t, type))
            || variable.Parent is not LocalDeclarationStatementSyntax { Parent: BlockSyntax block } statement
            || model.GetDeclaredSymbol(declarator, token) is not ILocalSymbol local) return bad;
        int at = block.Statements.IndexOf(statement);
        if (at + 1 >= block.Statements.Count) return bad;
        var next = block.Statements[at + 1];

        // The local's only use in its method: the Run call's 'ref job'.
        var scope = (SyntaxNode?)block.FirstAncestorOrSelf<MemberDeclarationSyntax>() ?? block;
        var references = scope.DescendantNodes().OfType<IdentifierNameSyntax>().Where(i => i.Identifier.ValueText == local.Name
            && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(i, token).Symbol, local)).ToList();
        if (references.Count != 1 || !next.Span.Contains(references[0].Span)
            || references[0].Parent is not ArgumentSyntax { RefKindKeyword.ValueText: "ref", Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax call } list }
            || list.Arguments[0] != references[0].Parent
            || model.GetOperation(call, token) is not IInvocationOperation { TargetMethod: { Name: "Run" } run } operation
            || !SymbolEqualityComparer.Default.Equals(run.ContainingType, entityType)
            || !SymbolEqualityComparer.Default.Equals(run.TypeArguments.LastOrDefault(), type)
            || call.Expression is not MemberAccessExpressionSyntax { Expression: var receiver }
            || Symbols.Unwrap(operation.Instance) is not (ILocalReferenceOperation or IParameterReferenceOperation or IFieldReferenceOperation)) return bad;

        // The filter: none, or a static readonly field built by QueryFilter.Create() and With / Without / WithAny.
        string filter = "";
        ISymbol? field = null;
        if (list.Arguments.Count == 2)
        {
            if (operation.Arguments.Length != 2 || Symbols.Unwrap(operation.Arguments[1].Value) is not IFieldReferenceOperation { Field: { IsStatic: true, IsReadOnly: true } f }
                || FilterAttributes(f, token) is not { } attributes) return bad;
            filter = attributes;
            field = f;
        }

        // The values, in the fields' order; side-effect free, since they are now evaluated where the call is.
        var values = new Dictionary<string, string>();
        if (creation.Initializer is { } initializer)
            foreach (var expression in initializer.Expressions)
            {
                if (expression is not AssignmentExpressionSyntax { Left: IdentifierNameSyntax member, Right: var value } assignment
                    || !assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                    || model.GetOperation(value, token) is not { } evaluated
                    || evaluated.DescendantsAndSelf().Any(o => o is IInvocationOperation or IObjectCreationOperation or IAssignmentOperation
                        or IIncrementOrDecrementOperation or IAnonymousFunctionOperation or IAwaitOperation)) return bad;
                values[member.Identifier.ValueText] = value.ToString();
            }

        var parts = new List<string> { receiver.ToString() };
        foreach (var member in type.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic && !f.IsImplicitlyDeclared))
            parts.Add(values.TryGetValue(member.Name, out var v) ? v : "default");
        string replacement = $"{creation.Type}.Run({string.Join(", ", parts)})";
        return new JobUse(type, UsePattern, statement.GetLocation(), call.GetLocation(), replacement, filter, field);
    }

    /// <summary>
    /// The attributes a filter field's initializer stands for (<c>QueryFilter.Create().Without&lt;Closed&gt;()</c> is
    /// <c>Without&lt;Closed&gt;</c>), or null when it calls anything else.
    /// </summary>
    private static string? FilterAttributes(IFieldSymbol field, System.Threading.CancellationToken token)
    {
        if (!Symbols.Named(field.Type, "EasyEcs.QueryFilter")
            || field.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(token) is not VariableDeclaratorSyntax { Initializer.Value: var value }) return null;
        var attributes = new List<string>();
        while (value is InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0, Expression: MemberAccessExpressionSyntax access })
        {
            if (access.Name is GenericNameSyntax { Identifier.ValueText: "With" or "Without" or "WithAny" } filter)
                attributes.Insert(0, $"{filter.Identifier.ValueText}<{string.Join(", ", filter.TypeArgumentList.Arguments)}>");
            else if (access is { Name.Identifier.ValueText: "Create", Expression: IdentifierNameSyntax { Identifier.ValueText: "QueryFilter" } or MemberAccessExpressionSyntax { Name.Identifier.ValueText: "QueryFilter" } })
                return string.Join(", ", attributes);
            else return null;
            value = access.Expression;
        }

        return null;
    }
}
