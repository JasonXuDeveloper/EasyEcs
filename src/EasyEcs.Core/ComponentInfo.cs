using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Nino.Core;

namespace EasyEcs;

/// <summary>
/// Process-wide description of a component type, indexed by its bit: element size and the typed column operations
/// archetypes need without knowing <c>T</c> (create, grow, copy a managed element, clear). Registered once when
/// <see cref="ComponentType{T}"/> assigns the bit; read lock-free (copy-on-write array).
/// <br/>
/// Bits of types from a collectible AssemblyLoadContext are released when it unloads (<see cref="Release"/>) and reused
/// once no live world mentions them any more (<see cref="Reclaim"/>): reloading game logic does not grow the masks.
/// </summary>
internal abstract class ComponentInfo
{
    private static ComponentInfo[] _all = new ComponentInfo[64];
    private static readonly Lock RegisterLock = new();
    private static int _next;                       // the next bit never assigned
    private static readonly List<int> Free = [];    // reclaimed bits, reused lowest first
    private static int _released;                   // released bits not reclaimed yet

    /// <summary>The type's context is unloading: the bit is reused once no live world mentions it.</summary>
    private bool _isReleased;

    /// <summary>Element size in bytes, or -1 when the type holds managed references (copied through the typed path).</summary>
    public readonly int Size;

    protected ComponentInfo(int size)
    {
        Size = size;
    }

    /// <summary>Bit of the change-tick column of a tracked component (<see cref="ITracked"/>), or -1.</summary>
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

    /// <summary>
    /// The bit of the <see cref="Disabled{T}"/> tag of component <paramref name="component"/>, assigned the first time
    /// it is needed: a <c>Disable</c>, a query naming the tag, a load of a disabled column. Its info is a
    /// <c>ComponentInfo&lt;DisabledTag&gt;</c> described by the component's type (released with it), never a
    /// <c>ComponentInfo&lt;Disabled&lt;T&gt;&gt;</c>: code generic over every component that names its tag's type makes the
    /// AOT compiler expand <c>Disabled&lt;Disabled&lt;...&gt;&gt;</c> without end (IL3054).
    /// </summary>
    public static ushort DisablerOf(int component)
    {
        var info = Of(component);
        if (info.Disabler < 0)
            lock (RegisterLock)
                if (info.Disabler < 0)
                {
                    int tag = Allocate(new ComponentInfo<DisabledTag>(info.Type));
                    Of(tag).DisabledOf = component;
                    info.Disabler = tag;
                }

        return (ushort)info.Disabler;
    }

    /// <summary>Whether any tracked component type was registered (archetype masks then get companion bits).</summary>
    public static bool AnyTracked => _anyTracked;

    private static volatile bool _anyTracked;

    /// <summary>Whether the type is an <see cref="ICleanupComponent"/> (kept by <c>Destroy</c>, which retires its entity).</summary>
    public readonly bool Cleanup;

    /// <summary>Whether the type is an <see cref="IGroup"/> (its value may split tables, see <see cref="World.GroupBy{T}"/>).</summary>
    public readonly bool Grouped;

    /// <summary>Whether the type is an <see cref="IUniqueIndexed{TKey}"/> (each world indexes it from its first table).</summary>
    public readonly bool Unique;

    /// <summary>Whether the type is an <see cref="IIndexed{TKey}"/>, unique or not (a world may keep a value index of it).</summary>
    public readonly bool Indexed;

    /// <summary>Whether any unique component type was registered (new tables then look for their unique columns).</summary>
    public static bool AnyUnique => _anyUnique;

    private static volatile bool _anyUnique;

    protected ComponentInfo(int size, bool cleanup, bool grouped, bool unique = false, bool indexed = false) : this(size)
    {
        Cleanup = cleanup;
        Grouped = grouped;
        Unique = unique;
        Indexed = indexed;
        if (unique) _anyUnique = true;
    }

    /// <summary>
    /// A new index of this unique component for <paramref name="world"/> (<see cref="Unique"/> types only); throws when
    /// no module registered how to create it (<see cref="TypeRegistry.UniqueIndex{T,TKey}"/>). Cold.
    /// </summary>
    public virtual World.IValueIndex CreateUniqueIndex(World world) => null;

    /// <summary>The setup every world gives the type (<see cref="TypeDefaults"/>), or null. Cold: read once per new table.</summary>
    public virtual TypeDefaults Defaults => null;

    /// <summary>Whether the type is [NotSaved]: saves leave its column out (<see cref="TypeRegistry.NotSaved{T}"/>). Cold: once per column per save.</summary>
    public virtual bool NotSaved => false;

    /// <summary>Whether the type holds managed references (cleared when its row dies, copied by type).</summary>
    public bool Managed => Size < 0;

    /// <summary>Whether the type is a <see cref="DynamicBuffer{T}"/> (its column holds lists).</summary>
    public abstract bool IsBuffer { get; }

    /// <summary>A <see cref="DynamicBuffer{T}"/>'s: fill the pool of its lists until it holds <paramref name="lists"/>; else nothing.</summary>
    public abstract void FillPool(int lists);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ComponentInfo Of(int bit) => Volatile.Read(ref _all)[bit];

    /// <summary>Bits assigned so far (reclaimed ones included).</summary>
    public static int Count => Volatile.Read(ref _next);

    /// <summary>The description of <paramref name="bit"/>, or null when it is unassigned or reclaimed (cold).</summary>
    public static ComponentInfo TryOf(int bit)
    {
        var all = Volatile.Read(ref _all);
        return bit < all.Length ? all[bit] : null;
    }

    /// <summary>Assign a bit to <paramref name="info"/>: a reclaimed one if any, else the next. Once per type.</summary>
    internal static int Allocate(ComponentInfo info)
    {
        lock (RegisterLock)
        {
            if (Free.Count == 0 && _released != 0) ReclaimLocked();
            int bit;
            if (Free.Count > 0)
            {
                int lowest = 0;
                for (int i = 1; i < Free.Count; i++)
                    if (Free[i] < Free[lowest]) lowest = i;
                bit = Free[lowest];
                Free.RemoveAt(lowest);
            }
            else bit = _next++;

            if (bit > ushort.MaxValue) throw new InvalidOperationException($"Too many component types (max {ushort.MaxValue + 1})");
            Register(bit, info);
            return bit;
        }
    }

    /// <summary>
    /// Release the bits of the types <paramref name="unloading"/> matches (their context is unloading); reclaim those
    /// no live world mentions. Cold: once per unloading context.
    /// </summary>
    internal static void Release(Func<Type, bool> unloading)
    {
        lock (RegisterLock)
        {
            var all = _all;
            for (int bit = 0; bit < _next; bit++)
                if (all[bit] is { _isReleased: false } info && unloading(info.Type))
                {
                    info._isReleased = true;
                    _released++;
                }

            ReclaimLocked();
        }
    }

    /// <summary>Reuse the released bits that no live world mentions (after a world dropped them or was disposed).</summary>
    internal static void Reclaim()
    {
        if (Volatile.Read(ref _released) == 0) return;
        lock (RegisterLock) ReclaimLocked();
    }

    private static void ReclaimLocked()
    {
        var all = _all;
        for (int bit = 0; bit < _next && _released != 0; bit++)
        {
            if (all[bit] is not { _isReleased: true } || Collectible.InUse(bit)) continue;
            all[bit] = null; // the type's last reference from EasyEcs: its context can be collected
            Free.Add(bit);
            _released--;
        }
    }

    // Inside RegisterLock.
    private static void Register(int bit, ComponentInfo info)
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

    /// <summary>The component type (error messages).</summary>
    public abstract Type Type { get; }

    public abstract Array Create(int capacity);

    /// <summary>A copy of <paramref name="column"/> with <paramref name="capacity"/> rows: grown, or shrunk to rows that hold no value any more.</summary>
    public abstract Array Resize(Array column, int capacity);

    public abstract void CopyManaged(Array source, int sourceSlot, Array target, int targetSlot);

    /// <summary>
    /// Copy a managed element into another entity (<see cref="World.Instantiate(Prefab)"/>, <see cref="Entity.Clone"/>): like
    /// <see cref="CopyManaged"/>, but a <see cref="DynamicBuffer{T}"/> shares its list until one side writes it
    /// (<see cref="BufferStore.Share"/>).
    /// </summary>
    public abstract void CloneManaged(Array source, int sourceSlot, Array target, int targetSlot);

    public abstract void Clear(Array column, int start, int count);

    /// <summary>
    /// <see cref="Clear"/> of values that die (their component was removed, their entity destroyed): the lists of a
    /// buffer column go back to the pool of their element type (<see cref="BufferStore.Recycle"/>). Never for rows
    /// whose values were copied elsewhere (a moved row): the copies still hold them.
    /// </summary>
    public abstract void Discard(Array column, int start, int count);

    /// <summary>
    /// Copy rows [0, <paramref name="count"/>) of a managed column for a snapshot (capture or restore): values, and for
    /// <see cref="DynamicBuffer{T}"/> columns their lists, shared until either side writes (<see cref="BufferStore.Assign"/>). With
    /// <paramref name="swap"/> the lists change sides instead (a Load's private snapshot): the target's old lists go to
    /// the source, for the next Load to refill. With <paramref name="own"/> (a restore into the live table) a row
    /// written since the capture takes copies of the source's elements instead of sharing its list
    /// (<see cref="BufferStore.AssignOwn"/>).
    /// </summary>
    public abstract void CopyRows(Array source, Array target, int count, bool swap = false, bool own = false);

    /// <summary>
    /// Copy the rows <paramref name="rows"/> selects (bit i: row <paramref name="first"/> + i) of a managed column
    /// between a table and a copy of it (an incremental capture or restore, <see cref="Archetype.Capture"/>): values,
    /// and for <see cref="DynamicBuffer{T}"/> columns copies of the elements in a list of the target row's own
    /// (<see cref="BufferStore.AssignOwn"/>), so neither side's next write copies a shared list.
    /// </summary>
    public abstract void CopyWritten(Array source, Array target, int first, ulong rows);

    /// <summary>
    /// Rows [0, order.Length) of managed column <paramref name="column"/> gathered into <paramref name="into"/> (a column
    /// of the same length holding no reference) in a new order: row <c>i</c> takes the value of row
    /// <paramref name="order"/>[i]; then the column's rows are cleared, so it holds no reference either (a spare).
    /// </summary>
    public abstract void Permute(Array column, Array into, ReadOnlySpan<int> order);

    /// <summary>A boxed copy of row <paramref name="slot"/> of <paramref name="column"/> (the debugger view; cold, allocates).</summary>
    public abstract object Box(Array column, int slot);

    /// <summary>Bytes of one row of the column (an element of a managed column: its references, not what they point at).</summary>
    public abstract int Stride { get; }

    /// <summary>The name of the type in messages (<see cref="TypeRegistry.NameOf{T}"/>).</summary>
    public abstract string Name { get; }

    /// <summary>The id a save records the type by (<see cref="TypeRegistry.IdOf"/>).</summary>
    public abstract int TypeId { get; }


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

    /// <summary>
    /// How a save writes the rows (of a buffer column: its elements): the size of a raw row, 0 when Nino writes them
    /// (<see cref="TypeRegistry.RowSize{T}"/>).
    /// </summary>
    public abstract int SavedSize { get; }

    /// <summary>The layout hash of raw rows the generator registered (<see cref="TypeRegistry.Raw{T}"/>), else 0.</summary>
    public abstract int SavedLayout { get; }

    /// <summary>Read <paramref name="count"/> rows written by <see cref="Write"/> into managed column <paramref name="column"/> from <paramref name="start"/>.</summary>
    public abstract void Read(ref Reader reader, Array column, int start, int count);
}

/// <param name="owner">
/// For a change-tick column or a <see cref="Disabled{T}"/> tag (<typeparamref name="T"/> is <see cref="RowTick"/> or
/// <see cref="DisabledTag"/>): the component it belongs to, whose context's unloading releases its bit too
/// (<see cref="ComponentInfo.Release"/>). Else null.
/// </param>
internal sealed class ComponentInfo<T>(Type owner = null) : ComponentInfo(RuntimeHelpers.IsReferenceOrContainsReferences<T>() ? -1 : Unsafe.SizeOf<T>(),
    default(T) is ICleanupComponent, default(T) is IGroup, default(T) is IUniqueIndexed, default(T) is IIndexed)
    where T : struct, IComponent
{
    public override TypeDefaults Defaults => TypeDefaults<T>.Value;

    public override bool NotSaved => NotSavedOf<T>.Value;

    public override World.IValueIndex CreateUniqueIndex(World world) =>
        UniqueIndexOf<T>.Create is { } create ? create(world) : throw new InvalidOperationException(
            $"{typeof(T).Name} is unique, but its module registered no index (the EasyEcs generator does it from C# 14 on, " +
            "for types it can name; a key type of EntityId cannot be unique): call world.Index<T, TKey>() once before writing it");

    // Unmanaged columns are pinned byte arrays (pinned object heap, never moved by the GC) with one cache line of
    // slack, so their rows can start on a 64-byte boundary (Archetype.Aligned): SIMD loads and power-of-two sized
    // components then never straddle two lines. Managed columns are plain T[] (the GC must see their references).
    public override Type Type => owner ?? typeof(T);

    public override string Name => TypeRegistry.NameOf<T>.Value;

    public override int TypeId => TypeRegistry.TypeId<T>.Value;

    public override int Stride => Unsafe.SizeOf<T>();


    // A boxed default buffer (null for other types): reads and writes the elements of a DynamicBuffer<TElement> column
    // without knowing TElement. Boxed once per type.
    private static readonly IBuffer Buffer = default(T) is IBuffer ? (IBuffer)default(T) : null;

    public override bool IsBuffer => default(T) is IBuffer;

    public override void FillPool(int lists) => Buffer?.FillPool(lists);

    public override void Write(ref Writer writer, Array column, int start, int count)
    {
        var rows = Unsafe.As<T[]>(column);
        if (default(T) is IBuffer) // a JIT-time constant
            for (int i = start; i < start + count; i++) Buffer.Write(ref writer, Unsafe.As<T, BufferStore>(ref rows[i]));
        else
            for (int i = start; i < start + count; i++) NinoSerializer.Serialize(rows[i], ref writer);
    }

    public override void WriteRows(ref Writer writer, ReadOnlySpan<byte> rows) => TypeRegistry.WriteRows<T>(ref writer, rows);

    public override void ReadRows(ref Reader reader, Span<byte> rows) => TypeRegistry.ReadRows<T>(ref reader, rows);

    public override int SavedSize => default(T) is IBuffer ? Buffer.RowSize : TypeRegistry.RowSize<T>();

    public override int SavedLayout => default(T) is IBuffer ? Buffer.Layout : TypeRegistry.RowSize<T>() != 0 ? TypeRegistry.RawLayout<T>.Hash : 0;

    public override void Remap(Array column, int start, int count, IdMap map)
    {
        if (default(T) is IBuffer) // a JIT-time constant
        {
            var stores = Unsafe.As<T[]>(column);
            for (int i = start; i < start + count; i++) Buffer.Remap(ref Unsafe.As<T, BufferStore>(ref stores[i]), map);
            return;
        }

        if (TypeRegistry.EntityIdOffsets<T>.Value.Length == 0) return;
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

    public override Array Create(int capacity)
    {
        if (Managed) return new T[capacity];
        var column = GC.AllocateArray<byte>(capacity * Size + Archetype.CacheLine, pinned: true);
        HugePages.Advise(column, column.Length);
        return column;
    }

    public override Array Resize(Array column, int capacity)
    {
        if (Managed)
        {
            var typed = Unsafe.As<T[]>(column);
            Array.Resize(ref typed, capacity);
            return typed;
        }

        var resized = Create(capacity);
        // The smaller capacity in bytes: both arrays have a line of slack before their aligned rows.
        int bytes = Math.Min(Unsafe.As<byte[]>(column).Length, Unsafe.As<byte[]>(resized).Length) - Archetype.CacheLine;
        Unsafe.CopyBlockUnaligned(ref Archetype.Aligned(resized), ref Archetype.Aligned(column), (uint)bytes);
        return resized;
    }

    public override void CopyManaged(Array source, int sourceSlot, Array target, int targetSlot) =>
        Unsafe.As<T[]>(target)[targetSlot] = Unsafe.As<T[]>(source)[sourceSlot];

    public override void CloneManaged(Array source, int sourceSlot, Array target, int targetSlot)
    {
        ref T value = ref Unsafe.As<T[]>(target)[targetSlot];
        value = Unsafe.As<T[]>(source)[sourceSlot];
        if (default(T) is not IBuffer) return; // a JIT-time constant
        ref var store = ref Unsafe.As<T, BufferStore>(ref value); // a DynamicBuffer's single field (see CopyRows)
        store = store?.Share();
    }

    public override void Permute(Array column, Array into, ReadOnlySpan<int> order)
    {
        var rows = Unsafe.As<T[]>(column);
        var moved = Unsafe.As<T[]>(into);
        for (int i = 0; i < order.Length; i++) moved[i] = rows[order[i]];
        Array.Clear(rows, 0, order.Length); // the spare keeps no reference alive
    }

    public override void Clear(Array column, int start, int count) => Array.Clear(column, start, count);

    public override void Discard(Array column, int start, int count)
    {
        if (default(T) is not IBuffer) // a JIT-time constant
        {
            Array.Clear(column, start, count);
            return;
        }

        var rows = Unsafe.As<T[]>(column);
        for (int i = start; i < start + count; i++)
        {
            ref var store = ref Unsafe.As<T, BufferStore>(ref rows[i]); // a DynamicBuffer's single field (see CopyRows)
            store?.Recycle();
            store = null;
        }
    }

    public override void CopyWritten(Array source, Array target, int first, ulong rows)
    {
        var from = Unsafe.As<T[]>(source);
        var to = Unsafe.As<T[]>(target);
        for (; rows != 0; rows &= rows - 1)
        {
            int i = first + System.Numerics.BitOperations.TrailingZeroCount(rows);
            if (default(T) is not IBuffer) to[i] = from[i]; // a JIT-time constant
            else BufferStore.AssignOwn(ref Unsafe.As<T, BufferStore>(ref to[i]), Unsafe.As<T, BufferStore>(ref from[i]));
        }
    }

    public override void CopyRows(Array source, Array target, int count, bool swap = false, bool own = false)
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
            else if (own) BufferStore.AssignOwn(ref store, saved);
            else BufferStore.Assign(ref store, saved);
        }
    }
}
