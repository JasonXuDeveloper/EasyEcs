using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using Nino.Core;

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
internal static class TypeIndex<TFamily, T> where T : allows ref struct
{
    internal static readonly int Value = Interlocked.Increment(ref TypeIndex<TFamily>.Count) - 1;
}

/// <summary>
/// Family of the generic signatures (T1, ..., Tn) of multi-component Set / CreateEntity calls: their dense
/// index keys the transition caches of each archetype (Archetype.GetAddSetEdge).
/// </summary>
internal readonly struct ComponentSignature;

/// <summary>Family of the job types whose passes run one query (a job's OneQuery): their dense index keys World._kept.</summary>
internal readonly struct KeptQuery;

/// <summary>Bit of component type <typeparamref name="T"/> in archetype masks (registers its <see cref="ComponentInfo"/>).</summary>
internal static class ComponentType<T> where T : struct, IComponent
{
    internal static readonly ushort Bit = Register();

    /// <summary>
    /// Bit of the hidden change-tick column of a tracked <typeparamref name="T"/> (<see cref="ITracked"/>); not
    /// meaningful for other components. Archetypes having <typeparamref name="T"/> always have it.
    /// </summary>
    internal static readonly ushort TicksBit = (ushort)ComponentInfo.Of(Bit).Companion;

    /// <summary>
    /// <typeparamref name="T"/> implements <see cref="IIndexed{TKey}"/>: writes of a value (Set, CreateEntity) tell its
    /// world's index. A static readonly bool, so Tier1 code folds the test away for every other component.
    /// </summary>
    internal static readonly bool Indexed = default(T) is IIndexed || default(T) is IBuffer buffer && buffer.Relational; // a buffer of relations: by its targets

    /// <summary>
    /// <typeparamref name="T"/> implements <see cref="IUniqueIndexed{TKey}"/>: a write claims its key first
    /// (<see cref="World.Claim{T}"/>), folded like <see cref="Indexed"/>.
    /// </summary>
    internal static readonly bool Unique = default(T) is IUniqueIndexed;

    /// <summary><typeparamref name="T"/> implements <see cref="IGroup"/>: a written value may move its row (folded like <see cref="Indexed"/>).</summary>
    internal static readonly bool Grouped = default(T) is IGroup;

    /// <summary>Writes of a value (Set, CreateEntity) are told to the world (<see cref="World.Written{T}"/>).</summary>
    internal static readonly bool Watched = Indexed || Grouped;

    private static ushort Register()
    {
        if (default(T) is IDisabledTag tag) return ComponentInfo.DisablerOf(tag.Component); // Disabled<U>: U's tag bit
        int index = ComponentInfo.Allocate(new ComponentInfo<T>());
        Collectible.Watch(typeof(T)); // its bit is released when its context unloads
        TypeRegistry.Component<T>(); // a type the process uses can be loaded
        // A tracked component brings a hidden column of change ticks into every archetype that has it: a RowTick
        // column with a bit of its own, described by the component's type so it is released with it. Not a generic
        // RowTicks<T> registered through ComponentType: the AOT compiler cannot see that such a registration stops
        // (ComponentType<RowTicks<RowTicks<...>>>) and warns IL3054 for every program with a tracked component.
        if (default(T) is ITracked) ComponentInfo.Pair(index, ComponentInfo.Allocate(new ComponentInfo<RowTick>(typeof(T))));
        return (ushort)index;
    }
}

/// <summary>Slot of singleton type <typeparamref name="T"/> in every world.</summary>
internal static class SingletonType<T> where T : struct, ISingletonComponent
{
    internal static readonly int Index = Register();

    private static int Register()
    {
        Collectible.Watch(typeof(T)); // its save id is forgotten when its context unloads
        TypeRegistry.Singleton<T>(); // a type the process uses can be loaded
        int index = TypeIndex<ISingletonComponent, T>.Value;
        SingletonNames.Set(index, ScheduleInfo.NameOf(typeof(T)));
        return index;
    }
}

/// <summary>
/// The names of the singleton types by slot, for reports (<see cref="World.DescribeSchedule"/>): names, not types, so an
/// unloaded plugin's types are not kept.
/// </summary>
internal static class SingletonNames
{
    private static string[] _names = [];
    private static readonly Lock Gate = new();

    public static void Set(int slot, string name)
    {
        lock (Gate)
        {
            var names = _names;
            if (slot >= names.Length) Array.Resize(ref names, Math.Max(slot + 1, names.Length * 2));
            names[slot] = name;
            Volatile.Write(ref _names, names);
        }
    }

    public static string Of(int slot)
    {
        var names = Volatile.Read(ref _names);
        return slot < names.Length && names[slot] != null ? names[slot] : "singleton " + slot;
    }
}

/// <summary>A singleton box without its type: copied by snapshots (<see cref="World.Capture()"/>).</summary>
internal abstract class SingletonBox
{
    /// <summary>The singleton's slot in every world (<see cref="SingletonType{T}.Index"/>).</summary>
    public abstract int Slot { get; }

    /// <summary>The name of the type in messages (<see cref="TypeRegistry.NameOf{T}"/>).</summary>
    public abstract string Name { get; }

    /// <summary>The id a save records the type by (<see cref="TypeRegistry.IdOf"/>).</summary>
    public abstract int TypeId { get; }

    /// <summary>Write the value (Nino).</summary>
    public abstract void Write(ref Writer writer);

    /// <summary>Read the value written by <see cref="Write"/>.</summary>
    public abstract void Read(ref Reader reader);

    public abstract SingletonBox Clone();

    /// <summary>Copy the value of <paramref name="source"/>, a box of the same type.</summary>
    public abstract void CopyFrom(SingletonBox source);

    /// <summary>Reset the value to default (a load then reads into an empty value).</summary>
    public abstract void Clear();

    /// <summary>Whether the type is [NotSaved]: no save writes it, a load keeps the world's own (<see cref="TypeRegistry.NotSaved{T}"/>).</summary>
    public abstract bool NotSaved { get; }
}

/// <summary>Heap box holding one world's singleton value; refs into it stay valid for the box lifetime.</summary>
internal sealed class SingletonBox<T> : SingletonBox where T : struct, ISingletonComponent
{
    public T Value;

    public override int Slot => SingletonType<T>.Index;

    public override string Name => TypeRegistry.NameOf<T>.Value;

    public override int TypeId => TypeRegistry.TypeId<T>.Value;

    // A singleton holding a list (Emitted<T>: its only field is a DynamicBuffer, whose only field is its store) is
    // copied, saved and loaded through the list's elements, as a buffer column is (ComponentInfo); null for others.
    private static readonly IBuffer Buffer = default(T) is IBuffer ? (IBuffer)default(T) : null;

    private ref BufferStore Store => ref Unsafe.As<T, BufferStore>(ref Value);

    public override void Write(ref Writer writer)
    {
        if (default(T) is IBuffer) Buffer.Write(ref writer, Store); // a JIT-time constant
        else NinoSerializer.Serialize(Value, ref writer);
    }

    public override void Read(ref Reader reader)
    {
        if (default(T) is IBuffer) Store = Buffer.Read(ref reader, Store);
        else NinoDeserializer.Deserialize(ref Value, ref reader);
    }

    public override SingletonBox Clone()
    {
        var copy = new SingletonBox<T>();
        copy.CopyFrom(this);
        return copy;
    }

    public override void CopyFrom(SingletonBox source)
    {
        var from = (SingletonBox<T>)source;
        if (default(T) is IBuffer) // the elements, not the list
        {
            // A world has one of each singleton: it keeps its list, emptied, when the copy has no elements (an
            // Emitted<T> restored to a capture taken after its records were cleared), so the next passes' records need
            // no new list. Rows give theirs back to the pool instead (BufferStore.Assign): a table's rows come and go.
            if (Store == null) Store = from.Store?.Copy(); // a copy, never shared: Emit writes the list without a test
            else if (!Store.CopyFrom(from.Store)) Store.Empty();
        }
        else Value = from.Value;
    }

    public override void Clear()
    {
        if (default(T) is IBuffer && Store is { } store)
        {
            Store = null;
            store.Recycle();
        }

        Value = default;
    }

    public override bool NotSaved => NotSavedOf<T>.Value;
}

/// <summary>Whether <typeparamref name="T"/> (a component or a singleton) is [NotSaved]: set when its module loads (<see cref="TypeRegistry.NotSaved{T}"/>).</summary>
internal static class NotSavedOf<T> where T : struct
{
    public static bool Value;
}
