using System;
using System.Runtime.CompilerServices;

namespace EasyEcs;

/// <summary>
/// What happens to the links to an entity when it is destroyed (<see cref="TargetRules.WhenTargetDestroyed{T}"/>,
/// <see cref="RelationIndex{T}.WhenTargetDestroyed"/>).
/// </summary>
public enum TargetPolicy
{
    /// <summary>The links stay, findable by the destroyed id (which no later entity has); a system cleans them.</summary>
    Keep,

    /// <summary>The links go: the component is removed from its entities, a relation's links are dropped.</summary>
    Remove,

    /// <summary>The linking entities are destroyed too, and what links to them in turn (as <see cref="ChildOf"/> does).</summary>
    Destroy,
}

/// <summary>Target policies of components that point at an entity (<see cref="IIndexed{TKey}"/> of <see cref="EntityId"/>).</summary>
public static class TargetRules
{
    /// <summary>
    /// Decide what destroying an entity does to the <typeparamref name="T"/> components whose key is that entity: an
    /// owner whose pet dies loses its <c>Pet</c> (<see cref="TargetPolicy.Remove"/>), a summon dies with its summoner
    /// (<see cref="TargetPolicy.Destroy"/>). <see cref="ChildOf"/> is <see cref="TargetPolicy.Destroy"/> from the
    /// start; others are <see cref="TargetPolicy.Keep"/>. Applied inside every destroy (the index is kept built); O(1)
    /// per destroyed entity, O(1) per link it had. Owner thread only.
    /// </summary>
    public static ValueIndex<T, EntityId> WhenTargetDestroyed<T>(this ValueIndex<T, EntityId> index, TargetPolicy policy)
        where T : struct, IComponent, IIndexed<EntityId>
    {
        index.World.SetRule(index, policy, static index => new World.ComponentRule<T>(index));
        return index;
    }
}

// Links to destroyed entities: the rules of the indexes that apply a policy (the ChildOf cascade among them), applied
// after each destroy; and the hierarchy's lookup of children.
public sealed partial class World
{
    /// <summary>Bit of <see cref="ChildOf"/>.</summary>
    private static readonly ushort ChildOfBit = ComponentType<ChildOf>.Bit;

    /// <summary>
    /// Whether this world has a table with <see cref="ChildOf"/>: until then destroying an entity has no children to
    /// look for (other worlds of the process do not matter). Set when such a table is created.
    /// </summary>
    private bool _hierarchy;

    // The ChildOf index, once an entity is destroyed while the hierarchy is in use (or Children is called).
    private ValueIndex<ChildOf, EntityId> _children;

    // ChildOf's rule (Destroy unless the user set another), once the hierarchy is in use: checked inline on every
    // destroy, as it is the common one. Then the rules of other indexes with a policy other than Keep, and their count.
    private ComponentRule<ChildOf> _childRule;
    private TargetRule[] _rules = [];
    private int _ruleCount;

    // Destroyed entities whose links are still to follow (a stack: cascades need no recursion). Reused.
    private EntityId[] _targets = [];
    private int _targetCount;

    /// <summary>
    /// The children of <paramref name="parent"/>: the entities whose <see cref="ChildOf"/> points at it. O(1), then O(1)
    /// per child; the first call indexes every <see cref="ChildOf"/> once. Owner thread only.
    /// </summary>
    public ValueIndex<ChildOf, EntityId>.Matches Children(EntityId parent) =>
        (_children ??= Index<ChildOf, EntityId>()).Find(parent);

    /// <summary>Have the rules' indexes ready before a destroy enters the structural gate (building one reads tables).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PrepareCascade()
    {
        if (_hierarchy | _ruleCount != 0) PrepareRules();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void PrepareRules()
    {
        if (_hierarchy && _childRule == null) ChildRule(TargetPolicy.Destroy);
        _childRule?.Prepare();
        for (int i = 0; i < _ruleCount; i++) _rules[i].Prepare();
    }

    /// <summary>Set the policy of <paramref name="index"/>'s links (Keep drops its rule). Cold.</summary>
    internal void SetRule<TIndex>(TIndex index, TargetPolicy policy, Func<TIndex, TargetRule> create) where TIndex : class
    {
        if (index is ValueIndex<ChildOf, EntityId> children)
        {
            _children = children;
            ChildRule(policy);
            return;
        }

        int at = 0;
        while (at < _ruleCount && !ReferenceEquals(_rules[at].Index, index)) at++;
        if (policy == TargetPolicy.Keep)
        {
            if (at == _ruleCount) return;
            _rules.AsSpan(at + 1, _ruleCount - at - 1).CopyTo(_rules.AsSpan(at));
            _rules[--_ruleCount] = null;
            return;
        }

        if (at == _ruleCount)
        {
            if (_ruleCount == _rules.Length) Array.Resize(ref _rules, Math.Max(4, _rules.Length * 2));
            _rules[_ruleCount++] = create(index);
        }

        _rules[at].Policy = policy;
        _rules[at].Prepare();
    }

    private void ChildRule(TargetPolicy policy)
    {
        _childRule ??= new ComponentRule<ChildOf>(_children ??= Index<ChildOf, EntityId>());
        _childRule.Policy = policy;
        _childRule.Prepare();
    }

    /// <summary>Entity <paramref name="id"/> was destroyed: remember it if a rule's index links to it. Inside the structural gate.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Orphaned(EntityId id)
    {
        if (_childRule is { Policy: not TargetPolicy.Keep } && _children.Count(id) != 0) PushTarget(id);
        else if (_ruleCount != 0) Targeted(id);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Targeted(EntityId id)
    {
        for (int i = 0; i < _ruleCount; i++)
            if (_rules[i].Links(id))
            {
                PushTarget(id);
                return;
            }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void PushTarget(EntityId id)
    {
        if (_targetCount == _targets.Length) Array.Resize(ref _targets, Math.Max(16, _targets.Length * 2));
        _targets[_targetCount++] = id;
    }

    /// <summary>
    /// Apply the rules to the links of the remembered entities, and of the entities that destroys in turn (one
    /// holding cleanup components is retired, see <see cref="Retire"/>). Inside the structural gate.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void FollowLinks()
    {
        while (_targetCount > 0)
        {
            var target = _targets[--_targetCount];
            if (_childRule is { Policy: not TargetPolicy.Keep }) _childRule.Apply(this, target);
            for (int i = 0; i < _ruleCount; i++) _rules[i].Apply(this, target);
        }
    }

    /// <summary>The policy of one index, and how to find and change the links of a destroyed entity in it.</summary>
    internal abstract class TargetRule
    {
        public TargetPolicy Policy;

        /// <summary>The index the rule follows (identifies the rule).</summary>
        public abstract object Index { get; }

        /// <summary>Build the index if it is not (before the structural gate).</summary>
        public abstract void Prepare();

        /// <summary>Whether something links to <paramref name="target"/>.</summary>
        public abstract bool Links(EntityId target);

        /// <summary>Remove or destroy what links to <paramref name="target"/>. Inside the structural gate.</summary>
        public abstract void Apply(World world, EntityId target);
    }

    internal sealed class ComponentRule<T>(ValueIndex<T, EntityId> index) : TargetRule where T : struct, IComponent, IIndexed<EntityId>
    {
        public override object Index => index;

        public override void Prepare() => index.Prepare();

        public override bool Links(EntityId target) => index.Count(target) != 0;

        // Removing or destroying during the walk is allowed: an unlinked entity is stepped past.
        public override void Apply(World world, EntityId target)
        {
            foreach (var source in index.Find(target))
            {
                ref var meta = ref world.MetaRef(source.Index);
                if (Policy == TargetPolicy.Destroy) world.Destroy(ref meta, source.Id);
                else world.RemoveBit<T>(ref meta);
            }
        }
    }

    internal sealed class RelationRule<T>(RelationIndex<T> index) : TargetRule where T : struct, IRelation
    {
        public override object Index => index;

        public override void Prepare() => index.Prepare();

        public override bool Links(EntityId target) => index.Count(target) != 0;

        // Dropping links or destroying sources during the walk is allowed: a released link is stepped past.
        public override void Apply(World world, EntityId target)
        {
            foreach (var source in index.Find(target))
            {
                if (Policy == TargetPolicy.Destroy) world.Destroy(ref world.MetaRef(source.Index), source.Id);
                else if (source.IsAlive) source.RemoveRelation<T>(target);
            }
        }
    }
}
