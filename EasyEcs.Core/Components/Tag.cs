using System;
using System.Runtime.InteropServices;

namespace EasyEcs.Core.Components;

[StructLayout(LayoutKind.Sequential)]
internal struct Tag : IEquatable<Tag>, IComparable<Tag>
{
    private long _item1;
    private long _item2;
    private long _item3;
    private long _item4;

    public void SetBit(int bitIndex)
    {
        switch (bitIndex)
        {
            case < 64:
                _item1 |= 1L << bitIndex;
                break;
            case < 128:
                _item2 |= 1L << (bitIndex - 64);
                break;
            case < 192:
                _item3 |= 1L << (bitIndex - 128);
                break;
            default:
                _item4 |= 1L << (bitIndex - 192);
                break;
        }
    }

    public void ClearBit(int bitIndex)
    {
        switch (bitIndex)
        {
            case < 64:
                _item1 &= ~(1L << bitIndex);
                break;
            case < 128:
                _item2 &= ~(1L << (bitIndex - 64));
                break;
            case < 192:
                _item3 &= ~(1L << (bitIndex - 128));
                break;
            default:
                _item4 &= ~(1L << (bitIndex - 192));
                break;
        }
    }

    public bool HasBit(int bitIndex)
    {
        return bitIndex switch
        {
            < 64 => (_item1 & (1L << bitIndex)) != 0,
            < 128 => (_item2 & (1L << (bitIndex - 64))) != 0,
            < 192 => (_item3 & (1L << (bitIndex - 128))) != 0,
            _ => (_item4 & (1L << (bitIndex - 192))) != 0
        };
    }

    public static Tag operator &(Tag a, Tag b)
    {
        return new Tag
        {
            _item1 = a._item1 & b._item1,
            _item2 = a._item2 & b._item2,
            _item3 = a._item3 & b._item3,
            _item4 = a._item4 & b._item4
        };
    }

    public static Tag operator |(Tag a, Tag b)
    {
        return new Tag
        {
            _item1 = a._item1 | b._item1,
            _item2 = a._item2 | b._item2,
            _item3 = a._item3 | b._item3,
            _item4 = a._item4 | b._item4
        };
    }

    public static Tag operator ^(Tag a, Tag b)
    {
        return new Tag
        {
            _item1 = a._item1 ^ b._item1,
            _item2 = a._item2 ^ b._item2,
            _item3 = a._item3 ^ b._item3,
            _item4 = a._item4 ^ b._item4
        };
    }

    public static Tag operator ~(Tag a)
    {
        return new Tag
        {
            _item1 = ~a._item1,
            _item2 = ~a._item2,
            _item3 = ~a._item3,
            _item4 = ~a._item4
        };
    }

    public static bool operator ==(Tag a, Tag b)
    {
        return a._item1 == b._item1 && a._item2 == b._item2 && a._item3 == b._item3 && a._item4 == b._item4;
    }

    public static bool operator !=(Tag a, Tag b)
    {
        return a._item1 != b._item1 || a._item2 != b._item2 || a._item3 != b._item3 || a._item4 != b._item4;
    }

    public override bool Equals(object obj)
    {
        return obj is Tag tag && tag == this;
    }

    public bool Equals(Tag other)
    {
        return other == this;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(_item1, _item2, _item3, _item4);
    }

    public override string ToString()
    {
        return $"Tag {_item1:x8} {_item2:x8} {_item3:x8} {_item4:x8}";
    }

    public int CompareTo(Tag other)
    {
        var item1Comparison = _item1.CompareTo(other._item1);
        if (item1Comparison != 0) return item1Comparison;
        var item2Comparison = _item2.CompareTo(other._item2);
        if (item2Comparison != 0) return item2Comparison;
        var item3Comparison = _item3.CompareTo(other._item3);
        if (item3Comparison != 0) return item3Comparison;
        return _item4.CompareTo(other._item4);
    }
}