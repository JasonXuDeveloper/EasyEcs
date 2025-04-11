using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace EasyEcs.Core.Components;

internal class TagRegistry
{
    private readonly Dictionary<Type, byte> _typeToBitIndex = new();
    public int TagCount => _typeToBitIndex.Count;

    public bool HasTag(Type type) => _typeToBitIndex.ContainsKey(type);

    public void RegisterTag(Type type)
    {
        if (_typeToBitIndex.ContainsKey(type))
        {
            return;
        }

        if (TagCount == Unsafe.SizeOf<Tag>())
        {
            throw new InvalidOperationException("Maximum number of tags is reached");
        }

        _typeToBitIndex.Add(type, (byte)TagCount);
    }

    public byte GetTagBitIndex(Type type)
    {
        if (!_typeToBitIndex.TryGetValue(type, out var bitIndex))
        {
            throw new InvalidOperationException($"Tag {type} not registered");
        }

        return bitIndex;
    }

    public bool TryGetTagBitIndex(Type type, out byte bitIndex)
    {
        return _typeToBitIndex.TryGetValue(type, out bitIndex);
    }
}