using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
namespace EasyEcs;

/// <summary>
/// What every <see cref="IUniqueIndexed{TKey}"/> is, so a component type is known to be unique without reflection (a
/// type test the JIT folds). Implement <see cref="IUniqueIndexed{TKey}"/>, not this.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IUniqueIndexed : IIndexed;

/// <summary>
/// An <see cref="IIndexed{TKey}"/> component whose key names one entity: a player's account id, a session token, the
/// occupant of a grid cell. A write that would give a second entity a key another entity has throws
/// <see cref="InvalidOperationException"/> before it changes anything (Set, CreateEntity / CreateEntities, GetOrAdd,
/// Instantiate, Clone, SetAll, Enable / EnableAll, LoadEntities / LoadEntitiesInto, MoveEntities), and a
/// <see cref="World.Load(System.ReadOnlySpan{byte}, bool)"/> of a save holding a key twice throws <see cref="System.IO.InvalidDataException"/>; the world
/// is then as it was. Writing an entity's own key again is fine; to move a key from one entity to another, change the
/// first one before the second. A null key (the default of a reference-type key) is no key: any number of entities may
/// have it, and no lookup finds them. <see cref="EntityId"/> keys (relations) cannot be unique.
/// <br/>
/// The index of a unique component is eager: each world creates it with the first table holding the component, so
/// every write is checked from the first one (an <see cref="IIndexed{TKey}"/> index is built by its first lookup).
/// Look entities up with <c>world.Index&lt;T, TKey&gt;().Get(key)</c> / <c>TryGet</c> (the generated
/// <c>world.XIndex</c>); <c>Find</c>, <c>Count</c> and the ordered walks work as for any index.
/// </summary>
public interface IUniqueIndexed<TKey> : IIndexed<TKey>, IUniqueIndexed where TKey : IEquatable<TKey>;

/// <summary>
/// A unique component whose keys map to a bounded range of slots (grid cells, tiles, rooms, player slots): its index
/// keeps an array of <see cref="Slots"/> entries beside the hash, the entry <see cref="SlotOf"/> of a key holding the
/// key's entity, so <c>Get</c> / <c>TryGet</c> and the uniqueness check of a write read one array entry instead of
/// probing the hash. The hash stays the index: a key whose slot is out of range, or taken by another key (two keys of
/// one slot: a collision), is found through it, and <c>Find</c>, <c>Count</c> and the ordered walks use it as for
/// any unique index. A slot function folds the keys a world holds at once onto distinct slots (a chunk coordinate
/// modulo the side of the loaded area); its array costs <see cref="Slots"/> times the key and 12 bytes, allocated with
/// the index.
/// <code>
/// public record struct Cell(int X, int Y) : IComponent, IDirectMapped&lt;(int, int)&gt;
/// {
///     public (int, int) Key => (X, Y);
///     static int IDirectMapped&lt;(int, int)&gt;.Slots => 64 * 64;
///     static int IDirectMapped&lt;(int, int)&gt;.SlotOf((int, int) key) => (key.Item2 &amp; 63) * 64 + (key.Item1 &amp; 63);
/// }
/// </code>
/// </summary>
public interface IDirectMapped<TKey> : IUniqueIndexed<TKey> where TKey : IEquatable<TKey>
{
    /// <summary>The length of the slot array (cold: read once, when a world creates the index).</summary>
    static abstract int Slots { get; }

    /// <summary>
    /// The slot of <paramref name="key"/>, 0 to <see cref="Slots"/> - 1; any other value leaves the key to the hash.
    /// Called on every lookup and write of a key: keep it a few instructions, with no state.
    /// </summary>
    static abstract int SlotOf(TKey key);
}

/// <summary>Lookups of a unique index (<see cref="IUniqueIndexed{TKey}"/>): the one entity of a key.</summary>
public static class UniqueIndexExtensions
{
    extension<T, TKey>(ValueIndex<T, TKey> index)
        where T : struct, IComponent, IUniqueIndexed<TKey> where TKey : IEquatable<TKey>
    {
        /// <summary>
        /// The entity whose <typeparamref name="T"/> has key <paramref name="key"/>; throws
        /// <see cref="InvalidOperationException"/> when none has it. O(1): a hash probe. Owner thread only.
        /// </summary>
        public Entity Get(TKey key)
        {
            if (!index.TryFind(key, out var entity)) ThrowNone(key);
            return entity;
        }

        /// <summary>The entity whose <typeparamref name="T"/> has key <paramref name="key"/>, if one has it. O(1): a hash probe. Owner thread only.</summary>
        public bool TryGet(TKey key, out Entity entity) => index.TryFind(key, out entity);
    }

    [DoesNotReturn, MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowNone<TKey>(TKey key) => throw new InvalidOperationException($"No entity has key {key}");
}

/// <summary>
/// How a world creates the index of unique component <typeparamref name="T"/> without knowing its key type: set when
/// the module of <typeparamref name="T"/> loads (<see cref="TypeRegistry.UniqueIndex{T,TKey}"/>, generated) or by the
/// first <see cref="World.Index{T,TKey}"/> of it in any world.
/// </summary>
internal static class UniqueIndexOf<T> where T : struct, IComponent
{
    internal static Func<World, World.IValueIndex> Create;
}

/// <summary>
/// The slot function of a direct-indexed component (<see cref="IDirectMapped{TKey}"/>) for an index that knows only
/// its key type: one sealed subclass per component, set when its module loads (<see cref="TypeRegistry.DirectIndex{T,TKey}"/>,
/// generated). Without it, the index of such a component is a plain unique index.
/// </summary>
internal abstract class KeySlots<TKey>
{
    public abstract int Length { get; }

    public abstract int SlotOf(TKey key);
}

internal sealed class KeySlots<T, TKey> : KeySlots<TKey> where T : IDirectMapped<TKey> where TKey : IEquatable<TKey>
{
    public override int Length => T.Slots;

    public override int SlotOf(TKey key) => T.SlotOf(key);
}

/// <summary>The <see cref="KeySlots{TKey}"/> of component <typeparamref name="T"/>, when it is direct-indexed.</summary>
internal static class KeySlotsOf<T> where T : struct, IComponent
{
    internal static object Map;
}
