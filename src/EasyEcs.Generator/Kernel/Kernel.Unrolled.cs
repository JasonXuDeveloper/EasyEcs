using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace EasyEcs.Generator;

/// <summary>
/// Inline arrays and loops of a constant trip count in the method for one row (inner-loops.md WP1). An
/// <c>[InlineArray(N)]</c> struct is N lanes, one per element, in memory order (<see cref="Paths"/>); an element is
/// read at a constant index (<c>s.Gain[3]</c>, <see cref="LaneChain"/>). A <c>for</c> loop of constant bounds and step
/// 1, or a <c>foreach</c> over an inline array, of at most <see cref="MaxSteps"/> steps, runs its iterations one after
/// another in the vector loop (each lane runs them in order, as the row would: loop-carried values and an argmin are
/// exact), its variable a constant in each (<see cref="_constants"/>) or the element it stands for
/// (<see cref="_elements"/>).
/// </summary>
internal sealed partial class Kernel
{
    /// <summary>The most iterations a loop of the method for one row is unrolled into, and the most elements of an inline array read as lanes.</summary>
    private const int MaxSteps = 16;

    /// <summary>The counter of a loop being unrolled, bound to the constant of the iteration translated.</summary>
    private readonly Dictionary<ILocalSymbol, int> _constants = new(SymbolEqualityComparer.Default);

    /// <summary>The variable of a <c>foreach</c> over an inline array being unrolled: the array and the element it is in the iteration translated.</summary>
    private readonly Dictionary<ILocalSymbol, (IOperation Array, int Element)> _elements = new(SymbolEqualityComparer.Default);

    /// <summary>Whether a loop was unrolled: the body's locals are counted against the inliner's budget (<see cref="FitUnrolled"/>).</summary>
    private bool _unrolled;

    /// <summary>
    /// A loop the vector loop runs as its iterations in order: its variable, the value of the counter per iteration
    /// (a <c>for</c>) or the array whose elements it walks (a <c>foreach</c>), and its body.
    /// </summary>
    private sealed record Unrolled(ILocalSymbol Variable, int[] Steps, IOperation? Array, IOperation Body)
    {
        /// <summary>Binds the variable for iteration <paramref name="step"/> in <paramref name="kernel"/>.</summary>
        public void Bind(Kernel kernel, int step)
        {
            if (Array == null) kernel._constants[Variable] = Steps[step];
            else kernel._elements[Variable] = (Array, Steps[step]);
        }

        public void Unbind(Kernel kernel)
        {
            kernel._constants.Remove(Variable);
            kernel._elements.Remove(Variable);
        }
    }

    /// <summary>Translates <paramref name="loop"/> iteration by iteration for the lanes of <paramref name="mask"/> (<see cref="Statement"/>'s contract).</summary>
    private bool Unroll(ILoopOperation loop, string? mask)
    {
        var unrolled = UnrolledLoop(loop);
        _unrolled = true;
        string[]? first = null; // the names the first step declares, which the later steps take again
        for (int k = 0; k < unrolled.Steps.Length; k++)
        {
            unrolled.Bind(this, k);
            var start = _body.Length;
            try
            {
                if (!Statement(unrolled.Body, mask)) return false; // every lane returned: what follows is not reached
            }
            finally
            {
                unrolled.Unbind(this);
            }
            var declared = Declares.Matches(_body.From(start.All)).Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value).ToArray();
            if (first == null) first = declared;
            else Reuse(start, declared, first);
        }
        return true;
    }

    /// <summary>A vector the loop's body declares: <c>var __l3 = </c>, at the start of a line.</summary>
    private static readonly System.Text.RegularExpressions.Regex Declares = new(@"(?m)^var (__[a-z]+\d+) = ");

    /// <summary>
    /// A later step's locals named as the first step's and assigned instead of declared: what a step declares is dead
    /// after it (the loop's body is its scope), and the steps translate one body, so the names come in the same order
    /// and of the same types. Every local counts toward the 32 a runner inlines (<see cref="SystemGenerator.InlinedLocals"/>):
    /// declared once, a step's locals cost what one step's do. Steps that declare otherwise (a branch one step finds
    /// cold) keep their own names.
    /// </summary>
    private void Reuse((int All, int Masked) start, string[] declared, string[] first)
    {
        if (declared.Length != first.Length) return;
        for (int j = 0; j < declared.Length; j++)
            if (Prefix(declared[j]) != Prefix(first[j]) || first.Contains(declared[j])) return;
        foreach (var name in first) _implied.Remove(name); // what a mask of the first step proved is not the later step's
        _body.Rewrite(start, text =>
        {
            for (int j = 0; j < declared.Length; j++)
            {
                text = System.Text.RegularExpressions.Regex.Replace(text, $@"(?m)^var {declared[j]} = ", $"{first[j]} = ");
                text = System.Text.RegularExpressions.Regex.Replace(text, $@"\b{declared[j]}\b", first[j]);
            }
            return text;
        });
        foreach (var name in declared) _implied.Remove(name);
    }

    private static string Prefix(string name) => name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');

    /// <summary>
    /// <paramref name="loop"/> as the iterations a vector loop runs in order, or the reason it cannot: a <c>for</c> whose
    /// counter is an int declared by it from a constant, compared with <c>&lt;</c> or <c>&lt;=</c> to a constant,
    /// stepped by <c>++</c> or <c>+= 1</c> and written nowhere else; or a <c>foreach</c> over an inline array; either of
    /// at most <see cref="MaxSteps"/> steps, left by no <c>break</c> or <c>continue</c>.
    /// </summary>
    private static Unrolled UnrolledLoop(ILoopOperation loop)
    {
        string text = LoopHeader(loop);
        Unsupported Inherent(string why) => new($"loop '{text}' {why}: a vector loop runs a loop of the method for one row as its steps one after another, for loops of constant bounds and at most {MaxSteps} steps");
        foreach (var branch in loop.Body.DescendantsAndSelf().OfType<IBranchOperation>())
            if (SymbolEqualityComparer.Default.Equals(branch.Target, loop.ExitLabel) || SymbolEqualityComparer.Default.Equals(branch.Target, loop.ContinueLabel))
                throw new Unsupported($"loop '{text}' leaves an iteration early ('{Text(branch)}'): a vector loop runs every step of a loop of the method for one row; write the rest of the step under an if");
        switch (loop)
        {
            case IForLoopOperation f:
            {
                if (f.Before.Length != 1 || f.Before[0] is not IVariableDeclarationGroupOperation { Declarations.Length: 1 } group
                    || group.Declarations[0].Declarators.Length != 1 || group.Declarations[0].Declarators[0] is not { Symbol: { Type.SpecialType: SpecialType.System_Int32 } counter } declarator
                    || (declarator.GetVariableInitializer()?.Value ?? group.Declarations[0].Initializer?.Value) is not { ConstantValue: { HasValue: true, Value: int from } })
                    throw Inherent("does not declare an int counter from a constant");
                var condition = f.Condition;
                if (condition is not IBinaryOperation { OperatorKind: BinaryOperatorKind.LessThan or BinaryOperatorKind.LessThanOrEqual } compare
                    || !IsLocal(compare.LeftOperand, counter) || compare.RightOperand.ConstantValue is not { HasValue: true, Value: int bound })
                    throw Inherent($"does not compare its counter '{counter.Name}' with < or <= to a constant");
                if (f.AtLoopBottom.Length != 1 || f.AtLoopBottom[0] is not IExpressionStatementOperation { Operation: var step }
                    || !(step is IIncrementOrDecrementOperation { Kind: OperationKind.Increment, Target: var t } && IsLocal(t, counter)
                         || step is ICompoundAssignmentOperation { OperatorKind: BinaryOperatorKind.Add, Target: var a, Value.ConstantValue: { HasValue: true, Value: 1 } } && IsLocal(a, counter)))
                    throw Inherent($"does not step its counter '{counter.Name}' by 1");
                if (Writes(f.Body, counter)) throw Inherent($"writes its counter '{counter.Name}' in its body");
                long last = compare.OperatorKind == BinaryOperatorKind.LessThan ? (long)bound - 1 : bound;
                long count = System.Math.Max(0, last - from + 1);
                if (count > MaxSteps) throw Inherent($"runs {count} steps");
                return new Unrolled(counter, Enumerable.Range(from, (int)count).ToArray(), null, f.Body);
            }
            case IForEachLoopOperation { IsAsynchronous: false, LoopControlVariable: IVariableDeclaratorOperation { Symbol: var variable } } each:
            {
                var array = each.Collection;
                while (array is IConversionOperation { IsImplicit: true } c) array = c.Operand;
                if (InlineArrayLength(array.Type) is not { } length) throw Inherent("walks no inline array");
                if (length > MaxSteps) throw Inherent($"runs {length} steps");
                return new Unrolled(variable, Enumerable.Range(0, length).ToArray(), array, each.Body);
            }
            default:
                throw Inherent("is not a for or foreach loop");
        }
    }

    /// <summary>The loop's header as the user wrote it, for a reason.</summary>
    private static string LoopHeader(ILoopOperation loop)
    {
        var close = loop.Syntax switch
        {
            Microsoft.CodeAnalysis.CSharp.Syntax.ForStatementSyntax f => f.CloseParenToken,
            Microsoft.CodeAnalysis.CSharp.Syntax.CommonForEachStatementSyntax f => f.CloseParenToken,
            Microsoft.CodeAnalysis.CSharp.Syntax.WhileStatementSyntax w => w.CloseParenToken,
            _ => default,
        };
        if (close.RawKind == 0) return Text(loop);
        string header = loop.Syntax.ToString().Substring(0, close.Span.End - loop.Syntax.SpanStart);
        return string.Join(" ", header.Split(['\r', '\n', '\t', ' '], System.StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool IsLocal(IOperation op, ILocalSymbol local)
    {
        while (op is IConversionOperation { IsImplicit: true } c) op = c.Operand;
        return op is ILocalReferenceOperation { Local: var l } && SymbolEqualityComparer.Default.Equals(l, local);
    }

    /// <summary>Whether <paramref name="body"/> writes <paramref name="local"/> (an assignment, ++ / --, a ref or out argument).</summary>
    private static bool Writes(IOperation body, ILocalSymbol local) => body.DescendantsAndSelf().Any(op => op switch
    {
        IDeconstructionAssignmentOperation { Target: ITupleOperation tuple } => tuple.Elements.Any(e => IsLocal(e, local)),
        IAssignmentOperation a => IsLocal(a.Target, local),
        IIncrementOrDecrementOperation i => IsLocal(i.Target, local),
        IArgumentOperation { Parameter.RefKind: not RefKind.None and not RefKind.In } arg => IsLocal(arg.Value, local),
        _ => false,
    });

    /// <summary>An [InlineArray(N)] struct: N values one after another behind its one declared field.</summary>
    private static bool IsInlineArray(ITypeSymbol? type) => InlineArrayLength(type) != null;

    /// <summary>The length of an [InlineArray(N)] struct, or null for any other type.</summary>
    private static int? InlineArrayLength(ITypeSymbol? type) =>
        type?.GetAttributes().FirstOrDefault(a => a.AttributeClass is { Name: "InlineArrayAttribute", ContainingNamespace.Name: "CompilerServices" }) is { ConstructorArguments.Length: 1 } attribute
        && attribute.ConstructorArguments[0].Value is int length ? length : null;

    /// <summary>The one field of an inline array: the type of its elements.</summary>
    private static IFieldSymbol ElementField(ITypeSymbol array) => array.GetMembers().OfType<IFieldSymbol>().First(f => !f.IsStatic);

    /// <summary>
    /// The lane paths of an inline array's elements (<paramref name="prefix"/> leads to the array): N copies of the
    /// paths of one element, the element's field in each, in memory order, so the k-th path of a given chain is element
    /// k's (<see cref="Leaf"/>). One level only: an element holding an inline array is refused.
    /// </summary>
    private static IEnumerable<IFieldSymbol[]> ElementPaths(ITypeSymbol array, IFieldSymbol[] prefix, string what)
    {
        int length = InlineArrayLength(array)!.Value;
        if (length > MaxSteps) throw new Unsupported($"{what} is an inline array of {length} elements: a vector loop reads one of at most {MaxSteps} as lanes");
        var element = ElementField(array);
        IFieldSymbol[][] one;
        if (IsLaneType(element.Type) || IsBool(element.Type)) one = [[element]];
        else if (element.Type is INamedTypeSymbol { TypeKind: TypeKind.Struct, SpecialType: SpecialType.None } inner && !IsInlineArray(inner) && SequentialLayout(inner))
        {
            one = Paths(inner, $"the elements of {what}").Select(p => (IFieldSymbol[])[element, .. p]).ToArray();
            if (one.Any(p => p.Any(f => IsInlineArray(f.Type)))) throw new Unsupported($"the elements of {what} hold an inline array: a vector loop reads one level of inline arrays");
        }
        else throw new Unsupported($"the elements of {what} are {element.Type.ToDisplayString()}, not a lane type (float, double, int, uint, long, ulong, byte, sbyte, short, ushort or an enum of them) or a struct of them");
        for (int k = 0; k < length; k++)
            foreach (var path in one)
                yield return [.. prefix, .. path];
    }

    /// <summary>
    /// The root of a chain of field, property and inline array element reads (<c>s.Gain[3]</c>: s), the members read,
    /// root first (an element is its array's field), and the element read (-1: none). An element's index is a constant,
    /// the counter of a loop being unrolled, or the variable of a <c>foreach</c> being unrolled; an index that is none of
    /// them, or out of the array's range, keeps the method scalar.
    /// </summary>
    private (IOperation? Root, ISymbol[] Members, int Element) LaneChain(IOperation op)
    {
        var members = new List<ISymbol>();
        int element = -1;
        while (true)
        {
            switch (op)
            {
                case IFieldReferenceOperation { Field.IsStatic: false, Instance: { } instance } f:
                    members.Add(f.Field);
                    op = instance;
                    continue;
                case IPropertyReferenceOperation { Property.IsStatic: false, Arguments.Length: 0, Instance: { } instance } p:
                    members.Add(p.Property);
                    op = instance;
                    continue;
                case IInvocationOperation { Arguments.Length: 1 } call when Recorded.FieldOf(call) is { } hidden:
                    members.Add(hidden);
                    op = call.Arguments[0].Value;
                    continue;
                case IInlineArrayAccessOperation { Instance: var array, Argument: var index } access when element < 0 && array.Type != null:
                    element = ElementIndex(index, array.Type, access);
                    members.Add(ElementField(array.Type));
                    op = array;
                    continue;
                case ILocalReferenceOperation { Local: var local } when element < 0 && _elements.TryGetValue(local, out var bound) && bound.Array.Type != null:
                    element = bound.Element;
                    members.Add(ElementField(bound.Array.Type));
                    op = bound.Array;
                    continue;
            }
            members.Reverse();
            return (op, members.ToArray(), element);
        }
    }

    /// <summary>The constant an inline array's index is (<see cref="LaneChain"/>), or the reason the method stays scalar.</summary>
    private int ElementIndex(IOperation index, ITypeSymbol array, IOperation access)
    {
        while (index is IConversionOperation { IsImplicit: true } c) index = c.Operand;
        int? k = index.ConstantValue is { HasValue: true, Value: int constant } ? constant
            : index is ILocalReferenceOperation { Local: var local } && _constants.TryGetValue(local, out int step) ? step : null;
        if (k == null)
            throw new Unsupported($"it reads '{Text(access)}' at an index that is not a constant: a vector loop reads an inline array's elements at constants or the counter of a loop of constant bounds of at most {MaxSteps} steps");
        if (k < 0 || k >= InlineArrayLength(array)) throw new Unsupported($"it reads '{Text(access)}' at {k}, outside the array (which throws)");
        return k.Value;
    }

    /// <summary>The uniform value of an inline array's element: the array's scalar, indexed by the constant.</summary>
    private string ScalarElement(IOperation array, int element) => $"{Scalar(array)}[{element}]";

    /// <summary>
    /// After a loop was unrolled: the loop's text within what a runner inlines (<see cref="SystemGenerator.InlinedLocals"/>,
    /// the job's own names aside), or the reason; each step's locals are declared again, so a long body times its steps
    /// would push the job out of the runner, a call per run (D135).
    /// </summary>
    /// <summary>
    /// Whether the masked words' loop of an unrolled body, <paramref name="text"/>, declares no more locals than the JIT
    /// inlines: its broadcasts of constants are locals before the loop (unlike the loop over whole vectors, which takes
    /// them in place) and it holds the word's passing lanes. Past the budget its masked words run the method (the job's
    /// runner would call the loop per word), while the loop over whole vectors keeps the vectors. A wide component's
    /// masked loop is counted per form it takes (each width's lines alone, Kernel.Wide.cs): a row of many words loads
    /// that many vectors, and the passing lanes come on top of them.
    /// </summary>
    private bool MaskedFits(string text, int columns)
    {
        int limit = SystemGenerator.InlinedLocals - columns - 2;
        if (_wideOf.Count > 0)
            return new[] { (_wide512, Mark512), (_wide256, Mark256), (_wide128, Mark128) }.All(w => !w.Item1
                || SystemGenerator.Locals(string.Join("\n", text.Split('\n').Where(l => !new[] { Mark512, Mark256, Mark128 }.Any(m => m != w.Item2 && l.Contains(m))))) <= limit);
        return !_unrolled || SystemGenerator.Locals(text) <= limit;
    }

    private void FitUnrolled(string text, int columns)
    {
        if (!_unrolled) return;
        // A loop over whole vectors takes its constant broadcasts in place (SystemGenerator.Constants), as Kernel.Wide counts.
        int locals = SystemGenerator.Locals(_lookups.Count == 0 ? SystemGenerator.Constant.Replace(text, "") : text) + columns + 1;
        if (locals > SystemGenerator.InlinedLocals)
            throw new Unsupported($"its loops unrolled declare {locals} locals, past the {SystemGenerator.InlinedLocals} the JIT inlines: split the loop's work into another [ForEach] method, or name fewer values per step", true);
    }
}
