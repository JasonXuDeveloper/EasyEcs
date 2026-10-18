using System;
using System.Runtime.CompilerServices;

namespace EasyEcs;

/// <summary>
/// The identity of an entity: its index and the incarnation of that index (8 bytes, unmanaged). Store it wherever an
/// entity is referred to from data (a relation, a target, an owner, a message): a component holding only unmanaged
/// values keeps an unmanaged column, which is pinned, copied as a block and never scanned by the GC, and serializes as
/// raw bytes. Turn it back into a handle with <see cref="World.TryGetEntity(EntityId, out Entity)"/>; a handle converts
/// to its id implicitly. <c>default</c> is <see cref="Null"/>, which no entity has.
/// </summary>
public readonly struct EntityId : IEquatable<EntityId>
{
    // Version first: a row of a table is an EntityId, and read as a long, a tombstone (negative index) is negative.

    /// <summary>Incarnation of <see cref="Index"/>; starts at 1 and grows each time an entity with the index is destroyed.</summary>
    public readonly int Version;

    /// <summary>Entity index (dense, recycled after destruction).</summary>
    public readonly int Index;

    /// <summary>The id of entity <paramref name="index"/>, incarnation <paramref name="version"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EntityId(int index, int version)
    {
        Version = version;
        Index = index;
    }

    /// <summary>The id no entity has (<c>default</c>): an empty reference.</summary>
    public static EntityId Null => default;

    /// <summary>Whether this is <see cref="Null"/>.</summary>
    public bool IsNull => Version == 0;

    /// <inheritdoc/>
    public bool Equals(EntityId other) =>
        Unsafe.As<EntityId, long>(ref Unsafe.AsRef(in this)) == Unsafe.As<EntityId, long>(ref other);

    /// <inheritdoc/>
    public override bool Equals(object obj) => obj is EntityId other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Index ^ (Version << 20);

    /// <summary>Equal ids name the same incarnation of the same entity.</summary>
    public static bool operator ==(EntityId left, EntityId right) => left.Equals(right);

    /// <summary>Different ids name different entities or incarnations.</summary>
    public static bool operator !=(EntityId left, EntityId right) => !left.Equals(right);

    /// <inheritdoc/>
    public override string ToString() => IsNull ? "Null" : $"{Index}v{Version}";
}
