using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace EasyEcs;

/// <summary>
/// An optional component of the entity a <c>[ForEach]</c> method visits: <c>static void Regen(ref Health h,
/// Maybe&lt;Buff&gt; buff)</c> visits every entity with Health, and <see cref="HasValue"/> tells whether this one also
/// has a Buff. One method instead of a With and a Without variant when the optional part is small. A method that
/// changes no structure finds the column once per run of rows and tests a presence bit per row; one that changes
/// structure looks the component up through each entity. Split the method in two with <c>[With&lt;T&gt;]</c> /
/// <c>[Without&lt;T&gt;]</c> in the hottest loops (no test per row). A method that only reads <see cref="Value"/> reads <typeparamref name="T"/>;
/// one that writes through it (assigns, increments, passes it by <c>ref</c>, calls a method on it) writes
/// <typeparamref name="T"/>, and a tracked component is stamped when present. Valid for the call it is passed to (a
/// ref struct).
/// </summary>
public readonly ref struct Maybe<T> where T : struct, IComponent
{
    private readonly ref T _value;

    /// <summary>Wraps <paramref name="value"/>, a null ref when the entity lacks the component.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Maybe(ref T value) => _value = ref value;

    /// <summary>The component of <paramref name="entity"/> for a method that only reads it (not stamped). Generated code calls this.</summary>
    [EditorBrowsable(EditorBrowsableState.Never), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Maybe<T> ForRead(Entity entity) => new(ref entity.Find<T>(false));

    /// <summary>The component of <paramref name="entity"/> for a method that writes it (a tracked one is stamped). Generated code calls this.</summary>
    [EditorBrowsable(EditorBrowsableState.Never), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Maybe<T> ForWrite(Entity entity) => new(ref entity.TryGetRef<T>(out _));

    /// <summary>Whether the entity has the component.</summary>
    public bool HasValue
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => !Unsafe.IsNullRef(ref _value);
    }

    /// <summary>The component by <c>ref</c>; throws when the entity lacks it (check <see cref="HasValue"/>).</summary>
    public ref T Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (Unsafe.IsNullRef(ref _value)) ThrowMissing();
            return ref _value;
        }
    }

    /// <summary>The same component, read-only: what a <see cref="ReadOnlyMaybe{T}"/> parameter is given.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator ReadOnlyMaybe<T>(Maybe<T> maybe) => new(in maybe._value);

    [DoesNotReturn]
    internal static void ThrowMissing() => throw new InvalidOperationException($"The entity has no {typeof(T).Name} (check HasValue)");
}

/// <summary>
/// An optional component of the entity a <c>[ForEach]</c> method visits, read-only: <see cref="Value"/> is a
/// <c>ref readonly</c>, so the method is scheduled as a reader of <typeparamref name="T"/>, a tracked component is never
/// stamped, and an indexed one (<see cref="IIndexed{TKey}"/>) may be taken, which a <see cref="Maybe{T}"/> may not
/// (EECS015: its ref could change the key behind the index's back). The same lookups as a <see cref="Maybe{T}"/>: the
/// column once per run of rows in a method that changes no structure, the entity otherwise. Valid for the call it is
/// passed to (a ref struct).
/// </summary>
public readonly ref struct ReadOnlyMaybe<T> where T : struct, IComponent
{
    private readonly ref readonly T _value;

    /// <summary>Wraps <paramref name="value"/>, a null ref when the entity lacks the component.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlyMaybe(ref readonly T value) => _value = ref value;

    /// <summary>Whether the entity has the component.</summary>
    public bool HasValue
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => !Unsafe.IsNullRef(in _value);
    }

    /// <summary>The component, read-only; throws when the entity lacks it (check <see cref="HasValue"/>).</summary>
    public ref readonly T Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (Unsafe.IsNullRef(in _value)) Maybe<T>.ThrowMissing();
            return ref _value;
        }
    }
}

/// <summary>
/// The optional <typeparamref name="T"/> column of one run of rows (<see cref="EntitySpan"/>): resolved once per run,
/// so a <c>[ForEach]</c> method taking a <see cref="Maybe{T}"/> that changes no structure finds each row's component
/// with a presence bit instead of a lookup through its entity. Generated code calls this.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public readonly ref struct MaybeColumn<T> where T : struct, IComponent
{
    private readonly ref T _first;        // the run's first row; a null ref when the table has no such column
    private readonly ulong[] _present;    // the column's presence bits; null when every row has it
    private readonly Archetype _table;    // for stamps: set only for a write of a tracked component
    private readonly ulong _tick;
    private readonly int _start;

    /// <summary>The column of <paramref name="entities"/>' table (found through the run's first entity, one lookup per run); <paramref name="write"/> stamps a tracked component per row reached.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MaybeColumn(in EntitySpan entities, bool write)
    {
        _first = ref Unsafe.NullRef<T>();
        if (entities.Length == 0) return;
        var world = entities.World;
        ref var meta = ref world.MetaRef(entities.Slots[0].Index);   // the run's first row: its table and slot
        var table = meta.Archetype;
        int column = table.ColumnIndex(ComponentType<T>.Bit);
        _start = meta.Slot;
        if (column < 0) return;
        _first = ref Unsafe.Add(ref table.Data<T>(column), _start);
        _present = table.Bits(column);
        if (write && default(T) is ITracked) (_table, _tick) = (table, world.ChangeTick);
    }

    /// <summary>The component of row <paramref name="index"/> of the run, if it has one.</summary>
    public Maybe<T> this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (Unsafe.IsNullRef(ref _first)) return default;
            int row = _start + index;
            if (_present != null && (_present[row >> 6] & (1UL << row)) == 0) return default; // shift is mod 64
            if (default(T) is ITracked && _table != null) _table.Stamp<T>(row, _tick);
            return new Maybe<T>(ref Unsafe.Add(ref _first, index));
        }
    }
}

