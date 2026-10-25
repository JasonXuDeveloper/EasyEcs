using System;

namespace EasyEcs;

// The attributes a component or singleton declares itself with. The generator reads them from the declaration and
// writes what they mean: the members of an interface (a reduction's Identity and Combine, an index's Key, a group's
// Group) in a generated part of the type, which is why their types are partial; or a registration the worlds read
// (a target policy, a grouping, a type no save holds). The runtime never reads the attributes themselves.

/// <summary>
/// A field of a reduction (<see cref="IReduction{T}"/>) that combines by addition: its <c>Identity</c> is 0, its
/// <c>Combine</c> is <c>left + right</c>. On a field, an auto-property with a setter, or a record's positional
/// parameter of a numeric type (an integer, <c>float</c>, <c>double</c>, <c>decimal</c>) of a partial struct:
/// <c>public partial struct Census : ISingletonComponent { [Sum] public int Alive; [Max] public float Oldest; }</c>.
/// The generator writes the struct's <c>IReduction&lt;T&gt;</c> (a public static <c>Identity</c> and <c>Combine</c>,
/// field by field), so every instance field takes one of <see cref="SumAttribute"/>, <see cref="ProductAttribute"/>,
/// <see cref="MinAttribute"/>, <see cref="MaxAttribute"/>, <see cref="AllAttribute"/> or <see cref="AnyAttribute"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class SumAttribute : Attribute;

/// <summary>
/// A field of a reduction that combines by multiplication: <c>Identity</c> 1, <c>Combine</c> <c>left * right</c>. A
/// numeric field (see <see cref="SumAttribute"/>).
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class ProductAttribute : Attribute;

/// <summary>
/// A field of a reduction that keeps the smaller value: <c>Identity</c> the type's <c>MaxValue</c> (+∞ for
/// <c>float</c> and <c>double</c>), <c>Combine</c> <c>Math.Min(left, right)</c>. A numeric field (see
/// <see cref="SumAttribute"/>).
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class MinAttribute : Attribute;

/// <summary>
/// A field of a reduction that keeps the larger value: <c>Identity</c> the type's <c>MinValue</c> (-∞ for
/// <c>float</c> and <c>double</c>), <c>Combine</c> <c>Math.Max(left, right)</c>. A numeric field (see
/// <see cref="SumAttribute"/>).
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class MaxAttribute : Attribute;

/// <summary>
/// A <c>bool</c> field of a reduction that holds when it holds on every row: <c>Identity</c> <c>true</c>,
/// <c>Combine</c> <c>left &amp; right</c> (see <see cref="SumAttribute"/>).
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class AllAttribute : Attribute;

/// <summary>
/// A <c>bool</c> field of a reduction that holds when it holds on some row: <c>Identity</c> <c>false</c>,
/// <c>Combine</c> <c>left | right</c> (see <see cref="SumAttribute"/>).
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class AnyAttribute : Attribute;

/// <summary>
/// The key entities are looked up by: a world indexes the component by it (<see cref="IIndexed{TKey}"/>), several
/// entities per key (<see cref="UniqueKeyAttribute"/> for one). On a field, a property or a record's
/// positional parameter, the key is that member (<c>public partial struct Owner : IComponent { [LookupKey] public EntityId Of; }</c>);
/// on the struct itself, the key is the whole value. The generator adds <c>IIndexed&lt;TKey&gt;</c> to the partial
/// struct (unless an interface the struct declares brings it) with an explicit <c>Key</c> returning the member. One key
/// per component; a key computed from several members is written by hand (<c>public (int, int) Key => (X, Y);</c>).
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Parameter | AttributeTargets.Struct)]
public sealed class LookupKeyAttribute : Attribute;

/// <summary>
/// <see cref="LookupKeyAttribute"/> for a key that names one entity (<see cref="IUniqueIndexed{TKey}"/>): the generator adds
/// <c>IUniqueIndexed&lt;TKey&gt;</c> with the explicit <c>Key</c>. A struct that declares
/// <see cref="IDirectIndexed{TKey}"/> keeps its own <c>Slots</c> and <c>SlotOf</c>; only <c>Key</c> is generated.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Parameter | AttributeTargets.Struct)]
public sealed class UniqueKeyAttribute : Attribute;

/// <summary>
/// The member a world groups the component's tables by (<see cref="IGroup"/>): an <c>int</c> or an enum of
/// <c>int</c>. The generator adds <c>IGroup</c> to the partial struct with an explicit <c>Group</c> returning it. The
/// tables are grouped in the worlds that call <see cref="World.GroupBy{T}"/>; <see cref="GroupByAttribute">[GroupBy]</see>
/// on the member instead groups them in every world.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class GroupAttribute : Attribute;

/// <summary>
/// What destroying an entity does to the links to it, in every world (<see cref="TargetPolicy"/>): on a relation
/// (<see cref="IRelation"/>) or a component indexed by an <see cref="EntityId"/>. A world holds the rule from its first
/// table holding the component (or the creation of its index), so the first destroy follows it; a world's own
/// <c>WhenTargetDestroyed</c> call on the index replaces it.
/// </summary>
/// <param name="policy">The policy every world starts with.</param>
[AttributeUsage(AttributeTargets.Struct)]
public sealed class WhenTargetDestroyedAttribute(TargetPolicy policy) : Attribute
{
    /// <summary>The policy every world starts with.</summary>
    public TargetPolicy Policy { get; } = policy;
}

/// <summary>
/// <see cref="GroupAttribute">[Group]</see>, and every world groups the component's tables by this member
/// (<see cref="World.GroupBy{T}"/>) from its first table holding it: a group filter (<see cref="QueryFilter.InGroup{T}"/>)
/// then visits that group's tables only, in every world, with no setup call to forget.
/// <c>public partial record struct Zone([GroupBy] int Id) : IComponent;</c> On the <c>Group</c> property of a
/// hand-written <see cref="IGroup"/>, it only makes every world group by it.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class GroupByAttribute : Attribute;

/// <summary>
/// A component or singleton no save holds: runtime state (a connection, a cache, a handle to a service) that a
/// restart rebuilds. Every save leaves it out (<see cref="World.Save(System.Buffers.IBufferWriter{byte})"/>,
/// <see cref="World.SaveWithout"/>, the saves of entities, even a set that names it), so it needs no Nino formatter,
/// and a load keeps the world's own value of a singleton (as <see cref="World.LoadKeeping"/> does). Snapshots
/// (<see cref="World.Capture()"/>) and moves between worlds (<see cref="World.MoveEntities"/>) copy it as any other.
/// </summary>
[AttributeUsage(AttributeTargets.Struct)]
public sealed class NotSavedAttribute : Attribute;

/// <summary>
/// A view of one entity's components: a <c>ref struct</c> whose instance fields are <c>ref</c> (written),
/// <c>ref readonly</c> (read) or <see cref="Mut{T}"/> (written, stamped only where written) fields of components, public
/// or internal, with the business methods that use them:
/// <c>[View] public ref struct Fighter { public ref Health Health; public ref readonly Armor Armor; public void TakeDamage(int n) =&gt; Health.Value -= Math.Max(0, n - Armor.Value); }</c>.
/// A <c>[ForEach]</c> or <c>[ForEntity]</c> method takes it by value (<c>static void Hit(Fighter fighter, in Damage damage)</c>):
/// the method visits the entities that have every component of the view, its fields read and write as its own
/// <c>ref</c> / <c>in</c> parameters would (the schedule, the change ticks of tracked components and the vector loop alike),
/// and the generated job builds the view over the row at no cost. The generator also writes <c>entity.AsFighter()</c>
/// and <c>entity.TryGetFighter(out fighter)</c>. A view holds refs into the entity's row, so a structural change of that
/// entity (or any change that moves rows) ends its use.
/// </summary>
[AttributeUsage(AttributeTargets.Struct)]
public sealed class ViewAttribute : Attribute;

/// <summary>
/// A <see cref="EasyEcs.Prefab"/> declared as the components its entities start with: a partial struct with one field per
/// component, each field's initializer its default value:
/// <c>[Prefab] public partial struct Goblin { public Health Health = new(30); public Position Position; }</c>. The
/// generator writes <c>Goblin.Prefab</c>, a static <see cref="EasyEcs.Prefab"/> built once from the defaults, and the
/// parameterless constructor field initializers need. <c>world.Instantiate(Goblin.Prefab)</c> creates a goblin (values
/// given to <c>Instantiate</c> replace the defaults, a span gets many), <c>new Prefab(Goblin.Prefab)</c> is a variant to
/// change (a buffer, a member). The declaration names no kind a query could find: give it a tag component to filter on.
/// </summary>
[AttributeUsage(AttributeTargets.Struct)]
public sealed class PrefabAttribute : Attribute;
