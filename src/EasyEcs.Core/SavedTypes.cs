using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core;
using System.Threading;

namespace EasyEcs;

/// <summary>
/// The component and singleton types a save can name (<see cref="World.Save(System.Buffers.IBufferWriter{byte})"/>, <see cref="World.Load"/>): a save
/// records types by name, and loading resolves each name to a type of this process. The generator registers every
/// component and singleton of a compilation (and every <c>DynamicBuffer&lt;T&gt;</c> it mentions) when the module
/// loads; a type gets no bit until a load or the world uses it. Types the world used are known anyway. Generated code
/// calls this; user code does not need to.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class SavedTypes
{
    private static readonly Lock Gate = new();
    // By save name, the types registered under it: one loads, several are ambiguous (two assemblies, or two versions
    // of a reloaded one until the old one unloads).
    private static readonly Dictionary<string, List<(Type Type, Func<ushort> Bit)>> Components = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, List<(Type Type, Func<SingletonBox> Box)>> Singletons = new(StringComparer.Ordinal);

    // Loads look names up as they lie in the bytes (no string is made).
    private static readonly Dictionary<string, List<(Type Type, Func<ushort> Bit)>>.AlternateLookup<ReadOnlySpan<char>> ComponentsByName =
        Components.GetAlternateLookup<ReadOnlySpan<char>>();
    private static readonly Dictionary<string, List<(Type Type, Func<SingletonBox> Box)>>.AlternateLookup<ReadOnlySpan<char>> SingletonsByName =
        Singletons.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>Make component <typeparamref name="T"/> loadable by name (no bit is assigned yet).</summary>
    public static void Component<T>() where T : struct, IComponent
    {
        lock (Gate) Add(Components, typeof(T), static () => ComponentType<T>.Bit);
    }

    /// <summary>Make singleton <typeparamref name="T"/> loadable by name.</summary>
    public static void Singleton<T>() where T : struct, ISingletonComponent
    {
        lock (Gate) Add(Singletons, typeof(T), static () => new SingletonBox<T>());
    }

    /// <summary>
    /// Tell <see cref="World.LoadEntities"/> where the <see cref="EntityId"/> fields of <typeparamref name="T"/> (a
    /// component or a buffer's element) are, so a migration points them at the moved entities. Each accessor returns
    /// one field of the value it is given (generated with <c>UnsafeAccessor</c>, private fields too).
    /// </summary>
    public static void EntityIds<T>(params ReadOnlySpan<EntityIdField<T>> fields) where T : struct
    {
        T probe = default;
        ref byte start = ref Unsafe.As<T, byte>(ref probe);
        var offsets = new int[fields.Length];
        for (int i = 0; i < fields.Length; i++)
            offsets[i] = (int)Unsafe.ByteOffset(ref start, ref Unsafe.As<EntityId, byte>(ref fields[i](ref probe)));
        EntityIdOffsets<T>.Value = offsets;
    }

    /// <summary>
    /// Write unmanaged <typeparamref name="T"/> (a component or a buffer's element) in Nino's canonical layout: rows are
    /// block copies with padding bytes cleared, so equal worlds save equal bytes. <paramref name="layout"/> is a hash of
    /// its fields (names, types, layout attributes): a load refuses rows saved with another one.
    /// </summary>
    public static void Raw<T>(int layout) where T : unmanaged
    {
        RawLayout<T>.Rows ??= new RawRows<T>();
        if (layout != 0) RawLayout<T>.Hash = layout; // 0: registered by a module that cannot see all of T's fields
    }

    /// <summary>Write unmanaged rows of <typeparamref name="T"/>: canonical when registered (<see cref="Raw{T}"/>), else as they are in memory.</summary>
    internal static void WriteRows<T>(ref Writer writer, ReadOnlySpan<byte> rows)
    {
        if (RawLayout<T>.Rows is { } raw) raw.Write(ref writer, rows);
        else writer.WriteRaw(rows);
    }

    /// <summary>Read rows written by <see cref="WriteRows{T}"/> into <paramref name="rows"/>.</summary>
    internal static void ReadRows<T>(ref Reader reader, Span<byte> rows)
    {
        if (RawLayout<T>.Rows is { } raw) raw.Read(ref reader, rows);
        else reader.ReadRaw(rows.Length).CopyTo(rows);
    }

    /// <summary>The canonical writer and the layout hash of unmanaged <typeparamref name="T"/> (null and 0 until registered).</summary>
    internal static class RawLayout<T>
    {
        public static RawRows Rows;
        public static int Hash;
    }

    internal abstract class RawRows
    {
        public abstract void Write(ref Writer writer, ReadOnlySpan<byte> rows);

        public abstract void Read(ref Reader reader, Span<byte> rows);
    }

    private sealed class RawRows<T> : RawRows where T : unmanaged
    {
        public override void Write(ref Writer writer, ReadOnlySpan<byte> rows) => writer.WriteUnmanagedRaw(MemoryMarshal.Cast<byte, T>(rows));

        public override void Read(ref Reader reader, Span<byte> rows) => reader.ReadUnmanagedRaw(MemoryMarshal.Cast<byte, T>(rows));
    }

    /// <summary>
    /// Register <paramref name="type"/> under its save name. Names leave out assemblies: when two types share one (in
    /// two assemblies), neither may load the other's rows, so a load of the name fails until one of them is forgotten
    /// (its context unloaded). Inside the lock.
    /// </summary>
    private static void Add<TValue>(Dictionary<string, List<(Type Type, TValue Value)>> types, Type type, TValue value)
    {
        string name = type.ToString();
        if (!types.TryGetValue(name, out var candidates)) types[name] = candidates = [];
        foreach (var candidate in candidates)
            if (candidate.Type == type)
                return;
        candidates.Add((type, value));
    }

    /// <summary>Forget the types <paramref name="unloading"/> matches: their collectible context is unloading.</summary>
    internal static void Forget(Func<Type, bool> unloading)
    {
        lock (Gate)
        {
            Forget(Components, unloading);
            Forget(Singletons, unloading);
        }
    }

    private static void Forget<TValue>(Dictionary<string, List<(Type Type, TValue Value)>> types, Func<Type, bool> unloading)
    {
        List<string> empty = null;
        foreach (var (name, candidates) in types)
        {
            candidates.RemoveAll(candidate => unloading(candidate.Type));
            if (candidates.Count == 0) (empty ??= []).Add(name);
        }

        if (empty != null)
            foreach (var name in empty)
                types.Remove(name);
    }

    private static InvalidDataException Ambiguous(ReadOnlySpan<char> name) =>
        new($"Two types of this process are saved as {name}: rename one to load it");

    /// <summary>The bit of the component saved as <paramref name="name"/>, or -1 when this process has no such type.</summary>
    internal static int FindComponent(ReadOnlySpan<char> name)
    {
        Func<ushort> bit;
        lock (Gate)
        {
            if (!ComponentsByName.TryGetValue(name, out var candidates)) return -1;
            if (candidates.Count > 1) throw Ambiguous(name);
            bit = candidates[0].Bit;
        }

        return bit(); // outside the lock: registering the type takes others
    }

    /// <summary>A new box of the singleton saved as <paramref name="name"/>, or null when this process has no such type.</summary>
    internal static SingletonBox NewSingleton(ReadOnlySpan<char> name)
    {
        Func<SingletonBox> create;
        lock (Gate)
        {
            if (!SingletonsByName.TryGetValue(name, out var candidates)) return null;
            if (candidates.Count > 1) throw Ambiguous(name);
            create = candidates[0].Box;
        }

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
