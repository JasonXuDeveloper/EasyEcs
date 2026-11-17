using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace EasyEcs;

// The lane kernel (docs/plans/designs/lane-kernel.md): the vector loop of a flat [ForEach] job written once over lane
// types the JIT specializes per width, with the width chain and the masked words held once here. Generated code only
// (SystemGenerator.Lanes.cs writes the steps).

/// <summary>How a step writes: every lane (a whole run) or only the passing lanes of a masked word.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IMode;

/// <summary>Every lane is written.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public struct EveryRow : IMode;

/// <summary>Only the lanes of the rows that pass are written (<see cref="ILane{TSelf,T}.Keep{M}"/>).</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public struct PassingRows : IMode;

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
    static abstract TSelf operator /(TSelf a, TSelf b);
    static abstract TSelf operator &(TSelf a, TSelf b);
    static abstract TSelf operator |(TSelf a, TSelf b);
    static abstract TSelf operator ^(TSelf a, TSelf b);
    static abstract TSelf operator ~(TSelf a);
    static abstract TSelf operator -(TSelf a);
    static abstract TSelf Min(TSelf a, TSelf b);
    static abstract TSelf Max(TSelf a, TSelf b);
    static abstract TSelf GreaterThan(TSelf a, TSelf b);
    static abstract TSelf Equal(TSelf a, TSelf b);
    static abstract TSelf Select(TSelf mask, TSelf a, TSelf b);
    static abstract TSelf Min(TSelf a, T constant);
    static abstract TSelf Max(TSelf a, T constant);
    static abstract TSelf SelectGreaterThan(TSelf x, TSelf y, TSelf a, TSelf b);
    static abstract TSelf SelectEqual(TSelf x, TSelf y, TSelf a, TSelf b);
    static abstract TSelf AddWhereEqual(TSelf acc, TSelf value, TSelf x, TSelf y);
    static abstract bool IsZero(TSelf a);
    static abstract T Sum(TSelf a);
    static abstract T Element(TSelf a, int index);
    static abstract TSelf Passing(ulong rows, int first);
    static abstract TTo As<TTo, U>(TSelf a) where TTo : struct, ILane<TTo, U> where U : unmanaged;
#pragma warning restore CS1591

    /// <summary>
    /// A write of <paramref name="value"/> over <paramref name="old"/>: every lane in a whole run (<see cref="EveryRow"/>),
    /// the lanes <paramref name="pass"/> sets in a masked word (<see cref="PassingRows"/>), the rest kept as they were.
    /// </summary>
    static abstract TSelf Keep<M>(TSelf pass, TSelf value, TSelf old) where M : struct, IMode;
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
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane512<T> operator /(Lane512<T> a, Lane512<T> b) => new(a.V / b.V);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane512<T> operator &(Lane512<T> a, Lane512<T> b) => new(a.V & b.V);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane512<T> operator |(Lane512<T> a, Lane512<T> b) => new(a.V | b.V);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane512<T> operator ^(Lane512<T> a, Lane512<T> b) => new(a.V ^ b.V);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane512<T> operator ~(Lane512<T> a) => new(~a.V);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane512<T> operator -(Lane512<T> a) => new(-a.V);
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
    // a sum under a condition (acc + value where x == y) as one expression: the add masked by the comparison's mask
    // register (vpaddd {k}); through SelectEqual the sum reaches the select as an argument's temporary and is blended
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane512<T> AddWhereEqual(Lane512<T> acc, Lane512<T> value, Lane512<T> x, Lane512<T> y) =>
        new(Vector512.ConditionalSelect(Vector512.Equals(x.V, y.V), acc.V + value.V, acc.V));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static bool IsZero(Lane512<T> a) => a.V == Vector512<T>.Zero;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static T Sum(Lane512<T> a) => Vector512.Sum(a.V);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static T Element(Lane512<T> a, int index) => a.V[index];
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane512<T> Passing(ulong rows, int first) => new(Interleaved.Lanes32In512(rows, first).As<int, T>());
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static TTo As<TTo, U>(Lane512<T> a) where TTo : struct, ILane<TTo, U> where U : unmanaged => Unsafe.BitCast<Lane512<T>, TTo>(a);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane512<T> Keep<M>(Lane512<T> pass, Lane512<T> value, Lane512<T> old) where M : struct, IMode =>
        typeof(M) == typeof(PassingRows) ? new(Vector512.ConditionalSelect(pass.V, value.V, old.V)) : value;
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
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane256<T> operator /(Lane256<T> a, Lane256<T> b) => new(a.V / b.V);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane256<T> operator &(Lane256<T> a, Lane256<T> b) => new(a.V & b.V);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane256<T> operator |(Lane256<T> a, Lane256<T> b) => new(a.V | b.V);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane256<T> operator ^(Lane256<T> a, Lane256<T> b) => new(a.V ^ b.V);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane256<T> operator ~(Lane256<T> a) => new(~a.V);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane256<T> operator -(Lane256<T> a) => new(-a.V);
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
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane256<T> AddWhereEqual(Lane256<T> acc, Lane256<T> value, Lane256<T> x, Lane256<T> y) =>
        new(Vector256.ConditionalSelect(Vector256.Equals(x.V, y.V), acc.V + value.V, acc.V));
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
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane256<T> Keep<M>(Lane256<T> pass, Lane256<T> value, Lane256<T> old) where M : struct, IMode =>
        typeof(M) == typeof(PassingRows) ? new(Vector256.ConditionalSelect(pass.V, value.V, old.V)) : value;
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
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane128<T> operator /(Lane128<T> a, Lane128<T> b) => new(a.V / b.V);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane128<T> operator &(Lane128<T> a, Lane128<T> b) => new(a.V & b.V);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane128<T> operator |(Lane128<T> a, Lane128<T> b) => new(a.V | b.V);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane128<T> operator ^(Lane128<T> a, Lane128<T> b) => new(a.V ^ b.V);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane128<T> operator ~(Lane128<T> a) => new(~a.V);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane128<T> operator -(Lane128<T> a) => new(-a.V);
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
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane128<T> AddWhereEqual(Lane128<T> acc, Lane128<T> value, Lane128<T> x, Lane128<T> y) =>
        new(Vector128.ConditionalSelect(Vector128.Equals(x.V, y.V), acc.V + value.V, acc.V));
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
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Lane128<T> Keep<M>(Lane128<T> pass, Lane128<T> value, Lane128<T> old) where M : struct, IMode =>
        typeof(M) == typeof(PassingRows) ? new(Vector128.ConditionalSelect(pass.V, value.V, old.V)) : value;
#pragma warning restore CS1591
}

/// <summary>
/// A generated job's vector loop over 4-byte lanes (<see cref="LaneKernel"/>): its body once, over the float and int lanes
/// of a width, each width its own instantiation.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface ILaneWalk4
{
    /// <summary>
    /// The vectors of rows (of lanes, for a flat job) from <paramref name="i"/> to <paramref name="end"/> (a whole number
    /// of vectors, none when <paramref name="end"/> is not past <paramref name="i"/>), in steps of <c>LF.Count</c>. A
    /// masked word writes only the lanes of the rows <paramref name="word"/> sets (<see cref="LaneKernel.Lanes{LI,M}"/>).
    /// The walk declares its uniforms' and constants' broadcasts before its loop: the JIT does not hoist a broadcast out
    /// of the loop once the loop holds as many vector locals as the registers it counts (a lane struct's inlined
    /// temporaries count too), and a constant written in the loop stays in a register only where its CSE takes it.
    /// </summary>
    void Walk<LF, LI, M>(nint i, nint end, ulong word) where LF : struct, ILane<LF, float> where LI : struct, ILane<LI, int> where M : struct, IMode;
}

/// <summary>
/// The width chain of generated vector loops: 512 bits where the runtime says it pays (<c>Vector512.IsHardwareAccelerated</c>,
/// never the instruction set alone: it is false on CPUs that lower their clock for 512-bit work and under
/// <c>DOTNET_PreferredVectorBitWidth=256</c>), then 256, then 128, each width taking the whole vectors the wider one
/// left. It returns where the vectors stopped; the job runs the rows after it through its method. Where the runtime
/// accelerates no width (arm32, <c>DOTNET_EnableHWIntrinsic=0</c>) it returns 0: every row runs the method.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class LaneKernel
{
    /// <summary>The whole vectors of a run of <paramref name="rows"/> rows; returns the first row no vector took.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nint Run<TJob>(ref TJob job, nint rows) where TJob : struct, ILaneWalk4, allows ref struct => RunFlat(ref job, rows, 1);

    /// <summary>
    /// A flat job's run: <paramref name="lanes"/> lanes, <paramref name="fields"/> per row (each field alike), walked
    /// as whole vectors of whole rows; returns the first lane no vector took (a multiple of <paramref name="fields"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nint RunFlat<TJob>(ref TJob job, nint lanes, int fields) where TJob : struct, ILaneWalk4, allows ref struct
    {
        // each width ends at a multiple of its vectors of whole rows (fields is a constant once this is inlined, so the
        // remainders are shifts or multiplies); a run too short for any vector makes no call
        nint e512 = Vector512.IsHardwareAccelerated && lanes >= 16 * fields ? lanes - lanes % (16 * fields) : 0;
        nint e256 = Vector256.IsHardwareAccelerated && lanes - e512 >= 8 * fields ? lanes - (lanes - e512) % (8 * fields) : e512;
        nint e128 = Vector128.IsHardwareAccelerated && lanes - e256 >= 4 * fields ? lanes - (lanes - e256) % (4 * fields) : e256;
        if (e128 != 0) Walk(job, e512, e256, e128);
        return e128;
    }

    /// <summary>
    /// The whole vectors of a word of <paramref name="rows"/> rows of which those <paramref name="pass"/> sets pass (a
    /// vector with none is skipped, the others write only their passing lanes); returns the first row no vector took.
    /// </summary>
    /// <remarks>
    /// The walks are inlined here, into the runner's walk over words (itself a root of its own, which holds nothing
    /// else of the job): a word is at most 64 rows, four 512-bit vectors, and a call per word cost as much as its
    /// vectors (the arguments spilled and reloaded around it, the job's copy on the stack, the walk's prologue and
    /// vzeroupper: HolesHaul 1.21x the emitted loops at 1,000 rows, 1.31x at 100,000; lane-kernel.md, package F).
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nint RunMasked<TJob>(ref TJob job, ulong pass, nint rows) where TJob : struct, ILaneWalk4, allows ref struct
    {
        nint e512 = Vector512.IsHardwareAccelerated && rows >= 16 ? rows & ~(nint)15 : 0;
        nint e256 = Vector256.IsHardwareAccelerated && rows - e512 >= 8 ? rows - ((rows - e512) & 7) : e512;
        nint e128 = Vector128.IsHardwareAccelerated && rows - e256 >= 4 ? rows - ((rows - e256) & 3) : e256;
        var copy = job;
        Widths<TJob, PassingRows>(ref copy, e512, e256, e128, pass);
        return e128;
    }

    /// <summary>The lanes a vector of rows from <paramref name="i"/> writes: every lane, or those of the rows of <paramref name="pass"/>; false when none passes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Lanes<LI, M>(ulong pass, nint i, out LI lanes) where LI : struct, ILane<LI, int> where M : struct, IMode
    {
        lanes = default;
        if (typeof(M) != typeof(PassingRows)) return true;
        lanes = LI.Passing(pass, (int)i);
        return !LI.IsZero(lanes);
    }

    // One method per job, the three widths' loops of a whole run in it, called once per run: the JIT's inlining budget is
    // per root method, and a runner holding every width of both modes ran out of it (the lane operations of a step of six
    // statements left as calls), so the walk is a root of its own. The job comes by value, its column refs in argument
    // registers: a ref to the caller's job made the runner keep the job in memory around the call, and with a method per
    // width that cost 1.13-1.18x the emitted loops on tables of 5-20 rows (docs/plans/designs/lane-kernel.md,
    // "Package D"). The copy is also what keeps the refs in registers inside the walk, where through a ref every store
    // to a column might write them (a walk never writes its job).
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Walk<TJob>(TJob copy, nint e512, nint e256, nint e128) where TJob : struct, ILaneWalk4, allows ref struct =>
        Widths<TJob, EveryRow>(ref copy, e512, e256, e128, 0);

    /// <summary>Each width's loop: the job's walk, inlined, its uniforms and constants broadcast once per width before its loop.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Widths<TJob, M>(ref TJob copy, nint e512, nint e256, nint e128, ulong pass)
        where TJob : struct, ILaneWalk4, allows ref struct where M : struct, IMode
    {
        if (Vector512.IsHardwareAccelerated) copy.Walk<Lane512<float>, Lane512<int>, M>(0, e512, pass);
        if (Vector256.IsHardwareAccelerated) copy.Walk<Lane256<float>, Lane256<int>, M>(e512, e256, pass);
        if (Vector128.IsHardwareAccelerated) copy.Walk<Lane128<float>, Lane128<int>, M>(e256, e128, pass);
    }
}
