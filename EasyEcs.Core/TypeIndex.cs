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

/// <summary>Bit of component type <typeparamref name="T"/> in archetype tags and its column index in every world.</summary>
internal static class ComponentType<T> where T : struct, IComponent
{
    internal static readonly ushort Bit = TypeIndex<IComponent, T>.Value <= ushort.MaxValue
        ? (ushort)TypeIndex<IComponent, T>.Value
        : throw new InvalidOperationException($"Too many component types (max {ushort.MaxValue + 1})");
}

/// <summary>Slot of singleton type <typeparamref name="T"/> in every world.</summary>
internal static class SingletonType<T> where T : struct, ISingletonComponent
{
    internal static readonly int Index = TypeIndex<ISingletonComponent, T>.Value;
}

/// <summary>Slot of event type <typeparamref name="T"/> in every world.</summary>
internal static class EventType<T> where T : struct, IEvent
{
    internal static readonly int Index = TypeIndex<IEvent, T>.Value;
}

/// <summary>Heap box holding one world's singleton value; refs into it stay valid for the box lifetime.</summary>
internal sealed class SingletonBox<T> where T : struct, ISingletonComponent
{
    public T Value;
}
