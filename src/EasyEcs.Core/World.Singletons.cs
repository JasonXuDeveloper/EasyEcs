using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
namespace EasyEcs;

// Singletons: one value of a struct type per world (clocks, configuration, shared state of a world).
public sealed partial class World
{
    // Indexed by SingletonType<T>.Index; each slot is a SingletonBox<T> (stable address for refs).
    private object[] _singletons = [];

    /// <summary>
    /// Add singleton <typeparamref name="T"/> (default) and return a ref to it. O(1).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The world has <typeparamref name="T"/> already: replace its value with <c>world.SetX(...)</c> (or
    /// <c>world.X = ...</c>), or take it as it is with <c>GetOrAddX()</c>.
    /// </exception>
    public ref T AddSingleton<T>() where T : struct, ISingletonComponent
    {
        using (Structural())
        {
            if (Box<T>() != null) ThrowSingletonExists(typeof(T));
            return ref Publish<T>().Value;
        }
    }

    /// <summary>Publish a fresh box for <typeparamref name="T"/>. Inside the structural gate.</summary>
    private SingletonBox<T> Publish<T>() where T : struct, ISingletonComponent
    {
        int index = SingletonType<T>.Index;
        if (index >= _singletons.Length)
            Array.Resize(ref _singletons, Math.Max(index + 1, _singletons.Length * 2));

        var box = new SingletonBox<T>();
        // Volatile publish so lock-free readers on other threads observe a fully constructed box.
        Volatile.Write(ref _singletons[index], box);
        return box;
    }

    /// <summary>Ref to singleton <typeparamref name="T"/>; throws if it was not added. O(1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T GetSingleton<T>() where T : struct, ISingletonComponent
    {
        var box = Box<T>();
        if (box == null) ThrowSingletonMissing(typeof(T));
        return ref box!.Value;
    }

    /// <summary>Ref to singleton <typeparamref name="T"/>, adding it (default) first if missing. O(1).</summary>
    public ref T GetOrAddSingleton<T>() where T : struct, ISingletonComponent
    {
        var box = Box<T>() ?? AddMissingSingleton<T>();
        return ref box.Value;
    }

    /// <summary>
    /// Stable handle to singleton <typeparamref name="T"/>; throws if it was not added: resolve it once, then read and
    /// write <see cref="SingletonRef{T}.Value"/> without a lookup. Generated <c>[ForEach]</c> jobs resolve singleton
    /// parameters through it once per pass, so a system whose singleton was never set fails on its first run. O(1).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SingletonRef<T> SingletonRef<T>() where T : struct, ISingletonComponent
    {
        var box = Box<T>();
        if (box == null) ThrowSingletonMissing(typeof(T));
        return new(box!);
    }

    /// <summary>
    /// Add <typeparamref name="T"/> unless another thread did first (systems of one parallel stage may resolve the same
    /// singleton concurrently; a plain add would reset the other's value). Cold path.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private SingletonBox<T> AddMissingSingleton<T>() where T : struct, ISingletonComponent
    {
        if (_disposed) throw new ObjectDisposedException(nameof(World)); // the shared gate does not test the owner's flag
        using (Shared())
        {
            return Box<T>() ?? Publish<T>();
        }
    }

    /// <summary>Copy of singleton <typeparamref name="T"/> if present. O(1).</summary>
    public bool TryGetSingleton<T>(out T value) where T : struct, ISingletonComponent
    {
        var box = Box<T>();
        value = box != null ? box.Value : default;
        return box != null;
    }

    /// <summary>Whether singleton <typeparamref name="T"/> exists. O(1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasSingleton<T>() where T : struct, ISingletonComponent => Box<T>() != null;

    /// <summary>Remove singleton <typeparamref name="T"/>; false if it was not added. O(1).</summary>
    public bool RemoveSingleton<T>() where T : struct, ISingletonComponent
    {
        using (Structural())
        {
            int index = SingletonType<T>.Index;
            if (index >= _singletons.Length || _singletons[index] == null) return false;
            Volatile.Write(ref _singletons[index], null); // as Publish: a reader on another thread sees it whole
            return true;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private SingletonBox<T> Box<T>() where T : struct, ISingletonComponent
    {
        int index = SingletonType<T>.Index;
        var singletons = _singletons;
        return (uint)index < (uint)singletons.Length ? Unsafe.As<SingletonBox<T>>(singletons[index]) : null;
    }

    private void ClearSingletons() => _singletons = [];

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowSingletonExists(Type type) =>
        throw new InvalidOperationException(
            $"Singleton {type.Name} was added to this world already: replace its value with world.Set{type.Name}(...) " +
            $"or world.{type.Name} = ..., or use GetOrAdd{type.Name}() to take it as it is");

    [DoesNotReturn] // no NoInlining: the JIT sees it never returns (Entity.ThrowStale)
    private static void ThrowSingletonMissing(Type type) =>
        throw new InvalidOperationException(
            $"Singleton {type.Name} was not added to this world: set it before the systems that read it run " +
            $"(world.Set{type.Name}(...) or world.AddSingleton<{type.Name}>()), or use GetOrAdd{type.Name}() where a default is meant");
}

/// <summary>
/// Handle to one world's singleton <typeparamref name="T"/>. The singleton's storage never moves, so the handle stays
/// valid until the singleton is removed (a removed singleton is detached: writes no longer reach the world).
/// </summary>
public readonly struct SingletonRef<T> where T : struct, ISingletonComponent
{
    private readonly SingletonBox<T> _box;

    internal SingletonRef(SingletonBox<T> box) => _box = box;

    /// <summary>The singleton's value, by reference.</summary>
    public ref T Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _box.Value;
    }
}
