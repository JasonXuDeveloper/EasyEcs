using System;
using System.Runtime.CompilerServices;
using System.Threading;
namespace EasyEcs;

// Singletons: one value of a struct type per world (clocks, configuration, shared state of a world).
public sealed partial class World
{
    // Indexed by SingletonType<T>.Index; each slot is a SingletonBox<T> (stable address for refs).
    private object[] _singletons = [];

    /// <summary>Add (or reset) singleton <typeparamref name="T"/> and return a ref to it. O(1).</summary>
    public ref T AddSingleton<T>() where T : struct, ISingletonComponent
    {
        using (Structural())
            return ref Publish<T>().Value;
    }

    /// <summary>Publish a fresh box for <typeparamref name="T"/>. Inside the structural lock.</summary>
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
    /// Stable handle to singleton <typeparamref name="T"/> (added first if missing): resolve it once, then read and write
    /// <see cref="SingletonRef{T}.Value"/> without a lookup. Generated <c>[ForEach]</c> jobs use it for singleton
    /// parameters. O(1).
    /// </summary>
    public SingletonRef<T> SingletonRef<T>() where T : struct, ISingletonComponent
    {
        return new SingletonRef<T>(Box<T>() ?? AddMissingSingleton<T>());
    }

    /// <summary>
    /// Add <typeparamref name="T"/> unless another thread did first (systems of one parallel batch may resolve the same
    /// singleton concurrently; a plain add would reset the other's value). Cold path.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private SingletonBox<T> AddMissingSingleton<T>() where T : struct, ISingletonComponent
    {
        using (Structural())
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasSingleton<T>() where T : struct, ISingletonComponent => Box<T>() != null;

    /// <summary>Remove singleton <typeparamref name="T"/>. O(1).</summary>
    public void RemoveSingleton<T>() where T : struct, ISingletonComponent
    {
        using (Structural())
        {
            int index = SingletonType<T>.Index;
            if (index < _singletons.Length)
                _singletons[index] = null;
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowSingletonMissing(Type type) =>
        throw new InvalidOperationException($"Singleton {type.Name} was not added to this world");
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
