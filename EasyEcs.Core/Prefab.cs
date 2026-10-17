namespace EasyEcs;

/// <summary>
/// A recipe for entities: a set of component values that belongs to no world, so no query ever sees it.
/// <see cref="World.Instantiate(Prefab)"/> creates an entity with a copy of every value, in any world. Build it once
/// (from code or configuration) and keep it; one prefab may serve worlds on several threads as long as nobody changes
/// it meanwhile. An <see cref="Entity"/> field in a prefab names no entity of the worlds it is instantiated into: set
/// relations after instantiating.
/// </summary>
public sealed partial class Prefab
{
    // One row in a world of its own: instantiating is the row copy every table already knows.
    private readonly World _store = new(new WorldOptions { InitialEntityCapacity = 1 });
    internal readonly Entity Row;

    public Prefab() => Row = _store.CreateEntity();

    /// <summary>
    /// A prefab with a copy of every value of <paramref name="basePrefab"/>, to change on its own: a variant of a
    /// configured NPC. Later changes of either prefab do not reach the other (a copy, no inheritance). O(components).
    /// </summary>
    public Prefab(Prefab basePrefab) => Row = _store.Instantiate(basePrefab);

    // Set(in T1..Tn) and Remove<T1..Tn>() are generated (Entity.Structural.g.cs), and ComponentGenerator writes the
    // named members of each component: prefab.SetHealth(…), HasHealth, Health (read-only), RemoveHealth(), IsDead.

    /// <summary>Whether the prefab has component <typeparamref name="T"/>.</summary>
    public bool Has<T>() where T : struct, IComponent => Row.Has<T>();

    /// <summary>The prefab's value of <typeparamref name="T"/>; throws if it has none (change it with <c>Set</c>).</summary>
    public ref readonly T Read<T>() where T : struct, IComponent => ref Row.Read<T>();
}
