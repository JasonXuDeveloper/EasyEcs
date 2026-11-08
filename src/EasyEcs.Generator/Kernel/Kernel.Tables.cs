using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace EasyEcs.Generator;

/// <summary>
/// A uniform table read at a lane index: an <c>[InlineArray]</c> of at most <c>TableLanes.MaxLength</c> elements that
/// is the same for every row (a singleton's, a value's), indexed by an int of the row
/// (<c>waypoints.Points[goal.Waypoint]</c>). Each field of the elements is a column, loaded once per run into the
/// vectors it fills (<c>TableLanes.Load</c>, a line of the setup), and each read is one permute per vector of the column
/// (<c>TableLanes.Pick</c>); the rows whose index is outside the table run the method, which throws there. The lanes are
/// the elements' bits, so a read is the scalar read exactly. Lanes of 4 bytes only (float, int, uint; a struct of them).
/// </summary>
internal sealed partial class Kernel
{
    /// <summary>The columns loaded so far ("array offset": the setup's local holding the column).</summary>
    private readonly Dictionary<string, string> _columns = new();

    /// <summary>
    /// The lanes of <paramref name="access"/>, one per lane path of its element, when it reads a uniform table at an index
    /// that varies by row; null when the index is uniform (a constant: Kernel.Unrolled.cs); a reason when the table or its
    /// elements are not the form's.
    /// </summary>
    private string?[]? TableRead(IInlineArrayAccessOperation access)
    {
        var (array, index) = (access.Instance, access.Argument);
        if (array.Type is not { } arrayType || IsUniform(index) || !IsUniform(array)) return null;
        while (index is IConversionOperation { IsImplicit: true } c && c.Operand.Type?.SpecialType == SpecialType.System_Int32) index = c.Operand;
        int length = InlineArrayLength(arrayType)!.Value;
        if (length > 16)
            throw new Unsupported($"it reads '{Text(access)}', a table of {length} elements, at an index that varies by row: a vector loop picks lanes from a table of at most 16");
        if (_size != 4 || index.Type?.SpecialType != SpecialType.System_Int32)
            throw new Unsupported($"it reads '{Text(access)}' at an index that varies by row in a loop of {LaneTypes()}: a vector loop picks a table's elements at int lanes of 4 bytes");
        var element = ElementField(arrayType).Type;
        string elementType = element.ToDisplayString(Symbols.Fqn);
        IFieldSymbol[][] paths;
        if (IsLane(element)) paths = [];
        else if (element is INamedTypeSymbol { TypeKind: TypeKind.Struct, SpecialType: SpecialType.None } inner && SequentialLayout(inner)
                 && TryPaths(inner) is { } fields && fields.All(p => IsLane(p[p.Length - 1].Type)))
            paths = fields;
        else
            throw new Unsupported($"it reads '{Text(access)}' at an index that varies by row, and its elements ({element.ToDisplayString()}) are not 4-byte lanes or a struct of them: a vector loop picks only those");

        string at = Hold(Expr(index), index.Type);
        // An index outside the table throws in the method: those rows run it
        Bail($"({Compare("LessThan", at, Broadcast("0", index.Type), index.Type)} | {Compare("GreaterThan", at, Broadcast(Literal(length - 1, index.Type), index.Type), index.Type)})");
        string table = Scalar(array);
        int stride = System.Math.Max(paths.Length, 1);
        string Pick(int offset, ITypeSymbol type)
        {
            string key = table + " " + offset;
            if (!_columns.TryGetValue(key, out var column))
            {
                column = Temp("col");
                _columns[key] = column;
                _setup.Append($"var {column} = global::EasyEcs.TableLanes.Load<{Vec("int")}>(global::System.Runtime.InteropServices.MemoryMarshal.Cast<{elementType}, int>({table}), {offset}, {stride}, {length}); // once per run\n");
            }
            return As("int", Elem(type), $"global::EasyEcs.TableLanes.Pick(in {column}, {At(at)}, {length})");
        }
        string At(string value) => As(Elem(index.Type), "int", value);
        return paths.Length == 0 ? [Pick(0, element)] : Each(paths.Length, f => Pick(f, paths[f][paths[f].Length - 1].Type));
    }

    /// <summary>A lane of a table read at a lane index: the element itself (a lane type), or a field of it (<c>points[i].X</c>).</summary>
    private string? TableLane(IOperation op) => op switch
    {
        IInlineArrayAccessOperation access when IsLaneType(op.Type) && TableRead(access) is { } lanes => lanes[0],
        IFieldReferenceOperation { Field: var field, Instance: IInlineArrayAccessOperation access } when TableRead(access) is { } lanes
            && TryPaths(access.Type) is { } paths && System.Array.FindIndex(paths, p => p.Length == 1 && SymbolEqualityComparer.Default.Equals(p[0], field)) is var f and >= 0 => lanes[f],
        _ => null,
    };
}
