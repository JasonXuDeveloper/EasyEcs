using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace EasyEcs.Core.Components;

/// <summary>
/// Global component type registry. Thread-safe and shared across all Context instances.
/// Component types are registered once globally and persist for the lifetime of the process.
/// </summary>
internal static class TagRegistry
{
    private interface ITypeBitIndex
    {
        void Reset();
    }

    private class TypeBitIndex<T> : ITypeBitIndex
    {
        // Volatile fields for cross-thread visibility
        internal volatile ushort BitIndex;  // Supports up to 65536 component types
        internal volatile bool IsRegistered;
        public static readonly TypeBitIndex<T> Instance = new();

        public void Reset()
        {
            BitIndex = 0;
            IsRegistered = false;
        }
    }

    // Global lock for thread-safe registration
    private static readonly object GlobalLock = new();

    // Global tag counter - thread-safe via lock
    private static int _tagCount;

    // Track all registered tags for cleanup (primarily for testing scenarios)
    private static readonly List<ITypeBitIndex> Tags = new();

    /// <summary>
    /// Clear all registered component types.
    /// WARNING: Only use this in testing scenarios. This will invalidate all existing Tags and Archetypes.
    /// </summary>
    public static void Clear()
    {
        lock (GlobalLock)
        {
            // Reset all registered types
            for (int i = 0; i < Tags.Count; i++)
            {
                Tags[i].Reset();
            }

            Tags.Clear();
            _tagCount = 0;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool HasTag<T>() where T : struct
    {
        return TypeBitIndex<T>.Instance.IsRegistered;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort GetTagBitIndex<T>() where T : struct
    {
        var instance = TypeBitIndex<T>.Instance;
        if (!instance.IsRegistered)
        {
            throw new InvalidOperationException("Tag not registered");
        }

        return instance.BitIndex;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryGetTagBitIndex<T>(out ushort bitIndex) where T : struct
    {
        var instance = TypeBitIndex<T>.Instance;
        if (!instance.IsRegistered)
        {
            bitIndex = 0;
            return false;
        }

        bitIndex = instance.BitIndex;
        return true;
    }

    /// <summary>
    /// Get existing tag index or register a new one if it doesn't exist.
    /// Supports up to 65536 component types (ushort max).
    /// Thread-safe: uses global lock for registration.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort GetOrRegisterTag<T>() where T : struct
    {
        var instance = TypeBitIndex<T>.Instance;

        // Fast path: already registered (volatile read ensures visibility)
        if (instance.IsRegistered)
            return instance.BitIndex;

        // Slow path: need to register
        lock (GlobalLock)
        {
            // Double-check after acquiring lock
            if (instance.IsRegistered)
                return instance.BitIndex;

            // Register new tag - limited to 65536 component types (ushort max)
            if (_tagCount >= ushort.MaxValue)
                throw new InvalidOperationException($"Maximum number of component types reached ({ushort.MaxValue})");

            ushort bitIndex = (ushort)_tagCount;
            instance.BitIndex = bitIndex;
            instance.IsRegistered = true;  // Volatile write ensures visibility
            _tagCount++;
            Tags.Add(instance);

            return bitIndex;
        }
    }
}