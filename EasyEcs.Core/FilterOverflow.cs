using System;
using System.Collections.Generic;
using System.Runtime.Intrinsics;
using System.Threading;

namespace EasyEcs;

/// <summary>
/// The bits past 255 of query filters, interned: a <see cref="QueryFilter"/> holds an id instead of five arrays, so it
/// carries no object reference, and equal sets have one id (filters compare ids). Only worlds with more than 256
/// component types reach it, when a filter names one of the later types; ids live for the process, one per distinct
/// combination a program builds.
/// </summary>
internal static class FilterOverflow
{
    private const int Sets = 5; // All, None, Any, Tracked, Removals

    private static readonly Lock Gate = new();
    private static readonly Dictionary<Key, int> Ids = new();
    private static Vector256<long>[][][] _byId = [null]; // id 0: no overflow; copy-on-write, read without the lock

    /// <summary>Overflow of set <paramref name="set"/> in combination <paramref name="id"/> (null: none). Never changed once interned.</summary>
    public static Vector256<long>[] Of(int id, int set) => id == 0 ? null : Volatile.Read(ref _byId)[id][set];

    /// <summary>The id of combination <paramref name="id"/> with <paramref name="bit"/> (past 255) added to set <paramref name="set"/>.</summary>
    public static int With(int id, int set, int bit)
    {
        var sets = new Vector256<long>[Sets][];
        if (id != 0) Array.Copy(Volatile.Read(ref _byId)[id], sets, Sets);
        var mask = new BitMask(default, sets[set]).Clone(); // a copy: interned arrays never change
        mask.SetBit(bit);
        sets[set] = mask.Overflow;

        lock (Gate)
        {
            var key = new Key(sets);
            if (Ids.TryGetValue(key, out int known)) return known;
            var byId = _byId;
            Array.Resize(ref byId, byId.Length + 1);
            byId[^1] = sets;
            Volatile.Write(ref _byId, byId);
            Ids.Add(key, byId.Length - 1);
            return byId.Length - 1;
        }
    }

    /// <summary>Five overflow arrays compared by content (trailing empty vectors aside).</summary>
    private readonly struct Key(Vector256<long>[][] sets) : IEquatable<Key>
    {
        private readonly Vector256<long>[][] _sets = sets;

        public bool Equals(Key other)
        {
            for (int i = 0; i < Sets; i++)
                if (!new BitMask(default, _sets[i]).Equals(new BitMask(default, other._sets[i])))
                    return false;
            return true;
        }

        public override bool Equals(object obj) => obj is Key other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            for (int i = 0; i < Sets; i++) hash.Add(new BitMask(default, _sets[i]).GetHashCode());
            return hash.ToHashCode();
        }
    }
}
