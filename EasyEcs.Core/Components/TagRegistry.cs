using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace EasyEcs.Core.Components;

internal class TagRegistry
{
    private class TypeBitIndex<T>
    {
        internal byte BitIndex;
        internal bool IsRegistered;
        public static TypeBitIndex<T> Instance = new();
    }

    public int TagCount;
    private List<object> _tags = new();

    public void Clear()
    {
        foreach (var tag in _tags)
        {
            var type = tag.GetType();
            FieldInfo bitIndexField = type.GetField("BitIndex", BindingFlags.Instance | BindingFlags.NonPublic);
            if (bitIndexField != null)
            {
                bitIndexField.SetValue(tag, (byte)0);
            }

            FieldInfo isRegisteredField = type.GetField("IsRegistered", BindingFlags.Instance | BindingFlags.NonPublic);
            if (isRegisteredField != null)
            {
                isRegisteredField.SetValue(tag, false);
            }
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
}