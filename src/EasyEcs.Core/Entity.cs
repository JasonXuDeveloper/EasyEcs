using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EasyEcs;

/// <summary>
/// Handle to an entity: id + version + world (16 bytes). Every access validates the version, so a handle to a
/// destroyed entity never touches the entity that reused its id: questions (<see cref="IsAlive"/>,
/// <see cref="Has{T}"/>, <see cref="TryGet{T}"/>, <see cref="TryGetRef{T}"/>) answer false, accesses and changes
/// (<see cref="Get{T}"/>, <see cref="Read{T}"/>, <see cref="GetOrAdd{T}"/>, <c>Set</c>, <c>Remove</c>) throw, and
/// <see cref="Destroy"/> does nothing. Safe to store and to compare.
/// </summary>
/// <remarks>
/// Refs returned by <see cref="Get{T}"/>/<see cref="GetOrAdd{T}"/> point into the row of the entity's archetype table: use
/// them immediately and never keep them across a structural change of that entity (it moves the row) or an
/// <c>await</c>.
/// </remarks>
[System.Diagnostics.DebuggerDisplay("{ToString(),nq}")]
[System.Diagnostics.DebuggerTypeProxy(typeof(EntityDebugView))]
public readonly partial struct Entity : IEquatable<Entity>
{
    private readonly World _world;

    /// <summary>
    /// The entity's identity (index and incarnation): what to store in a component or send elsewhere to refer to it
    /// (<see cref="World.TryGetEntity(EntityId, out Entity)"/> turns it back into a handle).
    /// </summary>
    public readonly EntityId Id;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Entity(World world, int index, int version)
    {
        _world = world;
        Id = new EntityId(index, version);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Entity(World world, EntityId id)
    {
        _world = world;
        Id = id;
    }

    /// <summary>Index of the entity (<see cref="EntityId.Index"/>).</summary>
    internal int Index
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Id.Index;
    }

    /// <summary>Incarnation of <see cref="Index"/> (<see cref="EntityId.Version"/>).</summary>
    internal int Version
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Id.Version;
    }

    /// <summary>The handle's <see cref="Id"/>: store ids, not handles, in components.</summary>
    public static implicit operator EntityId(Entity entity) => entity.Id;

    /// <summary>The world owning the entity (null for <c>default</c>).</summary>
    public World World => _world;

    /// <summary>Whether this incarnation is alive. O(1).</summary>
    public bool IsAlive => _world != null && (uint)Index < (uint)_world.Meta.Length && _world.Meta[Index].Version == Version &&
                           _world.Meta[Index].Archetype != null;

    /// <summary>
    /// Whether the entity has component <typeparamref name="T"/>; false for a destroyed or default handle. O(1).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Has<T>() where T : struct, IComponent => !Unsafe.IsNullRef(ref Locate(ComponentType<T>.Bit, out _, false));

    // Has<T1..Tn>() is generated (Entity.Structural.g.cs) on top of HasAll.

    /// <summary>Whether the entity has every component of <paramref name="bits"/>: one meta lookup for all of them.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool HasAll(ReadOnlySpan<ushort> bits)
    {
        var archetype = LocateArchetype(out int slot, false);
        if (archetype == null) return false;
        for (int i = 0; i < bits.Length; i++)
            if (Unsafe.IsNullRef(ref Column(archetype, bits[i], slot, false))) return false;
        return true;
    }

    /// <summary>
    /// Ref to component <typeparamref name="T"/>; throws if the entity does not have it or is not alive. Counts as a
    /// write of a tracked component (<see cref="ITrackChanges"/>). O(1).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T Get<T>() where T : struct, IComponent
    {
        ref T value = ref Find<T>(true);
        if (default(T) is ITrackChanges) Touch<T>(_world, Index);
        return ref value;
    }

    /// <summary>
    /// Read-only ref to component <typeparamref name="T"/>; throws if the entity does not have it or is not alive. Not
    /// a write: a tracked component (<see cref="ITrackChanges"/>) is not stamped. O(1).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T Read<T>() where T : struct, IComponent
    {
        return ref Find<T>(true);
    }

    /// <summary>
    /// Ref to component <typeparamref name="T"/> when the entity has it (<paramref name="found"/>), else a null ref:
    /// check <paramref name="found"/> before using it. False when the entity is not alive. Counts as a write of a
    /// tracked component when found. O(1).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T TryGetRef<T>(out bool found) where T : struct, IComponent
    {
        ref T value = ref Find<T>(false);
        found = !Unsafe.IsNullRef(ref value);
        if (default(T) is ITrackChanges && found) Touch<T>(_world, Index);
        return ref value;
    }

    /// <summary>
    /// Copy of component <typeparamref name="T"/> if the entity has it; false when it does not or is not alive. Not a
    /// write. O(1).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGet<T>(out T value) where T : struct, IComponent
    {
        ref T found = ref Find<T>(false);
        bool has = !Unsafe.IsNullRef(ref found);
        value = has ? found : default;
        return has;
    }

    /// <summary>
    /// Ref to <typeparamref name="T"/> in the entity's row. When the entity does not have it (no column, or the row
    /// lost the component) or is not alive: a null ref, or with <paramref name="strict"/> (an inlined constant) a throw
    /// straight from the failing test, so an access has no null ref to test again. One meta line plus one line of the
    /// archetype's lookup.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref T Find<T>(bool strict) where T : struct, IComponent
    {
        ref var lookup = ref Locate(ComponentType<T>.Bit, out int slot, strict);
        if (!strict && Unsafe.IsNullRef(ref lookup)) return ref Unsafe.NullRef<T>();
        return ref Row<T>(ref lookup, slot);
    }

    /// <summary>Row <paramref name="slot"/> of component <typeparamref name="T"/> in <paramref name="archetype"/>, or a null ref when the row lacks it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ref T At<T>(Archetype archetype, int slot) where T : struct, IComponent
    {
        ref var lookup = ref Column(archetype, ComponentType<T>.Bit, slot, false);
        if (Unsafe.IsNullRef(ref lookup)) return ref Unsafe.NullRef<T>();
        return ref Row<T>(ref lookup, slot);
    }

    /// <summary><paramref name="value"/> into row <paramref name="slot"/> of <paramref name="archetype"/>, when the row has <typeparamref name="T"/> (<c>Run</c> after a move).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void CarryTo<T>(Archetype archetype, int slot, in T value) where T : struct, IComponent
    {
        ref T row = ref At<T>(archetype, slot);
        if (!Unsafe.IsNullRef(ref row)) row = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe ref T Row<T>(ref Archetype.Lookup lookup, int slot) where T : struct, IComponent
    {
        // A JIT-time constant: an unmanaged column's row 0 is cached in the entry (pinned, see Lookup.Rows).
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            return ref Unsafe.Add(ref Unsafe.AsRef<T>((void*)lookup.Rows), slot);
        return ref Unsafe.Add(ref Archetype.Row0<T>(lookup.Column), slot);
    }

    /// <summary>
    /// The column entry of <paramref name="bit"/> and the entity's <paramref name="slot"/>, or a null ref when the row
    /// lacks the component or the entity is not alive (shared by <see cref="Has{T}"/>, which needs no row address, and
    /// <see cref="Find{T}"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref Archetype.Lookup Locate(uint bit, out int slot, bool strict)
    {
        var archetype = LocateArchetype(out slot, strict);
        if (!strict && archetype == null) return ref Unsafe.NullRef<Archetype.Lookup>();
        return ref Column(archetype, bit, slot, strict);
    }

    /// <summary>
    /// The entity's table and row; when the handle is destroyed or default, null (or with <paramref name="strict"/>
    /// a throw).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Archetype LocateArchetype(out int slot, bool strict)
    {
        var world = _world;
        if (world == null)
        {
            if (strict) ThrowStale();
            slot = 0;
            return null;
        }

        ref var meta = ref world.MetaRef(Index); // ids of this world's handles are always < Meta.Length
        slot = meta.Slot;
        if (meta.Version != Version)
        {
            if (strict) ThrowStale();
            return null;
        }

        // Matching versions mean a live entity, which has a table: destroying an entity, restoring a snapshot and
        // disposing the world all move the version of every record without one.
        return meta.Archetype;
    }

    /// <summary>The column entry of <paramref name="bit"/> in <paramref name="archetype"/> when row <paramref name="slot"/> has it, else a null ref.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref Archetype.Lookup Column(Archetype archetype, uint bit, int slot, bool strict)
    {
        var map = archetype.ByBit;
        if (bit >= (uint)map.Length) return ref Missing(bit, strict);
        // bit < map.Length: no bounds check. Column and presence bits share the entry's cache line.
        ref var lookup = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(map), bit);
        if (lookup.Column == null) return ref Missing(bit, strict);
        // Presence bits cover the table's capacity and the slot is below it: no bounds check.
        if (lookup.Bits is { } bits && (Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(bits), (uint)slot >> 6) & (1UL << slot)) == 0)
            return ref Missing(bit, strict);
        return ref lookup;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref Archetype.Lookup Missing(uint bit, bool strict)
    {
        if (strict) ThrowMissing(bit);
        return ref Unsafe.NullRef<Archetype.Lookup>();
    }

    // Structural changes and cold paths take the handle by value: a loop calling them keeps its handle in registers
    // (a call through `this` would take its address and store it to memory on every row, even when the call is rare).
    [MethodImpl(MethodImplOptions.NoInlining)] // structural change: keeps the loops that call it lean
    private static ref T AddCore<T>(World world, int id, int version) where T : struct, IComponent
    {
        if (world == null) ThrowStale(); // a default handle
        using (world.Structural())
        {
            ref var meta = ref world.Live(new Entity(world, id, version));
            ushort bit = ComponentType<T>.Bit;
            if (ComponentType<T>.Unique) world.Claim(id, default(T)); // the default's key, before the row moves
            world.AddBit(ref meta, id, bit);
            ref T value = ref World.Row<T>(ref meta, bit);
            value = default;
            meta.Archetype.Stamp<T>(meta.Slot, world.ChangeTick);
            if (ComponentType<T>.Unique) world.IndexUnique(id, version, in value); // a unique index knows every key
            return ref value;
        }
    }

    /// <summary>
    /// Ref to component <typeparamref name="T"/>, adding it (default) first if missing. Counts as a write of a tracked
    /// component. O(1); when missing, O(1) if the entity's table has a column for it (the row only gets its presence bit
    /// back), else O(c) amortized: the row moves to the table with the column (c components copied).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T GetOrAdd<T>() where T : struct, IComponent
    {
        ref T value = ref Find<T>(false);
        if (Unsafe.IsNullRef(ref value)) return ref AddCore<T>(_world, Index, Version); // throws for a handle that is not alive
        if (default(T) is ITrackChanges) Touch<T>(_world, Index);
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

    /// <summary>
    /// Disable component <typeparamref name="T"/>: the entity keeps its value but lacks it (queries, <c>Has</c>,
    /// <c>Get</c>) until <see cref="Enable{T}"/>, and gets the tag <see cref="Disabled{T}"/>. Counts as a removal for
    /// <c>[Removed&lt;T&gt;]</c>; an indexed component leaves its index. Returns false when the entity lacks
    /// <typeparamref name="T"/> (or has it disabled already); throws if it is not alive. O(1); the first disable in a
    /// table moves the row once (to the table with the tag's column). Not for an <see cref="ICleanupComponent"/> (it must
    /// stay to be released) or <see cref="ChildOf"/> (a parent's destruction must find its children): those throw.
    /// Owner thread only.
    /// </summary>
    public bool Disable<T>() where T : struct, IComponent
    {
        ThrowIfNotDisableable<T>();
        if (_world == null) ThrowStale();
        using (_world!.Structural())
        {
            ref var meta = ref _world.Live(this);
            return _world.DisableBit(ref meta, Index, ComponentType<T>.Bit, DisabledType<T>.Bit);
        }
    }

    /// <summary>Throws for a component <c>Disable</c> refuses (a cleanup component, <see cref="ChildOf"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ThrowIfNotDisableable<T>() where T : struct, IComponent
    {
        if (default(T) is ICleanupComponent || typeof(T) == typeof(ChildOf)) ThrowNotDisabled<T>(); // JIT-time constants
    }

    [DoesNotReturn, MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowNotDisabled<T>() =>
        throw new InvalidOperationException($"{typeof(T).Name} cannot be disabled: remove it instead");

    /// <summary>
    /// Give back a component disabled by <see cref="Disable{T}"/>, with the value it kept (<c>Set</c> would replace it).
    /// Counts as a write of a tracked component; an indexed one rejoins its index. Returns false when
    /// <typeparamref name="T"/> is not disabled; throws if the entity is not alive. O(1). Owner thread only.
    /// </summary>
    public bool Enable<T>() where T : struct, IComponent
    {
        if (_world == null) ThrowStale();
        using (_world!.Structural())
        {
            return _world.EnableRow<T>(ref _world.Live(this), Index, Version);
        }
    }

    /// <summary>Whether component <typeparamref name="T"/> is disabled (<see cref="Disable{T}"/>): the entity has <see cref="Disabled{T}"/>. O(1).</summary>
    public bool IsDisabled<T>() where T : struct, IComponent => Has<Disabled<T>>();

    /// <summary>
    /// A new entity of the same world with a copy of every component of this one (a <see cref="DynamicBuffer{T}"/> gets
    /// a list of its own, indexes learn the copy, tracked components count as written now); throws if this entity is
    /// not alive. To create entities from a template, use a <see cref="Prefab"/>. O(components). Owner thread only.
    /// </summary>
    public Entity Clone()
    {
        if (_world == null) ThrowStale();
        return _world!.Copy(this, true);
    }

    /// <summary>
    /// Boxed copies of the entity's components, disabled ones marked; none when it is not alive. For diagnostics (the
    /// debugger shows the same): cold, allocates. O(components). Owner thread only.
    /// </summary>
    public ComponentValue[] Describe() => World.Describe(this);

    /// <summary>Destroy the entity; a destroyed handle is ignored. O(components).</summary>
    public void Destroy() => _world?.DestroyEntity(this);

    // Throw-only helpers without NoInlining: the JIT then sees they never return, keeps the throw out of the
    // caller's register allocation and never inlines them anyway.
    [DoesNotReturn]
    internal static void ThrowStale() => throw new InvalidOperationException("Entity is not alive (destroyed, or a default handle)");

    [DoesNotReturn]
    private static void ThrowMissing(uint bit) =>
        throw new InvalidOperationException($"Entity has no {ComponentInfo.Of((int)bit).Type.Name} component");

    /// <summary>Whether both handles name the same entity of the same world: id and version. O(1).</summary>
    public bool Equals(Entity other) => Id == other.Id && ReferenceEquals(_world, other._world);

    /// <inheritdoc cref="Equals(Entity)"/>
    public override bool Equals(object obj) => obj is Entity other && Equals(other);

    /// <summary>A hash of the id and version. O(1).</summary>
    public override int GetHashCode() => Id.GetHashCode(); // cheap: hashed per dictionary lookup (relation indexes)

    /// <summary><c>Entity 3v1</c>: the id's index and version.</summary>
    public override string ToString() => $"Entity {Id}";

    /// <inheritdoc cref="Equals(Entity)"/>
    public static bool operator ==(Entity left, Entity right) => left.Equals(right);

    /// <summary>Whether the handles name different entities (or worlds). O(1).</summary>
    public static bool operator !=(Entity left, Entity right) => !left.Equals(right);
}
