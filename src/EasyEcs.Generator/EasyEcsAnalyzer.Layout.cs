using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace EasyEcs.Generator;

/// <summary>
/// EECS091: a component whose fields split into those its frequent passes touch and those they never touch, wide enough
/// that the untouched ones cost the passes memory. A pass walks a component's column row by row, so it reads every
/// byte of each row it visits, whichever fields its method names; moving the cold fields into a component of their
/// own leaves the frequent passes a column of only the fields they use. The layout stays the user's (D90): nothing moves
/// on its own, the code fix writes the split when the user applies it (the properties of <see cref="SplitFix"/>).
/// </summary>
public static partial class EasyEcsAnalyzer
{
    public static readonly DiagnosticDescriptor ColdFields = new("EECS091", "A component with fields its frequent passes never touch",
        "Component '{0}' is {1} bytes, and {2} only {3} ({4} bytes) of each row every update: move {5} ({6} bytes) into a component of their own, so {7} {4} bytes per row instead of {1}",
        Category, DiagnosticSeverity.Warning, true,
        "A [ForEach] pass walks the column of each component it takes, so it reads whole rows: the fields its method never " +
        "names come into the cache with the ones it does. When every pass that runs each update touches the same few fields " +
        "of a wide component, the others (spawn data, configuration, statistics read now and then) cost those passes memory " +
        "traffic on every row. Moved into a component of their own, they stay in another column that only the code reading " +
        "them walks. Reported at the end of the compilation, for a component of at least 32 bytes whose untouched fields " +
        "are at least half of it. A pass counts as frequent unless its system has [UpdateInterval] above 1, or the method " +
        "visits only changed rows ([Changed<T>], [Removed<T>]) or a slice of them (Slices); a pass that uses the component " +
        "whole (a copy, a call, a ref local) touches every field. The code fix declares the new component and rewrites " +
        "every use of the moved fields when it can do so without changing what the code does; otherwise move them by hand.",
        helpLinkUri: Help("EECS091"), customTags: WellKnownDiagnosticTags.CompilationEnd);

    /// <summary>
    /// The smallest component EECS091 reports, in bytes, and the share of it the untouched fields must take (half).
    /// HotColdBenchmark: a pass over 8 bytes of a row of 32 bytes or more takes measurably longer than over the same
    /// 8 bytes split out (D118).
    /// </summary>
    private const int ColdMinSize = 32;

    /// <summary>
    /// What EECS091 needs from a compilation: per component, the fields each [ForEach] pass touches (from AnalyzeBody,
    /// where the body is bound already) and the components EECS055 advises to split (it reports them).
    /// </summary>
    private sealed class ColdFieldPasses
    {
        /// <summary>A pass over <paramref name="Component"/>: its method, whether it runs each update, and the fields it touches (null: all).</summary>
        private readonly ConcurrentQueue<(ITypeSymbol Component, IMethodSymbol Method, bool Frequent, HashSet<IFieldSymbol>? Fields)> _passes = new();

        /// <summary>Components whose used fields EECS055 advises to split out: one mistake, one diagnostic.</summary>
        public readonly ConcurrentDictionary<string, byte> Advised = new();

        /// <summary>The fields <paramref name="method"/> touches of each component it takes, when the component is one EECS091 may report.</summary>
        public void Collect(IMethodSymbol method, IOperation body)
        {
            if (!Symbols.IsForEach(method) || Symbols.IsForEntity(method)) return; // one entity per call walks no column
            List<(IParameterSymbol P, ITypeSymbol Component)>? taken = null;
            foreach (var p in method.Parameters)
            {
                var component = Symbols.Classify(p) switch
                {
                    Symbols.ParamKind.Component => p.Type,
                    Symbols.ParamKind.ComponentSpan => Symbols.SpanElement(p.Type),
                    Symbols.ParamKind.Mutable => Symbols.MutElement(p.Type),
                    Symbols.ParamKind.Optional => Symbols.MaybeElement(p.Type),
                    _ => null,
                };
                if (component != null && Splittable(component)) (taken ??= []).Add((p, component));
            }
            if (taken == null) return;

            bool frequent = !Symbols.TracksSince(method) && Symbols.Slices(method) <= 1
                && !(Symbols.IsSystem(method.ContainingType) && Symbols.Schedule(method.ContainingType) is { Interval: > 1 });
            var fields = new Dictionary<IParameterSymbol, HashSet<IFieldSymbol>?>(SymbolEqualityComparer.Default);
            foreach (var (p, _) in taken) fields[p] = new HashSet<IFieldSymbol>(SymbolEqualityComparer.Default);
            foreach (var operation in body.Descendants())
            {
                if (operation is not IParameterReferenceOperation { Parameter: var p } reference || !fields.TryGetValue(p, out var used) || used == null) continue;
                var (row, field) = FieldOf(reference, p);
                if (!row) continue;
                if (field == null) fields[p] = null; // used whole
                else used.Add(field);
            }

            foreach (var (p, component) in taken) _passes.Enqueue((component, method, frequent, fields[p]));
        }

        /// <summary>
        /// The field of the component that a use of parameter <paramref name="p"/> touches (<c>c.X</c>, <c>m.Value.X</c>,
        /// <c>m.Write().X</c>, <c>span[i].X</c>; an auto-property is its backing field), null for a use of the component
        /// whole, and Row false for a use that reads no row (<c>span.Length</c>, <c>maybe.HasValue</c>).
        /// </summary>
        internal static (bool Row, IFieldSymbol? Field) FieldOf(IOperation reference, IParameterSymbol p)
        {
            IOperation at = reference;
            if (Symbols.Classify(p) != Symbols.ParamKind.Component)
                switch (at.Parent)
                {
                    case IPropertyReferenceOperation { Property.IsIndexer: true } indexer when indexer.Instance == at: // span[i]
                    case IPropertyReferenceOperation { Property.Name: "Value" } value when value.Instance == at:       // Mut, Maybe
                    case IInvocationOperation { TargetMethod.Name: "Write" } write when write.Instance == at:          // Mut.Write()
                        at = at.Parent;
                        break;
                    case IPropertyReferenceOperation { Property.Name: "Length" or "IsEmpty" or "HasValue" } other when other.Instance == at:
                        return (false, null);
                    default:
                        return (true, null);
                }
            return (true, at.Parent switch
            {
                IFieldReferenceOperation { Field: { IsStatic: false } field } f when f.Instance == at => field,
                IPropertyReferenceOperation { Property: { IsStatic: false, IsIndexer: false } property } r when r.Instance == at
                    => Symbols.BackingField(property),
                _ => null,
            });
        }

        /// <summary>
        /// A component whose fields could move: a struct declared in this compilation, of two or more instance fields,
        /// not a buffer, a relation, or indexed or grouped by a key a split would separate from its row.
        /// </summary>
        private static bool Splittable(ITypeSymbol type) =>
            type is INamedTypeSymbol { TypeKind: TypeKind.Struct, IsGenericType: false } named && named.Locations.Any(l => l.IsInSource)
            && InstanceFields(named).Length >= 2 && Symbols.BufferElement(named) == null
            && !Symbols.IsIndexed(named) && !Symbols.IsGrouped(named) && !Symbols.Implements(named, Symbols.Relation);

        private static IFieldSymbol[] InstanceFields(INamedTypeSymbol type) => type.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).ToArray();

        /// <summary>EECS091 for each component whose frequent passes all leave the same wide set of its fields untouched.</summary>
        public void Report(CompilationAnalysisContext context)
        {
            foreach (var group in _passes.GroupBy(p => p.Component, SymbolEqualityComparer.Default))
            {
                var component = (INamedTypeSymbol)group.Key!;
                if (Advised.ContainsKey(component.ToDisplayString())) continue; // EECS055 names the split already
                var frequent = group.Where(p => p.Frequent).ToList();
                if (frequent.Count == 0 || frequent.Any(p => p.Fields == null)) continue;
                int size = SizeOf(component, out _);
                if (size < ColdMinSize) continue;
                var hot = new HashSet<IFieldSymbol>(frequent.SelectMany(p => p.Fields!), SymbolEqualityComparer.Default);
                var fields = InstanceFields(component);
                var cold = fields.Where(f => !hot.Contains(f)).ToArray();
                if (cold.Length == 0 || hot.Count == 0) continue;
                int hotBytes = 0;
                foreach (var field in fields)
                    if (hot.Contains(field)) hotBytes += SizeOf(field.Type, out _);
                if (hotBytes <= 0 || 2 * (size - hotBytes) < size) continue;

                var methods = frequent.Select(p => p.Method).Distinct<IMethodSymbol>(SymbolEqualityComparer.Default)
                    .Select(m => $"{m.ContainingType.Name}.{m.Name}").OrderBy(n => n, System.StringComparer.Ordinal).ToList();
                string passes = methods.Count == 1 ? $"pass {methods[0]} touches" : $"passes {Names(methods)} touch";
                context.ReportDiagnostic(Diagnostic.Create(ColdFields, component.Locations.First(l => l.IsInSource), null,
                    SplitFix(component, cold), component.Name, size, passes, Names(fields.Where(hot.Contains).Select(Name)), hotBytes,
                    Names(cold.Select(Name)), size - hotBytes, methods.Count == 1 ? "the pass reads" : "those passes read"));
            }
        }

        /// <summary>
        /// The fields <paramref name="method"/> touches of the component parameter <paramref name="p"/> takes (null when
        /// it uses the component whole): what a split of that component moves for EECS055.
        /// </summary>
        internal static HashSet<IFieldSymbol>? FieldsUsed(IOperation body, IParameterSymbol p)
        {
            var used = new HashSet<IFieldSymbol>(SymbolEqualityComparer.Default);
            foreach (var operation in body.Descendants())
            {
                if (operation is not IParameterReferenceOperation reference || !SymbolEqualityComparer.Default.Equals(reference.Parameter, p)) continue;
                var (row, field) = FieldOf(reference, p);
                if (!row) continue;
                if (field == null) return null;
                used.Add(field);
            }
            return used;
        }

        /// <summary>
        /// The properties of the split code fix (EECS091, and EECS055 when it advises a split): <c>Fix=Split</c>, the
        /// component's documentation id and the names of the fields to move; null when a moved field is the backing
        /// field of a property (the fix moves declared fields only).
        /// </summary>
        internal static ImmutableDictionary<string, string?>? SplitFix(INamedTypeSymbol component, IEnumerable<IFieldSymbol> moved)
        {
            var fields = moved.ToList();
            if (fields.Count == 0 || fields.Any(f => Symbols.Associated(f) != null || f.IsImplicitlyDeclared) || component.GetDocumentationCommentId() is not { } id) return null;
            return ImmutableDictionary<string, string?>.Empty.Add(FixKind, "Split").Add("Component", id)
                .Add("Fields", string.Join(",", fields.Select(f => f.Name)));
        }

        private static string Name(IFieldSymbol field) => Symbols.MemberName(field);

        private static string Names(IEnumerable<string> names)
        {
            var list = names.ToList();
            return list.Count == 1 ? list[0] : string.Join(", ", list.Take(list.Count - 1)) + " and " + list[list.Count - 1];
        }

        /// <summary>
        /// The size of a value of <paramref name="type"/> in a column (fields in declaration order at their natural
        /// alignment, the struct padded to its largest), or -1 when it is not known: a struct with a [StructLayout], a
        /// type parameter, or a struct of another assembly whose fields this compilation does not see.
        /// </summary>
        private static int SizeOf(ITypeSymbol type, out int align)
        {
            align = 1;
            if (type.IsReferenceType || type.TypeKind == TypeKind.Pointer || type.TypeKind == TypeKind.FunctionPointer) return align = 8;
            if (type is INamedTypeSymbol { TypeKind: TypeKind.Enum, EnumUnderlyingType: { } underlying }) return SizeOf(underlying, out align);
            int primitive = type.SpecialType switch
            {
                SpecialType.System_Boolean or SpecialType.System_Byte or SpecialType.System_SByte => 1,
                SpecialType.System_Char or SpecialType.System_Int16 or SpecialType.System_UInt16 => 2,
                SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Single => 4,
                SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Double
                    or SpecialType.System_IntPtr or SpecialType.System_UIntPtr => 8,
                SpecialType.System_Decimal => 16,
                _ => 0,
            };
            if (primitive != 0)
            {
                align = System.Math.Min(primitive, 8);
                return primitive;
            }
            if (type is not INamedTypeSymbol { TypeKind: TypeKind.Struct } named
                || named.GetAttributes().Any(a => a.AttributeClass?.Name == "StructLayoutAttribute")) return -1;
            var fields = InstanceFields(named);
            if (fields.Length == 0) return named.Locations.Any(l => l.IsInSource) ? 1 : -1; // empty, or fields this compilation cannot see
            int offset = 0;
            foreach (var field in fields)
            {
                int size, fieldAlign;
                if (field.IsFixedSizeBuffer && field.Type is IPointerTypeSymbol pointer)
                    size = SizeOf(pointer.PointedAtType, out fieldAlign) * field.FixedSize;
                else size = SizeOf(field.Type, out fieldAlign);
                if (size < 0) return -1;
                offset = (offset + fieldAlign - 1) / fieldAlign * fieldAlign + size;
                align = System.Math.Max(align, fieldAlign);
            }
            return (offset + align - 1) / align * align;
        }
    }
}
