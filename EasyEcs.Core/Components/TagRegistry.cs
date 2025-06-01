using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace EasyEcs.Core.Components;

internal class TagRegistry
{
    private readonly Dictionary<IntPtr, byte> _typeToBitIndex = new();
    public int TagCount => _typeToBitIndex.Count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasTag(Type type) => _typeToBitIndex.ContainsKey(type.TypeHandle.Value);

    public void RegisterTag(Type type)
    {
        if (_typeToBitIndex.ContainsKey(type.TypeHandle.Value))
        {
            return;
        }

        if (TagCount == Unsafe.SizeOf<Tag>())
        {
            throw new InvalidOperationException("Maximum number of tags is reached");
        }

        _typeToBitIndex.Add(type.TypeHandle.Value, (byte)TagCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte GetTagBitIndex(Type type)
    {
        if (!_typeToBitIndex.TryGetValue(type.TypeHandle.Value, out var bitIndex))
        {
            throw new InvalidOperationException($"Tag {type} not registered");
        }

        return bitIndex;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetTagBitIndex(Type type, out byte bitIndex)
    {
        return _typeToBitIndex.TryGetValue(type.TypeHandle.Value, out bitIndex);
    }
}