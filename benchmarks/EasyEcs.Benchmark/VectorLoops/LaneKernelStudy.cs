using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace EasyEcs.Benchmark;

// Package C's study forms that Core does not ship (docs/plans/designs/lane-kernel.md): the width form (one width type
// argument W for every element type, Lane<W, T>, WidthKernel), the typed step form (ILaneStep4: Core's walk calling a
// step per vector, package D's form, whose uniforms' broadcasts the JIT left in the loop) and the study's walk form
// with the rows and a reduction's write-back (IStudyWalk4), measured by LaneKernelBenchmark against the generated
// loops. Core's LaneKernel holds the walk form the generator writes (ILaneWalk4).

/// <summary>
/// A vector width a kernel step is instantiated at (<see cref="W512"/>, <see cref="W256"/>, <see cref="W128"/>): the
/// width's own vector of bytes, and its operations as static abstract members over lanes of any T. A step calls them
/// through <see cref="Lane{W,T}"/>; each is one vector instruction of that width, a direct call the JIT inlines, and
/// each is small, so a body of many of them stays within the inliner's budget.
/// </summary>
internal interface IWidth<W> where W : struct, IWidth<W>
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
internal struct W512 : IWidth<W512>
{
#pragma warning disable CS0649 // the width's bytes, only ever bit-cast
    internal Vector512<byte> Bits;
#pragma warning restore CS0649
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
internal struct W256 : IWidth<W256>
{
#pragma warning disable CS0649 // the width's bytes, only ever bit-cast
    internal Vector256<byte> Bits;
#pragma warning restore CS0649
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
internal struct W128 : IWidth<W128>
{
#pragma warning disable CS0649 // the width's bytes, only ever bit-cast
    internal Vector128<byte> Bits;
#pragma warning restore CS0649
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

/// <summary>
/// One vector of <typeparamref name="T"/> lanes at width <typeparamref name="W"/>, with the operators a step reads like
/// the method with: the width is a struct type argument, so each width is its own instantiation, and each operation is a
/// constrained call of <typeparamref name="W"/>'s static abstract member: direct, inlined, one instruction.
/// </summary>
internal readonly struct Lane<W, T> where W : struct, IWidth<W> where T : unmanaged
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

/// <summary>A job whose method runs one vector of rows at a time (<see cref="WidthKernel"/>).</summary>
internal interface IWidthStep
{
    /// <summary>The bytes of the job's narrowest lane: a vector of rows is that many lanes of its width.</summary>
    static abstract int LaneBytes { get; }

    /// <summary>The rows from <paramref name="i"/>, a vector of them at width <typeparamref name="W"/>; a masked word writes only <paramref name="pass"/>.</summary>
    void Step<W, M>(nint i, Lane<W, int> pass) where W : struct, IWidth<W> where M : struct, IMode;

    /// <summary>Row <paramref name="i"/> through the method itself (the rows no width takes).</summary>
    void Row(nint i);
}

/// <summary>
/// A job that walks the vectors of a width itself (<see cref="WidthKernel.RunWalk"/>): a reduction's accumulators live
/// across the vectors of one width, declared before its loop and folded after it.
/// </summary>
internal interface IWidthWalk
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
internal static class WidthKernel
{
    /// <summary>Every row of a run of <paramref name="rows"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Run<TJob>(ref TJob job, nint rows) where TJob : struct, IWidthStep, allows ref struct
    {
        nint i = 0;
        if (Vector512.IsHardwareAccelerated) i = Walk<TJob, W512, EveryRow>(ref job, i, rows, 0);
        if (Vector256.IsHardwareAccelerated) i = Walk<TJob, W256, EveryRow>(ref job, i, rows, 0);
        if (Vector128.IsHardwareAccelerated) i = Walk<TJob, W128, EveryRow>(ref job, i, rows, 0);
        for (; i < rows; i++) job.Row(i);
    }

    /// <summary>The rows of a word that pass (<paramref name="pass"/>: one bit per row), of <paramref name="rows"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RunMasked<TJob>(ref TJob job, ulong pass, nint rows) where TJob : struct, IWidthStep, allows ref struct
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
    public static void RunWalk<TJob>(ref TJob job, nint rows) where TJob : struct, IWidthWalk, allows ref struct
    {
        nint i = 0;
        if (Vector512.IsHardwareAccelerated && rows - i >= 64 / TJob.LaneBytes) i = Walks<TJob, W512, EveryRow>(ref job, i, rows, 0);
        if (Vector256.IsHardwareAccelerated && rows - i >= 32 / TJob.LaneBytes) i = Walks<TJob, W256, EveryRow>(ref job, i, rows, 0);
        if (Vector128.IsHardwareAccelerated && rows - i >= 16 / TJob.LaneBytes) i = Walks<TJob, W128, EveryRow>(ref job, i, rows, 0);
        for (; i < rows; i++) job.Row(i);
    }

    /// <summary><see cref="RunMasked"/> for a job walking its vectors itself.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RunWalkMasked<TJob>(ref TJob job, ulong pass, nint rows) where TJob : struct, IWidthWalk, allows ref struct
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
        where TJob : struct, IWidthWalk, allows ref struct where W : struct, IWidth<W> where M : struct, IMode
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
        where TJob : struct, IWidthStep, allows ref struct where W : struct, IWidth<W> where M : struct, IMode
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


/// <summary>A reduction of 4-byte lanes in the typed form: it walks a width's vectors itself (the walk form, package F).</summary>
internal interface IStudyWalk4
{
    /// <summary>The whole vectors from <paramref name="i"/>; returns the row it stopped at.</summary>
    nint Walk<LF, LI, M>(nint i, nint rows, ulong pass) where LF : struct, ILane<LF, float> where LI : struct, ILane<LI, int> where M : struct, IMode;

    /// <summary>Row <paramref name="i"/> through the method itself.</summary>
    void Row(nint i);
}

/// <summary>Core's width chain (LaneKernel) for a job walking its vectors itself.</summary>
internal static class StudyWalk4
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RunWalk<TJob>(ref TJob job, nint rows) where TJob : struct, IStudyWalk4, allows ref struct
    {
        nint i = 0;
        if (Vector512.IsHardwareAccelerated && rows >= 16) i = Walks<TJob, Lane512<float>, Lane512<int>, EveryRow>(ref job, i, rows, 0);
        if (Vector256.IsHardwareAccelerated && rows - i >= 8) i = Walks<TJob, Lane256<float>, Lane256<int>, EveryRow>(ref job, i, rows, 0);
        if (Vector128.IsHardwareAccelerated && rows - i >= 4) i = Walks<TJob, Lane128<float>, Lane128<int>, EveryRow>(ref job, i, rows, 0);
        for (; i < rows; i++) job.Row(i);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RunWalkMasked<TJob>(ref TJob job, ulong pass, nint rows) where TJob : struct, IStudyWalk4, allows ref struct
    {
        nint i = 0;
        if (Vector512.IsHardwareAccelerated && rows >= 16) i = Walks<TJob, Lane512<float>, Lane512<int>, PassingRows>(ref job, i, rows, pass);
        if (Vector256.IsHardwareAccelerated && rows - i >= 8) i = Walks<TJob, Lane256<float>, Lane256<int>, PassingRows>(ref job, i, rows, pass);
        if (Vector128.IsHardwareAccelerated && rows - i >= 4) i = Walks<TJob, Lane128<float>, Lane128<int>, PassingRows>(ref job, i, rows, pass);
        for (; i < rows; i++)
            if (((pass >> (int)i) & 1) != 0) job.Row(i);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static nint Walks<TJob, LF, LI, M>(ref TJob job, nint i, nint rows, ulong pass)
        where TJob : struct, IStudyWalk4, allows ref struct where LF : struct, ILane<LF, float> where LI : struct, ILane<LI, int> where M : struct, IMode
    {
        var copy = job;
        i = copy.Walk<LF, LI, M>(i, rows, pass);
        job = copy;
        return i;
    }
}

/// <summary>Package D's step form: Core's walk calling the job's step per vector (<see cref="LaneStep4"/>).</summary>
internal interface ILaneStep4
{
    /// <summary>The vector of rows from <paramref name="i"/>; a masked word writes only the lanes of <paramref name="pass"/>.</summary>
    void Step<LF, LI, M>(nint i, LI pass, LF passF) where LF : struct, ILane<LF, float> where LI : struct, ILane<LI, int> where M : struct, IMode;
}

/// <summary>Core's width chain over a step per vector (the step form package D shipped and package F replaced by the walk).</summary>
internal static class LaneStep4
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nint Run<TJob>(ref TJob job, nint rows) where TJob : struct, ILaneStep4, allows ref struct
    {
        nint i = 0;
        if (Vector512.IsHardwareAccelerated && rows >= 16) i = Walk<TJob, Lane512<float>, Lane512<int>, EveryRow>(ref job, i, rows & ~(nint)15, 0);
        if (Vector256.IsHardwareAccelerated && rows - i >= 8) i = Walk<TJob, Lane256<float>, Lane256<int>, EveryRow>(ref job, i, rows - ((rows - i) & 7), 0);
        if (Vector128.IsHardwareAccelerated && rows - i >= 4) i = Walk<TJob, Lane128<float>, Lane128<int>, EveryRow>(ref job, i, rows - ((rows - i) & 3), 0);
        return i;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nint RunMasked<TJob>(ref TJob job, ulong pass, nint rows) where TJob : struct, ILaneStep4, allows ref struct
    {
        nint i = 0;
        if (Vector512.IsHardwareAccelerated && rows >= 16) i = Walk<TJob, Lane512<float>, Lane512<int>, PassingRows>(ref job, i, rows & ~(nint)15, pass);
        if (Vector256.IsHardwareAccelerated && rows - i >= 8) i = Walk<TJob, Lane256<float>, Lane256<int>, PassingRows>(ref job, i, rows - ((rows - i) & 7), pass);
        if (Vector128.IsHardwareAccelerated && rows - i >= 4) i = Walk<TJob, Lane128<float>, Lane128<int>, PassingRows>(ref job, i, rows - ((rows - i) & 3), pass);
        return i;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static nint Walk<TJob, LF, LI, M>(ref TJob job, nint i, nint end, ulong pass)
        where TJob : struct, ILaneStep4, allows ref struct where LF : struct, ILane<LF, float> where LI : struct, ILane<LI, int> where M : struct, IMode
    {
        var copy = job;
        for (; i < end; i += LF.Count)
            if (LaneKernel.Lanes<LI, M>(pass, i, out var lanes)) copy.Step<LF, LI, M>(i, lanes, LI.As<LF, float>(lanes));
        return i;
    }
}
