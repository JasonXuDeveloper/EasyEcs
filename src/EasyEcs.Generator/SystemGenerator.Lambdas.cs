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
/// before the lambda in the job's fields. One file per source file, implementation output (an IDE never needs it).
/// Calls the analyzer reports (EECS095) are skipped; a body naming what another file cannot reach (EECS096) runs
/// through the lambda's delegate.
/// </summary>
public sealed partial class SystemGenerator
{
    private static void InitializeLambdas(IncrementalGeneratorInitializationContext context)
    {
        var calls = context.SyntaxProvider
            .CreateSyntaxProvider(static (node, _) => LambdaPass.IsCandidate(node), static (ctx, ct) => LambdaModel.Create(ctx, ct))
            .Where(static m => m is not null)
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
    private sealed record LambdaModel(string File, int Position, int Version, string Data, string Where, ForEachModel Method,
        string? Body, EquatableArray<string> Usings, string Returns)
    {
        public static LambdaModel? Create(GeneratorSyntaxContext ctx, System.Threading.CancellationToken ct)
        {
            var model = ctx.SemanticModel;
            var invocation = (InvocationExpressionSyntax)ctx.Node;
            if (model.GetOperation(invocation, ct) is not IInvocationOperation operation || LambdaPass.Read(operation) is not { } call
                || LambdaPass.Problem(call, model, ct, out _) != null || model.GetInterceptableLocation(invocation, ct) is not { } location)
                return null;

            var lambda = call.Lambda!;
            var function = call.Function!;
            var parameters = lambda.Parameters.Select(p =>
            {
                string declared = LambdaPass.Declared(p);
                return Symbols.Classify(p, true) switch
                {
                    Symbols.ParamKind.Mutable => new ParamModel(Symbols.ParamKind.Mutable, Symbols.MutElement(p.Type)!.ToDisplayString(Symbols.Fqn), "ref", p.Name, declared,
                        Symbols.Implements(Symbols.MutElement(p.Type)!, Symbols.TrackChanges)),
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
            var simd = body != null && stable
                ? Kernel.Vectorize(lambda, function, call.Parallel, stable, model, call.Parallel
                    ? static (i, kind) => kind switch { Symbols.ParamKind.Singleton => $"s{i}.Value", Symbols.ParamKind.Reduction => $"l{i}", _ => $"x{i}" }
                    : static (i, kind) => kind switch { Symbols.ParamKind.Singleton => $"s{i}", Symbols.ParamKind.Reduction => $"l{i}", _ => $"x{i}" }, ct, out scalar)
                : null;
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
            return new LambdaModel(invocation.SyntaxTree.FilePath, invocation.SpanStart, location.Version, location.Data,
                LambdaPass.Where(invocation.GetLocation()), method, body,
                new EquatableArray<string>(body == null ? [] : LambdaPass.Usings(invocation).Distinct().OrderBy(u => u, System.StringComparer.Ordinal).ToArray()),
                lambda.ReturnsVoid ? "void" : lambda.ReturnType.ToDisplayString(Symbols.Fqn));
        }
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
        for (int k = 0; k < file.Calls.Length; k++)
        {
            var call = file.Calls[k];
            var method = call.Method;
            const string world = "__world", body = "__body";
            string job = $"{method.Name}Job{k}";
            var values = method.Parameters.Where(p => p.Kind == Symbols.ParamKind.Context).Select(p => $"{p.Type} {Escape(p.Name)}");
            string parameters = string.Concat(method.Parameters.Select(p => $", {p.Declared} {Escape(p.Name)}"));
            type.Append($"    /// <summary>The lambda pass at {call.Where.Replace("<", "&lt;")}.</summary>\n");
            type.Append($"    [global::System.Runtime.CompilerServices.InterceptsLocation({call.Version}, \"{call.Data}\")]\n");
            // A change filter's tick is the first value, the filter's and not the lambda's (LambdaPass.Problem).
            const string since = "__since";
            string tick = method.Since ? $", ulong {since}" : "";
            type.Append($"    internal static void {method.Name}{k}(this global::EasyEcs.World {world}{tick}{string.Concat(values.Select(v => ", " + v))}, global::System.Delegate {body})\n    {{\n");
            if (call.Body == null) type.Append($"        {method.Name}{k}Delegate = global::System.Runtime.CompilerServices.Unsafe.As<{method.Name}{k}Body>({body});\n");
            string filter = method.Since ? $"{method.Name}Filter{k}.Since({since})" : $"in {method.Name}Filter{k}";
            if (method.HasChaseTwin) EmitPass(type, method, "__ForEach", k, world, "job", ", " + filter, "", "        "); // the twin past the cache
            else
            {
                type.Append($"        var job = new {job}{Fields(method, world)};\n");
                type.Append($"        {world}.{method.ForEach}<{method.ComponentTypes}, {job}>(ref job, {filter}{method.Chunks});\n");
                EmitReductions(type, method, world, "job", "        ");
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

            EmitJob(type, method.Owner, method, k);
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

    /// <summary>A copied body's lines after the first, indented by <paramref name="indent"/> more.</summary>
    private static string Indent(string text, string indent) => text.Replace("\n", "\n" + indent);
}
