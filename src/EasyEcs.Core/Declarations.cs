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
/// On the struct itself, the attribute is every field's that carries none of its own:
/// <c>[Sum] public partial record struct Stats(long Kills, long Deaths, [Max] long Longest) : ISingletonComponent;</c>.
/// The same holds for each of the six attributes.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Parameter | AttributeTargets.Struct)]
public sealed class SumAttribute : Attribute;

/// <summary>
/// A field of a reduction that combines by multiplication: <c>Identity</c> 1, <c>Combine</c> <c>left * right</c>. A
/// numeric field (see <see cref="SumAttribute"/>).
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Parameter | AttributeTargets.Struct)]
public sealed class ProductAttribute : Attribute;

/// <summary>
/// A field of a reduction that keeps the smaller value: <c>Identity</c> the type's <c>MaxValue</c> (+∞ for
/// <c>float</c> and <c>double</c>), <c>Combine</c> <c>Math.Min(left, right)</c>. A numeric field (see
/// <see cref="SumAttribute"/>).
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Parameter | AttributeTargets.Struct)]
public sealed class MinAttribute : Attribute;

/// <summary>
/// A field of a reduction that keeps the larger value: <c>Identity</c> the type's <c>MinValue</c> (-∞ for
/// <c>float</c> and <c>double</c>), <c>Combine</c> <c>Math.Max(left, right)</c>. A numeric field (see
/// <see cref="SumAttribute"/>).
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Parameter | AttributeTargets.Struct)]
public sealed class MaxAttribute : Attribute;

/// <summary>
/// A <c>bool</c> field of a reduction that holds when it holds on every row: <c>Identity</c> <c>true</c>,
/// <c>Combine</c> <c>left &amp; right</c> (see <see cref="SumAttribute"/>).
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Parameter | AttributeTargets.Struct)]
public sealed class AllAttribute : Attribute;

/// <summary>
/// A <c>bool</c> field of a reduction that holds when it holds on some row: <c>Identity</c> <c>false</c>,
/// <c>Combine</c> <c>left | right</c> (see <see cref="SumAttribute"/>).
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Parameter | AttributeTargets.Struct)]
public sealed class AnyAttribute : Attribute;

/// <summary>
/// The key of a reduction that keeps the row with the lowest key (MPI's <c>MINLOC</c>, LINQ's <c>MinBy</c>): the other
/// fields travel with it. On one numeric field, auto-property with a setter or record's positional parameter (an integer,
/// <c>float</c>, <c>double</c>, <c>decimal</c>) of a partial struct:
/// <c>public partial record struct Weakest(EntityId Player, [MinBy] int Health) : ISingletonComponent;</c>. The generator
/// writes the struct's <c>IReduction&lt;T&gt;</c>: <c>Identity</c> holds the key's <c>MaxValue</c> (+∞ for <c>float</c>
/// and <c>double</c>) and every other field at its default, <c>Combine</c> keeps <c>right</c> only when its key is lower
/// (<c>right.Health &lt; left.Health ? right : left</c>). A tie keeps the left side, the earlier rows, so the result is
/// the same on any number of threads; a NaN key never replaces another. The method keeps the lowest of its chunk the same
/// way: <c>if (hp.Value &lt; weakest.Value.Health) weakest.Value = new(player.Id, hp.Value);</c>. One field of the struct
/// carries <see cref="MinByAttribute"/> or <see cref="MaxByAttribute"/>, and no field another reduction attribute.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class MinByAttribute : Attribute;

/// <summary>
/// The key of a reduction that keeps the row with the highest key (MPI's <c>MAXLOC</c>, LINQ's <c>MaxBy</c>):
/// <c>Identity</c> holds the key's <c>MinValue</c> (−∞ for <c>float</c> and <c>double</c>), <c>Combine</c> keeps
/// <c>right</c> only when its key is higher, so a tie keeps the earlier rows (see <see cref="MinByAttribute"/>).
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class MaxByAttribute : Attribute;

/// <summary>
/// A reduction singleton (<see cref="IReduction{T}"/>) that holds a total across passes: a pass with a
/// <see cref="Reduce{T}"/> parameter combines its result into the world's value (<c>Combine(world's value, pass's
/// result)</c>: the earlier rows first) instead of replacing it. Without it, each pass's result replaces the value.
/// <c>[Accumulate, Sum] public partial record struct Stats(long Kills, long Hits) : ISingletonComponent;</c>, then
/// <c>static void Die(in Health h, ref Reduce&lt;Stats&gt; stats) { if (h.Value &lt;= 0) stats.Value.Kills++; }</c>
/// adds the pass's kills to the run's total, on one thread or several, with no singleton of the pass's own and no
/// system that adds it to the total. A world without the singleton starts it at <c>Identity</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Struct)]
public sealed class AccumulateAttribute : Attribute;

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
/// <c>IUniqueIndexed&lt;TKey&gt;</c> with the explicit <c>Key</c>. With <see cref="SlotsPerAxis"/>, the index is
/// direct-mapped over a window of the key's coordinates (<see cref="IDirectMapped{TKey}"/>); a struct that declares
/// <see cref="IDirectMapped{TKey}"/> without it keeps its own <c>Slots</c> and <c>SlotOf</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Parameter | AttributeTargets.Struct)]
public sealed class UniqueKeyAttribute : Attribute
{
    /// <summary>
    /// The side of the window of keys the index maps to slots directly, a power of two (0, the default: a hashed index
    /// only). The generator adds <see cref="IDirectMapped{TKey}"/>: <c>Slots</c> is the side to the power of the key's
    /// axes, <c>SlotOf</c> each coordinate masked by side − 1, row-major (x fastest, then z, then y), so the keys of any
    /// window of that side get distinct slots, negative coordinates included. The key is an <c>int</c> (one axis), a
    /// struct with integer members <c>X</c> and <c>Z</c> (and <c>Y</c>), or a tuple (<c>Item1</c> fastest):
    /// <c>public partial record struct Chunk([UniqueKey(SlotsPerAxis = 64)] ChunkPos Pos) : IComponent;</c> writes
    /// <c>Slots =&gt; 4096</c> and <c>SlotOf(pos) =&gt; (pos.Z &amp; 63) * 64 + (pos.X &amp; 63)</c>.
    /// </summary>
    public int SlotsPerAxis { get; set; }
}

/// <summary>
/// A tile of a grid (<see cref="ITile{TKey}"/>) declared by its shape: the generator adds <c>ITile&lt;TKey&gt;</c> to the
/// partial component, its key the type of the member marked <see cref="LookupKeyAttribute">[LookupKey]</see> or
/// <see cref="UniqueKeyAttribute">[UniqueKey]</see>, with the shape's static members (<see cref="ITileShape"/>) from the
/// properties set here and <c>Beside</c> from the key:
/// <c>[Tile(Width = 16, Depth = 16)] public partial record struct HeatPlate([LookupKey] PlatePos Pos) : IComponent;</c>.
/// <c>Beside</c> steps an <c>int</c> key by one along x (west −1, east +1), and a key with integer members <c>X</c> and
/// <c>Z</c> (and <c>Y</c>, the layers) along the axis of the face, as <see cref="TileFaces"/> names them: north −z, south
/// +z, west −x, east +x, below −y, above +y. A key of another shape, or a grid whose <c>Beside</c> wraps (a torus), writes
/// <c>Beside</c> by hand; the generator then writes the rest. A type that declares <c>ITile&lt;TKey&gt;</c> itself gets
/// the same inferred <c>Beside</c> when it writes none.
/// </summary>
[AttributeUsage(AttributeTargets.Struct)]
public sealed class TileAttribute : Attribute
{
    /// <summary>The cells of a row (x), 1 or more (<see cref="ITileShape.Width"/>). Required.</summary>
    public int Width { get; set; }

    /// <summary>The rows of a layer (z), 1 to 64 (<see cref="ITileShape.Depth"/>). Required.</summary>
    public int Depth { get; set; }

    /// <summary>The layers (y), 1 or more (<see cref="ITileShape.Height"/>, default 1: a 2D tile).</summary>
    public int Height { get; set; } = 1;

    /// <summary>The faces that have neighbors (<see cref="ITileShape.Faces"/>, default <see cref="TileFaces.Sides"/>).</summary>
    public TileFaces Faces { get; set; } = TileFaces.Sides;

    /// <summary>The width of the halo in cells (<see cref="ITileShape.Halo"/>, default 1).</summary>
    public int Halo { get; set; } = 1;

    /// <summary>Which tiles around are neighbors (<see cref="ITileShape.Neighborhood"/>, default <see cref="TileNeighborhood.Faces"/>).</summary>
    public TileNeighborhood Neighborhood { get; set; } = TileNeighborhood.Faces;

    /// <summary>What the halo holds where there is no neighbor (<see cref="ITileShape.Boundary"/>, default <see cref="TileBoundary.Border"/>).</summary>
    public TileBoundary Boundary { get; set; } = TileBoundary.Border;
}

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
/// The key every world keeps the component's tables ordered by, and the point the boxes of their clusters cover. A table
/// needs no declaration to be ordered for a pair method: the generator infers the order from the method's distance
/// test (the Morton code of the position by the cutoff, <see cref="World.OrderRows{T, TOrder}"/>). <c>[OrderBy]</c>
/// overrides that order for every world: another key, a non-spatial order. On a member of a partial component (a field, a property or a record's positional parameter) whose type is an integer,
/// <c>char</c>, <c>bool</c>, <c>float</c>, <c>double</c> or an enum. A spatial order is a Morton code of a quantized
/// position the component computes, and the box axes name its coordinates:
/// <c>public partial record struct Position(float X, float Y) : IComponent { [OrderBy(nameof(X), nameof(Y))] public readonly uint Cell => Map.Morton(X, Y); }</c>
/// (<c>Map.Morton</c> standing for the game's own quantization).
/// The generator adds <see cref="IOrderKey{TKey}"/> with an explicit <c>OrderKey</c> returning the member and
/// <c>BoxPoint</c> returning the axes, and registers the order: every world re-sorts each table holding the component
/// every <see cref="Interval"/> updates (from the first update after the table appears), keeping the rows of the
/// table in key order and a box per cluster of rows (8, or 4 on ARM64) over the axes (<see cref="RowClusters"/>). Rows that join between
/// sorts wait at the end of their table; a row that moves into a cluster widens its box. A table that holds two ordered
/// components is ordered by the one with the lower bit.
/// </summary>
/// <param name="box">
/// The members the clusters' boxes cover, up to three: <c>float</c> fields or properties of the component, or one
/// <see cref="System.Numerics.Vector2"/> or <see cref="System.Numerics.Vector3"/>. None: rows are ordered, the clusters
/// have no box.
/// </param>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class OrderByAttribute(params string[] box) : Attribute
{
    /// <summary>The members the clusters' boxes cover.</summary>
    public string[] Box { get; } = box;

    /// <summary>
    /// <see cref="World.Update()"/> calls between sorts (default 10, at least 1): GROMACS rebuilds its pair lists every
    /// 10 steps, and a sort's cost spread over 10 updates is a small share of a tick (decision D137).
    /// </summary>
    public int Interval { get; set; } = 10;

    /// <summary>
    /// How far a row may move between sorts and stay inside its cluster's box: every box is widened by it on each side
    /// (the buffer of GROMACS's pair lists). 0 by default: a search that must find every row within a range adds the
    /// distance rows move per update times <see cref="Interval"/>.
    /// </summary>
    public float Margin { get; set; }
}

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
/// A <c>[ForEach]</c> or <c>[EntityMethod]</c> method takes it by value (<c>static void Hit(Fighter fighter, in Damage damage)</c>):
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
