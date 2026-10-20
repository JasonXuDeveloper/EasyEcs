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
/// A <c>ref</c> parameter of a tracked component (<see cref="ITrackChanges"/>) stamps every row the method visits, so
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
    private readonly Entity _entity; // the visited entity: a write finds its row (it may have moved) and stamps it
    private readonly Archetype _table; // set when the row cannot move during the call: a write stamps it directly
    private readonly int _row;

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
    /// Wraps <paramref name="value"/>, the component of row <paramref name="index"/> of <paramref name="entities"/>, a
    /// run of rows of a pass that changes no structure: a write stamps the row without looking the entity up. Generated
    /// code calls this.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Mut(ref T value, in EntitySpan entities, int index)
    {
        _value = ref value;
        _entity = new Entity(entities.World, default(EntityId)); // only its world: the row is known
        _table = entities.Table;
        _row = entities.First + index;
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
        if (default(T) is ITrackChanges) // a JIT-time constant: nothing for other components
        {
            if (_table != null) _table.Stamp<T>(_row, _entity.World.ChangeTick);
            else Entity.Stamp<T>(_entity);
        }
        return ref _value;
    }

    /// <summary>
    /// Store <paramref name="value"/> and stamp the row only when it differs from the component's value: true when it
    /// did. Equality is <see cref="IEquatable{T}"/> when <typeparamref name="T"/> implements it (record structs do), else
    /// the bytes of an unmanaged <typeparamref name="T"/>; a managed <typeparamref name="T"/> without
    /// <see cref="IEquatable{T}"/> throws (<see cref="NotSupportedException"/>). O(size of T), allocation-free.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool SetIfChanged(in T value)
    {
        if (Values.Same(in _value, in value)) return false;
        Write() = value;
        return true;
    }
}

/// <summary>Value equality of components without boxing (<see cref="Mut{T}.SetIfChanged"/>, <see cref="Entity.SetIfChanged{T}"/>).</summary>
internal static class Values
{
    /// <summary>
    /// Whether <paramref name="a"/> and <paramref name="b"/> are equal: <see cref="IEquatable{T}"/> (devirtualized, no
    /// box), else the bytes of an unmanaged type (equal bytes are equal values; values equal under other rules, such as
    /// -0.0 and 0.0, count as a change: a stamp too many, never one too few). The type tests are JIT-time constants.
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
