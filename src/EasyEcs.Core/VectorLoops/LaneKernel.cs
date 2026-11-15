using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace EasyEcs;

// Prototype of docs/plans/designs/lane-kernel.md (package C): one body per job, written once over a width type the
// JIT specializes (W512 / W256 / W128), and the width chain, tails and masked words held once here.

/// <summary>
/// A vector width a kernel step is instantiated at (<see cref="W512"/>, <see cref="W256"/>, <see cref="W128"/>): the
/// width's own vector of bytes, and its operations as static abstract members over lanes of any T. A step calls them
/// through <see cref="Lane{W,T}"/>; each is one vector instruction of that width, a direct call the JIT inlines, and
/// each is small, so a body of many of them stays within the inliner's budget.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IWidth<W> where W : struct, IWidth<W>
{
    /// <summary>The width's bytes.</summary>
    static abstract int Bytes { get; }
#pragma warning disable CS1591 // each is the Vector512 / Vector256 / Vector128 operation of its name
    static abstract W Load<T>(ref T at, nint i) where T : unmanaged;
    static abstract void Store<T>(W value, ref T at, nint i) where T : unmanaged;
    static abstract W Create<T>(T value) where T : unmanaged;
    static abstract W AllBitsSet<T>() where T : unmanaged;
    static abstract W One<T>() where T : unmanaged;
    static abstract W Add<T>(W a, W b) where T : unmanaged;
    static abstract W Subtract<T>(W a, W b) where T : unmanaged;
    static abstract W Multiply<T>(W a, W b) where T : unmanaged;
    static abstract W And<T>(W a, W b) where T : unmanaged;
    static abstract W Not<T>(W a) where T : unmanaged;
    static abstract W Min<T>(W a, W b) where T : unmanaged;
    static abstract W Max<T>(W a, W b) where T : unmanaged;
    static abstract W GreaterThan<T>(W a, W b) where T : unmanaged;
    static abstract W Equal<T>(W a, W b) where T : unmanaged;
    static abstract W Select<T>(W mask, W a, W b) where T : unmanaged;
    static abstract bool IsZero<T>(W a) where T : unmanaged;
    static abstract T Sum<T>(W a) where T : unmanaged;
    static abstract T Element<T>(W a, int index) where T : unmanaged;
#pragma warning restore CS1591

    /// <summary>All bits set in 4-byte lane j when row <c>first + j</c> passes (bit <c>first + j</c> of <paramref name="rows"/>).</summary>
    static abstract W Lanes32(ulong rows, int first);
}

/// <summary>512 bits: <c>Vector512</c>, taken only where the runtime says it pays (<c>Vector512.IsHardwareAccelerated</c>).</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public struct W512 : IWidth<W512>
{
    internal Vector512<byte> Bits;
#pragma warning disable CS1591
    public static int Bytes => 64;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static W512 Load<T>(ref T at, nint i) where T : unmanaged => Unsafe.BitCast<Vector512<T>, W512>(Vector512.LoadUnsafe(ref at, (nuint)i));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void Store<T>(W512 value, ref T at, nint i) where T : unmanaged => Vector512.StoreUnsafe(Unsafe.BitCast<W512, Vector512<T>>(value), ref at, (nuint)i);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static W512 Create<T>(T value) where T : unmanaged => Unsafe.BitCast<Vector512<T>, W512>(Vector512.Create(value));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static W512 AllBitsSet<T>() where T : unmanaged => Unsafe.BitCast<Vector512<T>, W512>(Vector512<T>.AllBitsSet);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static W512 One<T>() where T : unmanaged => Unsafe.BitCast<Vector512<T>, W512>(Vector512<T>.One);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static W512 Add<T>(W512 a, W512 b) where T : unmanaged => Unsafe.BitCast<Vector512<T>, W512>(Unsafe.BitCast<W512, Vector512<T>>(a) + Unsafe.BitCast<W512, Vector512<T>>(b));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static W512 Subtract<T>(W512 a, W512 b) where T : unmanaged => Unsafe.BitCast<Vector512<T>, W512>(Unsafe.BitCast<W512, Vector512<T>>(a) - Unsafe.BitCast<W512, Vector512<T>>(b));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static W512 Multiply<T>(W512 a, W512 b) where T : unmanaged => Unsafe.BitCast<Vector512<T>, W512>(Unsafe.BitCast<W512, Vector512<T>>(a) * Unsafe.BitCast<W512, Vector512<T>>(b));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static W512 And<T>(W512 a, W512 b) where T : unmanaged => Unsafe.BitCast<Vector512<T>, W512>(Unsafe.BitCast<W512, Vector512<T>>(a) & Unsafe.BitCast<W512, Vector512<T>>(b));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static W512 Not<T>(W512 a) where T : unmanaged => Unsafe.BitCast<Vector512<T>, W512>(~Unsafe.BitCast<W512, Vector512<T>>(a));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static W512 Min<T>(W512 a, W512 b) where T : unmanaged => Unsafe.BitCast<Vector512<T>, W512>(Vector512.Min(Unsafe.BitCast<W512, Vector512<T>>(a), Unsafe.BitCast<W512, Vector512<T>>(b)));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static W512 Max<T>(W512 a, W512 b) where T : unmanaged => Unsafe.BitCast<Vector512<T>, W512>(Vector512.Max(Unsafe.BitCast<W512, Vector512<T>>(a), Unsafe.BitCast<W512, Vector512<T>>(b)));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static W512 GreaterThan<T>(W512 a, W512 b) where T : unmanaged => Unsafe.BitCast<Vector512<T>, W512>(Vector512.GreaterThan(Unsafe.BitCast<W512, Vector512<T>>(a), Unsafe.BitCast<W512, Vector512<T>>(b)));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static W512 Equal<T>(W512 a, W512 b) where T : unmanaged => Unsafe.BitCast<Vector512<T>, W512>(Vector512.Equals(Unsafe.BitCast<W512, Vector512<T>>(a), Unsafe.BitCast<W512, Vector512<T>>(b)));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static W512 Select<T>(W512 mask, W512 a, W512 b) where T : unmanaged => Unsafe.BitCast<Vector512<T>, W512>(Vector512.ConditionalSelect(Unsafe.BitCast<W512, Vector512<T>>(mask), Unsafe.BitCast<W512, Vector512<T>>(a), Unsafe.BitCast<W512, Vector512<T>>(b)));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static bool IsZero<T>(W512 a) where T : unmanaged => Unsafe.BitCast<W512, Vector512<T>>(a) == Vector512<T>.Zero;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static T Sum<T>(W512 a) where T : unmanaged => Vector512.Sum(Unsafe.BitCast<W512, Vector512<T>>(a));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static T Element<T>(W512 a, int index) where T : unmanaged => Unsafe.BitCast<W512, Vector512<T>>(a)[index];
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static W512 Lanes32(ulong rows, int first) => Unsafe.BitCast<Vector512<int>, W512>(Interleaved.Lanes32In512(rows, first));
#pragma warning restore CS1591
}

/// <summary>256 bits: <c>Vector256</c> (AVX2; where <c>Vector512</c> is not accelerated, the widest step).</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public struct W256 : IWidth<W256>
{
    internal Vector256<byte> Bits;
#pragma warning disable CS1591
    public static int Bytes => 32;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static W256 Load<T>(ref T at, nint i) where T : unmanaged => Unsafe.BitCast<Vector256<T>, W256>(Vector256.LoadUnsafe(ref at, (nuint)i));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void Store<T>(W256 value, ref T at, nint i) where T : unmanaged => Vector256.StoreUnsafe(Unsafe.BitCast<W256, Vector256<T>>(value), ref at, (nuint)i);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static W256 Create<T>(T value) where T : unmanaged => Unsafe.BitCast<Vector256<T>, W256>(Vector256.Create(value));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static W256 AllBitsSet<T>() where T : unmanaged => Unsafe.BitCast<Vector256<T>, W256>(Vector256<T>.AllBitsSet);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static W256 One<T>() where T : unmanaged => Unsafe.BitCast<Vector256<T>, W256>(Vector256<T>.One);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static W256 Add<T>(W256 a, W256 b) where T : unmanaged => Unsafe.BitCast<Vector256<T>, W256>(Unsafe.BitCast<W256, Vector256<T>>(a) + Unsafe.BitCast<W256, Vector256<T>>(b));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static W256 Subtract<T>(W256 a, W256 b) where T : unmanaged => Unsafe.BitCast<Vector256<T>, W256>(Unsafe.BitCast<W256, Vector256<T>>(a) - Unsafe.BitCast<W256, Vector256<T>>(b));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static W256 Multiply<T>(W256 a, W256 b) where T : unmanaged => Unsafe.BitCast<Vector256<T>, W256>(Unsafe.BitCast<W256, Vector256<T>>(a) * Unsafe.BitCast<W256, Vector256<T>>(b));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static W256 And<T>(W256 a, W256 b) where T : unmanaged => Unsafe.BitCast<Vector256<T>, W256>(Unsafe.BitCast<W256, Vector256<T>>(a) & Unsafe.BitCast<W256, Vector256<T>>(b));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static W256 Not<T>(W256 a) where T : unmanaged => Unsafe.BitCast<Vector256<T>, W256>(~Unsafe.BitCast<W256, Vector256<T>>(a));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static W256 Min<T>(W256 a, W256 b) where T : unmanaged => Unsafe.BitCast<Vector256<T>, W256>(Vector256.Min(Unsafe.BitCast<W256, Vector256<T>>(a), Unsafe.BitCast<W256, Vector256<T>>(b)));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static W256 Max<T>(W256 a, W256 b) where T : unmanaged => Unsafe.BitCast<Vector256<T>, W256>(Vector256.Max(Unsafe.BitCast<W256, Vector256<T>>(a), Unsafe.BitCast<W256, Vector256<T>>(b)));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static W256 GreaterThan<T>(W256 a, W256 b) where T : unmanaged => Unsafe.BitCast<Vector256<T>, W256>(Vector256.GreaterThan(Unsafe.BitCast<W256, Vector256<T>>(a), Unsafe.BitCast<W256, Vector256<T>>(b)));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static W256 Equal<T>(W256 a, W256 b) where T : unmanaged => Unsafe.BitCast<Vector256<T>, W256>(Vector256.Equals(Unsafe.BitCast<W256, Vector256<T>>(a), Unsafe.BitCast<W256, Vector256<T>>(b)));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static W256 Select<T>(W256 mask, W256 a, W256 b) where T : unmanaged => Unsafe.BitCast<Vector256<T>, W256>(Vector256.ConditionalSelect(Unsafe.BitCast<W256, Vector256<T>>(mask), Unsafe.BitCast<W256, Vector256<T>>(a), Unsafe.BitCast<W256, Vector256<T>>(b)));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static bool IsZero<T>(W256 a) where T : unmanaged => Unsafe.BitCast<W256, Vector256<T>>(a) == Vector256<T>.Zero;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static T Sum<T>(W256 a) where T : unmanaged => Vector256.Sum(Unsafe.BitCast<W256, Vector256<T>>(a));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static T Element<T>(W256 a, int index) where T : unmanaged => Unsafe.BitCast<W256, Vector256<T>>(a)[index];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W256 Lanes32(ulong rows, int first)
    {
        var bits = Vector256.Create(1, 2, 4, 8, 16, 32, 64, 128);
        return Unsafe.BitCast<Vector256<int>, W256>(Vector256.Equals(Vector256.Create((int)(rows >> first)) & bits, bits));
    }
#pragma warning restore CS1591
}

/// <summary>128 bits: <c>Vector128</c> (ARM64's AdvSimd, SSE; the last step before the method per row).</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public struct W128 : IWidth<W128>
{
    internal Vector128<byte> Bits;
#pragma warning disable CS1591
    public static int Bytes => 16;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static W128 Load<T>(ref T at, nint i) where T : unmanaged => Unsafe.BitCast<Vector128<T>, W128>(Vector128.LoadUnsafe(ref at, (nuint)i));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void Store<T>(W128 value, ref T at, nint i) where T : unmanaged => Vector128.StoreUnsafe(Unsafe.BitCast<W128, Vector128<T>>(value), ref at, (nuint)i);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static W128 Create<T>(T value) where T : unmanaged => Unsafe.BitCast<Vector128<T>, W128>(Vector128.Create(value));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static W128 AllBitsSet<T>() where T : unmanaged => Unsafe.BitCast<Vector128<T>, W128>(Vector128<T>.AllBitsSet);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static W128 One<T>() where T : unmanaged => Unsafe.BitCast<Vector128<T>, W128>(Vector128<T>.One);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static W128 Add<T>(W128 a, W128 b) where T : unmanaged => Unsafe.BitCast<Vector128<T>, W128>(Unsafe.BitCast<W128, Vector128<T>>(a) + Unsafe.BitCast<W128, Vector128<T>>(b));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static W128 Subtract<T>(W128 a, W128 b) where T : unmanaged => Unsafe.BitCast<Vector128<T>, W128>(Unsafe.BitCast<W128, Vector128<T>>(a) - Unsafe.BitCast<W128, Vector128<T>>(b));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static W128 Multiply<T>(W128 a, W128 b) where T : unmanaged => Unsafe.BitCast<Vector128<T>, W128>(Unsafe.BitCast<W128, Vector128<T>>(a) * Unsafe.BitCast<W128, Vector128<T>>(b));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static W128 And<T>(W128 a, W128 b) where T : unmanaged => Unsafe.BitCast<Vector128<T>, W128>(Unsafe.BitCast<W128, Vector128<T>>(a) & Unsafe.BitCast<W128, Vector128<T>>(b));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static W128 Not<T>(W128 a) where T : unmanaged => Unsafe.BitCast<Vector128<T>, W128>(~Unsafe.BitCast<W128, Vector128<T>>(a));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static W128 Min<T>(W128 a, W128 b) where T : unmanaged => Unsafe.BitCast<Vector128<T>, W128>(Vector128.Min(Unsafe.BitCast<W128, Vector128<T>>(a), Unsafe.BitCast<W128, Vector128<T>>(b)));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static W128 Max<T>(W128 a, W128 b) where T : unmanaged => Unsafe.BitCast<Vector128<T>, W128>(Vector128.Max(Unsafe.BitCast<W128, Vector128<T>>(a), Unsafe.BitCast<W128, Vector128<T>>(b)));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static W128 GreaterThan<T>(W128 a, W128 b) where T : unmanaged => Unsafe.BitCast<Vector128<T>, W128>(Vector128.GreaterThan(Unsafe.BitCast<W128, Vector128<T>>(a), Unsafe.BitCast<W128, Vector128<T>>(b)));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static W128 Equal<T>(W128 a, W128 b) where T : unmanaged => Unsafe.BitCast<Vector128<T>, W128>(Vector128.Equals(Unsafe.BitCast<W128, Vector128<T>>(a), Unsafe.BitCast<W128, Vector128<T>>(b)));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static W128 Select<T>(W128 mask, W128 a, W128 b) where T : unmanaged => Unsafe.BitCast<Vector128<T>, W128>(Vector128.ConditionalSelect(Unsafe.BitCast<W128, Vector128<T>>(mask), Unsafe.BitCast<W128, Vector128<T>>(a), Unsafe.BitCast<W128, Vector128<T>>(b)));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static bool IsZero<T>(W128 a) where T : unmanaged => Unsafe.BitCast<W128, Vector128<T>>(a) == Vector128<T>.Zero;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static T Sum<T>(W128 a) where T : unmanaged => Vector128.Sum(Unsafe.BitCast<W128, Vector128<T>>(a));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static T Element<T>(W128 a, int index) where T : unmanaged => Unsafe.BitCast<W128, Vector128<T>>(a)[index];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W128 Lanes32(ulong rows, int first)
    {
        var bits = Vector128.Create(1, 2, 4, 8);
        return Unsafe.BitCast<Vector128<int>, W128>(Vector128.Equals(Vector128.Create((int)(rows >> first)) & bits, bits));
    }
#pragma warning restore CS1591
}

/// <summary>How a step writes: every lane (a whole run) or only the passing lanes of a masked word.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IMode;

/// <summary>Every lane is written.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public struct EveryRow : IMode;

/// <summary>Only the lanes of the rows that pass are written (<see cref="Lane{W,T}.Keep{M}"/>).</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public struct PassingRows : IMode;

/// <summary>
/// One vector of <typeparamref name="T"/> lanes at width <typeparamref name="W"/>, with the operators a step reads like
/// the method with: the width is a struct type argument, so each width is its own instantiation, and each operation is a
/// constrained call of <typeparamref name="W"/>'s static abstract member: direct, inlined, one instruction.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public readonly struct Lane<W, T> where W : struct, IWidth<W> where T : unmanaged
{
    private readonly W _v;

    private Lane(W v) => _v = v;

    /// <summary>The lanes of a vector.</summary>
    public static int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => W.Bytes / Unsafe.SizeOf<T>();
    }

#pragma warning disable CS1591 // the Vector512 / Vector256 / Vector128 operation of each name
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane<W, T> Load(ref T at, nint i) => new(W.Load(ref at, i));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public void Store(ref T at, nint i) => W.Store(_v, ref at, i);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane<W, T> Create(T value) => new(W.Create(value));
    public static Lane<W, T> AllBitsSet { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => new(W.AllBitsSet<T>()); }
    public static Lane<W, T> One { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => new(W.One<T>()); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane<W, T> operator +(Lane<W, T> a, Lane<W, T> b) => new(W.Add<T>(a._v, b._v));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane<W, T> operator -(Lane<W, T> a, Lane<W, T> b) => new(W.Subtract<T>(a._v, b._v));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane<W, T> operator *(Lane<W, T> a, Lane<W, T> b) => new(W.Multiply<T>(a._v, b._v));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane<W, T> operator &(Lane<W, T> a, Lane<W, T> b) => new(W.And<T>(a._v, b._v));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane<W, T> operator ~(Lane<W, T> a) => new(W.Not<T>(a._v));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane<W, T> Min(Lane<W, T> a, Lane<W, T> b) => new(W.Min<T>(a._v, b._v));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane<W, T> Max(Lane<W, T> a, Lane<W, T> b) => new(W.Max<T>(a._v, b._v));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane<W, T> GreaterThan(Lane<W, T> a, Lane<W, T> b) => new(W.GreaterThan<T>(a._v, b._v));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane<W, T> Equal(Lane<W, T> a, Lane<W, T> b) => new(W.Equal<T>(a._v, b._v));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane<W, T> Select(Lane<W, T> mask, Lane<W, T> a, Lane<W, T> b) => new(W.Select<T>(mask._v, a._v, b._v));
    public bool IsZero { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => W.IsZero<T>(_v); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public T Sum() => W.Sum<T>(_v);
    public T this[int index] { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => W.Element<T>(_v, index); }
#pragma warning restore CS1591

    /// <summary>The lanes' bits as lanes of <typeparamref name="U"/> of the same width (no instruction).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Lane<W, U> As<U>() where U : unmanaged => Unsafe.BitCast<Lane<W, T>, Lane<W, U>>(this);

    /// <summary>All bits set in lane j when row <c>first + j</c> of a word passes (lanes of 4 bytes).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Lane<W, T> Passing(ulong rows, int first) => new(W.Lanes32(rows, first));

    /// <summary>
    /// A write of <paramref name="value"/> over <paramref name="old"/>: every lane in a whole run (<see cref="EveryRow"/>),
    /// the passing lanes of a masked word (<see cref="PassingRows"/>: <paramref name="pass"/>), the rest kept as they were.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Lane<W, T> Keep<M>(Lane<W, int> pass, Lane<W, T> value, Lane<W, T> old) where M : struct, IMode =>
        typeof(M) == typeof(PassingRows) ? new(W.Select<T>(Unsafe.BitCast<Lane<W, int>, W>(pass), value._v, old._v)) : value;
}

/// <summary>A job whose method runs one vector of rows at a time (<see cref="LaneKernel"/>).</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface ILaneStep
{
    /// <summary>The bytes of the job's narrowest lane: a vector of rows is that many lanes of its width.</summary>
    static abstract int LaneBytes { get; }

    /// <summary>The rows from <paramref name="i"/>, a vector of them at width <typeparamref name="W"/>; a masked word writes only <paramref name="pass"/>.</summary>
    void Step<W, M>(nint i, Lane<W, int> pass) where W : struct, IWidth<W> where M : struct, IMode;

    /// <summary>Row <paramref name="i"/> through the method itself (the rows no width takes).</summary>
    void Row(nint i);
}

/// <summary>
/// A job that walks the vectors of a width itself (<see cref="LaneKernel.RunWalk"/>): a reduction's accumulators live
/// across the vectors of one width, declared before its loop and folded after it.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface ILaneWalk
{
    /// <summary>The bytes of the job's narrowest lane.</summary>
    static abstract int LaneBytes { get; }

    /// <summary>The whole vectors of width <typeparamref name="W"/> from <paramref name="i"/>; returns the row it stopped at.</summary>
    nint Walk<W, M>(nint i, nint rows, ulong pass) where W : struct, IWidth<W> where M : struct, IMode;

    /// <summary>Row <paramref name="i"/> through the method itself.</summary>
    void Row(nint i);
}

/// <summary>
/// The width chain every generated vector loop shares: 512 bits where the runtime says it pays, then 256, then 128,
/// each taking the whole vectors the wider one left, then the method per row. A machine whose runtime accelerates no
/// vector width (arm32, <c>DOTNET_EnableHWIntrinsic=0</c>) runs every row through the method.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class LaneKernel
{
    /// <summary>Every row of a run of <paramref name="rows"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Run<TJob>(ref TJob job, nint rows) where TJob : struct, ILaneStep, allows ref struct
    {
        nint i = 0;
        if (Vector512.IsHardwareAccelerated) i = Walk<TJob, W512, EveryRow>(ref job, i, rows, 0);
        if (Vector256.IsHardwareAccelerated) i = Walk<TJob, W256, EveryRow>(ref job, i, rows, 0);
        if (Vector128.IsHardwareAccelerated) i = Walk<TJob, W128, EveryRow>(ref job, i, rows, 0);
        for (; i < rows; i++) job.Row(i);
    }

    /// <summary>The rows of a word that pass (<paramref name="pass"/>: one bit per row), of <paramref name="rows"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RunMasked<TJob>(ref TJob job, ulong pass, nint rows) where TJob : struct, ILaneStep, allows ref struct
    {
        nint i = 0;
        if (Vector512.IsHardwareAccelerated) i = Walk<TJob, W512, PassingRows>(ref job, i, rows, pass);
        if (Vector256.IsHardwareAccelerated) i = Walk<TJob, W256, PassingRows>(ref job, i, rows, pass);
        if (Vector128.IsHardwareAccelerated) i = Walk<TJob, W128, PassingRows>(ref job, i, rows, pass);
        for (; i < rows; i++)
            if (((pass >> (int)i) & 1) != 0) job.Row(i);
    }

    /// <summary><see cref="Run"/> for a job walking its vectors itself (a reduction).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RunWalk<TJob>(ref TJob job, nint rows) where TJob : struct, ILaneWalk, allows ref struct
    {
        nint i = 0;
        if (Vector512.IsHardwareAccelerated && rows - i >= 64 / TJob.LaneBytes) i = Walks<TJob, W512, EveryRow>(ref job, i, rows, 0);
        if (Vector256.IsHardwareAccelerated && rows - i >= 32 / TJob.LaneBytes) i = Walks<TJob, W256, EveryRow>(ref job, i, rows, 0);
        if (Vector128.IsHardwareAccelerated && rows - i >= 16 / TJob.LaneBytes) i = Walks<TJob, W128, EveryRow>(ref job, i, rows, 0);
        for (; i < rows; i++) job.Row(i);
    }

    /// <summary><see cref="RunMasked"/> for a job walking its vectors itself.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RunWalkMasked<TJob>(ref TJob job, ulong pass, nint rows) where TJob : struct, ILaneWalk, allows ref struct
    {
        nint i = 0;
        if (Vector512.IsHardwareAccelerated && rows - i >= 64 / TJob.LaneBytes) i = Walks<TJob, W512, PassingRows>(ref job, i, rows, pass);
        if (Vector256.IsHardwareAccelerated && rows - i >= 32 / TJob.LaneBytes) i = Walks<TJob, W256, PassingRows>(ref job, i, rows, pass);
        if (Vector128.IsHardwareAccelerated && rows - i >= 16 / TJob.LaneBytes) i = Walks<TJob, W128, PassingRows>(ref job, i, rows, pass);
        for (; i < rows; i++)
            if (((pass >> (int)i) & 1) != 0) job.Row(i);
    }

    /// <summary>
    /// The lanes a vector of rows from <paramref name="i"/> writes: every lane in a whole run, the passing rows of a
    /// masked word (<paramref name="pass"/>); false when none passes (the vector is skipped).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Lanes<W, M>(ulong pass, nint i, out Lane<W, int> lanes) where W : struct, IWidth<W> where M : struct, IMode
    {
        lanes = default;
        if (typeof(M) != typeof(PassingRows)) return true;
        lanes = Lane<W, int>.Passing(pass, (int)i);
        return !lanes.IsZero;
    }

    // A reduction's walk at one width: a method of its own, as Walk below.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static nint Walks<TJob, W, M>(ref TJob job, nint i, nint rows, ulong pass)
        where TJob : struct, ILaneWalk, allows ref struct where W : struct, IWidth<W> where M : struct, IMode
    {
        var copy = job; // as Walk: the job's fields kept in registers, its result written back
        i = copy.Walk<W, M>(i, rows, pass);
        job = copy;
        return i;
    }

    // A method per job, width and mode: the JIT's inlining budget is per root method, and one root holding the runner,
    // three widths and two modes runs out of it (the steps' operations were left as calls); called once per run.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static nint Walk<TJob, W, M>(ref TJob job, nint i, nint rows, ulong pass)
        where TJob : struct, ILaneStep, allows ref struct where W : struct, IWidth<W> where M : struct, IMode
    {
        // a copy the walk owns: its fields (the column refs, a uniform) are locals the JIT keeps in registers and hoists,
        // where through the caller's ref every store to a column might write them (a step never writes its job)
        var copy = job;
        nint count = W.Bytes / TJob.LaneBytes;
        for (nint last = rows - count; i <= last; i += count)
        {
            if (Lanes<W, M>(pass, i, out var lanes)) copy.Step<W, M>(i, lanes);
        }
        return i;
    }
}

// ---------------- the typed form: one lane type per width and element type (item l2) ----------------

/// <summary>
/// A vector of <typeparamref name="T"/> lanes as a struct type argument (<see cref="Lane512{T}"/>, <see cref="Lane256{T}"/>,
/// <see cref="Lane128{T}"/>, each holding its width's own vector type): a step written over
/// <c>where LF : struct, ILane&lt;LF, float&gt;</c> is instantiated per width, and each operation is a constrained call of a
/// static abstract member: direct, inlined, one instruction on the exact vector type.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface ILane<TSelf, T> where TSelf : struct, ILane<TSelf, T> where T : unmanaged
{
#pragma warning disable CS1591 // each is the Vector512 / Vector256 / Vector128 operation of its name
    static abstract int Count { get; }
    static abstract TSelf Load(ref T at, nint i);
    static abstract void Store(TSelf value, ref T at, nint i);
    static abstract TSelf Create(T value);
    static abstract TSelf AllBitsSet { get; }
    static abstract TSelf One { get; }
    static abstract TSelf operator +(TSelf a, TSelf b);
    static abstract TSelf operator -(TSelf a, TSelf b);
    static abstract TSelf operator *(TSelf a, TSelf b);
    static abstract TSelf operator &(TSelf a, TSelf b);
    static abstract TSelf operator ~(TSelf a);
    static abstract TSelf Min(TSelf a, TSelf b);
    static abstract TSelf Max(TSelf a, TSelf b);
    static abstract TSelf GreaterThan(TSelf a, TSelf b);
    static abstract TSelf Equal(TSelf a, TSelf b);
    static abstract TSelf Select(TSelf mask, TSelf a, TSelf b);
    static abstract TSelf Min(TSelf a, T constant);
    static abstract TSelf Max(TSelf a, T constant);
    static abstract TSelf SelectGreaterThan(TSelf x, TSelf y, TSelf a, TSelf b);
    static abstract TSelf SelectEqual(TSelf x, TSelf y, TSelf a, TSelf b);
    static abstract bool IsZero(TSelf a);
    static abstract T Sum(TSelf a);
    static abstract T Element(TSelf a, int index);
    static abstract TSelf Passing(ulong rows, int first);
    static abstract TTo As<TTo, U>(TSelf a) where TTo : struct, ILane<TTo, U> where U : unmanaged;
#pragma warning restore CS1591
}

/// <summary>Lanes of <c>Vector512</c>.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public struct Lane512<T> : ILane<Lane512<T>, T> where T : unmanaged
{
#pragma warning disable CS1591
    public Vector512<T> V;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private Lane512(Vector512<T> v) => V = v;
    public static int Count => Vector512<T>.Count;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane512<T> Load(ref T at, nint i) => new(Vector512.LoadUnsafe(ref at, (nuint)i));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void Store(Lane512<T> value, ref T at, nint i) => Vector512.StoreUnsafe(value.V, ref at, (nuint)i);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane512<T> Create(T value) => new(Vector512.Create(value));
    public static Lane512<T> AllBitsSet { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => new(Vector512<T>.AllBitsSet); }
    public static Lane512<T> One { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => new(Vector512<T>.One); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane512<T> operator +(Lane512<T> a, Lane512<T> b) => new(a.V + b.V);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane512<T> operator -(Lane512<T> a, Lane512<T> b) => new(a.V - b.V);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane512<T> operator *(Lane512<T> a, Lane512<T> b) => new(a.V * b.V);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane512<T> operator &(Lane512<T> a, Lane512<T> b) => new(a.V & b.V);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane512<T> operator ~(Lane512<T> a) => new(~a.V);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane512<T> Min(Lane512<T> a, Lane512<T> b) => new(Vector512.Min(a.V, b.V));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane512<T> Max(Lane512<T> a, Lane512<T> b) => new(Vector512.Max(a.V, b.V));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane512<T> GreaterThan(Lane512<T> a, Lane512<T> b) => new(Vector512.GreaterThan(a.V, b.V));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane512<T> Equal(Lane512<T> a, Lane512<T> b) => new(Vector512.Equals(a.V, b.V));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane512<T> Select(Lane512<T> mask, Lane512<T> a, Lane512<T> b) => new(Vector512.ConditionalSelect(mask.V, a.V, b.V));
    // a constant operand imported as one (the JIT's cheap Min / Max where it cannot be NaN): the inliner passes a
    // constant argument through, where a vector of it in a lane struct reaches the call as a field
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane512<T> Min(Lane512<T> a, T constant) => new(Vector512.Min(a.V, Vector512.Create(constant)));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane512<T> Max(Lane512<T> a, T constant) => new(Vector512.Max(a.V, Vector512.Create(constant)));
    // the comparison and the select in one expression: the mask stays a mask register (AVX-512), not a vector
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane512<T> SelectGreaterThan(Lane512<T> x, Lane512<T> y, Lane512<T> a, Lane512<T> b) =>
        new(Vector512.ConditionalSelect(Vector512.GreaterThan(x.V, y.V), a.V, b.V));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane512<T> SelectEqual(Lane512<T> x, Lane512<T> y, Lane512<T> a, Lane512<T> b) =>
        new(Vector512.ConditionalSelect(Vector512.Equals(x.V, y.V), a.V, b.V));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static bool IsZero(Lane512<T> a) => a.V == Vector512<T>.Zero;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static T Sum(Lane512<T> a) => Vector512.Sum(a.V);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static T Element(Lane512<T> a, int index) => a.V[index];
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane512<T> Passing(ulong rows, int first) => new(Interleaved.Lanes32In512(rows, first).As<int, T>());
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static TTo As<TTo, U>(Lane512<T> a) where TTo : struct, ILane<TTo, U> where U : unmanaged => Unsafe.BitCast<Lane512<T>, TTo>(a);
#pragma warning restore CS1591
}

/// <summary>Lanes of <c>Vector256</c>.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public struct Lane256<T> : ILane<Lane256<T>, T> where T : unmanaged
{
#pragma warning disable CS1591
    public Vector256<T> V;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private Lane256(Vector256<T> v) => V = v;
    public static int Count => Vector256<T>.Count;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane256<T> Load(ref T at, nint i) => new(Vector256.LoadUnsafe(ref at, (nuint)i));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void Store(Lane256<T> value, ref T at, nint i) => Vector256.StoreUnsafe(value.V, ref at, (nuint)i);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane256<T> Create(T value) => new(Vector256.Create(value));
    public static Lane256<T> AllBitsSet { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => new(Vector256<T>.AllBitsSet); }
    public static Lane256<T> One { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => new(Vector256<T>.One); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane256<T> operator +(Lane256<T> a, Lane256<T> b) => new(a.V + b.V);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane256<T> operator -(Lane256<T> a, Lane256<T> b) => new(a.V - b.V);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane256<T> operator *(Lane256<T> a, Lane256<T> b) => new(a.V * b.V);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane256<T> operator &(Lane256<T> a, Lane256<T> b) => new(a.V & b.V);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane256<T> operator ~(Lane256<T> a) => new(~a.V);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane256<T> Min(Lane256<T> a, Lane256<T> b) => new(Vector256.Min(a.V, b.V));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane256<T> Max(Lane256<T> a, Lane256<T> b) => new(Vector256.Max(a.V, b.V));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane256<T> GreaterThan(Lane256<T> a, Lane256<T> b) => new(Vector256.GreaterThan(a.V, b.V));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane256<T> Equal(Lane256<T> a, Lane256<T> b) => new(Vector256.Equals(a.V, b.V));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane256<T> Select(Lane256<T> mask, Lane256<T> a, Lane256<T> b) => new(Vector256.ConditionalSelect(mask.V, a.V, b.V));
    // a constant operand imported as one (the JIT's cheap Min / Max where it cannot be NaN): the inliner passes a
    // constant argument through, where a vector of it in a lane struct reaches the call as a field
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane256<T> Min(Lane256<T> a, T constant) => new(Vector256.Min(a.V, Vector256.Create(constant)));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane256<T> Max(Lane256<T> a, T constant) => new(Vector256.Max(a.V, Vector256.Create(constant)));
    // the comparison and the select in one expression: the mask stays a mask register (AVX-512), not a vector
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane256<T> SelectGreaterThan(Lane256<T> x, Lane256<T> y, Lane256<T> a, Lane256<T> b) =>
        new(Vector256.ConditionalSelect(Vector256.GreaterThan(x.V, y.V), a.V, b.V));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane256<T> SelectEqual(Lane256<T> x, Lane256<T> y, Lane256<T> a, Lane256<T> b) =>
        new(Vector256.ConditionalSelect(Vector256.Equals(x.V, y.V), a.V, b.V));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static bool IsZero(Lane256<T> a) => a.V == Vector256<T>.Zero;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static T Sum(Lane256<T> a) => Vector256.Sum(a.V);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static T Element(Lane256<T> a, int index) => a.V[index];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Lane256<T> Passing(ulong rows, int first)
    {
        var bits = Vector256.Create(1, 2, 4, 8, 16, 32, 64, 128);
        return new(Vector256.Equals(Vector256.Create((int)(rows >> first)) & bits, bits).As<int, T>());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static TTo As<TTo, U>(Lane256<T> a) where TTo : struct, ILane<TTo, U> where U : unmanaged => Unsafe.BitCast<Lane256<T>, TTo>(a);
#pragma warning restore CS1591
}

/// <summary>Lanes of <c>Vector128</c>.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public struct Lane128<T> : ILane<Lane128<T>, T> where T : unmanaged
{
#pragma warning disable CS1591
    public Vector128<T> V;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private Lane128(Vector128<T> v) => V = v;
    public static int Count => Vector128<T>.Count;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane128<T> Load(ref T at, nint i) => new(Vector128.LoadUnsafe(ref at, (nuint)i));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void Store(Lane128<T> value, ref T at, nint i) => Vector128.StoreUnsafe(value.V, ref at, (nuint)i);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane128<T> Create(T value) => new(Vector128.Create(value));
    public static Lane128<T> AllBitsSet { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => new(Vector128<T>.AllBitsSet); }
    public static Lane128<T> One { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => new(Vector128<T>.One); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane128<T> operator +(Lane128<T> a, Lane128<T> b) => new(a.V + b.V);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane128<T> operator -(Lane128<T> a, Lane128<T> b) => new(a.V - b.V);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane128<T> operator *(Lane128<T> a, Lane128<T> b) => new(a.V * b.V);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane128<T> operator &(Lane128<T> a, Lane128<T> b) => new(a.V & b.V);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane128<T> operator ~(Lane128<T> a) => new(~a.V);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane128<T> Min(Lane128<T> a, Lane128<T> b) => new(Vector128.Min(a.V, b.V));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane128<T> Max(Lane128<T> a, Lane128<T> b) => new(Vector128.Max(a.V, b.V));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane128<T> GreaterThan(Lane128<T> a, Lane128<T> b) => new(Vector128.GreaterThan(a.V, b.V));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane128<T> Equal(Lane128<T> a, Lane128<T> b) => new(Vector128.Equals(a.V, b.V));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane128<T> Select(Lane128<T> mask, Lane128<T> a, Lane128<T> b) => new(Vector128.ConditionalSelect(mask.V, a.V, b.V));
    // a constant operand imported as one (the JIT's cheap Min / Max where it cannot be NaN): the inliner passes a
    // constant argument through, where a vector of it in a lane struct reaches the call as a field
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane128<T> Min(Lane128<T> a, T constant) => new(Vector128.Min(a.V, Vector128.Create(constant)));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane128<T> Max(Lane128<T> a, T constant) => new(Vector128.Max(a.V, Vector128.Create(constant)));
    // the comparison and the select in one expression: the mask stays a mask register (AVX-512), not a vector
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane128<T> SelectGreaterThan(Lane128<T> x, Lane128<T> y, Lane128<T> a, Lane128<T> b) =>
        new(Vector128.ConditionalSelect(Vector128.GreaterThan(x.V, y.V), a.V, b.V));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane128<T> SelectEqual(Lane128<T> x, Lane128<T> y, Lane128<T> a, Lane128<T> b) =>
        new(Vector128.ConditionalSelect(Vector128.Equals(x.V, y.V), a.V, b.V));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static bool IsZero(Lane128<T> a) => a.V == Vector128<T>.Zero;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static T Sum(Lane128<T> a) => Vector128.Sum(a.V);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static T Element(Lane128<T> a, int index) => a.V[index];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Lane128<T> Passing(ulong rows, int first)
    {
        var bits = Vector128.Create(1, 2, 4, 8);
        return new(Vector128.Equals(Vector128.Create((int)(rows >> first)) & bits, bits).As<int, T>());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static TTo As<TTo, U>(Lane128<T> a) where TTo : struct, ILane<TTo, U> where U : unmanaged => Unsafe.BitCast<Lane128<T>, TTo>(a);
#pragma warning restore CS1591
}

/// <summary>
/// A job of 4-byte lanes in the typed form: its step written once over the float and int lanes of a width
/// (<c>LF</c>, <c>LI</c>; a job of other lane sizes takes one type parameter per size).
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface ILaneStep4
{
    /// <summary>The rows from <paramref name="i"/>, a vector of them; a masked word writes only <paramref name="pass"/>.</summary>
    void Step<LF, LI, M>(nint i, LI pass) where LF : struct, ILane<LF, float> where LI : struct, ILane<LI, int> where M : struct, IMode;

    /// <summary>Row <paramref name="i"/> through the method itself.</summary>
    void Row(nint i);
}

/// <summary>A reduction of 4-byte lanes in the typed form: it walks a width's vectors itself.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface ILaneWalk4
{
    /// <summary>The whole vectors from <paramref name="i"/>; returns the row it stopped at.</summary>
    nint Walk<LF, LI, M>(nint i, nint rows, ulong pass) where LF : struct, ILane<LF, float> where LI : struct, ILane<LI, int> where M : struct, IMode;

    /// <summary>Row <paramref name="i"/> through the method itself.</summary>
    void Row(nint i);
}

/// <summary><see cref="LaneKernel"/>'s width chain for the typed form.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class LaneKernel4
{
#pragma warning disable CS1591
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Run<TJob>(ref TJob job, nint rows) where TJob : struct, ILaneStep4, allows ref struct
    {
        nint i = 0;
        if (Vector512.IsHardwareAccelerated && rows >= 16) i = Walk<TJob, Lane512<float>, Lane512<int>, EveryRow>(ref job, i, rows, 0);
        if (Vector256.IsHardwareAccelerated && rows - i >= 8) i = Walk<TJob, Lane256<float>, Lane256<int>, EveryRow>(ref job, i, rows, 0);
        if (Vector128.IsHardwareAccelerated && rows - i >= 4) i = Walk<TJob, Lane128<float>, Lane128<int>, EveryRow>(ref job, i, rows, 0);
        for (; i < rows; i++) job.Row(i);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RunMasked<TJob>(ref TJob job, ulong pass, nint rows) where TJob : struct, ILaneStep4, allows ref struct
    {
        nint i = 0;
        if (Vector512.IsHardwareAccelerated && rows >= 16) i = Walk<TJob, Lane512<float>, Lane512<int>, PassingRows>(ref job, i, rows, pass);
        if (Vector256.IsHardwareAccelerated && rows - i >= 8) i = Walk<TJob, Lane256<float>, Lane256<int>, PassingRows>(ref job, i, rows, pass);
        if (Vector128.IsHardwareAccelerated && rows - i >= 4) i = Walk<TJob, Lane128<float>, Lane128<int>, PassingRows>(ref job, i, rows, pass);
        for (; i < rows; i++)
            if (((pass >> (int)i) & 1) != 0) job.Row(i);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RunWalk<TJob>(ref TJob job, nint rows) where TJob : struct, ILaneWalk4, allows ref struct
    {
        nint i = 0;
        if (Vector512.IsHardwareAccelerated && rows >= 16) i = Walks<TJob, Lane512<float>, Lane512<int>, EveryRow>(ref job, i, rows, 0);
        if (Vector256.IsHardwareAccelerated && rows - i >= 8) i = Walks<TJob, Lane256<float>, Lane256<int>, EveryRow>(ref job, i, rows, 0);
        if (Vector128.IsHardwareAccelerated && rows - i >= 4) i = Walks<TJob, Lane128<float>, Lane128<int>, EveryRow>(ref job, i, rows, 0);
        for (; i < rows; i++) job.Row(i);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RunWalkMasked<TJob>(ref TJob job, ulong pass, nint rows) where TJob : struct, ILaneWalk4, allows ref struct
    {
        nint i = 0;
        if (Vector512.IsHardwareAccelerated && rows >= 16) i = Walks<TJob, Lane512<float>, Lane512<int>, PassingRows>(ref job, i, rows, pass);
        if (Vector256.IsHardwareAccelerated && rows - i >= 8) i = Walks<TJob, Lane256<float>, Lane256<int>, PassingRows>(ref job, i, rows, pass);
        if (Vector128.IsHardwareAccelerated && rows - i >= 4) i = Walks<TJob, Lane128<float>, Lane128<int>, PassingRows>(ref job, i, rows, pass);
        for (; i < rows; i++)
            if (((pass >> (int)i) & 1) != 0) job.Row(i);
    }

    /// <summary>The lanes a vector of rows from <paramref name="i"/> writes; false when none passes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Lanes<LI, M>(ulong pass, nint i, out LI lanes) where LI : struct, ILane<LI, int> where M : struct, IMode
    {
        lanes = default;
        if (typeof(M) != typeof(PassingRows)) return true;
        lanes = LI.Passing(pass, (int)i);
        return !LI.IsZero(lanes);
    }

    /// <summary>A write of <paramref name="value"/> over <paramref name="old"/>: every lane, or the passing lanes of a masked word.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static L Keep<M, L, T, LI>(LI pass, L value, L old) where M : struct, IMode where L : struct, ILane<L, T> where T : unmanaged where LI : struct, ILane<LI, int> =>
        typeof(M) == typeof(PassingRows) ? L.Select(LI.As<L, T>(pass), value, old) : value;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static nint Walk<TJob, LF, LI, M>(ref TJob job, nint i, nint rows, ulong pass)
        where TJob : struct, ILaneStep4, allows ref struct where LF : struct, ILane<LF, float> where LI : struct, ILane<LI, int> where M : struct, IMode
    {
        var copy = job;
        for (nint last = rows - LF.Count; i <= last; i += LF.Count)
            if (Lanes<LI, M>(pass, i, out var lanes)) copy.Step<LF, LI, M>(i, lanes);
        return i;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static nint Walks<TJob, LF, LI, M>(ref TJob job, nint i, nint rows, ulong pass)
        where TJob : struct, ILaneWalk4, allows ref struct where LF : struct, ILane<LF, float> where LI : struct, ILane<LI, int> where M : struct, IMode
    {
        var copy = job;
        i = copy.Walk<LF, LI, M>(i, rows, pass);
        job = copy;
        return i;
    }
#pragma warning restore CS1591
}
