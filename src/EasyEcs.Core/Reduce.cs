using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EasyEcs;

/// <summary>
/// How values of <typeparamref name="T"/> combine: an associative <see cref="Combine"/> and its
/// <see cref="Identity"/>, which changes nothing it is combined with (0 for a sum, <c>float.MaxValue</c> for a
/// minimum). A singleton implementing it for itself is the result of a <see cref="Reduce{T}"/> parameter. Combine
/// need not be commutative: partial results are combined in row order. Written field by field (each field of the
/// result from the same field of both sides), it is also the elementwise form a vector loop can apply per lane.
/// </summary>
/// <typeparam name="T">The combined value.</typeparam>
public interface IReduction<T> where T : struct
{
    /// <summary>The value that changes nothing it is combined with: what each partial result starts at.</summary>
    static abstract T Identity { get; }

    /// <summary><paramref name="left"/> combined with <paramref name="right"/>, the rows of <paramref name="left"/> coming first.</summary>
    static abstract T Combine(T left, T right);
}

/// <summary>
/// A reduction of a <c>[ForEach]</c> pass into singleton <typeparamref name="T"/> (OpenMP's <c>reduction</c>, MPI's
/// <c>Allreduce</c>): <c>static void Count(in Health h, Reduce&lt;Census&gt; census) =&gt; census.Value.Alive++;</c>.
/// Each chunk of a parallel pass (or the whole pass on one thread) accumulates into its own copy, which starts at
/// <see cref="IReduction{T}.Identity"/>: no lock and no atomic per row. When the pass ends, the copies are combined in
/// chunk order, which is row order, and the result replaces the world's <typeparamref name="T"/> (added when missing).
/// <para>
/// Integer results are exact and do not depend on the threads. A floating-point result depends on where chunks
/// begin, since each chunk's sum is rounded on its own: with a fixed <see cref="ForEachAttribute.ChunkRows"/> it is the
/// same on every run and for any thread count, but not bit for bit the result of the same method with
/// <c>Parallel = false</c> (one partial result). The default chunk size follows the thread count.
/// </para>
/// The system writes <typeparamref name="T"/> (once per pass); <see cref="Value"/> holds this chunk's partial result,
/// not the total. Valid for the call it is passed to (a ref struct).
/// </summary>
/// <typeparam name="T">The singleton holding the result, which combines with itself.</typeparam>
public readonly ref struct Reduce<T> where T : struct, ISingletonComponent, IReduction<T>
{
    private readonly ref T _value;

    /// <summary>Wraps the partial result <paramref name="value"/>. Generated code calls this.</summary>
    [EditorBrowsable(EditorBrowsableState.Never), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Reduce(ref T value) => _value = ref value;

    /// <summary>The partial result of this chunk of rows (of the pass, on one thread), by <c>ref</c>: write its fields.</summary>
    public ref T Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _value;
    }

    /// <summary>Combine <paramref name="value"/> into the partial result (<see cref="IReduction{T}.Combine"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Combine(T value) => _value = T.Combine(_value, value);
}
