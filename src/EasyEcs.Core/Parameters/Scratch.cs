using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EasyEcs;

/// <summary>
/// A <c>[ForEach]</c> method's working memory on the thread running it (OpenMP's <c>threadprivate</c>): a list of
/// neighbors, the open set of a path search, a buffer to fill. <c>static void Plan(ref Path path, in Goal goal,
/// Scratch&lt;Frontier&gt; frontier)</c>: <see cref="Value"/> is one <typeparamref name="T"/> per method and thread,
/// <c>default</c> at first and kept for the thread's lifetime, so what a row allocates into it
/// (<c>value.Ids ??= new int[256]</c>) each thread allocates once, the first time it runs the method, and steady state
/// allocates nothing. The value's own room is made on every helper thread (<see cref="WorldOptions.MaxDegreeOfParallelism"/>)
/// as soon as one thread runs the method. Two threads never share one; the chunks of a parallel pass running on one
/// thread run one after another, so a row has it to itself for its call.
/// <para>
/// It is never reset: a row finds what the last row on its thread left, of this pass or an earlier one, of this world
/// or another. Clear what you use at the start of the row (<c>list.Clear()</c>); nothing that belongs to the world may
/// live there between rows. Storing what it holds by reference into a component or a singleton (EECS058) hands one
/// thread's memory to the next row that reuses it. Its memory is the thread's: it is released when the thread ends, not
/// when the world is disposed.
/// </para>
/// Valid for the call it is passed to (a ref struct).
/// </summary>
/// <typeparam name="T">The scratch value: a struct holding buffers, or a class such as a <c>List&lt;int&gt;</c> (created on first use: <c>scratch.Value ??= new()</c>).</typeparam>
public readonly ref struct Scratch<T>
{
    private readonly ref T _value;

    /// <summary>Wraps <paramref name="value"/>, this thread's scratch value. Generated code calls this.</summary>
    [EditorBrowsable(EditorBrowsableState.Never), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Scratch(ref T value) => _value = ref value;

    /// <summary>This thread's value, by <c>ref</c>, as the last row on this thread left it (<c>default</c> the first time).</summary>
    public ref T Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _value;
    }

    /// <summary>
    /// The calling thread's box of <typeparamref name="T"/> for <typeparamref name="TOwner"/> (a generated job: one per
    /// method), created on the thread's first call. Generated jobs resolve it once per chunk (once per pass on one thread).
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StrongBox<T> Of<TOwner>() where TOwner : allows ref struct => Store<TOwner>.Box ?? Create<TOwner>();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static StrongBox<T> Create<TOwner>() where TOwner : allows ref struct
    {
        ThreadSetup.Add(ref Store<TOwner>.SetUp, static () => Store<TOwner>.Box ??= new StrongBox<T>(), typeof(Store<TOwner>));
        return Store<TOwner>.Box ??= new StrongBox<T>();
    }

    private static class Store<TOwner> where TOwner : allows ref struct
    {
        [ThreadStatic] public static StrongBox<T> Box;
        public static int SetUp; // the ThreadSetup step that gives every helper thread its box
    }
}
