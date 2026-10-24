using System;
using System.Runtime.CompilerServices;

namespace EasyEcs;

/// <summary>
/// Deterministic random numbers for <c>[ForEach]</c> methods, parallel ones included (cuRAND's Philox, a counter-based
/// generator): each number is a pure function of a seed, an entity, a tick and a stream, with no state to advance, so
/// it is the same on every run, for any thread count and any order the rows run in. A singleton holding the seed (and
/// the tick, which the world advances): <c>world.SetEntityRandom(Seed: 42)</c>, then
/// <c>static void Wander(ref Velocity v, Entity entity, in EntityRandom random) =&gt; v.X = random.Range(entity.Id, 0,
/// -1f, 1f);</c> draws new numbers on every update. The static members take the four inputs explicitly (a seed of your
/// own, the tick of your clock).
/// <para>
/// The <c>stream</c> tells apart the draws of one entity in one tick: give each call site of a method its own (0, 1,
/// 2, ...). The same four inputs give the same number, so two call sites with one stream draw the same number.
/// </para>
/// <para>
/// The bits are four rounds of SplitMix64's finalizer, one per input: a bijection of 64 bits per round, so two inputs
/// that differ in one bit differ in about half the output bits. It is a hash, not a cryptographic generator.
/// </para>
/// </summary>
/// <param name="Seed">The world's seed: a different seed, a different sequence for every entity.</param>
public record struct EntityRandom(ulong Seed) : ISingletonComponent
{
    /// <summary>
    /// The step the numbers are drawn for: the number of <see cref="World.Update()"/> calls since the singleton was
    /// added. The world adds one at the start of every <see cref="World.Update()"/>, before any system runs, so each
    /// update draws new numbers with no code of yours. <see cref="World.Update{TGroup}"/> leaves it: a group's systems
    /// draw with the tick of the last <see cref="World.Update()"/>, so a group run between two updates (replication
    /// every third tick) draws what the update drew, and a world updated only by groups calls
    /// <see cref="World.Update()"/> once per tick (its default group may hold no system) or draws through the static
    /// members with a tick of its own. It is saved, captured and restored with the singleton, so a loaded or restored
    /// world continues its sequence.
    /// </summary>
    public ulong Tick { readonly get; internal set; }

    /// <summary>64 random bits for <paramref name="entity"/> in this tick, stream <paramref name="stream"/>. O(1), pure.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ulong Bits(EntityId entity, uint stream) => Bits(Seed, entity, Tick, stream);

    /// <summary>A float in [0, 1) for <paramref name="entity"/> in this tick, stream <paramref name="stream"/>. O(1), pure.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly float Float(EntityId entity, uint stream) => Float(Seed, entity, Tick, stream);

    /// <summary>An int in [<paramref name="min"/>, <paramref name="max"/>) (<paramref name="min"/> when they are equal). O(1), pure.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly int Range(EntityId entity, uint stream, int min, int max) => Range(Seed, entity, Tick, stream, min, max);

    /// <summary>A float in [<paramref name="min"/>, <paramref name="max"/>). O(1), pure.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly float Range(EntityId entity, uint stream, float min, float max) => Range(Seed, entity, Tick, stream, min, max);

    /// <summary>True with probability <paramref name="probability"/> (0: never, 1: always). O(1), pure.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Chance(EntityId entity, uint stream, float probability) => Chance(Seed, entity, Tick, stream, probability);

    /// <summary>
    /// 64 random bits for (<paramref name="seed"/>, <paramref name="entity"/>, <paramref name="tick"/>,
    /// <paramref name="stream"/>): the same inputs always give the same bits. O(1), pure, allocation-free.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Bits(ulong seed, EntityId entity, ulong tick, uint stream)
    {
        ulong id = (uint)entity.Index | (ulong)(uint)entity.Version << 32;
        // The seed is mixed first: with seed ^ id, seeds 1 and 2 would only swap the numbers of entities 1 and 2 (and every
        // pair id ^ 3 apart). After a mix, inputs that give one state are 64 random bits apart, never two ids or ticks.
        ulong x = Mix(Mix(seed) ^ id);
        x = Mix(x ^ tick);
        return Mix(x ^ stream);
    }

    /// <summary>A float in [0, 1): the top 24 bits of <see cref="Bits(ulong, EntityId, ulong, uint)"/>, so every value is a multiple of 2^-24. O(1), pure.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Float(ulong seed, EntityId entity, ulong tick, uint stream) =>
        (Bits(seed, entity, tick, stream) >> 40) * (1f / (1 << 24));

    /// <summary>
    /// An int in [<paramref name="min"/>, <paramref name="max"/>) (<paramref name="min"/> when they are equal): the high
    /// half of the bits times the range, without a division, a bias below range / 2^64. O(1), pure.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="max"/> is below <paramref name="min"/>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Range(ulong seed, EntityId entity, ulong tick, uint stream, int min, int max)
    {
        if (max < min) ThrowRange();
        ulong range = (ulong)((long)max - min);
        return (int)(min + (long)Math.BigMul(Bits(seed, entity, tick, stream), range, out _));
    }

    /// <summary>A float in [<paramref name="min"/>, <paramref name="max"/>): <c>min + Float × (max - min)</c>. O(1), pure.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Range(ulong seed, EntityId entity, ulong tick, uint stream, float min, float max) =>
        min + Float(seed, entity, tick, stream) * (max - min);

    /// <summary>True with probability <paramref name="probability"/>: <c>Float &lt; probability</c>. O(1), pure.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Chance(ulong seed, EntityId entity, ulong tick, uint stream, float probability) =>
        Float(seed, entity, tick, stream) < probability;

    // SplitMix64's finalizer (Stafford's Mix13): a bijection of 64 bits with full avalanche.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Mix(ulong x)
    {
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EB;
        return x ^ (x >> 31);
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn, MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowRange() => throw new ArgumentOutOfRangeException("max", "max is below min");
}
