using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace EasyEcs.Generator;

/// <summary>
/// Patterns and switches as lane masks. A pattern over a value of the lanes (<c>is</c>, a switch arm, a case label) is
/// one mask: a relational or constant pattern a comparison (a constant NaN matches NaN, as C# tests it), <c>and</c> /
/// <c>or</c> / <c>not</c> the masks combined, a property pattern (<c>p is { X: &gt; 0, Y: &lt; 5 }</c>) the masks of the
/// fields it names, a positional pattern (<c>v is (&gt; 0, &gt; 0)</c>) those of the fields its <c>Deconstruct</c> sets or
/// of a tuple's elements, a type, <c>var</c> or <c>_</c> pattern over a value type every lane (a variable it declares
/// holds the value). A switch expression is first match wins: each arm's mask (its pattern, and its <c>when</c> guard read
/// under the lanes no arm before took) selects its value over the arms after it, from the last arm up; the lanes no arm
/// matches send their rows to the method, which throws as written, unless the compiler proves the arms cover every value.
/// A switch statement runs each section's statements under the lanes whose first matching label is its own (a
/// <c>default</c> label wherever it stands: the lanes no other label took), as an <c>if</c> / <c>else</c> chain does; a
/// section the loop cannot express is a cold branch. The value tested is translated once.
/// </summary>
internal sealed partial class Kernel
{
    /// <summary>
    /// A value a pattern tests, translated once: one lane (a number, an enum or a bool's mask), an int of a narrow kernel
    /// (<see cref="Wide"/>), a struct's lanes in lane order (<paramref name="Lanes"/> along <paramref name="Paths"/>), or a
    /// tuple literal's elements, each a value of its own.
    /// </summary>
    private sealed record Subject(ITypeSymbol Type, IOperation Op, string? Lane = null, Wide? Wide = null, string?[]? Lanes = null,
        IFieldSymbol[][]? Paths = null, Subject[]? Elements = null);

    /// <summary>
    /// A conversion that changes nothing: an identity, or a target-typed switch expression's to the type its arms were
    /// already converted to (<c>byte b = v switch { 0 =&gt; 1, _ =&gt; b }</c>), which Roslyn does not call one.
    /// </summary>
    // An identity conversion only: a field set through any other conversion is not the field's value.
    private static IOperation Unwrap(IOperation op)
    {
        while (op is IConversionOperation { Conversion.IsIdentity: true } c) op = c.Operand;
        return op;
    }

    private static bool Keeps(IConversionOperation c) => c.Conversion.IsIdentity
        || c is { OperatorMethod: null, Operand: ISwitchExpressionOperation } && SymbolEqualityComparer.Default.Equals(c.Type, c.Operand.Type);

    /// <summary>
    /// <paramref name="value"/> as the subject of a pattern of <paramref name="user"/> (an <c>is</c>, a switch); a switch's
    /// is <paramref name="held"/> in locals, since every arm reads it.
    /// </summary>
    private Subject Of(IOperation value, bool held, IOperation user)
    {
        while (value is IConversionOperation c && Keeps(c)) value = c.Operand;
        var type = value.Type;
        if (value is ITupleOperation tuple)
            return new Subject(type!, value, Elements: tuple.Elements.Select(e => Of(e, held, user)).ToArray());
        if (_narrow && IsInt(type))
        {
            var wide = WideOf(value);
            return new Subject(type!, value, Wide: held && wide.Low != null ? wide with { Low = Held(wide.Low) } : wide);
        }
        if (IsLane(type) || IsBool(type))
        {
            string lane;
            try
            {
                lane = Expr(value);
            }
            catch (Unsupported) when (Split(value, user) is { } split)
            {
                throw split;
            }
            return new Subject(type!, value, Lane: held ? Hold(lane, type!) : lane);
        }
        if (StructPaths(type) is { } paths)
        {
            var lanes = StructOf(value);
            if (held) lanes = lanes.Select(l => l == null ? null : Hold(l, type!)).ToArray();
            return new Subject(type!, value, Lanes: lanes, Paths: paths);
        }
        if (Split(value, user) is { } call) throw call;
        if (user.Descendants().OfType<IListPatternOperation>().FirstOrDefault() is { } list) throw ListPattern(list);
        string verb = user is IIsPatternOperation ? "tests" : "switches over";
        throw new Unsupported(IsNumber(type)
            ? $"it {verb} '{Text(value)}', a {type!.ToDisplayString()}, not a lane type of {LaneSizes} bytes"
            : $"it {verb} '{Text(value)}', a {type?.ToDisplayString()}, which no vector lane holds: "
              + $"a vector loop matches numbers, enums and bools of {LaneSizes} bytes, structs of them and tuples of them");
    }

    private static bool IsNumber(ITypeSymbol? type) =>
        type is { SpecialType: >= SpecialType.System_SByte and <= SpecialType.System_Double } or { TypeKind: TypeKind.Enum };

    /// <summary>
    /// A switch over a call on a struct the loop cannot inline (a helper struct walking other entities' data,
    /// VoxelServer's <c>new Nearby(...).Trail(x, z) switch</c>): the split, the call in a pass of its own storing its
    /// result in a component, the switch over that field here; null for any other value.
    /// </summary>
    private Unsupported? Split(IOperation value, IOperation user)
    {
        if (user is IIsPatternOperation || value is not IInvocationOperation { Instance.Type: { TypeKind: TypeKind.Struct } type, TargetMethod: var m }
            || IsMath(m) || KnownVector(type))
            return null;
        string field = IsLane(value.Type) || IsBool(value.Type) ? "a component" : $"a component field of {LaneSizes} bytes (not a {value.Type?.ToDisplayString()})";
        return new Unsupported($"it switches over a call on struct {type.Name} ('{Text(value)}'), which runs row by row: call {type.Name}.{m.Name} "
            + $"in a [ForEach] method of its own that stores the result in {field}, and switch over that field here", true);
    }

    /// <summary>
    /// The lanes of <paramref name="subject"/> that <paramref name="pattern"/> matches, as a mask, or null when it matches
    /// every lane (a discard, <c>var</c>, a type pattern of the value's own type, <c>{ }</c>).
    /// </summary>
    private string? Match(Subject subject, IPatternOperation pattern)
    {
        switch (pattern)
        {
            case IDiscardPatternOperation:
                return null;
            case IBinaryPatternOperation { OperatorKind: var kind, LeftPattern: var left, RightPattern: var right }:
            {
                string? a = Match(subject, left), b = Match(subject, right);
                if (kind == BinaryOperatorKind.And) return And(a, b);
                return a == null || b == null ? null : $"({a} | {b})";
            }
            case INegatedPatternOperation { Pattern: var negated }:
                return Match(subject, negated) is { } inner ? $"(~{inner})" : $"{Vec(_mask)}.Zero";
            case IDeclarationPatternOperation { MatchedType: var declaredType, DeclaredSymbol: var declared }:
                string? typed = OfType(subject, declaredType, pattern);
                if (declared is ILocalSymbol local) Declare(local, subject, pattern);
                return typed;
            case ITypePatternOperation { MatchedType: var testedType }:
                return OfType(subject, testedType, pattern);
            case IRecursivePatternOperation recursive:
                return Recursive(subject, recursive);
            case IListPatternOperation or ISlicePatternOperation:
                throw ListPattern(pattern);
            case IRelationalPatternOperation or IConstantPatternOperation:
                if (subject.Wide is { } wide)
                {
                    IOperation user = pattern; // the reason names the whole test
                    while (user.Parent is IPatternOperation or IIsPatternOperation) user = user.Parent;
                    return WidePattern(wide, pattern, user);
                }
                if (subject.Lane is not { } lane)
                    throw subject.Lanes == null && subject.Elements == null
                        ? new Unsupported("a statement mixes fields") // a lane a flat loop's field leaves out: Flat falls back, never reported
                        : Not(pattern);
                if (IsBool(subject.Type)) // true or false: the mask, or its complement
                    return pattern is IConstantPatternOperation { Value.ConstantValue: { HasValue: true, Value: bool truth } } ? truth ? lane : $"(~{lane})" : throw Not(pattern);
                return Pattern(lane, subject.Type, pattern);
            default:
                throw Not(pattern);
        }
    }

    private static Unsupported ListPattern(IOperation pattern) =>
        new($"it matches a list pattern ('{Text(pattern)}'), which tests the elements of a list: a vector loop tests lanes, not lists");

    /// <summary>
    /// A type pattern over a value type: every lane where the value is of <paramref name="matched"/> (its own type, or a
    /// type it converts to by boxing, which C# tests as always true), none where it cannot be; null for every lane.
    /// </summary>
    private string? OfType(Subject subject, ITypeSymbol? matched, IPatternOperation pattern)
    {
        if (matched == null || SymbolEqualityComparer.Default.Equals(matched, subject.Type)) return null;
        if (_compilation == null) throw Not(pattern);
        var conversion = _compilation.ClassifyConversion(subject.Type, matched);
        if (conversion.IsIdentity || conversion.IsBoxing) return null; // a struct is always an object, an interface it implements
        return conversion.Exists && !conversion.IsUnboxing ? throw Not(pattern) : $"{Vec(_mask)}.Zero";
    }

    /// <summary>
    /// <c>{ X: p, Y: q }</c> and <c>(p, q)</c>: each named member or deconstructed element of the subject matched by its own
    /// pattern, every mask and-ed; a variable the pattern declares holds the subject.
    /// </summary>
    private string? Recursive(Subject subject, IRecursivePatternOperation pattern)
    {
        if (OfType(subject, pattern.MatchedType, pattern) is { } typed) return typed; // a struct of another type: never
        string? mask = null;
        for (int i = 0; i < pattern.DeconstructionSubpatterns.Length; i++)
            mask = And(mask, Match(Element(subject, pattern, i), pattern.DeconstructionSubpatterns[i]));
        foreach (var sub in pattern.PropertySubpatterns)
        {
            if (sub.Member is not IMemberReferenceOperation { Member: var member }) throw Not(pattern);
            mask = And(mask, Match(MemberOf(subject, member, sub), sub.Pattern));
        }
        if (pattern.DeclaredSymbol is ILocalSymbol local) Declare(local, subject, pattern);
        return mask;
    }

    /// <summary>Element <paramref name="i"/> of a positional pattern: a tuple's, or the member the type's Deconstruct sets.</summary>
    private Subject Element(Subject subject, IRecursivePatternOperation pattern, int i)
    {
        if (subject.Elements != null) return subject.Elements[i];
        if (subject.Type.IsTupleType && subject.Type is INamedTypeSymbol { TupleElements: var elements } && i < elements.Length)
            return MemberOf(subject, elements[i], pattern);
        if (pattern.DeconstructSymbol is IMethodSymbol deconstruct)
            return MemberOf(subject, Deconstructs(deconstruct, pattern)[i], pattern);
        throw new Unsupported($"its positional pattern '{Text(pattern)}' reads {subject.Type.Name} through {pattern.DeconstructSymbol?.Name ?? "no Deconstruct"}, which sets no fields the vector loop can name");
    }

    /// <summary>The subject's member <paramref name="member"/>: a tuple's element, a lane of a struct, or a struct of lanes under it.</summary>
    private Subject MemberOf(Subject subject, ISymbol member, IOperation op)
    {
        if (subject.Elements != null)
        {
            var field = (member as IFieldSymbol)?.CorrespondingTupleField ?? member as IFieldSymbol;
            int at = field?.Name is { Length: > 4 } name && name.StartsWith("Item", System.StringComparison.Ordinal)
                && int.TryParse(name.Substring(4), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n) ? n - 1 : -1;
            return at >= 0 && at < subject.Elements.Length ? subject.Elements[at] : throw Not(op);
        }
        if (subject.Lanes is not { } lanes || subject.Paths is not { } paths) throw Not(op);
        var under = Enumerable.Range(0, paths.Length).Where(f => Is(member, paths[f][0])).ToArray();
        if (under.Length == 0) throw Not(op);
        var type = member switch { IFieldSymbol f => f.Type, IPropertySymbol p => p.Type, _ => throw Not(op) };
        if (under.Length == 1 && paths[under[0]].Length == 1)
            return new Subject(type, op, Lane: lanes[under[0]]);
        return new Subject(type, op, Lanes: under.Select(f => lanes[f]).ToArray(), Paths: under.Select(f => paths[f].Skip(1).ToArray()).ToArray());
    }

    /// <summary>
    /// The member each out parameter of a <c>Deconstruct</c> sets: a record's synthesized one its positional properties;
    /// one declared here whose body only assigns members of the struct to its out parameters, those; the vector types'
    /// own, their fields by name.
    /// </summary>
    private ISymbol[] Deconstructs(IMethodSymbol deconstruct, IOperation pattern)
    {
        var type = deconstruct.ContainingType;
        var set = new ISymbol?[deconstruct.Parameters.Length];
        ISymbol? Named(string name) => type.GetMembers().FirstOrDefault(m => m is IFieldSymbol { IsStatic: false } or IPropertySymbol { IsStatic: false }
            && string.Equals(m.Name, name, System.StringComparison.OrdinalIgnoreCase));
        if (deconstruct.IsImplicitlyDeclared && type.IsRecord || KnownVector(type))
            for (int i = 0; i < set.Length; i++) set[i] = Named(deconstruct.Parameters[i].Name);
        else if (deconstruct.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is MethodDeclarationSyntax syntax && _compilation != null
                 && ModelOf(_compilation, syntax.SyntaxTree).GetOperation(syntax) is IMethodBodyOperation body)
        {
            var statements = body.BlockBody?.Operations ?? (body.ExpressionBody != null ? body.ExpressionBody.Operations : []);
            foreach (var statement in statements)
            {
                var e = statement is IExpressionStatementOperation { Operation: var inner } ? inner : statement;
                var pairs = e switch
                {
                    ISimpleAssignmentOperation a => [(a.Target, a.Value)],
                    IDeconstructionAssignmentOperation { Target: ITupleOperation targets, Value: ITupleOperation values } when targets.Elements.Length == values.Elements.Length
                        => targets.Elements.Zip(values.Elements, (t, v) => (t, v)).ToArray(),
                    IReturnOperation { ReturnedValue: null } => [],
                    _ => null as (IOperation, IOperation)[],
                } ?? throw new Unsupported($"{type.Name}.Deconstruct does more than set its out parameters from {type.Name}'s fields ('{Text(statement)}')");
                foreach (var (target, value) in pairs)
                {
                    if (target is not IParameterReferenceOperation { Parameter: var p } || !SymbolEqualityComparer.Default.Equals(p.ContainingSymbol, deconstruct)
                        || Chain(Unwrap(value)) is not (IInstanceReferenceOperation, { Length: 1 } chain) || set[p.Ordinal] != null)
                        throw new Unsupported($"{type.Name}.Deconstruct does more than set its out parameters from {type.Name}'s fields ('{Text(statement)}')");
                    set[p.Ordinal] = chain[0];
                }
            }
        }
        for (int i = 0; i < set.Length; i++)
            if (set[i] == null)
                throw new Unsupported($"its positional pattern '{Text(pattern)}' reads {type.Name} through {type.Name}.Deconstruct, whose out parameter '{deconstruct.Parameters[i].Name}' is set from no field the vector loop can name");
        return set!;
    }

    /// <summary>
    /// A variable a pattern declares (<c>var v</c>, <c>float f</c>, <c>{ } p</c>): a local of the loop holding the subject,
    /// copied, since a section may write what the subject read before reading the variable.
    /// </summary>
    private void Declare(ILocalSymbol local, Subject subject, IOperation pattern)
    {
        if (subject.Lane != null && (IsLane(local.Type) || IsBool(local.Type)))
        {
            string name = Temp("l");
            _body.Append($"var {name} = {subject.Lane};\n");
            _locals[local] = name;
        }
        else if (subject.Wide != null && IsInt(local.Type)) DeclareWide(local, subject.Wide);
        else if (subject.Lanes != null && StructPaths(local.Type) != null)
            _structs[local] = subject.Lanes.Select(l =>
            {
                if (l == null) return null;
                string name = Temp("l");
                _body.Append($"var {name} = {l};\n");
                return name;
            }).ToArray();
        else
            throw new Unsupported($"pattern '{Text(pattern)}' declares '{local.Name}' as a {local.Type.ToDisplayString()}, which no vector lane holds: "
                + "name the value it tests, or a field of it, instead");
    }

    /// <summary>A mask used again (negated, or-ed): a local of the loop, with what its atoms prove about the optionals.</summary>
    private string HoldMask(string mask)
    {
        if (mask.StartsWith("__", System.StringComparison.Ordinal) && mask.All(c => char.IsLetterOrDigit(c) || c == '_')) return mask;
        var proven = Implied(mask);
        string name = Temp("m");
        _body.Append($"var {name} = {mask};\n");
        Alias(name, mask);
        if (TracksPresence)
        {
            _implied[name] = proven;
            _implied["~" + name] = [];
        }
        return name;
    }

    /// <summary>
    /// A switch expression of values <paramref name="value"/> translates (lanes, a struct's lanes, a narrow int), first
    /// match wins: from the last arm up, each arm's value where its mask holds (<paramref name="choose"/>), the arms below
    /// elsewhere. Each arm's guard and value are read under the lanes it may take.
    /// </summary>
    private T Switch<T>(ISwitchExpressionOperation select, System.Func<IOperation, T> value, System.Func<string, T, T, T> choose)
    {
        // A pair's halves select in masks twice as wide, and a bail is a mask of the lanes: the switch is the lanes'.
        if (_half >= 0)
            throw new Unsupported($"its switch expression ('{Text(select)}') gives a value twice as wide as the lanes: assign it to a local of its own first");
        string? outer = _active, rest = null; // rest: the lanes no arm above took (null: every lane)
        var subject = Of(select.Value, true, select);
        var arms = new List<(string? Mask, T Value)>();
        foreach (var arm in select.Arms)
        {
            _active = And(outer, rest);
            string? mask = Match(subject, arm.Pattern);
            if (arm.Guard != null)
            {
                _active = And(outer, And(rest, mask));
                string guard = Hold(Expr(arm.Guard), arm.Guard.Type!);
                Prove(guard, arm.Guard);
                mask = And(mask, guard);
            }
            if (mask != null) mask = HoldMask(mask);
            _active = And(outer, And(rest, mask));
            arms.Add((mask, value(arm.Value)));
            if (mask == null) break; // every lane left takes it: the arms after it are never reached
            rest = And(rest, $"~{mask}");
        }
        _active = outer;
        // A row no arm matches throws (SwitchExpressionException): its vector runs the method, which throws as written.
        if (arms[arms.Count - 1].Mask != null && !select.IsExhaustive) Bail(rest!);
        T result = arms[arms.Count - 1].Value;
        for (int i = arms.Count - 2; i >= 0; i--) result = choose(arms[i].Mask!, arms[i].Value, result);
        return result;
    }

    /// <summary>
    /// A switch statement in the lanes of <paramref name="mask"/>: each label's mask (case labels in order, first match
    /// wins), then each section's statements under its lanes, the trailing <c>break</c> left out (a branch: cold where the
    /// loop cannot express it). A <c>default</c> label takes the lanes no other label took, wherever it stands.
    /// </summary>
    private void Switch(ISwitchOperation select, string? mask)
    {
        string? outer = And(mask, _alive);
        _active = outer;
        var subject = Of(select.Value, true, select);
        string? rest = null; // the lanes no label above took (null: every lane)
        bool exhausted = false; // a label took every lane: the labels after it are never reached
        var labels = new string?[select.Cases.Length]; // each section's lanes from its own labels, NoLanes for none
        for (int i = 0; i < select.Cases.Length; i++)
        {
            labels[i] = NoLanes;
            var masks = new List<string?>();
            foreach (var clause in select.Cases[i].Clauses)
            {
                if (exhausted || clause is IDefaultCaseClauseOperation) continue;
                _active = And(outer, rest);
                string? label = clause switch
                {
                    IPatternCaseClauseOperation p => Match(subject, p.Pattern),
                    ISingleValueCaseClauseOperation { Value: var v } when subject.Lane != null && IsBool(subject.Type) && v.ConstantValue is { HasValue: true, Value: bool truth }
                        => truth ? subject.Lane : $"(~{subject.Lane})",
                    ISingleValueCaseClauseOperation { Value: var v } when subject.Lane != null => Equal(subject.Lane, subject.Type, v),
                    ISingleValueCaseClauseOperation { Value: var v } when subject.Wide != null && v.ConstantValue is { HasValue: true, Value: int c }
                        => WideCompare(BinaryOperatorKind.Equals, subject.Wide, Constant(c), clause, clause),
                    _ => throw Not(clause),
                };
                if (clause is IPatternCaseClauseOperation { Guard: { } condition })
                {
                    _active = And(outer, And(rest, label));
                    string guard = Hold(Expr(condition), condition.Type!);
                    Prove(guard, condition);
                    label = And(label, guard);
                }
                masks.Add(label == null ? null : HoldMask(label));
            }
            if (masks.Count == 0) continue;
            string? any = masks.Contains(null) ? null : masks.Count == 1 ? masks[0] : HoldMask($"({string.Join(" | ", masks)})");
            labels[i] = any == null ? rest : rest == null ? any : HoldMask(And(rest, any)!);
            if (any == null) exhausted = true;
            else rest = And(rest, $"~{any}");
        }
        _active = outer;
        for (int i = 0; i < select.Cases.Length; i++)
        {
            // The section's lanes: its labels' where no label above matched, and the default's (the lanes no label took).
            string? taken = labels[i];
            if (select.Cases[i].Clauses.Any(c => c is IDefaultCaseClauseOperation) && !exhausted)
                taken = taken == NoLanes ? rest : taken == null || rest == null ? null : HoldMask($"({taken} | {rest})");
            if (taken == NoLanes) continue;
            var statements = select.Cases[i].Body;
            int count = statements.Length;
            if (count > 0 && statements[count - 1] is IBranchOperation { BranchKind: BranchKind.Break }) count--;
            if (count == 0) continue;
            string lanesOf = And(mask, taken) ?? $"{Vec(_mask)}.AllBitsSet";
            Branch(() =>
            {
                for (int s = 0; s < count; s++)
                    if (!Statement(statements[s], lanesOf)) break;
            }, lanesOf);
        }
    }

    /// <summary>The lanes of a section no label reaches (it follows one that took every lane).</summary>
    private const string NoLanes = "\u0002none";

    // ---------------- switches in inlined helpers (Kernel.Inlining.cs) ----------------

    private sealed partial class Rewriter
    {
        /// <summary>A switch statement with its value, labels and sections' statements rewritten (calls in them inlined).</summary>
        private StatementSyntax Switch(SwitchStatementSyntax node) => node
            .WithExpression((ExpressionSyntax)Visit(node.Expression)!)
            .WithSections(SyntaxFactory.List(node.Sections.Select(section => section
                .WithLabels(SyntaxFactory.List(section.Labels.Select(label => (SwitchLabelSyntax)Visit(label)!)))
                .WithStatements(SyntaxFactory.List(section.Statements.SelectMany(Statements))))));

        /// <summary>
        /// A helper's switch statement whose sections return: each section's returns lowered (<see cref="Lower"/>) and a
        /// <c>break</c> at its end. Where every section returns and one is the default, what follows is never reached;
        /// otherwise a section that returns clears the flag of the lanes still running, which guards the rest of the body.
        /// </summary>
        private List<StatementSyntax> LowerSwitch(SwitchStatementSyntax select, List<StatementSyntax> rest, Exit exit)
        {
            bool all = select.Sections.All(s => s.Statements.Any(AlwaysReturns)) && select.Sections.Any(s => s.Labels.Any(l => l is DefaultSwitchLabelSyntax));
            bool flagged = exit.Flagged;
            if (!all)
            {
                exit.Live ??= owner.Fresh("__live");
                exit.Flagged = true;
            }
            var sections = select.Sections.Select(section =>
            {
                var statements = section.Statements.ToList();
                if (statements.Count > 0 && statements[statements.Count - 1] is BreakStatementSyntax) statements.RemoveAt(statements.Count - 1);
                return section.WithStatements(SyntaxFactory.List([.. Lower(statements, exit), SyntaxFactory.BreakStatement()]));
            }).ToArray();
            var lowered = new List<StatementSyntax> { select.WithSections(SyntaxFactory.List(sections)) };
            if (!all && rest.Count > 0) lowered.Add(SyntaxFactory.IfStatement(SyntaxFactory.IdentifierName(exit.Live!), SyntaxFactory.Block(Lower(rest, exit))));
            exit.Flagged = flagged;
            return lowered;
        }

        /// <summary>The member a property pattern names (<c>{ X: &gt; 0 }</c>, <c>{ A.B: 1 }</c>): a member of the value it tests.</summary>
        private static bool NamesSubpattern(SyntaxNode node)
        {
            for (var at = node; at.Parent is { } parent; at = parent)
            {
                if (parent is BaseExpressionColonSyntax { Parent: SubpatternSyntax }) return true;
                if (parent is not MemberAccessExpressionSyntax) return false;
            }
            return false;
        }
    }
}
