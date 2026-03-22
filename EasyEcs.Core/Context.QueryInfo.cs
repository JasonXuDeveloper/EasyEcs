using System.Collections.Generic;
using System.Runtime.CompilerServices;
using EasyEcs.Core.Components;

namespace EasyEcs.Core;

public partial class Context
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SumAliveCount(List<Archetype> archetypes)
    {
        int count = 0;
        for (int i = 0; i < archetypes.Count; i++)
            count += archetypes[i].AliveCount;
        return count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasAlive(List<Archetype> archetypes)
    {
        for (int i = 0; i < archetypes.Count; i++)
            if (archetypes[i].AliveCount > 0)
                return true;
        return false;
    }

    /// <summary>
    /// Get the count of alive entities matching the specified component query.
    /// Lock-free. Does not create a full enumerator.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CountOf<T>() where T : struct, IComponent
    {
        if (!TagRegistry.TryGetTagBitIndex<T>(out var bitIdx)) return 0;
        var queryTag = new Tag();
        queryTag.SetBit(bitIdx);
        return SumAliveCount(GetMatchingArchetypes(in queryTag));
    }

    /// <summary>
    /// Check if any alive entities match the specified component query.
    /// Lock-free with early exit. Does not create a full enumerator.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AnyOf<T>() where T : struct, IComponent
    {
        if (!TagRegistry.TryGetTagBitIndex<T>(out var bitIdx)) return false;
        var queryTag = new Tag();
        queryTag.SetBit(bitIdx);
        return HasAlive(GetMatchingArchetypes(in queryTag));
    }

    /// <inheritdoc cref="CountOf{T}"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CountOf<T1, T2>()
        where T1 : struct, IComponent
        where T2 : struct, IComponent
    {
        if (!TagRegistry.TryGetTagBitIndex<T1>(out var b1) ||
            !TagRegistry.TryGetTagBitIndex<T2>(out var b2)) return 0;
        var q = new Tag();
        q.SetBits(b1, b2);
        return SumAliveCount(GetMatchingArchetypes(in q));
    }

    /// <inheritdoc cref="AnyOf{T}"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AnyOf<T1, T2>()
        where T1 : struct, IComponent
        where T2 : struct, IComponent
    {
        if (!TagRegistry.TryGetTagBitIndex<T1>(out var b1) ||
            !TagRegistry.TryGetTagBitIndex<T2>(out var b2)) return false;
        var q = new Tag();
        q.SetBits(b1, b2);
        return HasAlive(GetMatchingArchetypes(in q));
    }

    /// <inheritdoc cref="CountOf{T}"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CountOf<T1, T2, T3>()
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
    {
        if (!TagRegistry.TryGetTagBitIndex<T1>(out var b1) ||
            !TagRegistry.TryGetTagBitIndex<T2>(out var b2) ||
            !TagRegistry.TryGetTagBitIndex<T3>(out var b3)) return 0;
        var q = new Tag();
        q.SetBits(b1, b2, b3);
        return SumAliveCount(GetMatchingArchetypes(in q));
    }

    /// <inheritdoc cref="AnyOf{T}"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AnyOf<T1, T2, T3>()
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
    {
        if (!TagRegistry.TryGetTagBitIndex<T1>(out var b1) ||
            !TagRegistry.TryGetTagBitIndex<T2>(out var b2) ||
            !TagRegistry.TryGetTagBitIndex<T3>(out var b3)) return false;
        var q = new Tag();
        q.SetBits(b1, b2, b3);
        return HasAlive(GetMatchingArchetypes(in q));
    }

    /// <inheritdoc cref="CountOf{T}"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CountOf<T1, T2, T3, T4>()
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
    {
        if (!TagRegistry.TryGetTagBitIndex<T1>(out var b1) ||
            !TagRegistry.TryGetTagBitIndex<T2>(out var b2) ||
            !TagRegistry.TryGetTagBitIndex<T3>(out var b3) ||
            !TagRegistry.TryGetTagBitIndex<T4>(out var b4)) return 0;
        var q = new Tag();
        q.SetBits(b1, b2, b3, b4);
        return SumAliveCount(GetMatchingArchetypes(in q));
    }

    /// <inheritdoc cref="AnyOf{T}"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AnyOf<T1, T2, T3, T4>()
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
    {
        if (!TagRegistry.TryGetTagBitIndex<T1>(out var b1) ||
            !TagRegistry.TryGetTagBitIndex<T2>(out var b2) ||
            !TagRegistry.TryGetTagBitIndex<T3>(out var b3) ||
            !TagRegistry.TryGetTagBitIndex<T4>(out var b4)) return false;
        var q = new Tag();
        q.SetBits(b1, b2, b3, b4);
        return HasAlive(GetMatchingArchetypes(in q));
    }

    /// <inheritdoc cref="CountOf{T}"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CountOf<T1, T2, T3, T4, T5>()
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
        where T5 : struct, IComponent
    {
        if (!TagRegistry.TryGetTagBitIndex<T1>(out var b1) ||
            !TagRegistry.TryGetTagBitIndex<T2>(out var b2) ||
            !TagRegistry.TryGetTagBitIndex<T3>(out var b3) ||
            !TagRegistry.TryGetTagBitIndex<T4>(out var b4) ||
            !TagRegistry.TryGetTagBitIndex<T5>(out var b5)) return 0;
        var q = new Tag();
        q.SetBits(b1, b2, b3, b4, b5);
        return SumAliveCount(GetMatchingArchetypes(in q));
    }

    /// <inheritdoc cref="AnyOf{T}"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AnyOf<T1, T2, T3, T4, T5>()
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
        where T5 : struct, IComponent
    {
        if (!TagRegistry.TryGetTagBitIndex<T1>(out var b1) ||
            !TagRegistry.TryGetTagBitIndex<T2>(out var b2) ||
            !TagRegistry.TryGetTagBitIndex<T3>(out var b3) ||
            !TagRegistry.TryGetTagBitIndex<T4>(out var b4) ||
            !TagRegistry.TryGetTagBitIndex<T5>(out var b5)) return false;
        var q = new Tag();
        q.SetBits(b1, b2, b3, b4, b5);
        return HasAlive(GetMatchingArchetypes(in q));
    }

    /// <inheritdoc cref="CountOf{T}"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CountOf<T1, T2, T3, T4, T5, T6>()
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
        where T5 : struct, IComponent
        where T6 : struct, IComponent
    {
        if (!TagRegistry.TryGetTagBitIndex<T1>(out var b1) ||
            !TagRegistry.TryGetTagBitIndex<T2>(out var b2) ||
            !TagRegistry.TryGetTagBitIndex<T3>(out var b3) ||
            !TagRegistry.TryGetTagBitIndex<T4>(out var b4) ||
            !TagRegistry.TryGetTagBitIndex<T5>(out var b5) ||
            !TagRegistry.TryGetTagBitIndex<T6>(out var b6)) return 0;
        var q = new Tag();
        q.SetBits(b1, b2, b3, b4, b5, b6);
        return SumAliveCount(GetMatchingArchetypes(in q));
    }

    /// <inheritdoc cref="AnyOf{T}"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AnyOf<T1, T2, T3, T4, T5, T6>()
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
        where T5 : struct, IComponent
        where T6 : struct, IComponent
    {
        if (!TagRegistry.TryGetTagBitIndex<T1>(out var b1) ||
            !TagRegistry.TryGetTagBitIndex<T2>(out var b2) ||
            !TagRegistry.TryGetTagBitIndex<T3>(out var b3) ||
            !TagRegistry.TryGetTagBitIndex<T4>(out var b4) ||
            !TagRegistry.TryGetTagBitIndex<T5>(out var b5) ||
            !TagRegistry.TryGetTagBitIndex<T6>(out var b6)) return false;
        var q = new Tag();
        q.SetBits(b1, b2, b3, b4, b5, b6);
        return HasAlive(GetMatchingArchetypes(in q));
    }

    /// <inheritdoc cref="CountOf{T}"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CountOf<T1, T2, T3, T4, T5, T6, T7>()
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
        where T5 : struct, IComponent
        where T6 : struct, IComponent
        where T7 : struct, IComponent
    {
        if (!TagRegistry.TryGetTagBitIndex<T1>(out var b1) ||
            !TagRegistry.TryGetTagBitIndex<T2>(out var b2) ||
            !TagRegistry.TryGetTagBitIndex<T3>(out var b3) ||
            !TagRegistry.TryGetTagBitIndex<T4>(out var b4) ||
            !TagRegistry.TryGetTagBitIndex<T5>(out var b5) ||
            !TagRegistry.TryGetTagBitIndex<T6>(out var b6) ||
            !TagRegistry.TryGetTagBitIndex<T7>(out var b7)) return 0;
        var q = new Tag();
        q.SetBits(b1, b2, b3, b4, b5, b6, b7);
        return SumAliveCount(GetMatchingArchetypes(in q));
    }

    /// <inheritdoc cref="AnyOf{T}"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AnyOf<T1, T2, T3, T4, T5, T6, T7>()
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
        where T5 : struct, IComponent
        where T6 : struct, IComponent
        where T7 : struct, IComponent
    {
        if (!TagRegistry.TryGetTagBitIndex<T1>(out var b1) ||
            !TagRegistry.TryGetTagBitIndex<T2>(out var b2) ||
            !TagRegistry.TryGetTagBitIndex<T3>(out var b3) ||
            !TagRegistry.TryGetTagBitIndex<T4>(out var b4) ||
            !TagRegistry.TryGetTagBitIndex<T5>(out var b5) ||
            !TagRegistry.TryGetTagBitIndex<T6>(out var b6) ||
            !TagRegistry.TryGetTagBitIndex<T7>(out var b7)) return false;
        var q = new Tag();
        q.SetBits(b1, b2, b3, b4, b5, b6, b7);
        return HasAlive(GetMatchingArchetypes(in q));
    }

    /// <inheritdoc cref="CountOf{T}"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CountOf<T1, T2, T3, T4, T5, T6, T7, T8>()
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
        where T5 : struct, IComponent
        where T6 : struct, IComponent
        where T7 : struct, IComponent
        where T8 : struct, IComponent
    {
        if (!TagRegistry.TryGetTagBitIndex<T1>(out var b1) ||
            !TagRegistry.TryGetTagBitIndex<T2>(out var b2) ||
            !TagRegistry.TryGetTagBitIndex<T3>(out var b3) ||
            !TagRegistry.TryGetTagBitIndex<T4>(out var b4) ||
            !TagRegistry.TryGetTagBitIndex<T5>(out var b5) ||
            !TagRegistry.TryGetTagBitIndex<T6>(out var b6) ||
            !TagRegistry.TryGetTagBitIndex<T7>(out var b7) ||
            !TagRegistry.TryGetTagBitIndex<T8>(out var b8)) return 0;
        var q = new Tag();
        q.SetBits(b1, b2, b3, b4, b5, b6, b7, b8);
        return SumAliveCount(GetMatchingArchetypes(in q));
    }

    /// <inheritdoc cref="AnyOf{T}"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AnyOf<T1, T2, T3, T4, T5, T6, T7, T8>()
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
        where T5 : struct, IComponent
        where T6 : struct, IComponent
        where T7 : struct, IComponent
        where T8 : struct, IComponent
    {
        if (!TagRegistry.TryGetTagBitIndex<T1>(out var b1) ||
            !TagRegistry.TryGetTagBitIndex<T2>(out var b2) ||
            !TagRegistry.TryGetTagBitIndex<T3>(out var b3) ||
            !TagRegistry.TryGetTagBitIndex<T4>(out var b4) ||
            !TagRegistry.TryGetTagBitIndex<T5>(out var b5) ||
            !TagRegistry.TryGetTagBitIndex<T6>(out var b6) ||
            !TagRegistry.TryGetTagBitIndex<T7>(out var b7) ||
            !TagRegistry.TryGetTagBitIndex<T8>(out var b8)) return false;
        var q = new Tag();
        q.SetBits(b1, b2, b3, b4, b5, b6, b7, b8);
        return HasAlive(GetMatchingArchetypes(in q));
    }

    /// <inheritdoc cref="CountOf{T}"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CountOf<T1, T2, T3, T4, T5, T6, T7, T8, T9>()
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
        where T5 : struct, IComponent
        where T6 : struct, IComponent
        where T7 : struct, IComponent
        where T8 : struct, IComponent
        where T9 : struct, IComponent
    {
        if (!TagRegistry.TryGetTagBitIndex<T1>(out var b1) ||
            !TagRegistry.TryGetTagBitIndex<T2>(out var b2) ||
            !TagRegistry.TryGetTagBitIndex<T3>(out var b3) ||
            !TagRegistry.TryGetTagBitIndex<T4>(out var b4) ||
            !TagRegistry.TryGetTagBitIndex<T5>(out var b5) ||
            !TagRegistry.TryGetTagBitIndex<T6>(out var b6) ||
            !TagRegistry.TryGetTagBitIndex<T7>(out var b7) ||
            !TagRegistry.TryGetTagBitIndex<T8>(out var b8) ||
            !TagRegistry.TryGetTagBitIndex<T9>(out var b9)) return 0;
        var q = new Tag();
        q.SetBits(b1, b2, b3, b4, b5, b6, b7, b8, b9);
        return SumAliveCount(GetMatchingArchetypes(in q));
    }

    /// <inheritdoc cref="AnyOf{T}"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AnyOf<T1, T2, T3, T4, T5, T6, T7, T8, T9>()
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
        where T5 : struct, IComponent
        where T6 : struct, IComponent
        where T7 : struct, IComponent
        where T8 : struct, IComponent
        where T9 : struct, IComponent
    {
        if (!TagRegistry.TryGetTagBitIndex<T1>(out var b1) ||
            !TagRegistry.TryGetTagBitIndex<T2>(out var b2) ||
            !TagRegistry.TryGetTagBitIndex<T3>(out var b3) ||
            !TagRegistry.TryGetTagBitIndex<T4>(out var b4) ||
            !TagRegistry.TryGetTagBitIndex<T5>(out var b5) ||
            !TagRegistry.TryGetTagBitIndex<T6>(out var b6) ||
            !TagRegistry.TryGetTagBitIndex<T7>(out var b7) ||
            !TagRegistry.TryGetTagBitIndex<T8>(out var b8) ||
            !TagRegistry.TryGetTagBitIndex<T9>(out var b9)) return false;
        var q = new Tag();
        q.SetBits(b1, b2, b3, b4, b5, b6, b7, b8, b9);
        return HasAlive(GetMatchingArchetypes(in q));
    }
}