using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Nino.Core;

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

    /// <summary>Whether the type is an <see cref="ICleanupComponent"/> (kept when its entity is destroyed).</summary>
    public readonly bool Cleanup;

    /// <summary>Whether the type is an <see cref="IGroup"/> (its value may split tables, see <see cref="World.GroupBy{T}"/>).</summary>
    public readonly bool Grouped;

    protected ComponentInfo(int size, bool cleanup, bool grouped) : this(size)
    {
        Cleanup = cleanup;
        Grouped = grouped;
    }

    /// <summary>Whether the type holds managed references (cleared when its row dies, copied by type).</summary>
    public bool Managed => Size < 0;

    /// <summary>Whether the type is a <see cref="DynamicBuffer{T}"/> (its column holds lists).</summary>
    public abstract bool IsBuffer { get; }

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
    /// <see cref="DynamicBuffer{T}"/> columns copies of their lists, so the two sides never share one. With
    /// <paramref name="swap"/> the lists change sides instead (a Load's private snapshot): the target's old lists go to
    /// the source, for the next Load to refill.
    /// </summary>
    public abstract void CopyRows(Array source, Array target, int count, bool swap = false);

    /// <summary>A boxed copy of row <paramref name="slot"/> of <paramref name="column"/> (the debugger view; cold, allocates).</summary>
    public abstract object Box(Array column, int slot);

    /// <summary>Bytes of one row of the column (an element of a managed column: its references, not what they point at).</summary>
    public abstract int Stride { get; }

    /// <summary>The name a save records the type by (<see cref="SavedTypes.NameOf{T}"/>).</summary>
    public abstract string Name { get; }

    /// <summary>Bit of <see cref="Disabled{T}"/> of this component (registered on first use).</summary>
    public abstract ushort DisabledBit { get; }

    /// <summary>
    /// Write rows [<paramref name="start"/>, +<paramref name="count"/>) of managed column <paramref name="column"/> with
    /// Nino (a <see cref="DynamicBuffer{T}"/> as its elements). Unmanaged columns are written as raw rows by the table.
    /// </summary>
    public abstract void Write(ref Writer writer, Array column, int start, int count);

    /// <summary>
    /// Point the <see cref="EntityId"/> fields of rows [<paramref name="start"/>, +<paramref name="count"/>) (and of a
    /// buffer's elements) at the entities a migration created (<see cref="World.LoadEntities"/>).
    /// </summary>
    public abstract void Remap(Array column, int start, int count, IdMap map);

    /// <summary>Write unmanaged rows (<c>count × Size</c> bytes) for a save: canonical when the generator registered the type.</summary>
    public abstract void WriteRows(ref Writer writer, ReadOnlySpan<byte> rows);

    /// <summary>Read unmanaged rows written by <see cref="WriteRows"/>.</summary>
    public abstract void ReadRows(ref Reader reader, Span<byte> rows);

    /// <summary>The layout hash of an unmanaged type the generator registered (<see cref="SavedTypes.Raw{T}"/>), else 0.</summary>
    public abstract int Layout { get; }

    /// <summary>Read <paramref name="count"/> rows written by <see cref="Write"/> into managed column <paramref name="column"/> from <paramref name="start"/>.</summary>
    public abstract void Read(ref Reader reader, Array column, int start, int count);
}

internal sealed class ComponentInfo<T>() : ComponentInfo(RuntimeHelpers.IsReferenceOrContainsReferences<T>() ? -1 : Unsafe.SizeOf<T>(),
    default(T) is ICleanupComponent, default(T) is IGroup)
    where T : struct, IComponent
{
    // Unmanaged columns are pinned byte arrays (pinned object heap, never moved by the GC) with one cache line of
    // slack, so their rows can start on a 64-byte boundary (Archetype.Aligned): SIMD loads and power-of-two sized
    // components then never straddle two lines. Managed columns are plain T[] (the GC must see their references).
    public override Type Type => typeof(T);

    public override string Name => SavedTypes.NameOf<T>.Value;

    public override int Stride => Unsafe.SizeOf<T>();

    public override ushort DisabledBit => DisabledType<T>.Bit;

    // A boxed default buffer (null for other types): reads and writes the elements of a DynamicBuffer<TElement> column
    // without knowing TElement. Boxed once per type.
    private static readonly IBuffer Buffer = default(T) is IBuffer ? (IBuffer)default(T) : null;

    public override bool IsBuffer => default(T) is IBuffer;

    public override void Write(ref Writer writer, Array column, int start, int count)
    {
        var rows = Unsafe.As<T[]>(column);
        if (default(T) is IBuffer) // a JIT-time constant
            for (int i = start; i < start + count; i++) Buffer.Write(ref writer, Unsafe.As<T, BufferStore>(ref rows[i]));
        else
            for (int i = start; i < start + count; i++) NinoSerializer.Serialize(rows[i], ref writer);
    }

    public override void WriteRows(ref Writer writer, ReadOnlySpan<byte> rows) => SavedTypes.WriteRows<T>(ref writer, rows);

    public override void ReadRows(ref Reader reader, Span<byte> rows) => SavedTypes.ReadRows<T>(ref reader, rows);

    public override int Layout => SavedTypes.RawLayout<T>.Hash;

    public override void Remap(Array column, int start, int count, IdMap map)
    {
        if (default(T) is IBuffer) // a JIT-time constant
        {
            var stores = Unsafe.As<T[]>(column);
            for (int i = start; i < start + count; i++) Buffer.Remap(Unsafe.As<T, BufferStore>(ref stores[i]), map);
            return;
        }

        if (SavedTypes.EntityIdOffsets<T>.Value.Length == 0) return;
        ref T row0 = ref Managed ? ref MemoryMarshal.GetArrayDataReference(Unsafe.As<T[]>(column)) : ref Archetype.Row0<T>(column);
        map.Apply(MemoryMarshal.CreateSpan(ref Unsafe.Add(ref row0, start), count));
    }

    public override void Read(ref Reader reader, Array column, int start, int count)
    {
        var rows = Unsafe.As<T[]>(column);
        if (default(T) is IBuffer)
            for (int i = start; i < start + count; i++)
            {
                ref var store = ref Unsafe.As<T, BufferStore>(ref rows[i]);
                store = Buffer.Read(ref reader, store);
            }
        else
            for (int i = start; i < start + count; i++) NinoDeserializer.Deserialize(ref rows[i], ref reader);
    }

    public override object Box(Array column, int slot) =>
        Managed ? Unsafe.As<T[]>(column)[slot] : Unsafe.Add(ref Archetype.Row0<T>(column), slot);

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

    public override void CopyRows(Array source, Array target, int count, bool swap = false)
    {
        if (default(T) is not IBuffer) // a JIT-time constant
        {
            Array.Copy(source, target, count);
            return;
        }

        // A DynamicBuffer<TElement> has a single field, its BufferStore<TElement>: the value is that reference.
        var from = Unsafe.As<T[]>(source);
        var rows = Unsafe.As<T[]>(target);
        for (int i = 0; i < count; i++)
        {
            ref var store = ref Unsafe.As<T, BufferStore>(ref rows[i]);
            ref var saved = ref Unsafe.As<T, BufferStore>(ref from[i]);
            if (swap) (store, saved) = (saved, store);
            else store = saved?.Clone();
        }
    }
}
