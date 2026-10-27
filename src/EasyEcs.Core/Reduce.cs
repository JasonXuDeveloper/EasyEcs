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
/// <c>Allreduce</c>), taken by <c>ref</c>: <c>static void Count(in Health h, ref Reduce&lt;Census&gt; census) =&gt;
/// census.Value.Alive++;</c>. Each chunk of a parallel pass (or the whole pass on one thread) accumulates into its own
/// copy, which starts at <see cref="IReduction{T}.Identity"/>: no lock and no atomic per row. When the pass ends, the
/// copies are combined in chunk order, which is row order, and the result replaces the world's <typeparamref name="T"/>
/// (added when missing), or, for a <typeparamref name="T"/> declared <see cref="AccumulateAttribute">[Accumulate]</see>,
/// is combined into it: a total that every pass adds to.
/// <para>
/// Integer results are exact and do not depend on the threads. A floating-point result depends on where chunks
/// begin, since each chunk's sum is rounded on its own. Chunks of a pass that reduces begin where the row count alone
/// says (16 chunks of 1,024 to 16,384 rows, or <see cref="ForEachAttribute.ChunkRows"/> when set), so the result is the
/// same on every run and for any thread count, but not bit for bit the result of the same method with
/// <c>Parallel = false</c> (one partial result).
/// </para>
/// <para>
/// A plain struct passed by <c>ref</c>, not a wrapper holding a reference: the pass keeps each run's partial result in
/// a local, and the JIT keeps a local passed by <c>ref</c> to an inlined method in registers, while a local whose
/// address a ref field holds stays in memory, a load and a store per row (a float sum then runs several times
/// slower).
/// </para>
/// The system writes <typeparamref name="T"/> (once per pass); <see cref="Value"/> holds this chunk's partial result,
/// not the total.
/// </summary>
/// <typeparam name="T">The singleton holding the result, which combines with itself.</typeparam>
public struct Reduce<T> where T : struct, ISingletonComponent, IReduction<T>
{
    /// <summary>The partial result of this chunk of rows (of the pass, on one thread): write its fields.</summary>
    public T Value;

    /// <summary>Combine <paramref name="value"/> into the partial result (<see cref="IReduction{T}.Combine"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Combine(T value) => Value = T.Combine(Value, value);
}
