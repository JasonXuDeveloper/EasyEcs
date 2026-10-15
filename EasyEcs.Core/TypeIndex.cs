using System;
using System.Threading;
namespace EasyEcs;

/// <summary>
/// Process-wide dense index per type within a family (component types, singleton types). Assigned on first use,
/// thread-safe (static initialization), and read as a static readonly field (constant-folded by the JIT).
/// </summary>
internal static class TypeIndex<TFamily>
{
    internal static int Count;
}

/// <inheritdoc cref="TypeIndex{TFamily}"/>
internal static class TypeIndex<TFamily, T>
{
    internal static readonly int Value = Interlocked.Increment(ref TypeIndex<TFamily>.Count) - 1;
}

/// <summary>
/// Family of the generic signatures (T1, ..., Tn) of multi-component Set / CreateEntity calls: their dense
/// index keys the transition caches of each archetype (Archetype.GetAddSetEdge).
/// </summary>
internal readonly struct ComponentSignature;

/// <summary>
/// The hidden column of a tracked component <typeparamref name="T"/> (<see cref="ITrackChanges"/>): the
/// <see cref="World.ChangeTick"/> of each row's last write. Archetypes having <typeparamref name="T"/> always have it.
/// </summary>
internal struct ChangeTicks<T> : IComponent where T : struct, IComponent
{
    public ulong Tick;
}

/// <summary>Bit of component type <typeparamref name="T"/> in archetype masks (registers its <see cref="ComponentInfo"/>).</summary>
internal static class ComponentType<T> where T : struct, IComponent
{
    internal static readonly ushort Bit = Register();

    private static ushort Register()
    {
        int index = TypeIndex<IComponent, T>.Value;
        if (index > ushort.MaxValue)
            throw new InvalidOperationException($"Too many component types (max {ushort.MaxValue + 1})");
        var info = new ComponentInfo<T>();
        ComponentInfo.Register(index, info);
        // A tracked component brings a hidden column of change ticks into every archetype that has it.
        if (default(T) is ITrackChanges) ComponentInfo.Pair(index, ComponentType<ChangeTicks<T>>.Bit);
        return (ushort)index;
    }
}

/// <summary>Slot of singleton type <typeparamref name="T"/> in every world.</summary>
internal static class SingletonType<T> where T : struct, ISingletonComponent
{
    internal static readonly int Index = TypeIndex<ISingletonComponent, T>.Value;
}

/// <summary>A singleton box without its type: copied by snapshots (<see cref="World.Capture"/>).</summary>
internal abstract class SingletonBox
{
    public abstract SingletonBox Clone();

    /// <summary>Copy the value of <paramref name="source"/>, a box of the same type.</summary>
    public abstract void CopyFrom(SingletonBox source);
}

/// <summary>Heap box holding one world's singleton value; refs into it stay valid for the box lifetime.</summary>
internal sealed class SingletonBox<T> : SingletonBox where T : struct, ISingletonComponent
{
    public T Value;

    public override SingletonBox Clone() => new SingletonBox<T> { Value = Value };

    public override void CopyFrom(SingletonBox source) => Value = ((SingletonBox<T>)source).Value;
}
