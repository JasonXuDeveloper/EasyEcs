using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EasyEcs;

/// <summary>
/// An optional component of the entity a <c>[ForEach]</c> method visits: <c>static void Regen(ref Health h,
/// Maybe&lt;Buff&gt; buff)</c> visits every entity with Health, and <see cref="HasValue"/> tells whether this one also
/// has a Buff. One method instead of a With and a Without variant when the optional part is small. A method that
/// changes no structure finds the column once per run of rows and tests a presence bit per row; one that changes
/// structure looks the component up through each entity. Split the method in two with <c>[With&lt;T&gt;]</c> /
/// <c>[Without&lt;T&gt;]</c> in the hottest loops (no test per row). The type declares the access, as <c>ref</c> does:
/// the method writes <typeparamref name="T"/>, so the system is scheduled as its writer and a tracked component is
/// stamped on every row that has it; a method that only reads takes a <see cref="ReadOnlyMaybe{T}"/> (EECS025). Valid
/// for the call it is passed to (a ref struct).
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
/// An optional component of the entity a <c>[ForEach]</c> method visits, read-only (as <c>in</c> beside <c>ref</c>):
/// <see cref="Value"/> is a <c>ref readonly</c>, so the method is scheduled as a reader of <typeparamref name="T"/>, a
/// tracked component is never stamped, and an indexed one (<see cref="IIndexed{TKey}"/>) may be taken, which a <see cref="Maybe{T}"/> may not
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
    private readonly ref ulong _present;  // the column's presence bits; a null ref when every row has it
    private readonly Archetype _table;    // for stamps: set only for a write of a tracked component
    private readonly ulong _tick;
    private readonly int _start;
    private readonly int _words;          // the presence bits' length

    /// <summary>
    /// 64 rows of zeros: the lanes a vector loop loads where the table has no such column (<see cref="RowMask"/> keeps
    /// it at row 0). Never written: the loop stores nothing for such a column.
    /// </summary>
    private static readonly T[] Zeros = new T[64];

    /// <summary>The column of <paramref name="entities"/>' table (found through the run's first entity, one lookup per run); <paramref name="write"/> stamps a tracked component per row reached.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MaybeColumn(in EntitySpan entities, bool write)
    {
        _first = ref Unsafe.NullRef<T>();
        _present = ref Unsafe.NullRef<ulong>();
        if (entities.Length == 0) return;
        var world = entities.World;
        ref var meta = ref world.MetaRef(entities.Slots[0].Index);   // the run's first row: its table and slot
        var table = meta.Archetype;
        int column = table.ColumnIndex(ComponentType<T>.Bit);
        _start = meta.Slot;
        if (column < 0) return;
        _first = ref Unsafe.Add(ref table.Data<T>(column), _start);
        if (table.Bits(column) is { } bits)
        {
            _present = ref MemoryMarshal.GetArrayDataReference(bits);
            _words = bits.Length;
        }
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
            // No bounds check: the row is one of the table's, and its presence bits cover every row of its capacity.
            if (!Unsafe.IsNullRef(ref _present) && (Unsafe.Add(ref _present, row >> 6) & (1UL << row)) == 0) return default; // shift is mod 64
            if (default(T) is ITracked && _table != null) _table.Stamp<T>(row, _tick);
            return new Maybe<T>(ref Unsafe.Add(ref _first, index));
        }
    }

    // A vector loop's view of the column (Kernel.cs): the run's rows as lanes, a lane mask of the rows that have the
    // component, and the stamps of a write. Each lane computes what the method computes for its row; the lanes of rows
    // without the component are stored back as loaded.

    /// <summary>
    /// The run's first row of the column, or row 0 of 64 rows of zeros when the table has no such column (a loop
    /// indexes it through <see cref="RowMask"/>, so it stays at row 0).
    /// </summary>
    public ref T Lanes
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref Unsafe.IsNullRef(ref _first) ? ref MemoryMarshal.GetArrayDataReference(Zeros) : ref _first;
    }

    /// <summary>
    /// The mask a vector loop applies to its row index before it reads <see cref="Lanes"/>: all bits set when the table
    /// has the column, 0 when it reads the zeros (and stores nothing).
    /// </summary>
    public nuint RowMask
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Unsafe.IsNullRef(ref _first) ? 0 : nuint.MaxValue;
    }

    /// <summary>
    /// Which of the 64 rows from row <paramref name="index"/> of the run have the component: bit j for row
    /// <c>index + j</c> (0 for a table without the column, every bit for a column every row has). Bits past the table's
    /// capacity are 0 or not, as the column's last presence word holds them: a loop reads only the rows of its vector.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Present(nint index)
    {
        if (Unsafe.IsNullRef(ref _first)) return 0;
        if (Unsafe.IsNullRef(ref _present)) return ulong.MaxValue;
        int row = _start + (int)index, word = row >> 6, shift = row & 63;
        // No bounds check: the row is one of the table's, its presence bits cover every row of its capacity; the next
        // word is read only when the array holds it.
        ulong bits = Unsafe.Add(ref _present, word) >> shift;
        if (shift != 0 && word + 1 < _words) bits |= Unsafe.Add(ref _present, word + 1) << (64 - shift);
        return bits;
    }

    /// <summary>
    /// Stamp, for a write of a tracked component, the rows of the first <paramref name="count"/> rows of the run that
    /// have it and whose bit is set in <paramref name="rows"/> (bit j for row j; every row for
    /// <see cref="ulong.MaxValue"/>, else at most 64 rows): what the indexer stamps for each row it reaches. Nothing
    /// for a read, or for a component that does not track changes.
    /// </summary>
    public void StampPresent(int count, ulong rows)
    {
        if (_table == null || count <= 0) return;
        int first = _start, end = _start + count;
        for (int w = first >> 6; w <= (end - 1) >> 6; w++)
        {
            ulong bits = Archetype.RunMask(first, end, w);
            if (!Unsafe.IsNullRef(ref _present)) bits &= Unsafe.Add(ref _present, w);
            if (rows != ulong.MaxValue)
            {
                int offset = (w << 6) - first; // in (-64, 64): the run holds at most 64 rows
                bits &= offset >= 0 ? rows >> offset : rows << -offset;
            }
            _table.StampWord<T>(w, bits, _tick);
        }
    }
}

