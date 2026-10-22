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
/// The key a world indexes the component by (<see cref="IIndexed{TKey}"/>): on a field, a property or a record's
/// positional parameter, the key is that member (<c>public partial struct Owner : IComponent { [Key] public EntityId Of; }</c>);
/// on the struct itself, the key is the whole value. The generator adds <c>IIndexed&lt;TKey&gt;</c> to the partial
/// struct (unless an interface the struct declares brings it) with an explicit <c>Key</c> returning the member. One key
/// per component; a key computed from several members is written by hand (<c>public (int, int) Key => (X, Y);</c>).
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Parameter | AttributeTargets.Struct)]
public sealed class KeyAttribute : Attribute;

/// <summary>
/// <see cref="KeyAttribute"/> for a key that names one entity (<see cref="IUniqueIndexed{TKey}"/>): the generator adds
/// <c>IUniqueIndexed&lt;TKey&gt;</c> with the explicit <c>Key</c>. A struct that declares
/// <see cref="IDirectIndexed{TKey}"/> keeps its own <c>Slots</c> and <c>SlotOf</c>; only <c>Key</c> is generated.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Parameter | AttributeTargets.Struct)]
public sealed class UniqueKeyAttribute : Attribute;

/// <summary>
/// The member a world groups the component's tables by (<see cref="IGroup"/>): an <c>int</c> or an enum of
/// <c>int</c>. The generator adds <c>IGroup</c> to the partial struct with an explicit <c>Group</c> returning it.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class GroupAttribute : Attribute;
