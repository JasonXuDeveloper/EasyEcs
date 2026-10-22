using System;
using System.ComponentModel;
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
    private readonly Archetype _table; // the table of the run and its first row: a Mut of a row stamps it directly
    private readonly int _first;

    internal World World => _world;
    internal ReadOnlySpan<EntityId> Slots => _slots;
    internal Archetype Table => _table;
    internal int First => _first;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal EntitySpan(World world, Archetype table, int first, ref EntityId slots, int length)
    {
        _world = world;
        _table = table;
        _first = first;
        _slots = MemoryMarshal.CreateReadOnlySpan(ref slots, length);
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

    /// <summary>
    /// The entity of row <paramref name="index"/> of the run, without a bounds check: generated per-row loops call it,
    /// whose index stays below the spans' length, which is the run's (every span of a run has its rows). O(1).
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Entity At(nint index) => new Entity(_world, Unsafe.Add(ref MemoryMarshal.GetReference(_slots), index));

    /// <summary>The ids of the run's entities, read straight from the table (no copy): to store or send them.</summary>
    public ReadOnlySpan<EntityId> Ids
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _slots;
    }
}
