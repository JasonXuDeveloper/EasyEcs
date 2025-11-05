using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace EasyEcs.Core.Components;

internal class TagRegistry
{
    private interface ITypeBitIndex
    {
        void Reset();
    }

    private class TypeBitIndex<T> : ITypeBitIndex
    {
        // Volatile fields for cross-thread visibility
        // Static instance shared across all Context instances by design
        internal volatile ushort BitIndex;  // Supports up to 65536 component types
        internal volatile bool IsRegistered;
        public static TypeBitIndex<T> Instance = new();

        public void Reset()
        {
            BitIndex = 0;
            IsRegistered = false;
        }
    }

    // Static lock for thread-safe registration across all Context instances
    private static readonly object GlobalRegistrationLock = new();

    public int TagCount;
    private List<ITypeBitIndex> _tags = new();

    public void Clear()
    {
        // Zero-allocation iteration with direct method call
        for (int i = 0; i < _tags.Count; i++)
        {
            _tags[i].Reset();
        }

        _tags.Clear();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasTag<T>() where T : struct
    {
        return TypeBitIndex<T>.Instance.IsRegistered;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort GetTagBitIndex<T>() where T : struct
    {
        var instance = TypeBitIndex<T>.Instance;
        if (!instance.IsRegistered)
        {
            throw new InvalidOperationException("Tag not registered");
        }

        return instance.BitIndex;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetTagBitIndex<T>(out ushort bitIndex) where T : struct
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
    /// Thread-safe: uses static lock to protect shared TypeBitIndex across contexts.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort GetOrRegisterTag<T>() where T : struct
    {
        var instance = TypeBitIndex<T>.Instance;

        // Fast path: already registered (volatile read ensures visibility)
        if (instance.IsRegistered)
            return instance.BitIndex;

        // Slow path: need to register (lock globally since TypeBitIndex is static)
        lock (GlobalRegistrationLock)
        {
            // Double-check after acquiring lock
            if (instance.IsRegistered)
                return instance.BitIndex;

            // Register new tag - limited to 65536 component types (ushort max)
            if (TagCount >= ushort.MaxValue)
                throw new InvalidOperationException($"Maximum number of component types reached ({ushort.MaxValue})");

            ushort bitIndex = (ushort)TagCount;
            instance.BitIndex = bitIndex;
            instance.IsRegistered = true;  // Volatile write ensures visibility
            TagCount++;
            _tags.Add(instance);

            return bitIndex;
        }
    }
}