using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace EasyEcs;

/// <summary>
/// Process-wide description of a component type, indexed by its bit: element size and the typed column operations
/// archetypes need without knowing <c>T</c> (create, grow, copy a managed element, clear). Registered once when
/// <see cref="ComponentType{T}"/> assigns the bit; read lock-free (copy-on-write array).
/// </summary>
internal abstract class ComponentInfo
{
    private static ComponentInfo[] _all = new ComponentInfo[64];
    private static readonly Lock RegisterLock = new();

    /// <summary>Element size in bytes, or -1 when the type holds managed references (copied through the typed path).</summary>
    public readonly int Size;

    protected ComponentInfo(int size)
    {
        Size = size;
    }

    /// <summary>Bit of the change-tick column of a tracked component (<see cref="ITrackChanges"/>), or -1.</summary>
    public int Companion { get; private set; } = -1;

    /// <summary>For a change-tick column: bit of the tracked component it belongs to, or -1.</summary>
    public int Owner { get; private set; } = -1;

    /// <summary>Pair tracked component <paramref name="owner"/> with its change-tick column <paramref name="companion"/>.</summary>
    public static void Pair(int owner, int companion)
    {
        Of(owner).Companion = companion;
        Of(companion).Owner = owner;
        _anyTracked = true;
    }

    /// <summary>Bit of <see cref="Disabled{T}"/> of this component, once an entity disabled it; else -1.</summary>
    public int Disabler { get; private set; } = -1;

    /// <summary>For a <see cref="Disabled{T}"/> tag: bit of the component it disables, or -1.</summary>
    public int DisabledOf { get; private set; } = -1;

    /// <summary>Link component <paramref name="component"/> with its <see cref="Disabled{T}"/> tag <paramref name="tag"/>.</summary>
    public static void LinkDisabled(int component, int tag)
    {
        Of(component).Disabler = tag;
        Of(tag).DisabledOf = component;
    }

    /// <summary>Whether any tracked component type was registered (archetype masks then get companion bits).</summary>
    public static bool AnyTracked => _anyTracked;

    private static volatile bool _anyTracked;

    /// <summary>Whether the type holds managed references (cleared when its row dies, copied by type).</summary>
    public bool Managed => Size < 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ComponentInfo Of(int bit) => Volatile.Read(ref _all)[bit];

    internal static void Register(int bit, ComponentInfo info)
    {
        lock (RegisterLock)
        {
            var all = _all;
            if (bit >= all.Length)
            {
                var grown = new ComponentInfo[Math.Max(all.Length * 2, bit + 1)];
                Array.Copy(all, grown, all.Length);
                all = grown;
            }

            all[bit] = info;
            Volatile.Write(ref _all, all);
        }
    }

    /// <summary>The component type (error messages).</summary>
    public abstract Type Type { get; }

    public abstract Array Create(int capacity);

    public abstract Array Grow(Array column, int capacity);

    public abstract void CopyManaged(Array source, int sourceSlot, Array target, int targetSlot);

    /// <summary>
    /// Copy a managed element into another entity (<see cref="World.Instantiate(Prefab)"/>, <see cref="Entity.Clone"/>): like
    /// <see cref="CopyManaged"/>, but a <see cref="DynamicBuffer{T}"/> gets a list of its own.
    /// </summary>
    public abstract void CloneManaged(Array source, int sourceSlot, Array target, int targetSlot);

    public abstract void Clear(Array column, int start, int count);

    /// <summary>
    /// Copy rows [0, <paramref name="count"/>) of a managed column for a snapshot (capture or restore): values, and for
    /// <see cref="DynamicBuffer{T}"/> columns copies of their lists, so the two sides never share one.
    /// </summary>
    public abstract void CopyRows(Array source, Array target, int count);
}

internal sealed class ComponentInfo<T>() : ComponentInfo(RuntimeHelpers.IsReferenceOrContainsReferences<T>() ? -1 : Unsafe.SizeOf<T>())
    where T : struct, IComponent
{
    // Unmanaged columns are pinned byte arrays (pinned object heap, never moved by the GC) with one cache line of
    // slack, so their rows can start on a 64-byte boundary (Archetype.Aligned): SIMD loads and power-of-two sized
    // components then never straddle two lines. Managed columns are plain T[] (the GC must see their references).
    public override Type Type => typeof(T);

    public override Array Create(int capacity) =>
        Managed ? new T[capacity] : GC.AllocateArray<byte>(capacity * Size + Archetype.CacheLine, pinned: true);

    public override Array Grow(Array column, int capacity)
    {
        if (Managed)
        {
            var typed = Unsafe.As<T[]>(column);
            Array.Resize(ref typed, capacity);
            return typed;
        }

        var grown = Create(capacity);
        int bytes = Unsafe.As<byte[]>(column).Length - Archetype.CacheLine; // the old capacity in bytes
        Unsafe.CopyBlockUnaligned(ref Archetype.Aligned(grown), ref Archetype.Aligned(column), (uint)bytes);
        return grown;
    }

    public override void CopyManaged(Array source, int sourceSlot, Array target, int targetSlot) =>
        Unsafe.As<T[]>(target)[targetSlot] = Unsafe.As<T[]>(source)[sourceSlot];

    public override void CloneManaged(Array source, int sourceSlot, Array target, int targetSlot)
    {
        ref T value = ref Unsafe.As<T[]>(target)[targetSlot];
        value = Unsafe.As<T[]>(source)[sourceSlot];
        if (default(T) is not IBuffer) return; // a JIT-time constant
        ref var store = ref Unsafe.As<T, BufferStore>(ref value); // a DynamicBuffer's single field (see CopyRows)
        store = store?.Clone();
    }

    public override void Clear(Array column, int start, int count) => Array.Clear(column, start, count);

    public override void CopyRows(Array source, Array target, int count)
    {
        Array.Copy(source, target, count);
        if (default(T) is not IBuffer) return; // a JIT-time constant
        // A DynamicBuffer<TElement> has a single field, its BufferStore<TElement>: the value is that reference.
        var rows = Unsafe.As<T[]>(target);
        for (int i = 0; i < count; i++)
        {
            ref var store = ref Unsafe.As<T, BufferStore>(ref rows[i]);
            store = store?.Clone();
        }
    }
}
