using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EasyEcs.Core.Components;

/// <summary>
/// Optimized component reference with packed ID+version and Unsafe access.
/// 24 bytes total (down from 32+).
/// </summary>
public readonly struct ComponentRef<T> where T : struct, IComponent
{
    // Pack entity ID (lower 32 bits) + version (upper 32 bits)
    private readonly long _packed;
    private readonly byte _componentIndex;
    private readonly Context _context;

    public ComponentRef(int entityId, int version, byte componentIndex, Context context)
    {
        _packed = ((long)version << 32) | (uint)entityId;
        _componentIndex = componentIndex;
        _context = context;
    }

    private int EntityId
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (int)_packed;
    }

    private int Version
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (int)(_packed >> 32);
    }

    public ref T Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        get
        {
            int id = EntityId;
            int version = Version;

            // Validate version using Unsafe (zero bounds checks)
            ref int actualVersion = ref Unsafe.Add(
                ref MemoryMarshal.GetArrayDataReference(_context.EntityVersions),
                id);

            if (actualVersion != version)
                ThrowEntityDestroyed();

            // Bounds checking for Components array
            if (_context.Components == null || _componentIndex >= _context.Components.Length)
                ThrowComponentNotInitialized();

            var componentArrayObj = _context.Components[_componentIndex];
            if (componentArrayObj == null)
                ThrowComponentNotInitialized();

            // Get component array and access using Unsafe (zero bounds checks)
            var componentArray = Unsafe.As<T[]>(componentArrayObj);

            return ref Unsafe.Add(
                ref MemoryMarshal.GetArrayDataReference(componentArray),
                id);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowEntityDestroyed() =>
        throw new InvalidOperationException("Entity has been destroyed");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowComponentNotInitialized() =>
        throw new InvalidOperationException($"Component array for {typeof(T).Name} is not initialized");

    public static implicit operator T(ComponentRef<T> componentRef)
    {
        return componentRef.Value;
    }
}

public readonly struct SingletonComponentRef<T> where T : struct, ISingletonComponent
{
    public ref T Value => ref Singleton<T>.Instance.Value;

    public static implicit operator T(SingletonComponentRef<T> componentRef)
    {
        return componentRef.Value;
    }
}