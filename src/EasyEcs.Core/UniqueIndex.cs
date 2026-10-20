using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
namespace EasyEcs;

/// <summary>
/// What every <see cref="IUniqueIndexed{TKey}"/> is, so a component type is known to be unique without reflection (a
/// type test the JIT folds). Implement <see cref="IUniqueIndexed{TKey}"/>, not this.
/// </summary>
public interface IUniqueIndexed : IIndexed;

/// <summary>
/// An <see cref="IIndexed{TKey}"/> component whose key names one entity: a player's account id, a session token, the
/// occupant of a grid cell. A write that would give a second entity a key another entity has throws
/// <see cref="InvalidOperationException"/> before it changes anything (Set, CreateEntity / CreateEntities, GetOrAdd,
/// Instantiate, Clone, SetAll, Enable / EnableAll, LoadEntities / LoadEntitiesInto, MoveEntities), and a
/// <see cref="World.Load"/> of a save holding a key twice throws <see cref="System.IO.InvalidDataException"/>; the world
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
/// the module of <typeparamref name="T"/> loads (<see cref="SavedTypes.UniqueIndex{T,TKey}"/>, generated) or by the
/// first <see cref="World.Index{T,TKey}"/> of it in any world.
/// </summary>
internal static class UniqueIndexOf<T> where T : struct, IComponent
{
    internal static Func<World, World.IValueIndex> Create;
}
