using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EasyEcs.Generator;

/// <summary>
/// What a method's body decides, in stages only implementation output reads: its vector loop (which reads the bodies of
/// the helpers it calls too, Kernel.Inlining.cs), whether it changes structure, uses its entity only as an identity,
/// runs in parallel, finds a Lookup by key, chases other entities, runs out of line, and what pass fusion knows of it.
/// The code of systems and the passes of entry points are implementation output, so an IDE that skips that output while
/// the user types (Visual Studio, Rider) never binds a body. The declaration models (<see cref="ForEachModel.Declare"/>)
/// leave all of it out; <see cref="WithBodies"/> puts each method's back into its type's model before the code is
/// written.
/// </summary>
public sealed partial class SystemGenerator
{
    /// <summary>
    /// What the body of a method says: its owner (<see cref="OwnerModel.FullName"/>) and declaration order, then the
    /// facts <see cref="ForEachModel.WithBody"/> puts in (<see cref="Keys"/>: per parameter, a Lookup's index key
    /// when the pass builds its index, else "").
    /// </summary>
    private sealed record BodyModel(string Owner, int Order, bool InSystem, bool Stable, bool Parallel, bool Identifies, EquatableArray<string> Keys,
        EquatableArray<Chase> Chases, Kernel.Code? Simd, string Scalar, PairModel? Pair, bool OutOfLine, FusionSide? Fusion)
    {
        public static BodyModel? Create(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model, System.Threading.CancellationToken ct)
        {
            if (!Symbols.IsValidForEach(method)) return null;
            ct.ThrowIfCancellationRequested();
            bool outside = Symbols.IsRunner(method), one = Symbols.IsEntityMethod(method), record = Symbols.IsDrain(method), pair = PairForm.IsPairMethod(method);
            bool budget = Symbols.MaxRows(method) > 0; // its collected rows run one by one (World.ForEachCollected)
            bool stable = Symbols.IsStable(method, syntax, model);
            bool parallel = Symbols.IsParallel(method) && Symbols.ParallelProblem(method, syntax, model, stable) == null;
            bool identifies = Symbols.OnlyIdentifies(method, syntax, model);
            var body = Symbols.WithBody(method, syntax, model).Item1 is { } declaration ? (SyntaxNode?)declaration.Body ?? declaration.ExpressionBody : null;
            var keys = new string[method.Parameters.Length];
            for (int i = 0; i < keys.Length; i++)
                keys[i] = !(i == 0 && record) && Symbols.Classify(method.Parameters[i], outside) == Symbols.ParamKind.Lookup
                    ? Symbols.LookupKey(method.Parameters[i], body)?.ToDisplayString(Symbols.Fqn) ?? "" : "";
            string? scalar = null; // why the method runs scalar; an [EntityMethod] method has no loop to explain
            // Kernel.Vectorize decides alone (static, stable, its body; in parallel too): the analyzer reports its reasons.
            // One entity has no run of rows to vectorize; a pair method's loop is its pair job's (SystemGenerator.Pairs.cs).
            var simd = one || pair || record || budget ? null : ForEachModel.VectorLoop(method, syntax, model, parallel, ct, out scalar);
            return new BodyModel(OwnerModel.From(method.ContainingType).FullName, method.ContainingType.GetMembers().IndexOf(method), !outside,
                stable, parallel, identifies, new EquatableArray<string>(keys),
                // Reads of other entities at the row's ids, by EntityId (a keyed lookup finds through its index).
                one || record || budget ? new([]) : Generator.Chases.Of(method, syntax, model, ct, out _),
                simd, scalar ?? "",
                pair && !record ? PairModel.Create(method, syntax, model, parallel, ct) : null,
                method.Parameters.Any(p => Symbols.Classify(p, outside) == Symbols.ParamKind.Scratch) && ForEachModel.LoopsOrCalls(syntax),
                outside || pair || record ? null : FusionSide.Of(method, stable, identifies, parallel)); // a pair pass walks windows
        }
    }

    /// <summary>
    /// The bodies of the [ForEach], [Drain] and [EntityMethod] methods, by owner and declaration order: per kind of
    /// type, so a body's edit reruns only the merge of its own kind (<see cref="WithBodies"/>): systems' passes, other
    /// types' passes ([ForEach] and [Drain]), the [Drain] methods of types without a [ForEach] method, entity methods.
    /// </summary>
    private static (IncrementalValueProvider<Bodies> Systems, IncrementalValueProvider<Bodies> Runners, IncrementalValueProvider<Bodies> Records,
        IncrementalValueProvider<Bodies> Ones) BodiesOf(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<BodyModel> Of(string attribute, string name) => context.SyntaxProvider
            .ForAttributeWithMetadataName(attribute,
                static (node, _) => node is MethodDeclarationSyntax,
                static (ctx, ct) => BodyModel.Create((IMethodSymbol)ctx.TargetSymbol, (MethodDeclarationSyntax)ctx.TargetNode, ctx.SemanticModel, ct))
            .Where(static m => m is not null)
            .Select(static (m, _) => m!)
            .WithTrackingName(name);
        var passes = Of(Symbols.ForEachAttribute, "ForEachBodies");
        var records = Of(Symbols.DrainAttribute, "RecordBodies");
        var runnerRecords = records.Where(static b => !b.InSystem).Collect();
        return (passes.Where(static b => b.InSystem).Collect().Combine(records.Where(static b => b.InSystem).Collect()).Select(static (all, _) => new Bodies(all.Left, all.Right)),
            passes.Where(static b => !b.InSystem).Collect().Combine(runnerRecords).Select(static (all, _) => new Bodies(all.Left, all.Right)),
            runnerRecords.Select(static (all, _) => new Bodies(all, [])),
            Of(Symbols.EntityMethodAttribute, "EntityMethodBodies").Collect().Select(static (all, _) => new Bodies(all, [])));
    }

    /// <summary>
    /// Bodies found by owner and declaration order. Equal when its arrays are the same ones (the Collects cached): a body
    /// that changed makes a new one, and every model of its kind is put together again.
    /// </summary>
    private sealed class Bodies : System.IEquatable<Bodies>
    {
        private readonly ImmutableArray<BodyModel> _first, _second;
        private readonly Dictionary<(string, int), BodyModel> _byMethod = new();

        public Bodies(ImmutableArray<BodyModel> first, ImmutableArray<BodyModel> second)
        {
            (_first, _second) = (first, second);
            foreach (var body in first.Concat(second)) _byMethod[(body.Owner, body.Order)] = body;
        }

        public BodyModel? Of(ForEachModel method) => _byMethod.TryGetValue((method.Owner.FullName, method.Order), out var body) ? body : null;

        public bool Equals(Bodies? other) => other != null && _first == other._first && _second == other._second;

        public override bool Equals(object? obj) => Equals(obj as Bodies);

        public override int GetHashCode() => _first.GetHashCode();
    }

    /// <summary>The types' models with each method's body (<see cref="BodyModel"/>) put back.</summary>
    private static IncrementalValuesProvider<UpdateModel> WithBodies(IncrementalValuesProvider<UpdateModel> types, IncrementalValueProvider<Bodies> bodies, string name) =>
        types.Combine(bodies).Select(static (pair, ct) => With(pair.Left, pair.Right, ct)).WithTrackingName(name);

    private static UpdateModel With(UpdateModel type, Bodies bodies, System.Threading.CancellationToken ct)
    {
        var methods = new ForEachModel[type.Methods.Length];
        for (int i = 0; i < methods.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var method = type.Methods[i];
            methods[i] = bodies.Of(method) is { } body ? method.WithBody(body) : method;
        }

        return type with { Methods = new EquatableArray<ForEachModel>(methods) };
    }
}
