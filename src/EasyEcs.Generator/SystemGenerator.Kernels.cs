using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EasyEcs.Generator;

/// <summary>
/// The vector loops of systems' [ForEach] methods, in a stage only implementation output reads: a loop reads the
/// method's body and the bodies of the helpers it calls (Kernel.Inlining.cs), and a system's code is implementation
/// output, so an IDE that skips that output while the user types (Visual Studio, Rider) never binds them. The
/// declaration models (<see cref="ForEachModel"/>) leave a system method's loop out; <see cref="WithLoops"/> puts each
/// back into its system's model before the code is written. Runner types' loops stay in their models: their entry
/// points are API the user's code calls, written as source output.
/// </summary>
public sealed partial class SystemGenerator
{
    /// <summary>A system method's vector loop: its owner (<see cref="OwnerModel.FullName"/>), its declaration order, the loop or the reason.</summary>
    private sealed record LoopModel(string Owner, int Order, Kernel.Code? Simd, string Scalar, PairModel? Pair)
    {
        public static LoopModel? Create(IMethodSymbol method, MethodDeclarationSyntax syntax, SemanticModel model, System.Threading.CancellationToken ct)
        {
            if (Symbols.IsRunner(method) || !Symbols.GeneratesSystem(method.ContainingType)) return null;
            string owner = OwnerModel.From(method.ContainingType).FullName;
            int order = method.ContainingType.GetMembers().IndexOf(method);
            bool parallel = Symbols.IsParallel(method) && Symbols.ParallelProblem(method, syntax, model) == null;
            // A pair method's loop is its pair job's (SystemGenerator.Pairs.cs).
            if (PairForm.IsPairMethod(method)) return new LoopModel(owner, order, null, "", PairModel.Create(method, syntax, model, parallel, ct));
            if (Symbols.MaxRows(method) > 0) return null; // its collected rows run one by one (World.ForEachCollected)
            var simd = ForEachModel.VectorLoop(method, syntax, model, parallel, ct, out string? scalar);
            return new LoopModel(owner, order, simd, scalar ?? "", null);
        }
    }

    /// <summary>The systems' models with each method's vector loop (<see cref="LoopModel"/>) put back.</summary>
    private static IncrementalValuesProvider<UpdateModel> WithLoops(IncrementalGeneratorInitializationContext context, IncrementalValuesProvider<UpdateModel> systems)
    {
        var loops = context.SyntaxProvider
            .ForAttributeWithMetadataName(Symbols.ForEachAttribute,
                static (node, _) => node is MethodDeclarationSyntax,
                static (ctx, ct) => LoopModel.Create((IMethodSymbol)ctx.TargetSymbol, (MethodDeclarationSyntax)ctx.TargetNode, ctx.SemanticModel, ct))
            .Where(static m => m is not null)
            .WithTrackingName("SystemLoops");
        return systems.Combine(loops.Collect())
            .Select(static (pair, _) => With(pair.Left, pair.Right!))
            .WithTrackingName("SystemCode");
    }

    private static UpdateModel With(UpdateModel system, ImmutableArray<LoopModel> loops)
    {
        var methods = new ForEachModel[system.Methods.Length];
        for (int i = 0; i < methods.Length; i++)
        {
            var method = system.Methods[i];
            var loop = loops.FirstOrDefault(l => l.Order == method.Order && l.Owner == method.Owner.FullName);
            methods[i] = loop == null ? method : method with { Simd = loop.Simd, Scalar = loop.Scalar, Pair = loop.Pair };
        }

        return system with { Methods = new EquatableArray<ForEachModel>(methods) };
    }
}
