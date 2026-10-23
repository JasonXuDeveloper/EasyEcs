using System;

namespace EasyEcs;

/// <summary>
/// The data a system reads and writes, declared once when the system is added. With
/// <see cref="WorldOptions.ParallelSystems"/>, systems with no order between them whose access does not conflict run at once:
/// two systems conflict when one writes a component or singleton the other reads or writes, or when either
/// <see cref="RunAlone">runs alone</see>.
/// <para>
/// The generator declares it for every system with <see cref="ForEachAttribute">[ForEach]</see> methods: a
/// <c>ref</c> parameter is a write, an <c>in</c> parameter a read. A system runs alone when it can reach the world
/// beyond its parameters: an <see cref="Entity"/> or <see cref="World"/> parameter, a <see cref="OnUpdateAttribute">[OnUpdate]</see> method, a <c>[Consume]</c> method, or no <c>[ForEach]</c> method at all.
/// </para>
/// </summary>
public struct SystemAccess
{
    internal BitMask ReadComponents;
    internal BitMask WriteComponents;
    internal BitMask ReadSingletons;
    internal BitMask WriteSingletons;
    internal bool Alone;
    internal Action<World>[] Indexes; // the indexes its key lookups use, built before its parallel stage runs

    /// <summary>The system reads component <typeparamref name="T"/>.</summary>
    public void Read<T>() where T : struct, IComponent => ReadComponents.SetBit(ComponentType<T>.Bit);

    /// <summary>The system writes component <typeparamref name="T"/>.</summary>
    public void Write<T>() where T : struct, IComponent => WriteComponents.SetBit(ComponentType<T>.Bit);

    /// <summary>The system reads singleton <typeparamref name="T"/>.</summary>
    public void ReadSingleton<T>() where T : struct, ISingletonComponent => ReadSingletons.SetBit(SingletonType<T>.Index);

    /// <summary>The system writes singleton <typeparamref name="T"/>.</summary>
    public void WriteSingleton<T>() where T : struct, ISingletonComponent => WriteSingletons.SetBit(SingletonType<T>.Index);

    /// <summary>
    /// The system reads component <typeparamref name="T"/> and finds it by key (<see cref="Lookup{T}"/>,
    /// <see cref="LookupExtensions"/>): before a parallel stage holding the system runs, <typeparamref name="T"/>'s index
    /// is created and built on the updating thread, so no lookup of the stage changes it.
    /// </summary>
    public void Index<T, TKey>() where T : struct, IComponent, IIndexed<TKey> where TKey : IEquatable<TKey>
    {
        Read<T>();
        var prepare = PrepareIndex<T, TKey>.Run;
        if (Indexes == null) Indexes = [prepare];
        else if (Array.IndexOf(Indexes, prepare) < 0) Indexes = [.. Indexes, prepare]; // cold: once per AddSystem
    }

    private static class PrepareIndex<T, TKey> where T : struct, IComponent, IIndexed<TKey> where TKey : IEquatable<TKey>
    {
        public static readonly Action<World> Run = static world => LookupExtensions.Prepared<T, TKey>(world);
    }

    /// <summary>The system may touch anything (structural changes, any component): it never shares a stage.</summary>
    public void RunAlone() => Alone = true;

    /// <summary>True when the two systems may not run at the same time.</summary>
    internal readonly bool ConflictsWith(in SystemAccess other) =>
        Alone || other.Alone
        || WriteComponents.Intersects(other.WriteComponents) || WriteComponents.Intersects(other.ReadComponents)
        || ReadComponents.Intersects(other.WriteComponents)
        || WriteSingletons.Intersects(other.WriteSingletons) || WriteSingletons.Intersects(other.ReadSingletons)
        || ReadSingletons.Intersects(other.WriteSingletons);
}
