using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace EasyEcs;

/// <summary>
/// An optional component of the entity a <c>[ForEach]</c> method visits: <c>static void Regen(ref Health h,
/// Maybe&lt;Buff&gt; buff)</c> visits every entity with Health, and <see cref="HasValue"/> tells whether this one also
/// has a Buff. One method instead of a With and a Without variant when the optional part is small. It costs a lookup
/// per entity (the method runs per entity, with its handle), and counts as a write of the component when present
/// (it hands out a <c>ref</c>); split the method in two with <c>[With&lt;T&gt;]</c> / <c>[Without&lt;T&gt;]</c> in hot
/// loops. Valid for the call it is passed to (a ref struct).
/// </summary>
public readonly ref struct Maybe<T> where T : struct, IComponent
{
    private readonly ref T _value;

    /// <summary>Wraps <paramref name="value"/>, a null ref when the entity lacks the component.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Maybe(ref T value) => _value = ref value;

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

    [DoesNotReturn]
    private static void ThrowMissing() => throw new InvalidOperationException($"The entity has no {typeof(T).Name} (check HasValue)");
}
