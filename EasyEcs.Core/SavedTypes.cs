using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;

namespace EasyEcs;

/// <summary>
/// The component and singleton types a save can name (<see cref="World.Save"/>, <see cref="World.Load"/>): a save
/// records types by name, and loading resolves each name to a type of this process. The generator registers every
/// component and singleton of a compilation (and every <c>DynamicBuffer&lt;T&gt;</c> it mentions) when the module
/// loads; a type gets no bit until a load or the world uses it. Types the world used are known anyway. Generated code
/// calls this; user code does not need to.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class SavedTypes
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, Func<ushort>> Components = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Func<SingletonBox>> Singletons = new(StringComparer.Ordinal);

    /// <summary>Make component <typeparamref name="T"/> loadable by name (no bit is assigned yet).</summary>
    public static void Component<T>() where T : struct, IComponent
    {
        lock (Gate) Components.TryAdd(NameOf<T>.Value, static () => ComponentType<T>.Bit);
    }

    /// <summary>Make singleton <typeparamref name="T"/> loadable by name.</summary>
    public static void Singleton<T>() where T : struct, ISingletonComponent
    {
        lock (Gate) Singletons.TryAdd(NameOf<T>.Value, static () => new SingletonBox<T>());
    }

    /// <summary>
    /// Tell <see cref="World.LoadEntities"/> where the <see cref="EntityId"/> fields of <typeparamref name="T"/> (a
    /// component or a buffer's element) are, so a migration points them at the moved entities. Each accessor returns
    /// one field of the value it is given (generated with <c>UnsafeAccessor</c>, private fields too).
    /// </summary>
    public static void EntityIds<T>(params EntityIdField<T>[] fields) where T : struct
    {
        T probe = default;
        ref byte start = ref Unsafe.As<T, byte>(ref probe);
        var offsets = new int[fields.Length];
        for (int i = 0; i < fields.Length; i++)
            offsets[i] = (int)Unsafe.ByteOffset(ref start, ref Unsafe.As<EntityId, byte>(ref fields[i](ref probe)));
        EntityIdOffsets<T>.Value = offsets;
    }

    /// <summary>The bit of the component saved as <paramref name="name"/>, or -1 when this process has no such type.</summary>
    internal static int FindComponent(string name)
    {
        Func<ushort> bit;
        lock (Gate)
            if (!Components.TryGetValue(name, out bit)) return -1;
        return bit(); // outside the lock: registering the type takes others
    }

    /// <summary>A new box of the singleton saved as <paramref name="name"/>, or null when this process has no such type.</summary>
    internal static SingletonBox NewSingleton(string name)
    {
        Func<SingletonBox> create;
        lock (Gate)
            if (!Singletons.TryGetValue(name, out create)) return null;
        return create();
    }

    /// <summary>Byte offsets of the <see cref="EntityId"/> fields of <typeparamref name="T"/> (none until registered).</summary>
    internal static class EntityIdOffsets<T>
    {
        public static int[] Value = [];
    }

    /// <summary>
    /// The name a save records <typeparamref name="T"/> by: namespace, nesting and type arguments without assemblies
    /// (<c>Game.Health</c>, <c>EasyEcs.DynamicBuffer`1[Game.Item]</c>), so it survives version changes.
    /// </summary>
    internal static class NameOf<T>
    {
        public static readonly string Value = typeof(T).ToString();
    }
}

/// <summary>An <see cref="EntityId"/> field of <paramref name="value"/> (see <see cref="SavedTypes.EntityIds{T}"/>).</summary>
public delegate ref EntityId EntityIdField<T>(ref T value);
