using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EasyEcs;

/// <summary>
/// A component of the entity a <c>[ForEach]</c> method visits that counts as written only when the method writes it:
/// <c>static void Aim(Mut&lt;Target&gt; target, in Position p) =&gt; target.SetIfChanged(new Target(Nearest(p)))</c>.
/// A <c>ref</c> parameter of a tracked component (<see cref="ITracked"/>) stamps every row the method visits, so
/// <c>[Changed&lt;T&gt;]</c> readers visit rows whose value never changed; through a <see cref="Mut{T}"/> only the rows
/// the method writes (<see cref="Write"/>, a <see cref="SetIfChanged"/> that changed the value) are stamped, each with
/// the exact per-row stamp of <see cref="Entity.Get{T}"/>. Reading <see cref="Value"/> stamps nothing.
/// <para>The scheduler treats the parameter as a write of <typeparamref name="T"/> (a parallel stage never runs the
/// system beside a reader of it). It works in per-entity methods, also <c>[ForEach(Parallel = true)]</c> ones and ones
/// that change structure (a write after the entity moved stamps its new row); batch methods take spans and stamp their
/// runs. For a component that does not track changes it is a plain <c>ref</c>. Valid for the call it is passed to (a
/// ref struct). An indexed or grouped component is not written through a ref (EECS015).</para>
/// </summary>
public readonly ref struct Mut<T> where T : struct, IComponent
{
    private readonly ref T _value;
    private readonly ref readonly MutColumn _run; // the run's change ticks when the row cannot move during the call; a null ref otherwise
    private readonly Entity _entity; // otherwise the visited entity: a write finds its row (it may have moved) and stamps it
    private readonly int _index;     // the row in the run

    /// <summary>
    /// Wraps <paramref name="value"/>, the component of <paramref name="entity"/> a pass hands out (its row in the table
    /// being walked). Generated code calls this.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Mut(ref T value, Entity entity)
    {
        _value = ref value;
        _entity = entity;
    }

    /// <summary>
    /// Wraps <paramref name="value"/>, the component of row <paramref name="index"/> of a run of rows of a pass that
    /// changes no structure, whose change-tick column <paramref name="run"/> holds (<see cref="MutColumn.Of{T}"/>, made
    /// once per run): a write stamps the row without looking the entity or the column up. Generated code calls this.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Mut(ref T value, in MutColumn run, int index)
    {
        _value = ref value;
        _run = ref run;
        _index = index;
    }

    /// <summary>The component, read-only: not a write, nothing is stamped. O(1).</summary>
    public ref readonly T Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _value;
    }

    /// <summary>
    /// The component by <c>ref</c>, stamped as written (a tracked component: its row, found by one meta lookup when the
    /// method may move the entity).
    /// Call it once per write, when the method knows it writes. O(1).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T Write()
    {
        if (default(T) is ITracked) // a JIT-time constant: nothing for other components
        {
            if (!Unsafe.IsNullRef(in _run)) _run.Stamp(_index);
            else Entity.Stamp<T>(_entity);
        }
        return ref _value;
    }

    /// <summary>
    /// Store <paramref name="value"/> and stamp the row only when it differs from the component's value: true when it
    /// did. Equality is <see cref="IEquatable{T}"/> when <typeparamref name="T"/> implements it (record structs do), else
    /// the bytes of an unmanaged <typeparamref name="T"/>; a managed <typeparamref name="T"/> without
    /// <see cref="IEquatable{T}"/> throws (<see cref="NotSupportedException"/>). O(size of T), allocation-free.
    /// <para>Bytes compare what is stored, not what it means: padding between fields, <c>-0.0</c> against <c>0.0</c> and
    /// two NaNs with different payloads count as different, so such a value is written and stamped although it is equal
    /// (a stamp too many, never a change missed). Make a component with <c>float</c> or <c>double</c> fields a
    /// <c>record struct</c> (or implement <see cref="IEquatable{T}"/>) to compare its values.</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool SetIfChanged(in T value)
    {
        if (Values.Same(in _value, in value)) return false;
        Write() = value;
        return true;
    }
}

/// <summary>
/// The change-tick column of a tracked component in the table of a run of rows, looked up once per run: each
/// <see cref="Mut{T}"/> of the run stamps the row it writes through it, with the stamp of <c>Archetype.Stamp</c>
/// (word summary, then the row's tick) and no lookup of the world's tick, the column or its arrays. Valid for the run
/// (a pass that changes no structure: the table's storage is not reallocated). Generated code makes it.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public readonly unsafe struct MutColumn
{
    private readonly Archetype _table;
    private readonly WordTicks[] _words;
    private readonly RowTick* _ticks; // a pinned column (Archetype.TicksOf)
    private readonly int _column, _first;
    private readonly ulong _tick;

    private MutColumn(Archetype table, WordTicks[] words, RowTick* ticks, int column, int first, ulong tick)
    {
        _table = table;
        _words = words;
        _ticks = ticks;
        _column = column;
        _first = first;
        _tick = tick;
    }

    /// <summary>
    /// The change-tick column of <typeparamref name="T"/> for the run <paramref name="entities"/> (default for a
    /// component that does not track changes: its <see cref="Mut{T}"/> stamps nothing). Generated code calls this.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static MutColumn Of<T>(in EntitySpan entities) where T : struct, IComponent
    {
        if (default(T) is not ITracked) return default; // a JIT-time constant
        var table = entities.Table;
        int column = table.TicksOf<T>(out var words, out var ticks);
        return new MutColumn(table, words, ticks, column, entities.First, entities.World.ChangeTick);
    }

    /// <summary>Stamps row <paramref name="index"/> of the run as written at the pass's tick.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Stamp(int index)
    {
        int row = _first + index;
        // No bounds checks: the row is one of the run's, below the table's capacity, which its summaries cover. Summaries
        // only grow and no row holds a later tick than the world's: a word at this tick has its block and column at it.
        ref var word = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_words), row >> 6);
        ulong bit = 1UL << row; // shift is mod 64
        if (word.Write == _tick) word.Rows |= bit;
        else _table.NoteWord(_column, row, bit, _tick);
        _ticks[row].Value = _tick;
    }
}

/// <summary>Value equality of components without boxing (<see cref="Mut{T}.SetIfChanged"/>, <see cref="Entity.SetIfChanged{T}"/>).</summary>
internal static class Values
{
    /// <summary>
    /// Whether <paramref name="a"/> and <paramref name="b"/> are equal: <see cref="IEquatable{T}"/> (devirtualized, no
    /// box), else the bytes of an unmanaged type (equal bytes are equal values; values equal under other rules count as a
    /// change: padding bytes, -0.0 against 0.0, NaN payloads; a stamp too many, never one too few). The type tests are
    /// JIT-time constants.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Same<T>(in T a, in T b) where T : struct
    {
        if (typeof(IEquatable<T>).IsAssignableFrom(typeof(T))) return EqualityComparer<T>.Default.Equals(a, b);
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            return MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in a)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in b)));
        ThrowNotEquatable<T>();
        return false;
    }

    [DoesNotReturn, MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowNotEquatable<T>() => throw new NotSupportedException(
        $"{typeof(T).Name} holds references and does not implement IEquatable<{typeof(T).Name}>: SetIfChanged cannot compare it without boxing (make it a record struct or implement IEquatable)");
}
