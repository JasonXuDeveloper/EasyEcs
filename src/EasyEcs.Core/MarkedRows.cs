using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EasyEcs;

/// <summary>
/// The rows a pass marked for one structural change, applied in bulk when the pass ends: the vector loop of a
/// <c>[ForEach]</c> method that adds or removes a component or destroys its entity under a condition
/// (<c>if (hp.Value &lt;= 0) unit.SetDead();</c>) computes the condition as a lane mask, marks the rows it holds, and
/// after the pass one call gives every marked row the change, whole words of rows at a time where a table changes them
/// in place, else row by row in the order the pass visited them (the method's own order, so every table ends with the
/// rows the method would have left, in its order). The marks are words of 64 rows of a table, so memory is bounded by
/// the rows marked; a pool per world keeps them, so steady passes allocate nothing. Owner thread only. Generated code
/// uses this.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class MarkedRows
{
    private readonly World _world;
    private Archetype[] _tables = new Archetype[16];
    private int[] _words = new int[16];
    private ulong[] _bits = new ulong[16];
    private int _count;
    private int _version; // the world's StorageVersion at the first mark: the rows must not have moved since
    internal MarkedRows NextFree; // the world's pool, a stack

    private MarkedRows(World world) => _world = world;

    /// <summary>Empty marks of <paramref name="world"/>, from its pool. O(1).</summary>
    public static MarkedRows Rent(World world)
    {
        var marks = world._freeMarks;
        if (marks == null) return new MarkedRows(world);
        world._freeMarks = marks.NextFree;
        marks.NextFree = null;
        return marks;
    }

    /// <summary>Mark the rows of <paramref name="lanes"/> (bit j: row <paramref name="row"/> + j of the run) for the change. O(1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Mark(in EntitySpan rows, int row, ulong lanes)
    {
        if (lanes != 0) Add(rows.Table, rows.First + row, lanes);
    }

    // At most 64 lanes from row: the low part in the row's word, the rest in the next one.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Add(Archetype table, int row, ulong lanes)
    {
        int word = row >> 6, shift = row & 63;
        Put(table, word, lanes << shift);
        if (shift != 0 && lanes >> (64 - shift) is var high and not 0) Put(table, word + 1, high);
    }

    private void Put(Archetype table, int word, ulong bits)
    {
        if (bits == 0) return;
        int last = _count - 1;
        if (last >= 0 && _words[last] == word && ReferenceEquals(_tables[last], table))
        {
            _bits[last] |= bits;
            return;
        }

        if (_count == 0) _version = _world.StorageVersion;
        if (_count == _tables.Length)
        {
            Array.Resize(ref _tables, _count * 2);
            Array.Resize(ref _words, _count * 2);
            Array.Resize(ref _bits, _count * 2);
        }

        _tables[_count] = table;
        _words[_count] = word;
        _bits[_count++] = bits;
    }

    /// <summary>
    /// Set component <typeparamref name="T"/> = <paramref name="value"/> on every marked row (added or replaced), as
    /// <see cref="Entity.Set{T}(in T)"/> would one by one, then give the marks back to the pool. O(marked rows).
    /// </summary>
    public void SetAll<T>(in T value) where T : struct, IComponent => _world.SetMarked(this, in value);

    /// <summary>Remove <typeparamref name="T"/> from every marked row, as <see cref="Entity.Remove{T}"/> would, then give the marks back. O(marked rows).</summary>
    public void RemoveAll<T>() where T : struct, IComponent => _world.RemoveMarked<T>(this);

    /// <summary>Disable <typeparamref name="T"/> on every marked row, as <see cref="Entity.Disable{T}"/> would, then give the marks back. O(marked rows).</summary>
    public void DisableAll<T>() where T : struct, IComponent => _world.DisableMarked<T>(this);

    /// <summary>Enable <typeparamref name="T"/> on every marked row, as <see cref="Entity.Enable{T}"/> would, then give the marks back. O(marked rows).</summary>
    public void EnableAll<T>() where T : struct, IComponent => _world.EnableMarked<T>(this);

    /// <summary>Destroy every marked entity, as <see cref="Entity.Destroy"/> would, then give the marks back. O(marked rows).</summary>
    public void DestroyAll() => _world.DestroyMarked(this);

    internal int Count => _count;

    internal Archetype Table(int i) => _tables[i];

    internal int Word(int i) => _words[i];

    internal ulong Bits(int i) => _bits[i];

    /// <summary>Throws when a structural change moved rows after the first mark: the marks no longer name the rows marked.</summary>
    internal void CheckUnmoved()
    {
        if (_count != 0 && _version != _world.StorageVersion) ThrowMoved();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowMoved()
    {
        Return();
        throw new InvalidOperationException("The rows moved after they were marked: apply marks right after the pass that marked them.");
    }

    /// <summary>Empty, back to the world's pool.</summary>
    internal void Return()
    {
        Array.Clear(_tables, 0, _count); // no table outlives the marks
        _count = 0;
        NextFree = _world._freeMarks;
        _world._freeMarks = this;
    }
}
