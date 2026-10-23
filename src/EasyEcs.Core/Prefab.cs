namespace EasyEcs;

/// <summary>
/// A recipe for entities: a set of component values that belongs to no world, so no query ever sees it.
/// <see cref="World.Instantiate(Prefab)"/> creates an entity with a copy of every value, in any world. Build it once
/// (from code or configuration) and keep it; one prefab may serve worlds on several threads as long as nobody changes
/// it meanwhile.
/// <br/>
/// A prefab may have members (<see cref="AddMember"/>): a boss with its minions and its mount. Instantiating any of
/// them creates the whole group, and an <see cref="EntityId"/> field that names a member (<see cref="Id"/>, for
/// example <c>new ChildOf(boss.Id)</c>) names that member's new entity, any other is <see cref="EntityId.Null"/>: ids
/// in a prefab name members only (an id of a world's entity may equal a member's). An <see cref="Entity"/> field is
/// copied as it is: set relations to a world's entities after instantiating.
/// </summary>
public sealed partial class Prefab
{
    // Rows in a world of their own (one per member): instantiating is the row copy every table already knows.
    private readonly World _store;
    internal readonly Entity Row;

    /// <summary>An empty recipe. O(1); allocates its private store once.</summary>
    public Prefab()
    {
        _store = new World(new WorldOptions { InitialEntityCapacity = 1 });
        Row = _store.CreateEntity();
    }

    /// <summary>
    /// A prefab with a copy of every value of <paramref name="basePrefab"/> (and of its members), to change on its own:
    /// a variant of a configured NPC. Later changes of either prefab do not reach the other (a copy, no inheritance).
    /// O(components).
    /// </summary>
    public Prefab(Prefab basePrefab)
    {
        _store = new World(new WorldOptions { InitialEntityCapacity = basePrefab._store.EntityCount });
        Row = _store.Instantiate(basePrefab);
    }

    private Prefab(World store)
    {
        _store = store;
        Row = store.CreateEntity();
    }

    /// <summary>
    /// A new member of this prefab's group, without components: instantiating the prefab or any member creates every
    /// member. O(1).
    /// </summary>
    public Prefab AddMember() => new(_store);

    /// <summary>The id that names this member in the components of the prefab's entities (rewritten on every instance).</summary>
    public EntityId Id => Row.Id;

    /// <summary>The prefab's world: one row per member.</summary>
    internal World Store => _store;

    /// <summary>Whether the prefab has members (an instance is several entities).</summary>
    internal bool IsGroup => _store.EntityCount > 1;

    // Set(in T1..Tn) and Remove<T1..Tn>() are generated (Entity.Structural.g.cs), and ComponentGenerator writes the
    // named members of each component: prefab.SetHealth(…), HasHealth, Health (read-only), RemoveHealth(), IsDead.

    /// <summary>Whether the prefab has component <typeparamref name="T"/>.</summary>
    public bool Has<T>() where T : struct, IComponent => Row.Has<T>();

    /// <summary>The prefab's value of <typeparamref name="T"/>; throws if it has none (change it with <c>Set</c>).</summary>
    public ref readonly T Read<T>() where T : struct, IComponent => ref Row.Read<T>();

    /// <summary>
    /// Gives the prefab a <see cref="DynamicBuffer{T}"/> of <paramref name="elements"/>, replacing the one it has: every
    /// instance gets a list of its own with these elements. Returns the prefab. O(n).
    /// </summary>
    public Prefab SetBuffer<T>(params System.ReadOnlySpan<T> elements) where T : struct
    {
        Row.Set(new DynamicBuffer<T>());
        Row.Get<DynamicBuffer<T>>().AddRange(elements);
        return this;
    }

    /// <summary>
    /// Give the recipe a column for <typeparamref name="T"/> without the component: instances lack it, but their table
    /// has room for it, so adding it later (a tag set and cleared per tick, a state some instances enter) flips a
    /// presence bit instead of moving the row to another table, and every instance stays in one table. A value the
    /// recipe had is dropped. Returns this prefab. O(1).
    /// </summary>
    public Prefab SetAbsent<T>() where T : struct, IComponent
    {
        Row.Set(default(T)); // the column...
        Row.Remove<T>();     // ...its row absent: the table keeps the column
        return this;
    }
}
