using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EasyEcs;

/// <summary>
/// Handle to an entity: id + version + world (16 bytes). Every access validates the version, so a handle to a
/// destroyed entity throws instead of touching the entity that reused its id. Safe to store and to compare.
/// </summary>
/// <remarks>
/// Refs returned by <see cref="Get{T}"/>/<see cref="Add{T}"/> point into the row of the entity's archetype table: use
/// them immediately and never keep them across a structural change of that entity (it moves the row) or an
/// <c>await</c>.
/// </remarks>
public readonly partial struct Entity : IEquatable<Entity>
{
    private readonly World _world;

    /// <summary>Entity id (dense, recycled after destruction).</summary>
    public readonly int Id;

    /// <summary>Incarnation of <see cref="Id"/>; bumped when the entity is destroyed.</summary>
    public readonly int Version;

    internal Entity(World world, int id, int version)
    {
        _world = world;
        Id = id;
        Version = version;
    }

    /// <summary>The world owning the entity (null for <c>default</c>).</summary>
    public World World => _world;

    /// <summary>Whether this incarnation is alive. O(1).</summary>
    public bool IsAlive => _world != null && (uint)Id < (uint)_world.Meta.Length && _world.Meta[Id].Version == Version &&
                           _world.Meta[Id].Archetype != null;

    /// <summary>Whether the entity has component <typeparamref name="T"/>. O(1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Has<T>() where T : struct, IComponent => !Unsafe.IsNullRef(ref Locate(ComponentType<T>.Bit, out _));

    /// <summary>
    /// Ref to component <typeparamref name="T"/>; throws if the entity does not have it. Counts as a write of a tracked
    /// component (<see cref="ITrackChanges"/>). O(1).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T Get<T>() where T : struct, IComponent
    {
        ref T value = ref Find<T>();
        if (Unsafe.IsNullRef(ref value)) ThrowMissing(typeof(T));
        if (default(T) is ITrackChanges) Touch<T>(_world, Id);
        return ref value;
    }

    /// <summary>
    /// Read-only ref to component <typeparamref name="T"/>; throws if the entity does not have it. Not a write: a tracked
    /// component (<see cref="ITrackChanges"/>) is not stamped. O(1).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T Read<T>() where T : struct, IComponent
    {
        ref T value = ref Find<T>();
        if (Unsafe.IsNullRef(ref value)) ThrowMissing(typeof(T));
        return ref value;
    }

    /// <summary>
    /// Ref to component <typeparamref name="T"/> if the entity has it (<paramref name="found"/>), else a null ref
    /// (check <paramref name="found"/> before using it). Counts as a write of a tracked component when found. O(1).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T TryGetRef<T>(out bool found) where T : struct, IComponent
    {
        ref T value = ref Find<T>();
        found = !Unsafe.IsNullRef(ref value);
        if (default(T) is ITrackChanges && found) Touch<T>(_world, Id);
        return ref value;
    }

    /// <summary>Copy of component <typeparamref name="T"/> if the entity has it. Not a write. O(1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGet<T>(out T value) where T : struct, IComponent
    {
        ref T found = ref Find<T>();
        bool has = !Unsafe.IsNullRef(ref found);
        value = has ? found : default;
        return has;
    }

    /// <summary>
    /// Ref to <typeparamref name="T"/> in the entity's row, or a null ref when the entity does not have it (no column,
    /// or the row lost the component). Throws for a stale handle. One meta line plus one line of the archetype's lookup.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe ref T Find<T>() where T : struct, IComponent
    {
        ref var lookup = ref Locate(ComponentType<T>.Bit, out int slot);
        if (Unsafe.IsNullRef(ref lookup)) return ref Unsafe.NullRef<T>();
        // A JIT-time constant: an unmanaged column's row 0 is cached in the entry (pinned, see Lookup.Rows).
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            return ref Unsafe.Add(ref Unsafe.AsRef<T>((void*)lookup.Rows), slot);
        return ref Unsafe.Add(ref Archetype.Row0<T>(lookup.Column), slot);
    }

    /// <summary>
    /// The column entry of <paramref name="bit"/> and the entity's <paramref name="slot"/>, or a null ref when the row
    /// lacks the component (shared by <see cref="Has{T}"/>, which needs no row address, and <see cref="Find{T}"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref Archetype.Lookup Locate(uint bit, out int slot)
    {
        ref var meta = ref _world.MetaRef(Id);
        var archetype = meta.Archetype;
        if (meta.Version != Version || archetype == null) ThrowStale();
        var map = archetype.ByBit;
        slot = meta.Slot;
        if (bit >= (uint)map.Length) return ref Unsafe.NullRef<Archetype.Lookup>();
        // bit < map.Length: no bounds check. Column and presence bits share the entry's cache line.
        ref var lookup = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(map), bit);
        if (lookup.Column == null) return ref Unsafe.NullRef<Archetype.Lookup>();
        // Presence bits cover the table's capacity and the slot is below it: no bounds check.
        if (lookup.Bits is { } bits && (Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(bits), (uint)slot >> 6) & (1UL << slot)) == 0)
            return ref Unsafe.NullRef<Archetype.Lookup>();
        return ref lookup;
    }

    /// <summary>
    /// Add component <typeparamref name="T"/> (reset to default if present) and return a ref to it. O(1) when the
    /// entity's table has a column for it (the row only gets its presence bit back), else O(c) amortized: the row moves
    /// to the table with the column (c components copied).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T Add<T>() where T : struct, IComponent => ref AddCore<T>(_world, Id, Version);

    // Structural changes and cold paths take the handle by value: a loop calling them keeps its handle in registers
    // (a call through `this` would take its address and store it to memory on every row, even when the call is rare).
    [MethodImpl(MethodImplOptions.NoInlining)] // structural change: keeps the loops that call it lean
    private static ref T AddCore<T>(World world, int id, int version) where T : struct, IComponent
    {
        using (world.Structural())
        {
            ref var meta = ref world.Live(new Entity(world, id, version));
            ushort bit = ComponentType<T>.Bit;
            world.AddBit(ref meta, id, bit);
            ref T value = ref World.Row<T>(ref meta, bit);
            value = default;
            meta.Archetype.Stamp<T>(meta.Slot, world.ChangeTick);
            return ref value;
        }
    }

    /// <summary>Ref to component <typeparamref name="T"/>, adding it (default) first if missing. O(1), or as <see cref="Add{T}"/> when missing.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T GetOrAdd<T>() where T : struct, IComponent
    {
        ref T value = ref Find<T>();
        if (Unsafe.IsNullRef(ref value)) return ref Add<T>();
        if (default(T) is ITrackChanges) Touch<T>(_world, Id);
        return ref value;
    }

    /// <summary>Stamp the entity's row of tracked component <typeparamref name="T"/>, which it has (cold path).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Touch<T>(World world, int id) where T : struct, IComponent
    {
        ref var meta = ref world.MetaRef(id);
        meta.Archetype.Stamp<T>(meta.Slot, world.ChangeTick);
    }

    // Set(in T1..Tn) and Remove<T1..Tn>() are generated (Entity.Structural.g.cs).

    /// <summary>Destroy the entity; a destroyed handle is ignored. O(components).</summary>
    public void Destroy() => _world?.DestroyEntity(this);

    // Throw-only helpers without NoInlining: the JIT then sees they never return, keeps the throw out of the
    // caller's register allocation and never inlines them anyway.
    [DoesNotReturn]
    internal static void ThrowStale() => throw new InvalidOperationException("Entity has been destroyed");

    [DoesNotReturn]
    private static void ThrowMissing(Type type) => throw new InvalidOperationException($"Entity has no {type.Name} component");

    public bool Equals(Entity other) => Id == other.Id && Version == other.Version && ReferenceEquals(_world, other._world);

    public override bool Equals(object obj) => obj is Entity other && Equals(other);

    public override int GetHashCode() => unchecked(Id * 397 ^ Version); // cheap: hashed per dictionary lookup (relation indexes)

    public override string ToString() => $"Entity {Id}v{Version}";

    public static bool operator ==(Entity left, Entity right) => left.Equals(right);

    public static bool operator !=(Entity left, Entity right) => !left.Equals(right);
}
