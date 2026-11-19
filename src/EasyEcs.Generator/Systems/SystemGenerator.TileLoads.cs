using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using static EasyEcs.Generator.Code;

namespace EasyEcs.Generator;

/// <summary>
/// Tile loads checked once (<see cref="TileLoad"/>): each <c>c.Load128&lt;T&gt;(x + k, dy, dz)</c> in a column loop is
/// intercepted by a method that checks the loop's first and last column in constants (folded by the JIT, so no
/// instruction remains) and loads at one zero-extended index off the row's first halo cell, which the cursor's indexer
/// checks for (dy, dz). Calls with the same loop and offset share one method. One file per source file,
/// implementation output (an IDE never needs it); the stage reads method bodies, so it never runs while typing.
/// </summary>
public sealed partial class SystemGenerator
{
    private static void InitializeTileLoads(IncrementalGeneratorInitializationContext context)
    {
        var loads = BoundLater(context, static (node, _) => TileLoad.IsCandidate(node), TileLoadCall.Create)
            .WithTrackingName("TileLoads");
        // Per source file: an edit changes every location of its file (they hold its checksum), and no other file.
        var files = loads.Collect()
            .SelectMany(static (all, _) => all.GroupBy(c => c!.File, System.StringComparer.Ordinal)
                .Select(g => new TileLoadFile(g.Key, new EquatableArray<TileLoadCall>(g.OrderBy(c => c!.Position).ToArray()!))))
            .WithTrackingName("TileLoadFiles");
        context.RegisterImplementationSourceOutput(files, static (spc, file) =>
            spc.AddSource($"{System.IO.Path.GetFileNameWithoutExtension(file.File)}.{Fnv(file.File):x8}.TileLoads.g.cs", SourceText.From(EmitTileLoads(file), Encoding.UTF8)));
    }

    /// <summary><paramref name="name"/> plus <paramref name="k"/> as C# (<c>x</c>, <c>x + 2</c>, <c>x - 1</c>).</summary>
    private static string Plus(string name, long k) => k == 0 ? name : k > 0 ? $"{name} + {k}" : $"{name} - {-k}";

    /// <summary>
    /// The check of a load's loop: Fits over the loop's first and last column when its bound is a number (true when the
    /// body never runs), else Covers, which works them out from the shape's extent.
    /// </summary>
    private static string CoversText(TileLoad load, string bytes)
    {
        string end = load.End;
        bool plusOne = end.EndsWith(" + 1L", System.StringComparison.Ordinal);
        if (plusOne) end = end.Substring(0, end.Length - " + 1L".Length);
        if (!end.EndsWith("L", System.StringComparison.Ordinal) || !long.TryParse(end.Substring(0, end.Length - 1), out long bound))
            return $"Covers<TCell, TTile>({load.First}, {load.End}, {load.Step}, {load.Offset}, {bytes})";
        if (plusOne) bound++;
        if (bound <= load.First) return "true";
        long last = load.First + (bound - 1 - load.First) / load.Step * load.Step;
        return $"Fits<TCell, TTile>({load.First + (long)load.Offset}, {last + load.Offset}, {bytes})";
    }

    /// <summary>The tile loads of one source file, in the order they appear.</summary>
    private sealed record TileLoadFile(string File, EquatableArray<TileLoadCall> Calls);

    /// <param name="Version">The interceptable location's version (<c>InterceptsLocation(version, data)</c>).</param>
    /// <param name="Data">The interceptable location's data: the file's checksum and the call's position.</param>
    /// <param name="Where">The call as a reader finds it (<c>Heat.cs(12,9)</c>).</param>
    private sealed record TileLoadCall(string File, int Position, int Version, string Data, string Where, TileLoad Load)
    {
        public static TileLoadCall? Create(SemanticModel model, InvocationExpressionSyntax invocation, System.Threading.CancellationToken ct)
        {
            try
            {
                if (TileLoad.Read(invocation, model, ct, out _) is not { } load
                    || model.GetInterceptableLocation(invocation, ct) is not { } location)
                    return null;
                return new TileLoadCall(invocation.SyntaxTree.FilePath, invocation.SpanStart, location.Version, location.Data,
                    LambdaPass.Where(invocation.GetLocation()), load);
            }
            catch (System.Exception e) when (e is not System.OperationCanceledException)
            {
                return null; // the call keeps the cursor's own check: never a reason to drop the project's generated files
            }
        }
    }

    /// <summary>
    /// The file of one source file's tile loads: per loop and offset, the interceptor of its calls, then the check of a
    /// loop (Covers) and the column load (At) they share.
    /// </summary>
    private static string EmitTileLoads(TileLoadFile file)
    {
        var sb = new StringBuilder(Header);
        sb.Append("using __MethodImpl = global::System.Runtime.CompilerServices.MethodImplAttribute;\n");
        sb.Append("using __Inline = global::System.Runtime.CompilerServices.MethodImplOptions;\n");
        sb.Append("using __Unsafe = global::System.Runtime.CompilerServices.Unsafe;\n");
        foreach (var load in file.Calls.Select(c => c.Load.Load).Distinct().OrderBy(l => l, System.StringComparer.Ordinal))
            sb.Append(load switch
            {
                "Load128" => "using __Vector128 = global::System.Runtime.Intrinsics.Vector128;\n",
                "Load256" => "using __Vector256 = global::System.Runtime.Intrinsics.Vector256;\n",
                "Load512" => "using __Vector512 = global::System.Runtime.Intrinsics.Vector512;\n",
                _ => "using __Vector = global::System.Numerics.Vector;\n",
            });
        sb.Append($"\nnamespace {LambdaPass.Namespace}\n{{\n    file static class __TileLoads\n    {{\n");
        int n = 0;
        foreach (var group in file.Calls.GroupBy(c => c.Load).OrderBy(g => g.First().Position))
        {
            var load = group.Key;
            var (vector, bytes, type) = load.Load switch
            {
                "Load128" => ("__Vector128", "16", "global::System.Runtime.Intrinsics.Vector128<TLane>"),
                "Load256" => ("__Vector256", "32", "global::System.Runtime.Intrinsics.Vector256<TLane>"),
                "Load512" => ("__Vector512", "64", "global::System.Runtime.Intrinsics.Vector512<TLane>"),
                _ => ("__Vector", "global::System.Numerics.Vector<byte>.Count", "global::System.Numerics.Vector<TLane>"),
            };
            string column = load.Offset == 0 ? "x" : load.Offset > 0 ? $"x + {load.Offset}" : $"x - {-(long)load.Offset}";
            string end = load.End.Replace("L", "").Replace(" + 1", " + 1 (<=)");
            sb.Append($"        /// <summary>{load.Load} at {column} of for (x = {load.First}; x &lt; {end}; x += {load.Step}): {string.Join(", ", group.Select(c => c.Where.Replace("<", "&lt;")))}.</summary>\n");
            foreach (var call in group) sb.Append($"        [global::System.Runtime.CompilerServices.InterceptsLocation({call.Version}, \"{call.Data}\")]\n");
            // By force, as the cursor's own loads: a stencil's method is the JIT's root, and its inline budget would leave calls.
            sb.Append("        [__MethodImpl(__Inline.AggressiveInlining)]\n");
            sb.Append($"        internal static {type} {load.Load}_{n++}<TCell, TTile, TLane>(this in global::EasyEcs.TileCursor<TCell, TTile> c, int dx, int dy, int dz)\n");
            sb.Append("            where TCell : unmanaged where TTile : struct, global::EasyEcs.ITileShape");
            string covers = CoversText(load, bytes);
            if (load.First >= 0 && load.Offset != 0)
            {
                // A loop from 0 or later never makes its column negative: the loop's x taken back out of dx (dx used once,
                // so the JIT folds (x + k) - k to the loop's own variable), zero-extended once for every load of the column,
                // and k added in 64 bits, which the JIT folds into each load's displacement (Column).
                sb.Append($"\n        {{\n            int x = {Plus("dx", -(long)load.Offset)};\n");
                sb.Append($"            return {covers}\n");
                sb.Append($"                ? {vector}.LoadUnsafe(ref __Unsafe.As<TCell, TLane>(ref Column(in c, x, {load.Offset}, dy, dz)))\n");
                sb.Append($"                : c.{load.Load}<TLane>({Plus("x", load.Offset)}, dy, dz);\n        }}\n\n");
            }
            else
            {
                // At the loop's own column (dx is x itself), or from a negative column (dx + r zero-extended: At).
                string at = load.First >= 0 ? "Column(in c, dx, 0, dy, dz)" : "At(in c, dx, dy, dz)";
                sb.Append(" =>\n");
                sb.Append($"            {covers}\n");
                sb.Append($"                ? {vector}.LoadUnsafe(ref __Unsafe.As<TCell, TLane>(ref {at}))\n");
                sb.Append($"                : c.{load.Load}<TLane>(dx, dy, dz);\n\n");
            }
        }

        sb.Append("""
                    /// <summary>
                    /// Whether every load the loop makes lies in the padded row, as the cursor's own check requires: x takes first,
                    /// first + step, ... while under end, the load of bytes at x + offset, so the first and the last x decide. In
                    /// constants and the shape's extents: the JIT folds it, and the call keeps one of its two branches.
                    /// </summary>
                    [__MethodImpl(__Inline.AggressiveInlining)]
                    private static bool Covers<TCell, TTile>(long first, long end, long step, long offset, int bytes)
                        where TCell : unmanaged where TTile : struct, global::EasyEcs.ITileShape =>
                        end <= first || Fits<TCell, TTile>(first + offset, first + (end - 1 - first) / step * step + offset, bytes);

                    /// <summary>
                    /// Whether loads of bytes at the columns first to last lie in the padded row: Covers with the loop's first and
                    /// last column worked out by the generator when its bound is a number (less code for the JIT to inline: a
                    /// stencil's method inlines one check per load, and its budget left later calls as calls).
                    /// </summary>
                    [__MethodImpl(__Inline.AggressiveInlining)]
                    private static bool Fits<TCell, TTile>(long first, long last, int bytes)
                        where TCell : unmanaged where TTile : struct, global::EasyEcs.ITileShape =>
                        first >= -TTile.Halo && (last + TTile.Halo) * __Unsafe.SizeOf<TCell>() + bytes <= (TTile.Width + 2L * TTile.Halo) * __Unsafe.SizeOf<TCell>();

                    /// <summary>
                    /// The cell at dx of the row (dy, dz) from the cursor: the row's first halo cell through the cursor's indexer,
                    /// which checks dy and dz, then dx + r cells on without a check. The invariant: Covers held for the loop the call
                    /// is in, and dx is that loop's x plus the offset Covers was given, so 0 &lt;= dx + r and the load ends in the row.
                    /// </summary>
                    [__MethodImpl(__Inline.AggressiveInlining)]
                    private static ref TCell At<TCell, TTile>(in global::EasyEcs.TileCursor<TCell, TTile> c, int dx, int dy, int dz)
                        where TCell : unmanaged where TTile : struct, global::EasyEcs.ITileShape =>
                        ref __Unsafe.Add(ref __Unsafe.AsRef(in c[-TTile.Halo, dy, dz]), (nint)(uint)(dx + TTile.Halo));

                    /// <summary>
                    /// The cell at x + k of the row (dy, dz): the row's first halo cell through the cursor's indexer, then x zero-extended
                    /// plus k + r in 64 bits, one offset whose constant the JIT folds into the load's displacement, so every load of a
                    /// column shares one index (the address and the column, as a stencil written by hand over its own texture).
                    /// The invariant: Covers held for the loop the call is in, whose x starts at 0 or later and only grows, so
                    /// 0 &lt;= x and 0 &lt;= x + k + r, and the load ends in the row.
                    /// </summary>
                    [__MethodImpl(__Inline.AggressiveInlining)]
                    private static ref TCell Column<TCell, TTile>(in global::EasyEcs.TileCursor<TCell, TTile> c, int x, int k, int dy, int dz)
                        where TCell : unmanaged where TTile : struct, global::EasyEcs.ITileShape =>
                        ref __Unsafe.Add(ref __Unsafe.AsRef(in c[-TTile.Halo, dy, dz]), (nint)(uint)x + (k + TTile.Halo));
                }
            }

            namespace System.Runtime.CompilerServices
            {
                [global::System.AttributeUsage(global::System.AttributeTargets.Method, AllowMultiple = true)]
                // The compiler reads the arguments; the attribute itself keeps nothing (CS9113 on a primary constructor).
                file sealed class InterceptsLocationAttribute : global::System.Attribute
                {
                    public InterceptsLocationAttribute(int version, string data) { }
                }
            }

            """);
        return sb.ToString();
    }
}
