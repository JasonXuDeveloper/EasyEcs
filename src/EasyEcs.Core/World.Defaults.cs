using System;
using System.Runtime.CompilerServices;

namespace EasyEcs;

/// <summary>
/// The setup every world gives one component type, declared on the type ([WhenTargetDestroyed]) or its grouped member ([GroupBy]) and
/// registered when its module loads (<see cref="TypeRegistry.GroupBy{T}"/>, <see cref="TypeRegistry.WhenTargetDestroyed{T}"/>):
/// a world applies it with its first table holding the type, unless the world was set up for the type by hand first.
/// </summary>
internal sealed class TypeDefaults
{
    /// <summary>Whether any type has defaults (new tables then look for them).</summary>
    public static volatile bool Any;

    /// <summary>The grouping a world starts with (<see cref="World.GroupBy{T}"/>), or null.</summary>
    public Func<World.IGroupTables> Group;

    /// <summary>The target rule a world starts with: applied outside the structural gate, before a destroy needs it. Null for none.</summary>
    public Action<World> Rule;
}

/// <summary>The defaults of <typeparamref name="T"/> (a component, or the <see cref="DynamicBuffer{T}"/> of a relation), or null.</summary>
internal static class TypeDefaults<T> where T : struct
{
    public static TypeDefaults Value;

    public static TypeDefaults GetOrAdd()
    {
        TypeDefaults.Any = true;
        return Value ??= new TypeDefaults();
    }
}

// Type-level setup: the defaults of a type applied by each world (TypeDefaults), and the types a world was set up for.
public sealed partial class World
{
    // By component bit: this world is set up for it (by its defaults or by hand), so defaults no longer apply.
    private bool[] _setUp = [];

    // Bits of tables created since the last destroy whose rule defaults are still to apply (cold), and their count.
    private ushort[] _pendingRules = [];
    private int _pendingRuleCount;

    /// <summary>Whether this world was set up for component <paramref name="bit"/>; marks it from now on.</summary>
    private bool SetUp(int bit)
    {
        if (bit >= _setUp.Length) Array.Resize(ref _setUp, Math.Max(bit + 1, _setUp.Length * 2));
        if (_setUp[bit]) return true;
        _setUp[bit] = true;
        return false;
    }

    /// <summary>
    /// New <paramref name="table"/> holds types with defaults: install their grouping now (no row holds the type yet,
    /// so nothing moves), and leave their target rules for <see cref="PrepareRules"/> (building a rule's index reads
    /// tables, which a table's creation inside the structural gate must not). Cold: once per new table.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ApplyDefaults(Archetype table)
    {
        foreach (var bit in table.Components)
        {
            if (ComponentInfo.Of(bit).Defaults is not { } defaults || bit < _setUp.Length && _setUp[bit]) continue;
            if (defaults.Group != null)
            {
                SetUp(bit);
                _groupByBit ??= [];
                if (bit >= _groupByBit.Length) Array.Resize(ref _groupByBit, bit + 1);
                _groupByBit[bit] ??= defaults.Group();
            }

            if (defaults.Rule != null)
            {
                if (_pendingRuleCount == _pendingRules.Length) Array.Resize(ref _pendingRules, Math.Max(4, _pendingRules.Length * 2));
                _pendingRules[_pendingRuleCount++] = bit;
            }
        }
    }

    /// <summary>The target rules new tables left to apply (<see cref="ApplyDefaults"/>): outside the structural gate.</summary>
    private void ApplyPendingRules()
    {
        while (_pendingRuleCount != 0)
        {
            int bit = _pendingRules[--_pendingRuleCount];
            if (bit < _setUp.Length && _setUp[bit] || ComponentInfo.TryOf(bit)?.Defaults is not { Rule: { } rule }) continue;
            rule(this); // creates the index (which applies it) or finds it and sets the rule: either way the bit is set up
        }
    }

    /// <summary>
    /// The index of component <paramref name="bit"/> was created: apply the target rule its type declares unless the
    /// world was set up for it (cold). <paramref name="apply"/> sets the rule (and marks the bit).
    /// </summary>
    private void DefaultRule(int bit, TypeDefaults defaults, Action<World> apply)
    {
        if (defaults?.Rule != null && !(bit < _setUp.Length && _setUp[bit])) apply(this);
    }
}
