namespace EasyEcs;

/// <summary>
/// A recipe for entities: a set of component values that belongs to no world, so no query ever sees it.
/// <see cref="World.Instantiate(Prefab)"/> creates an entity with a copy of every value, in any world. Build it once
/// (from code or configuration) and keep it; one prefab may serve worlds on several threads as long as nobody changes
/// it meanwhile. An <see cref="Entity"/> field in a prefab names no entity of the worlds it is instantiated into: set
/// relations after instantiating.
/// </summary>
public sealed class Prefab
{
    // One row in a world of its own: instantiating is the row copy every table already knows.
    private readonly World _store = new(new WorldOptions { InitialEntityCapacity = 1 });
    internal readonly Entity Row;

    public Prefab() => Row = _store.CreateEntity();

    /// <summary>Add <paramref name="value"/>, or replace the value of its type. Returns this prefab (chain calls).</summary>
    public Prefab Set<T>(in T value) where T : struct, IComponent
    {
        Row.Set(in value);
        return this;
    }

    /// <summary>Drop component <typeparamref name="T"/> if the prefab has it. Returns this prefab.</summary>
    public Prefab Remove<T>() where T : struct, IComponent
    {
        if (Row.Has<T>()) Row.Remove<T>();
        return this;
    }

    /// <summary>Whether the prefab has component <typeparamref name="T"/>.</summary>
    public bool Has<T>() where T : struct, IComponent => Row.Has<T>();

    /// <summary>The prefab's value of <typeparamref name="T"/>; throws if it has none (change it with <see cref="Set{T}"/>).</summary>
    public ref readonly T Read<T>() where T : struct, IComponent => ref Row.Read<T>();
}
