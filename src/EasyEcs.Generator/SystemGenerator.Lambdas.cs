using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;
using static EasyEcs.Generator.Code;

namespace EasyEcs.Generator;

/// <summary>
/// Lambda passes (<see cref="LambdaPass"/>): each call <c>world.ForEach(static (ref P p, in V v) =&gt; …)</c> is
/// intercepted by a method that runs the job a [ForEach] method with the lambda's parameters gets (<see cref="EmitJob"/>,
/// its vector loop from <see cref="Kernel"/>), the lambda's body copied into the method the job calls, the values
/// before the lambda in the job's fields; <c>world.ParallelFor(ref items, …, static (ref T item, …) =&gt; …)</c> runs an
/// <c>IForSpan&lt;T&gt;</c> job whose loop over a chunk's elements is the vector loop of the element as one column
/// (<see cref="EmitBufferJob"/>). One file per source file, implementation output (an IDE never needs it).
/// Calls the analyzer reports (EECS095) are skipped; a body naming what another file cannot reach (EECS096) runs
/// through the lambda's delegate.
/// </summary>
public sealed partial class SystemGenerator
{
    private static void InitializeLambdas(IncrementalGeneratorInitializationContext context)
    {
        var calls = BoundLater(context, static (node, _) => LambdaPass.IsCandidate(node), LambdaModel.Create)
            .WithTrackingName("LambdaPasses");
        // Per source file: an edit changes every location of its file (they hold its checksum), and no other file.
        var files = calls.Collect()
            .SelectMany(static (all, _) => all.GroupBy(c => c!.File, System.StringComparer.Ordinal)
                .Select(g => new LambdaFile(g.Key, new EquatableArray<LambdaModel>(g.OrderBy(c => c!.Position).ToArray()!))))
            .WithTrackingName("LambdaFiles");
        context.RegisterImplementationSourceOutput(files, static (spc, file) =>
            spc.AddSource($"{System.IO.Path.GetFileNameWithoutExtension(file.File)}.{Fnv(file.File):x8}.Lambdas.g.cs", SourceText.From(EmitLambdas(file), Encoding.UTF8)));
    }

    /// <summary>The lambda passes of one source file, in the order they appear.</summary>
    private sealed record LambdaFile(string File, EquatableArray<LambdaModel> Calls);

    /// <param name="Version">The interceptable location's version (<c>InterceptsLocation(version, data)</c>).</param>
    /// <param name="Data">The interceptable location's data: the file's checksum and the call's position.</param>
    /// <param name="Where">The call as a reader finds it (<c>Game.cs(12,9)</c>).</param>
    /// <param name="Body">The lambda's body as generated code (<see cref="LambdaPass.Copy"/>), or null: the job calls the lambda's delegate.</param>
    /// <param name="Usings">The using directives the body's extension calls need.</param>
    /// <param name="Returns">The return type of the lambda's delegate: void, or the type of an expression body's value
    /// (<c>h.Value += f.Value</c> is a float), which the pass drops.</param>
    /// <param name="Element">A ParallelFor over a buffer's elements: the element's type, else null.</param>
    /// <param name="Fuses">Where the call stands for pass fusion with the calls next to it, or null when it shares no walk.</param>
    private sealed record LambdaModel(string File, int Position, int Version, string Data, string Where, ForEachModel Method,
        string? Body, EquatableArray<string> Usings, string Returns, string? Element = null, LambdaPlace? Fuses = null)
    {
        public static LambdaModel? Create(SemanticModel model, InvocationExpressionSyntax invocation, System.Threading.CancellationToken ct)
        {
            if (model.GetOperation(invocation, ct) is not IInvocationOperation operation || LambdaPass.Read(operation) is not { } call
                || LambdaPass.Problem(call, model, ct, out _) != null || model.GetInterceptableLocation(invocation, ct) is not { } location)
                return null;

            var lambda = call.Lambda!;
            var function = call.Function!;
            if (call.Element is { } element) return Buffer(call, element, model, location, invocation, ct);
            var parameters = lambda.Parameters.Select(p =>
            {
                string declared = LambdaPass.Declared(p);
                return Symbols.Classify(p, true) switch
                {
                    Symbols.ParamKind.Mutable => new ParamModel(Symbols.ParamKind.Mutable, Symbols.MutOf(p)!.ToDisplayString(Symbols.Fqn), "ref", p.Name, declared,
                        Symbols.Implements(Symbols.MutOf(p)!, Symbols.TrackChanges)),
                    Symbols.ParamKind.Reduction => new ParamModel(Symbols.ParamKind.Reduction, Symbols.ReduceElement(p.Type)!.ToDisplayString(Symbols.Fqn), "ref", p.Name, declared,
                        Accumulates: Symbols.Accumulates(Symbols.ReduceElement(p.Type)!)),
                    Symbols.ParamKind.Scratch => new ParamModel(Symbols.ParamKind.Scratch, Symbols.ScratchElement(p.Type)!.ToDisplayString(Symbols.Fqn), "", p.Name, declared),
                    Symbols.ParamKind.Emit => new ParamModel(Symbols.ParamKind.Emit, Symbols.EmitElement(p.Type)!.ToDisplayString(Symbols.Fqn), "ref", p.Name, declared),
                    Symbols.ParamKind.Lookup => new ParamModel(Symbols.ParamKind.Lookup, Symbols.LookupElement(p.Type)!.ToDisplayString(Symbols.Fqn), "in", p.Name, declared,
                        Key: Symbols.LookupKey(p, function.Syntax)?.ToDisplayString(Symbols.Fqn) ?? ""),
                    Symbols.ParamKind.Context => new ParamModel(Symbols.ParamKind.Context, p.Type.ToDisplayString(Symbols.Fqn), p.RefKind == RefKind.In ? "in" : "", p.Name, declared),
                    Symbols.ParamKind.View => ViewParameter(p, declared),
                    var kind => new ParamModel(kind, p.Type.ToDisplayString(Symbols.Fqn), p.RefKind == RefKind.Ref ? "ref" : "in", p.Name, declared),
                };
            }).ToArray();

            var with = new List<string>();
            var without = new List<string>();
            var withAny = new List<string>();
            var changed = new List<string>();
            var removed = new List<string>();
            var changedAny = new List<string>();
            var removedAny = new List<string>();
            foreach (var (kind, type, _) in Symbols.AttributeFilters(lambda))
            {
                var list = kind switch
                {
                    Symbols.FilterKind.With => with, Symbols.FilterKind.Without => without, Symbols.FilterKind.WithAny => withAny,
                    Symbols.FilterKind.Changed => changed, Symbols.FilterKind.Removed => removed,
                    Symbols.FilterKind.ChangedAny => changedAny, _ => removedAny,
                };
                string name = type.ToDisplayString(Symbols.Fqn);
                if (!list.Contains(name)) list.Add(name);
            }

            var query = parameters.Where(p => p.Kind is Symbols.ParamKind.Component or Symbols.ParamKind.Mutable).Select(p => p.Type).ToList();
            query.AddRange(parameters.Where(p => p.Kind == Symbols.ParamKind.View).SelectMany(p => p.Fields.Select(f => f.Type))); // as a method's
            // No component: the first [With<T>] (else [Changed<T>], which keeps filtering) is the queried one (LambdaPass.Problem: there is one).
            if (query.Count == 0 && with.Count == 0) query.Add(changed[0]);
            else if (query.Count == 0)
            {
                query.Add(with[0]);
                with.RemoveAt(0);
            }
            with.RemoveAll(query.Contains);

            bool stable = LambdaPass.IsStable(function);
            string? body = LambdaPass.Unreachable(call, model, ct) == null ? LambdaPass.Copy(call, model, ct) : null;
            string? scalar = null;
            // A body copied into the generated file is the method's; one run through its delegate has no loop to translate.
            var simd = body != null
                ? Kernel.Vectorize(lambda, function, call.Parallel, stable, model, call.Parallel
                    ? static (i, kind) => kind switch { Symbols.ParamKind.Singleton => $"s{i}.Value", Symbols.ParamKind.Reduction => $"l{i}", _ => $"x{i}" }
                    : static (i, kind) => kind switch { Symbols.ParamKind.Singleton => $"s{i}", Symbols.ParamKind.Reduction => $"l{i}", _ => $"x{i}" }, ct, out scalar)
                : null;
            // A body left row by row whose loops over spans have vector forms: copied with them (Kernel.Loops.cs).
            if (simd == null && body != null && call.Syntax!.Body is BlockSyntax block && Symbols.Simd(lambda) != Symbols.SimdOff
                && block.DescendantNodes().Any(static n => n is ForStatementSyntax or ForEachStatementSyntax)
                && Kernel.ElementLoops(lambda, block, model, ct) is var found && found.Any(f => f.Vector != null))
                body = WithElementLoops(lambda, block, model, found, "", ct).Trim();
            var owner = new OwnerModel("", "", "", "Lambda", "", true, false, false, "", "", "", "", 0);
            var method = new ForEachModel(owner, "Lambda", true, stable, call.Parallel, 0,
                new EquatableArray<string>(query.ToArray()), new EquatableArray<ParamModel>(parameters),
                new EquatableArray<string>(with.ToArray()), new EquatableArray<string>(without.ToArray()), new EquatableArray<string>(withAny.ToArray()),
                new EquatableArray<string>(changed.ToArray()), new EquatableArray<string>(removed.ToArray()),
                new EquatableArray<string>(changedAny.ToArray()), new EquatableArray<string>(removedAny.ToArray()),
                "void", "", "", new EquatableArray<string>([]), false)
            {
                Simd = simd,
                Scalar = scalar ?? (body == null ? "it runs through its delegate" : ""),
                // Reads of other entities at the row's ids (and probes, buffers of links), as a [ForEach] method's.
                Chases = ForEachModel.Usable(Generator.Chases.Of(function, out _), parameters),
                OutOfLine = parameters.Any(p => p.Kind == Symbols.ParamKind.Scratch) && LambdaPass.LoopsOrCalls(call.Syntax!),
            };
            var place = body == null || method.Since || method.HasChaseTwin || method.Simd is { VectorTail: true } ? null : LambdaPlace.Of(call, model, ct);
            if (place != null)
                method = method with
                {
                    Fusion = FusionSide.Of(lambda, stable, !lambda.Parameters.Any(p => Symbols.Named(p.Type, Symbols.Entity)), call.Parallel),
                };
            return new LambdaModel(invocation.SyntaxTree.FilePath, invocation.SpanStart, location.Version, location.Data,
                LambdaPass.Where(invocation.GetLocation()), method, body,
                new EquatableArray<string>(body == null ? [] : BodyCopy.Usings(invocation).Distinct().OrderBy(u => u, System.StringComparer.Ordinal).ToArray()),
                lambda.ReturnsVoid ? "void" : lambda.ReturnType.ToDisplayString(Symbols.Fqn), Fuses: place);
        }

        /// <summary>
        /// A ParallelFor over a buffer's elements: the element as the job's one column (a component parameter of the
        /// model, by its ref kind), the values after it; the vector loop from <see cref="Kernel.VectorizeElements"/>.
        /// </summary>
        private static LambdaModel Buffer(LambdaPass.Call call, ITypeSymbol element, SemanticModel model, InterceptableLocation location,
            InvocationExpressionSyntax invocation, System.Threading.CancellationToken ct)
        {
            var lambda = call.Lambda!;
            string type = element.ToDisplayString(Symbols.Fqn);
            var parameters = lambda.Parameters.Select((p, i) => i == 0
                ? new ParamModel(Symbols.ParamKind.Component, type, p.RefKind == RefKind.Ref ? "ref" : "in", p.Name, LambdaPass.Declared(p))
                : new ParamModel(Symbols.ParamKind.Context, p.Type.ToDisplayString(Symbols.Fqn), p.RefKind == RefKind.In ? "in" : "", p.Name, LambdaPass.Declared(p))).ToArray();
            string? body = LambdaPass.Unreachable(call, model, ct) == null ? LambdaPass.Copy(call, model, ct) : null;
            string? scalar = null;
            var simd = body != null ? Kernel.VectorizeElements(lambda, call.Function!, model, ct, out scalar) : null;
            var owner = new OwnerModel("", "", "", "Lambda", "", true, false, false, "", "", "", "", 0);
            var method = new ForEachModel(owner, "Lambda", true, true, true, 0,
                new EquatableArray<string>([type]), new EquatableArray<ParamModel>(parameters),
                new EquatableArray<string>([]), new EquatableArray<string>([]), new EquatableArray<string>([]),
                new EquatableArray<string>([]), new EquatableArray<string>([]), new EquatableArray<string>([]), new EquatableArray<string>([]),
                "void", "", "", new EquatableArray<string>([]), false)
            {
                Simd = simd,
                Scalar = scalar ?? (body == null ? "it runs through its delegate" : ""),
            };
            return new LambdaModel(invocation.SyntaxTree.FilePath, invocation.SpanStart, location.Version, location.Data,
                LambdaPass.Where(invocation.GetLocation()), method, body,
                new EquatableArray<string>(body == null ? [] : BodyCopy.Usings(invocation).Distinct().OrderBy(u => u, System.StringComparer.Ordinal).ToArray()),
                lambda.ReturnsVoid ? "void" : lambda.ReturnType.ToDisplayString(Symbols.Fqn), type);
        }
    }

    /// <summary>
    /// Where a lambda pass stands for pass fusion (<see cref="Fusion"/>): a call that is a statement of its own in a block
    /// (no label), on a World that is a local or a parameter (<see cref="World"/>), passing values that are locals and
    /// parameters by name (<see cref="Values"/>, null for one that is not). Two calls share a walk when the second is the
    /// next statement of the same block on the same World and every value it passes is one the first passes too: the
    /// first call's interceptor runs both lambdas in one walk, with the values it was given, and the second's does
    /// nothing. Evaluating the second call's arguments after the walk instead of between the passes changes nothing:
    /// they are a static lambda (a cached delegate) and names of locals no static lambda can write.
    /// </summary>
    /// <param name="Block">The block's position in the file.</param>
    /// <param name="Statement">The statement's index in its block.</param>
    /// <param name="Names">The lambda's value parameters, in order.</param>
    private sealed record LambdaPlace(int Block, int Statement, string World, EquatableArray<string?> Values, EquatableArray<string> Names)
    {
        public static LambdaPlace? Of(LambdaPass.Call call, SemanticModel model, System.Threading.CancellationToken ct)
        {
            var invocation = call.Invocation;
            if (invocation.Parent is not ExpressionStatementSyntax statement || statement.Parent is not BlockSyntax block
                || ((MemberAccessExpressionSyntax)invocation.Expression).Expression is not IdentifierNameSyntax receiver || Plain(receiver) is not { } world)
                return null;
            var arguments = call.Operation.Arguments.Where(a => a.Parameter is { } p && p.Ordinal < call.Operation.TargetMethod.Parameters.Length - 1)
                .OrderBy(a => a.Parameter!.Ordinal).Select(a => a.Syntax is ArgumentSyntax { Expression: IdentifierNameSyntax name } ? Plain(name) : null).ToArray();
            var names = call.Lambda!.Parameters.Where(p => Symbols.Classify(p, true) == Symbols.ParamKind.Context).Select(p => p.Name).ToArray();
            if (arguments.Length != names.Length) return null;
            return new LambdaPlace(block.SpanStart, block.Statements.IndexOf(statement), world, new EquatableArray<string?>(arguments), new EquatableArray<string>(names));

            // A local or a parameter held by value: nothing a pass does changes what its name reads.
            string? Plain(IdentifierNameSyntax name) => model.GetSymbolInfo(name, ct).Symbol switch
            {
                ILocalSymbol { RefKind: RefKind.None, IsConst: false } local => local.Name,
                IParameterSymbol { RefKind: RefKind.None, IsThis: false } parameter => parameter.Name,
                _ => null,
            };
        }

        /// <summary>
        /// The names the first call's interceptor gives this call's values (the first lambda's parameter names), or null
        /// when this call does not follow <paramref name="first"/>'s group (<paramref name="previous"/> its last call)
        /// or passes a value the first call does not.
        /// </summary>
        public string[]? Follows(LambdaPlace first, LambdaPlace previous)
        {
            if (Block != previous.Block || Statement != previous.Statement + 1 || World != first.World) return null;
            var renamed = new string[Values.Length];
            for (int i = 0; i < Values.Length; i++)
            {
                int j = Values[i] is { } value ? System.Array.IndexOf(first.Values.ToArray(), value) : -1;
                if (j < 0) return null;
                renamed[i] = first.Names[j];
            }

            return renamed;
        }
    }

    /// <summary>
    /// The file's lambda passes cut into walks (as <see cref="FusionGroups"/> cuts a system's methods): per call, the
    /// number of calls of the walk it starts (1: alone), 0 for a call inside a walk; and per call, the names its values
    /// take in the interceptor of its walk's first call (null for a first call).
    /// </summary>
    private static (int[] Groups, string[]?[] Names) LambdaGroups(LambdaFile file)
    {
        var groups = new int[file.Calls.Length];
        var names = new string[]?[file.Calls.Length];
        var group = new List<FusionSide>();
        for (int i = 0, start = 0; i < file.Calls.Length; i++)
        {
            var call = file.Calls[i];
            if (i > 0 && group.Count > 0 && call.Fuses is { } place && call.Method.Fusion is { } side && file.Calls[start].Method.Parallel == call.Method.Parallel
                && place.Follows(file.Calls[start].Fuses!, file.Calls[i - 1].Fuses!) is { } renamed && Fusion.Problem(group, side) == null)
            {
                group.Add(side);
                groups[start]++;
                names[i] = renamed;
                continue;
            }

            start = i;
            groups[i] = 1;
            group.Clear();
            if (call.Fuses != null && call.Method.Fusion is { } first) group.Add(first);
        }

        return (groups, names);
    }

    /// <summary>
    /// The file of one source file's lambda passes: per call, its interceptor (the call's own signature, the World as
    /// <c>this</c>), the method its job calls (the lambda's body, or a call of its delegate) and its job and filter, as a
    /// [ForEach] method's. Numbered in file order: <c>Lambda3</c>, <c>LambdaJob3</c>, <c>LambdaFilter3</c>.
    /// </summary>
    private static string EmitLambdas(LambdaFile file)
    {
        var sb = new StringBuilder(Header);
        foreach (var u in file.Calls.SelectMany(c => c.Usings).Distinct().OrderBy(u => u, System.StringComparer.Ordinal)) sb.Append(u).Append('\n');
        var type = new StringBuilder();
        type.Append("file static class __ForEach\n{\n");
        var (groups, renamed) = LambdaGroups(file);
        // The file's calls as a system's methods, numbered as their jobs (LambdaJob{k}): what the fused walks are made of.
        var walks = new UpdateModel(file.Calls[0].Method.Owner, new EquatableArray<ForEachModel>(file.Calls.Select(c => c.Method).ToArray()));
        for (int k = 0; k < file.Calls.Length; k++)
        {
            var call = file.Calls[k];
            var method = call.Method;
            const string world = "__world", body = "__body";
            var values = method.Parameters.Where(p => p.Kind == Symbols.ParamKind.Context).Select(p => $"{p.Type} {Escape(p.Name)}");
            string parameters = string.Concat(method.Parameters.Select(p => $", {p.Declared} {Escape(p.Name)}"));
            type.Append($"    /// <summary>The lambda pass at {call.Where.Replace("<", "&lt;")}.</summary>\n");
            type.Append($"    [global::System.Runtime.CompilerServices.InterceptsLocation({call.Version}, \"{call.Data}\")]\n");
            // A change filter's tick is the first value, the filter's and not the lambda's (LambdaPass.Problem).
            const string since = "__since";
            string tick = method.Since ? $", ulong {since}" : "";
            string items = call.Element != null ? $", ref global::EasyEcs.DynamicBuffer<{call.Element}> __items" : "";
            type.Append($"    internal static void {method.Name}{k}(this global::EasyEcs.World {world}{items}{tick}{string.Concat(values.Select(v => ", " + v))}, global::System.Delegate {body})\n    {{\n");
            if (call.Body == null) type.Append($"        {method.Name}{k}Delegate = global::System.Runtime.CompilerServices.Unsafe.As<{method.Name}{k}Body>({body});\n");
            if (groups[k] == 0)
            {
                int first = k;
                while (groups[first] == 0) first--;
                type.Append($"        // the walk of the lambda pass before it ({method.Name}{first}) ran this one\n");
            }
            else if (groups[k] > 1) EmitLambdaWalk(type, walks, k, groups[k], renamed, world);
            else if (call.Element != null)
            {
                // The values into the job's fields, the chunks over the buffer's elements (World.ParallelFor's runner).
                string fields = string.Join(", ", method.Parameters.Select((p, i) => (p, i)).Where(x => x.p.Kind == Symbols.ParamKind.Context).Select(x => $"x{x.i} = {Escape(x.p.Name)}"));
                type.Append($"        var job = new {method.Name}Job{k} {{ {fields} }};\n");
                type.Append($"        {world}.ParallelFor(ref __items, ref job);\n");
            }
            else
            {
                string filter = method.Since ? $"{method.Name}Filter{k}.Since({since})" : $"in {method.Name}Filter{k}";
                // as a [ForEach] method's pass: the twin past the cache, the rows a vector loop marked changed after it
                EmitPass(type, method, "__ForEach", k, world, "job", ", " + filter, "", "        ");
            }
            type.Append("    }\n\n");

            // What the job calls per row: the lambda's body (inlined like a [ForEach] method), or its delegate.
            string impl = method.OutOfLine ? "    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]\n" : "";
            if (call.Body != null)
                type.Append($"{impl}    internal static void {method.Name}_{k}(object __owner{parameters})\n    {Indent(call.Body, "    ")}\n\n");
            else
            {
                // A static lambda's delegate is one object for the process (the compiler caches it): the field holds that one.
                type.Append($"    private delegate {call.Returns} {method.Name}{k}Body({string.Join(", ", method.Parameters.Select(p => $"{p.Declared} {Escape(p.Name)}"))});\n\n");
                type.Append($"    private static {method.Name}{k}Body {method.Name}{k}Delegate;\n\n");
                string arguments = string.Join(", ", method.Parameters.Select(p => (p.Declared.StartsWith("ref readonly ") ? "in " : p.Declared.StartsWith("ref ") ? "ref " : p.Declared.StartsWith("in ") ? "in " : "") + Escape(p.Name)));
                type.Append($"    internal static void {method.Name}_{k}(object __owner{parameters}) => {method.Name}{k}Delegate({arguments});\n\n");
            }

            if (call.Element != null) EmitBufferJob(type, method, call.Element, k);
            else EmitJob(type, method.Owner, method, k);
            if (groups[k] > 1) EmitFusedJob(type, walks, k, groups[k]);
        }

        type.Append("}\n");
        sb.Append($"\nnamespace {LambdaPass.Namespace}\n{{\n");
        foreach (var line in type.ToString().TrimEnd('\n').Split('\n')) sb.Append(line.Length > 0 ? "    " + line : "").Append('\n');
        sb.Append("}\n\nnamespace System.Runtime.CompilerServices\n{\n");
        sb.Append("    [global::System.AttributeUsage(global::System.AttributeTargets.Method, AllowMultiple = true)]\n");
        // The compiler reads the arguments; the attribute itself keeps nothing (CS9113 on a primary constructor).
        sb.Append("    file sealed class InterceptsLocationAttribute : global::System.Attribute\n    {\n");
        sb.Append("        public InterceptsLocationAttribute(int version, string data) { }\n    }\n}\n");
        return sb.ToString();
    }

    /// <summary>
    /// The interceptor's body of the first of <paramref name="count"/> lambda passes that share one walk
    /// (<see cref="LambdaGroups"/>): each pass on its own while the rows fit in cache (<see cref="Sized"/>, as a system's
    /// walk), else the fused job (<see cref="EmitFusedJob"/>) made of each call's job, the later calls' values under the
    /// names this interceptor has them (<paramref name="renamed"/>), then each call's reductions.
    /// </summary>
    private static void EmitLambdaWalk(StringBuilder sb, UpdateModel walks, int start, int count, string[]?[] renamed, string world)
    {
        var methods = Enumerable.Range(start, count).Select(i => Renamed(walks.Methods[i], renamed[i])).ToArray();
        string name = FusedName(walks, start, count), types = string.Join(", ", FusedQuery(walks, start, count)), indent = "        ";
        bool sized = Sized(walks, start, count);
        if (sized)
        {
            sb.Append($"{indent}if ({world}.Count<{types}>(in {name}Filter) < global::EasyEcs.SystemCode.FusedRows) // in cache: each pass on its own\n{indent}{{\n");
            for (int j = 0; j < count; j++)
                EmitPass(sb, methods[j], "__ForEach", start + j, world, $"job{j}", $", in {methods[j].Name}Filter{start + j}", "", indent + "    ");
            sb.Append($"{indent}}}\n{indent}else\n{indent}{{\n");
            indent += "    ";
        }

        string local = $"fused{start}";
        var members = methods.Select((m, j) => $"j{j} = new {m.Name}Job{start + j}{Fields(m, world)}");
        sb.Append($"{indent}var {local} = new {name} {{ {string.Join(", ", members)} }};\n");
        string loop = FusedInterface(walks, start, count).Substring(1) + (methods[0].Parallel ? "Parallel" : "");
        sb.Append($"{indent}{world}.{loop}<{types}, {name}>(ref {local}, in {name}Filter{methods[0].Chunks}); // one walk: {count} lambda passes per {(FusedPerRow(walks, start, count) ? "row" : "block of rows")}\n");
        for (int j = 0; j < count; j++) EmitReductions(sb, methods[j], world, $"{local}.j{j}", indent);
        if (sized) sb.Append("        }\n");
    }

    /// <summary>A call's model with its values named as the walk's interceptor has them (<paramref name="names"/>, in order; null: its own).</summary>
    private static ForEachModel Renamed(ForEachModel method, string[]? names)
    {
        if (names == null) return method;
        var parameters = method.Parameters.ToArray();
        for (int i = 0, v = 0; i < parameters.Length; i++)
            if (parameters[i].Kind == Symbols.ParamKind.Context)
                parameters[i] = parameters[i] with { Name = names[v++] };
        return method with { Parameters = new EquatableArray<ParamModel>(parameters) };
    }

    /// <summary>
    /// The job of a ParallelFor over a buffer's elements: an <c>IForSpan&lt;T&gt;</c> whose Execute runs a chunk's elements
    /// as a [ForEach] job runs a run of rows with one column (the element): the vector loop (<see cref="EmitLoops"/>),
    /// then the elements left one by one through the method the lambda's body became. The values are its fields.
    /// </summary>
    private static void EmitBufferJob(StringBuilder sb, ForEachModel method, string element, int number)
    {
        sb.Append($"    internal struct {method.Name}Job{number} : global::EasyEcs.IForSpan<{element}>\n    {{\n");
        for (int i = 0; i < method.Parameters.Length; i++)
            if (method.Parameters[i] is { Kind: Symbols.ParamKind.Context } p)
                sb.Append($"        public {p.Type} x{i};\n");
        if (sb[sb.Length - 2] != '{') sb.Append('\n');
        string args = string.Join(", ", method.Parameters.Select((p, i) => i == 0
            ? $"{p.RefKind} global::System.Runtime.CompilerServices.Unsafe.Add(ref r1, i)"
            : $"{(p.RefKind.Length == 0 ? "" : p.RefKind + " ")}x{i}").Prepend("null"));
        string call = $"__ForEach.{method.Name}_{number}({args});";
        const string indent = "            ";
        sb.Append("        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]\n");
        sb.Append($"        public void Execute(int start, global::System.Span<{element}> c1)\n        {{\n");
        int start = sb.Length;
        // The span's ref and length in locals, as a run's columns (EmitJob).
        sb.Append($"{indent}ref {element} r1 = ref global::System.Runtime.InteropServices.MemoryMarshal.GetReference(c1);\n");
        sb.Append($"{indent}nint rows = c1.Length;\n");
        var loops = new StringBuilder();
        if (method.Simd is { } simd)
        {
            EmitLoops(sb, loops, simd, [element], indent, call, false, null, [], start);
            sb.Append($"{indent}for (; i < rows; i++) // the elements after the last whole vector: the body itself\n{indent}    {call}\n");
        }
        else sb.Append($"{indent}for (nint i = 0; i < rows; i++)\n{indent}    {call}\n");
        sb.Append("        }\n");
        sb.Append(loops);
        sb.Append("    }\n\n");
    }

    /// <summary>A copied body's lines after the first, indented by <paramref name="indent"/> more.</summary>
    private static string Indent(string text, string indent) => text.Replace("\n", "\n" + indent);
}
