namespace EasyEcs;

/// <summary>
/// The data a system reads and writes, declared once when the system is added. With
/// <see cref="WorldOptions.Parallel"/>, systems of one priority whose access does not conflict run at the same time:
/// two systems conflict when one writes a component or singleton the other reads or writes, or when either
/// <see cref="RunsAlone">runs alone</see>.
/// <para>
/// The generator declares it for every system with <see cref="ForEachAttribute">[ForEach]</see> methods: a
/// <c>ref</c> parameter is a write, an <c>in</c> parameter a read. A system runs alone when it can reach the world
/// beyond its parameters: an <see cref="Entity"/> or <see cref="World"/> parameter, an instance <c>[ForEach]</c>
/// method, an <see cref="EcsSystem.OnUpdate"/> override, or no <c>[ForEach]</c> method at all.
/// </para>
/// </summary>
public struct SystemAccess
{
    internal Tag ReadComponents;
    internal Tag WriteComponents;
    internal Tag ReadSingletons;
    internal Tag WriteSingletons;
    internal bool Alone;

    /// <summary>The system reads component <typeparamref name="T"/>.</summary>
    public void Read<T>() where T : struct, IComponent => ReadComponents.SetBit(ComponentType<T>.Bit);

    /// <summary>The system writes component <typeparamref name="T"/>.</summary>
    public void Write<T>() where T : struct, IComponent => WriteComponents.SetBit(ComponentType<T>.Bit);

    /// <summary>The system reads singleton <typeparamref name="T"/>.</summary>
    public void ReadSingleton<T>() where T : struct, ISingletonComponent => ReadSingletons.SetBit(SingletonType<T>.Index);

    /// <summary>The system writes singleton <typeparamref name="T"/>.</summary>
    public void WriteSingleton<T>() where T : struct, ISingletonComponent => WriteSingletons.SetBit(SingletonType<T>.Index);

    /// <summary>The system may touch anything (structural changes, any component): it never shares a batch.</summary>
    public void RunsAlone() => Alone = true;

    /// <summary>True when the two systems may not run at the same time.</summary>
    internal readonly bool ConflictsWith(in SystemAccess other) =>
        Alone || other.Alone
        || WriteComponents.Intersects(other.WriteComponents) || WriteComponents.Intersects(other.ReadComponents)
        || ReadComponents.Intersects(other.WriteComponents)
        || WriteSingletons.Intersects(other.WriteSingletons) || WriteSingletons.Intersects(other.ReadSingletons)
        || ReadSingletons.Intersects(other.WriteSingletons);
}
