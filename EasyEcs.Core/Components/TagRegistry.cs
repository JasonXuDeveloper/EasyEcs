using System;
using System.Collections.Generic;
using System.Reflection;
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
        internal byte BitIndex;
        internal bool IsRegistered;
        public static TypeBitIndex<T> Instance = new();

        public void Reset()
        {
            BitIndex = 0;
            IsRegistered = false;
        }
    }

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

    public void RegisterTag<T>() where T : struct
    {
        var instance = TypeBitIndex<T>.Instance;
        if (instance.IsRegistered)
        {
            return;
        }

        if (TagCount == Unsafe.SizeOf<Tag>())
        {
            throw new InvalidOperationException("Maximum number of tags is reached");
        }

        var bitIndex = (byte)TagCount;
        instance.BitIndex = bitIndex;
        instance.IsRegistered = true;
        TagCount++;
        _tags.Add(instance);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte GetTagBitIndex<T>() where T : struct
    {
        var instance = TypeBitIndex<T>.Instance;
        if (!instance.IsRegistered)
        {
            throw new InvalidOperationException($"Tag {typeof(T)} not registered");
        }

        return instance.BitIndex;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetTagBitIndex<T>(out byte bitIndex) where T : struct
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
    /// Thread-safe via caller's lock.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte GetOrRegisterTag<T>() where T : struct
    {
        var instance = TypeBitIndex<T>.Instance;
        if (instance.IsRegistered)
            return instance.BitIndex;

        // Register new tag
        if (TagCount >= 256 * 4)  // Max supported by Tag structure
            throw new InvalidOperationException("Maximum number of component types reached");

        var bitIndex = (byte)TagCount;
        instance.BitIndex = bitIndex;
        instance.IsRegistered = true;
        TagCount++;
        _tags.Add(instance);

        return bitIndex;
    }
}