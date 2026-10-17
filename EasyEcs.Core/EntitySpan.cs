using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EasyEcs;

/// <summary>
/// The entities of one run of rows in a batch job: element <c>i</c> is the entity whose components are element
/// <c>i</c> of the run's spans. A handle is built only when indexed, so a batch that needs the entity of a few rows
/// reads only those rows' ids. Valid for the call it is passed to (a ref struct, like the spans).
/// </summary>
public readonly ref struct EntitySpan
{
    private readonly World _world;
    private readonly ReadOnlySpan<EntityId> _slots;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal EntitySpan(World world, ref EntityId first, int length)
    {
        _world = world;
        _slots = MemoryMarshal.CreateReadOnlySpan(ref first, length);
    }

    /// <summary>Number of entities (the length of the run's spans).</summary>
    public int Length
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _slots.Length;
    }

    /// <summary>The entity of row <paramref name="index"/> of the run. O(1): one 8-byte load.</summary>
    public Entity this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new Entity(_world, _slots[index]);
    }

    /// <summary>The ids of the run's entities, read straight from the table (no copy): to store or send them.</summary>
    public ReadOnlySpan<EntityId> Ids
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _slots;
    }
}
