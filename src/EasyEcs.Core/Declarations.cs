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
