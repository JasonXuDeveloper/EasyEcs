using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace EasyEcs.Generator;

/// <summary>
/// Writes a vector loop applies apart from its stores: a structural change of the row's entity and the records of an
/// <c>Emit&lt;T&gt;</c>. A structural change under a condition (<c>if (hp.Value &lt;= 0) unit.SetDead();</c>) is the
/// condition as a lane mask, OR'ed over the method's calls of that change, turned into bits after the method's
/// statements and marked (<c>EasyEcs.MarkedRows</c>); the system applies the change to the marked rows in bulk when the
/// pass ends (<c>SetAll</c>, <c>RemoveAll</c>, <c>DisableAll</c>, <c>EnableAll</c>, <c>DestroyAll</c>). That is the
/// method's result exactly when the method cannot observe its own change: it takes no parameter of the component it
/// changes, the value it sets is the same for every row, it makes one change (several would move rows the other's marks
/// name), and nothing else it does depends on the rows a destroy would have removed before the pass reached them. An
/// <c>Emit&lt;T&gt;.Add(new T(x))</c> appends the passing lanes' <c>x</c> (a lane, or the entity's id) compacted, in row
/// order. Every row then takes the vector form (<see cref="Code.VectorTail"/>): the rows past the last whole vector are copied
/// into a vector's worth of rows, and hardware without the loop's vectors runs the method's own job instead.
/// </summary>
internal sealed partial class Kernel
{
    /// <summary>
    /// The token of the row a vector starts at, in the run: <c>(int)i</c> in the loops, the first row copied in the last
    /// vector of a run (<c>SystemGenerator.TailLoop</c>).
    /// </summary>
    internal const string Row = "\u0001Row";

    /// <summary>The Entity parameter: its uses are its id (in a record) and the changes it makes.</summary>
    private IParameterSymbol? _entity;

    /// <summary>The Emit&lt;T&gt; parameters: the job field holding each one's list (<c>e{i}</c>), and whether its one Add was translated.</summary>
    private readonly Dictionary<IParameterSymbol, (int Index, bool Added)> _emits = new(SymbolEqualityComparer.Default);

    /// <summary>The structural change of the method (at most one): what <see cref="Code.StructuralChanges"/> lists.</summary>
    private Change? _change;

    /// <param name="Kind">Set, Remove, Disable, Enable or Destroy.</param>
    /// <param name="Type">The component, fully qualified ("" for Destroy).</param>
    /// <param name="Value">The value a Set gives every marked row, as C# over the job's fields, else "".</param>
    /// <param name="Text">The first call, for reasons.</param>
    private sealed record Change(string Kind, string Type, string Value, string Text);

    private SemanticModel? _model;

    // Whether the method makes no structural change (Symbols.IsStable): only then may a cold branch run its rows during
    // the pass, which is frozen; a method that changes structure takes every row in the vector loop, or stays scalar.
    private bool _stable;

    /// <summary>Why a method with a change or a record stays scalar, the fix in the reason (fixable).</summary>
    private static Unsupported FixableReason(string reason) => new(reason, isFixable: true) { IsWholeMethod = true };

    /// <summary>
    /// A statement that changes the row's entity or emits a record: translated (true), or the reason it cannot be;
    /// false for any other statement.
    /// </summary>
    private bool Deferred(IOperation op)
    {
        if (_entity != null && _model != null && Symbols.StructuralChange(op, _entity, _model) is { } change)
        {
            Mark(change, op);
            return true;
        }

        if (op is IInvocationOperation { TargetMethod.Name: "Add", Instance: IParameterReferenceOperation { Parameter: var p }, Arguments.Length: 1 } add
            && _emits.TryGetValue(p, out var emit))
        {
            if (emit.Added) throw FixableReason($"it emits to '{p.Name}' twice: a vector loop emits one record per row, in row order (one Add per Emit<T>; take a second Emit<T> for the other records)");
            _emits[p] = (emit.Index, true);
            Record(p, emit.Index, add.Arguments[0].Value);
            return true;
        }

        return false;
    }

    /// <summary>A structural change of the row's entity, for the lanes of <see cref="_active"/>: marked.</summary>
    private void Mark(Symbols.EntityChange change, IOperation op)
    {
        if (change.Kind == "Other")
            throw FixableReason($"it calls '{Text(op)}': a vector loop applies one Set, Remove, Disable, Enable or Destroy of one component to the rows it marks");
        string type = change.Component?.ToDisplayString(Symbols.Fqn) ?? "";
        if (change.Component is { } component)
        {
            // The method reads the component it changes: it would see the change at once, a vector loop after the pass.
            foreach (var p in _components.Keys.Concat(_optionals.Keys))
                if (SymbolEqualityComparer.Default.Equals(p.Type, component) || Symbols.MaybeElement(p.Type) is { } element && SymbolEqualityComparer.Default.Equals(element, component))
                    throw FixableReason($"it changes {component.Name}, which it also takes as '{p.Name}': the change would be seen at once by the method and after the pass by a vector loop; change it in a [ForEach] method of its own");
            if (change.Kind is "Set" or "Enable" && Symbols.IndexedKey(component, out bool unique) != null && unique)
                throw new Unsupported($"it gives {component.Name}, a unique key, to the rows it marks: one value would be claimed by every row") { IsWholeMethod = true };
        }

        string value = change.Kind == "Set" ? Value(change, op) : "";
        var marked = new Change(change.Kind, type, value, Text(op));
        if (_change == null) _change = marked;
        else if (_change with { Text = "" } != marked with { Text = "" })
            throw FixableReason($"it makes two structural changes ('{_change.Text}' and '{Text(op)}'): a vector loop applies one; split the method into one [ForEach] method per change");
        _live = true;
        string lanes = _active == null ? "__live" : $"(__live & {_active})";
        _body.Append($"__d0 |= {lanes};\n");
    }

    /// <summary>The one value a Set gives every row it marks, as C# over the job's fields (computed once per run).</summary>
    private string Value(Symbols.EntityChange change, IOperation op)
    {
        var type = (INamedTypeSymbol)change.Component!;
        string t = type.ToDisplayString(Symbols.Fqn);
        foreach (var argument in change.Arguments)
            if (!IsUniform(argument))
                throw FixableReason($"'{Text(op)}' sets {type.Name} to a value that differs per row: a vector loop gives the rows it marks one value; set the tag here and the value in a [ForEach] method over the tag");
        if (!change.Generated) return ValueText(change.Arguments[0]);
        if (change.Arguments.Length == 0) return $"default({t})";
        if (change.Arguments.Any(a => a.Syntax.Parent is ArgumentSyntax { NameColon: not null }))
            throw FixableReason($"'{Text(op)}' names its arguments: write the value, {type.Name}'s constructor with its arguments in order");
        // The generated SetX(args) builds the value with the public constructor of that many parameters, or, without one,
        // sets every field in order (ComponentGenerator.SettersOf).
        string args = string.Join(", ", change.Arguments.Select(Scalar));
        var ctors = type.InstanceConstructors.Where(c => c.DeclaredAccessibility == Accessibility.Public && c.Parameters.Length > 0).ToArray();
        if (ctors.Length > 0)
            return ctors.Count(c => c.Parameters.Length == change.Arguments.Length) == 1 ? $"new {t}({args})"
                : throw FixableReason($"'{Text(op)}' has an overload of its arity: write unit.Set(new {type.Name}(...))");
        var fields = type.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).ToArray();
        if (fields.Length != change.Arguments.Length) throw FixableReason($"'{Text(op)}': write unit.Set(new {type.Name} {{ ... }})");
        return $"new {t} {{ {string.Join(", ", fields.Select((f, i) => $"{f.AssociatedSymbol?.Name ?? f.Name} = {Scalar(change.Arguments[i])}"))} }}";
    }

    /// <summary>A uniform value as C#: <c>default</c>, <c>new T(...) { ... }</c> of uniform values, or a scalar.</summary>
    private string ValueText(IOperation op)
    {
        while (op is IConversionOperation { Conversion.IsIdentity: true } c) op = c.Operand;
        switch (op)
        {
            case IDefaultValueOperation { Type: { } type }:
                return $"default({type.ToDisplayString(Symbols.Fqn)})";
            case IObjectCreationOperation { Constructor: { } ctor, Type: { } type } creation:
                var text = new StringBuilder($"new {type.ToDisplayString(Symbols.Fqn)}({string.Join(", ", creation.Arguments.OrderBy(a => a.Parameter?.Ordinal ?? 0).Select(a => Scalar(a.Value)))})");
                if (creation.Initializer is { } initializer)
                {
                    var sets = new List<string>();
                    foreach (var init in initializer.Initializers)
                        sets.Add(init is ISimpleAssignmentOperation { Target: IMemberReferenceOperation { Member: var member } } a
                            ? $"{member.Name} = {ValueText(a.Value)}" : throw FixableReason($"'{Text(init)}' in the value it sets: set the fields one by one"));
                    text.Append($" {{ {string.Join(", ", sets)} }}");
                }
                _ = ctor;
                return text.ToString();
            default:
                return Scalar(op);
        }
    }

    /// <summary><c>emit.Add(new T(x))</c> for the lanes of <see cref="_active"/>: the record holds x alone, a lane or the entity's id.</summary>
    private void Record(IParameterSymbol p, int index, IOperation record)
    {
        var type = Symbols.EmitElement(p.Type)!;
        var value = record;
        while (value is IConversionOperation { Conversion.IsIdentity: true } c) value = c.Operand;
        var fields = type.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).ToArray();
        string Shape() => $"a vector loop emits records of one field set from a lane or the entity's id ({type.Name} {(fields.Length == 1 ? "is one" : $"has {fields.Length} fields")})";
        if (fields.Length != 1) throw FixableReason($"it emits '{Text(record)}': {Shape()}; emit a record of one field, or emit in a [ForEach] method over a tag this one sets");
        if (value is IObjectCreationOperation { Constructor: { } ctor, Arguments.Length: 1, Initializer: null } creation)
        {
            if (Assigns(ctor) is not { Length: 1 } member || member[0] is not { } assigned || !Is(assigned, fields[0])) throw FixableReason($"it emits '{Text(record)}', whose constructor does not set {(fields[0].AssociatedSymbol ?? fields[0]).Name} alone: {Shape()}");
            value = creation.Arguments[0].Value;
            while (value is IConversionOperation { Conversion.IsIdentity: true } c) value = c.Operand;
        }
        else throw FixableReason($"it emits '{Text(record)}': {Shape()}, written new {type.Name}(value)");
        string t = type.ToDisplayString(Symbols.Fqn);
        _live = true;
        string lanes = $"global::EasyEcs.Lanes.Bits({(_active == null ? "__live" : $"(__live & {_active})")})";
        string emit = $"new global::EasyEcs.Emit<{t}>(ref this.e{index})";
        if (EntityId(value))
        {
            _body.Append($"{emit}.AddIds(in entities, {Row}, {lanes});\n");
            return;
        }

        if (!IsLane(fields[0].Type) || !SymbolEqualityComparer.Default.Equals(fields[0].Type, value.Type))
            throw FixableReason($"it emits '{Text(record)}': {Shape()}, and {(fields[0].AssociatedSymbol ?? fields[0]).Name} is a {fields[0].Type.ToDisplayString()}, not a lane of {LaneSizes} bytes");
        _body.Append($"{emit}.AddLanes({Expr(value)}, {lanes});\n");
    }

    /// <summary>The entity's id: <c>unit.Id</c>, or the handle converted to an EntityId.</summary>
    private bool EntityId(IOperation op) =>
        op is IMemberReferenceOperation { Member.Name: "Id", Instance: IParameterReferenceOperation { Parameter: var p } } && SymbolEqualityComparer.Default.Equals(p, _entity)
        || op is IConversionOperation { Operand: IParameterReferenceOperation { Parameter: var q } } && SymbolEqualityComparer.Default.Equals(q, _entity);

    /// <summary>A use of the Entity parameter the loop does not translate: the reason.</summary>
    private Unsupported EntityUse(IOperation op) => FixableReason(
        $"it uses the Entity in '{Text(op)}': a vector loop takes the entity's id in a record and its own Set, Remove, Disable, Enable or Destroy; "
        + "read components as parameters (a tag as [With<T>] / [Without<T>], or a Maybe<T>)");

    /// <summary>Whether <paramref name="op"/> is the Entity parameter itself.</summary>
    private bool IsEntity(IOperation op) => _entity != null && op is IParameterReferenceOperation { Parameter: var p } && SymbolEqualityComparer.Default.Equals(p, _entity);

    /// <summary>
    /// After the method's statements: the checks that need all of them, and the bits of the change's mask marked.
    /// Throws the reason a method with a change or a record stays scalar.
    /// </summary>
    private void FinishChanges(IMethodSymbol method)
    {
        if (_change == null && !_emits.Values.Any(e => e.Added)) return;
        string what = _change != null ? $"'{_change.Text}'" : "its records";
        if (_lookups.Count > 0)
            throw FixableReason($"it reads other entities through a Lookup<T> and applies {what} after the pass: a vector whose entities live in several tables runs the method, and every row must take the vector loop; emit in a [ForEach] method of its own");
        if (_bails) throw FixableReason($"it has a branch the loop cannot express ({_cold ?? "a bound that may throw"}) and applies {what} after the pass: every row must take the vector loop; move that branch into a [ForEach] method of its own");
        if (_optionals.Count > 0) throw new Unsupported($"it takes a Maybe<T> and applies {what} after the pass: the last rows of a run take the vector loop through a copy, which an optional column has not");
        if (_wideOf.Count > 0 || _size < 4) throw new Unsupported($"it applies {what} after the pass with {(_size < 4 ? $"lanes of {_size} byte{(_size == 1 ? "" : "s")}" : "a wide component")}, whose words of passing rows run the method itself");
        if (Symbols.IsConsume(method)) throw new Unsupported("it is [Consume] and changes structure itself: the pass destroys each row after it");
        if (_change?.Kind == "Destroy" && (_reductions.Count > 0 || _emits.Count > 0))
            throw FixableReason($"it destroys its entity and reduces or emits: a destroy that takes a child with it (a hierarchy) skips the child's row, which a vector loop applies after the pass; emit or reduce in a [ForEach] method of its own");
        if (_change != null)
        {
            _body.Insert(0, $"var __d0 = {Vec(_mask)}.Zero; // the rows the change marks\n");
            _body.Append($"this.d0.Mark(in entities, {Row}, global::EasyEcs.Lanes.Bits(__d0));\n");
            if (_change.Value.Length > 0) _setup.Append($"this.v0 = {_change.Value};\n");
        }
    }

    /// <summary>The change and its type for the job (<see cref="Code.StructuralChanges"/>): "Set global::Dead", or none.</summary>
    private EquatableArray<string> StructuralChanges() => new(_change == null ? [] : [$"{_change.Kind} {_change.Type}".TrimEnd()]);
}
