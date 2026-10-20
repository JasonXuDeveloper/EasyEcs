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
/// records each type by its Nino type id (<see cref="NinoTypeId"/>: a hash of its full name, or of the name its
/// <c>[NinoFormerName]</c> gives, so a renamed type keeps loading its rows), and loading resolves each id to a type
/// of this process. The generator registers every component and singleton of a compilation (and every
/// <c>DynamicBuffer&lt;T&gt;</c> it mentions) when the module loads; a type gets no bit until a load or the world uses
/// it. Types the world used are known anyway. Generated code calls this; user code does not need to.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class SavedTypes
{
    private static readonly Lock Gate = new();
    // By type id, the types registered under it: one loads, several are ambiguous (two types of one name in two
    // assemblies, two versions of a reloaded one until the old one unloads, a former name another type still has).
    private static readonly Dictionary<int, List<(Type Type, Func<ushort> Bit)>> Components = new();
    private static readonly Dictionary<int, List<(Type Type, Func<SingletonBox> Box)>> Singletons = new();
    // The components by type, for sets built from types a framework learns at run time (ComponentSet.With(Type)).
    private static readonly Dictionary<Type, Func<ushort>> ComponentsByType = new();

    /// <summary>Make component <typeparamref name="T"/> loadable by its type id (no bit is assigned yet).</summary>
    public static void Component<T>() where T : struct, IComponent
    {
        Func<ushort> bit = static () => ComponentType<T>.Bit;
        lock (Gate)
        {
            Add(Components, TypeId<T>.Value, typeof(T), bit);
            ComponentsByType.TryAdd(typeof(T), bit);
        }
    }

    /// <summary>Make singleton <typeparamref name="T"/> loadable by its type id.</summary>
    public static void Singleton<T>() where T : struct, ISingletonComponent
    {
        lock (Gate) Add(Singletons, TypeId<T>.Value, typeof(T), static () => new SingletonBox<T>());
    }

    /// <summary>
    /// Tell worlds how to create the index of unique component <typeparamref name="T"/> (<see cref="IUniqueIndexed{TKey}"/>):
    /// a world creates it with its first table holding <typeparamref name="T"/>, knowing only the component's bit.
    /// <see cref="EntityId"/> keys are not registered (they cannot be unique: <see cref="World.Index{T,TKey}"/> says why).
    /// </summary>
    public static void UniqueIndex<T, TKey>() where T : struct, IComponent, IUniqueIndexed<TKey> where TKey : IEquatable<TKey>
    {
        if (typeof(TKey) != typeof(EntityId)) UniqueIndexOf<T>.Create ??= static world => world.Index<T, TKey>();
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

    /// <summary>
    /// Write unmanaged rows of <typeparamref name="T"/>: by its Nino formatter when it is a <c>[NinoType]</c>
    /// (<see cref="Coded{T}"/>), else canonical when registered (<see cref="Raw{T}"/>), else as they are in memory.
    /// </summary>
    internal static void WriteRows<T>(ref Writer writer, ReadOnlySpan<byte> rows)
    {
        if (Coded<T>.Value)
        {
            var values = Values<T>(rows);
            for (int i = 0; i < values.Length; i++) NinoSerializer.Serialize(values[i], ref writer);
        }
        else if (RawLayout<T>.Rows is { } raw) raw.Write(ref writer, rows);
        else writer.WriteRaw(rows);
    }

    /// <summary>Read rows written by <see cref="WriteRows{T}"/> into <paramref name="rows"/>.</summary>
    internal static void ReadRows<T>(ref Reader reader, Span<byte> rows)
    {
        if (Coded<T>.Value)
        {
            var values = Values<T>(rows);
            for (int i = 0; i < values.Length; i++) NinoDeserializer.Deserialize(ref values[i], ref reader);
        }
        else if (RawLayout<T>.Rows is { } raw) raw.Read(ref reader, rows);
        else reader.ReadRaw(rows.Length).CopyTo(rows);
    }

    /// <summary>
    /// The rows of unmanaged <typeparamref name="T"/> in <paramref name="rows"/> (whole rows: the caller passes count ×
    /// size bytes of a column or a buffer, aligned for T).
    /// </summary>
    private static Span<T> Values<T>(ReadOnlySpan<byte> rows) =>
        MemoryMarshal.CreateSpan(ref Unsafe.As<byte, T>(ref MemoryMarshal.GetReference(rows)), rows.Length / Unsafe.SizeOf<T>());

    /// <summary>
    /// How a save writes the rows of <typeparamref name="T"/> (a component, or a buffer's element): the size of a raw
    /// row, 0 when Nino writes them (managed values, <c>[NinoType]</c> ones). A load refuses rows written another way.
    /// </summary>
    internal static int RowSize<T>() =>
        RuntimeHelpers.IsReferenceOrContainsReferences<T>() || Coded<T>.Value ? 0 : Unsafe.SizeOf<T>();

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
    /// Register <paramref name="type"/> under its type <paramref name="id"/>. When two types share one, neither may load
    /// the other's rows, so a load of the id fails until one of them is forgotten (its context unloaded) or renamed.
    /// Inside the lock.
    /// </summary>
    private static void Add<TValue>(Dictionary<int, List<(Type Type, TValue Value)>> types, int id, Type type, TValue value)
    {
        if (!types.TryGetValue(id, out var candidates)) types[id] = candidates = [];
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
            List<Type> gone = null;
            foreach (var type in ComponentsByType.Keys)
                if (unloading(type)) (gone ??= []).Add(type);
            if (gone != null)
                foreach (var type in gone)
                    ComponentsByType.Remove(type);
        }
    }

    private static void Forget<TValue>(Dictionary<int, List<(Type Type, TValue Value)>> types, Func<Type, bool> unloading)
    {
        List<int> empty = null;
        foreach (var (id, candidates) in types)
        {
            candidates.RemoveAll(candidate => unloading(candidate.Type));
            if (candidates.Count == 0) (empty ??= []).Add(id);
        }

        if (empty != null)
            foreach (var id in empty)
                types.Remove(id);
    }

    private static InvalidDataException Ambiguous<TValue>(int id, List<(Type Type, TValue Value)> candidates)
    {
        var names = new List<string>();
        foreach (var candidate in candidates) names.Add(candidate.Type.AssemblyQualifiedName);
        return new InvalidDataException($"Types of this process share the save id {id} ({string.Join(", ", names)}): " +
            "unload or rename one of them (one type per name, its own or the one its [NinoFormerName] keeps)");
    }

    /// <summary>The bit of the component saved as type <paramref name="id"/>, or -1 when this process has no such type.</summary>
    internal static int FindComponent(int id)
    {
        Func<ushort> bit;
        lock (Gate)
        {
            if (!Components.TryGetValue(id, out var candidates)) return -1;
            if (candidates.Count > 1) throw Ambiguous(id, candidates);
            bit = candidates[0].Bit;
        }

        return bit(); // outside the lock: registering the type takes others
    }

    /// <summary>The bit of component type <paramref name="type"/>, or -1 when it is not a registered component type.</summary>
    internal static int BitOf(Type type)
    {
        Func<ushort> bit;
        lock (Gate)
            if (!ComponentsByType.TryGetValue(type, out bit))
                return -1;
        return bit();
    }

    /// <summary>A new box of the singleton saved as type <paramref name="id"/>, or null when this process has no such type.</summary>
    internal static SingletonBox NewSingleton(int id)
    {
        Func<SingletonBox> create;
        lock (Gate)
        {
            if (!Singletons.TryGetValue(id, out var candidates)) return null;
            if (candidates.Count > 1) throw Ambiguous(id, candidates);
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
    /// The name of <typeparamref name="T"/> in messages: namespace, nesting and type arguments without assemblies
    /// (<c>Game.Health</c>, <c>EasyEcs.DynamicBuffer`1[Game.Item]</c>).
    /// </summary>
    internal static class NameOf<T>
    {
        public static readonly string Value = typeof(T).ToString();
    }

    /// <summary>The id a save records <typeparamref name="T"/> by (see <see cref="IdOf"/>).</summary>
    internal static class TypeId<T>
    {
        public static readonly int Value = IdOf(typeof(T));
    }

    /// <summary>
    /// The id a save records <paramref name="type"/> by: Nino's type id (the FNV-1a hash of its full name, or of its
    /// <c>[NinoFormerName]</c>). A <c>DynamicBuffer&lt;T&gt;</c> is named after its element's id name, so renaming
    /// the element keeps its buffers loading too. Computed without Nino's cache, which would keep collectible types alive.
    /// </summary>
    internal static int IdOf(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(DynamicBuffer<>)
            ? NinoTypeId.Compute("EasyEcs.DynamicBuffer<" + NinoTypeId.GetIdName(type.GetGenericArguments()[0]) + ">")
            : NinoTypeId.Compute(NinoTypeId.GetIdName(type));

    /// <summary>
    /// Whether unmanaged <typeparamref name="T"/> (a component or a buffer's element) is written by its Nino formatter
    /// (<c>[NinoType]</c>: its format decides which changes old saves survive) instead of as raw rows.
    /// </summary>
    internal static class Coded<T>
    {
        public static readonly bool Value = !RuntimeHelpers.IsReferenceOrContainsReferences<T>() && typeof(T).IsDefined(typeof(NinoTypeAttribute), false);
    }
}

/// <summary>An <see cref="EntityId"/> field of <paramref name="value"/> (see <see cref="SavedTypes.EntityIds{T}"/>).</summary>
public delegate ref EntityId EntityIdField<T>(ref T value);
