using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
namespace EasyEcs;

/// <summary>
/// The world's structural gate. Structural changes belong to the world's owner thread (one thread at a time), so the
/// owner's gate is a plain flag: entering checks it and sets it, leaving clears it, no atomic instruction. The check
/// turns misuse into an exception instead of torn tables: a structural change reentered from code running under the
/// gate, or made while the structure is frozen: while the world runs systems or chunks of a parallel [ForEach] on several
/// threads, or a batch job that holds spans into the tables (each such region adds <see cref="FrozenRegion"/> to the flag
/// while it runs). It cannot catch every change made from another thread
/// at the same moment as the owner's; the analyzer (EECS013, EECS014) reports those it can see.
/// <para>
/// The cold paths the threads of a parallel batch reach on their own (adding a missing singleton, a query's first
/// match, closing the tombstones when the last pass ends) take the shared gate instead: one compare-and-swap, spinning
/// under contention. Both flags sit on their own cache line, away from the world's hot fields.
/// </para>
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 2 * Archetype.CacheLine)]
internal struct StructuralGate
{
    /// <summary>What a running frozen region adds to the owner's flag (the owner itself holds 1).</summary>
    private const int FrozenRegion = 2;

    [FieldOffset(Archetype.CacheLine)] private int _held;
    [FieldOffset(Archetype.CacheLine + sizeof(int))] private int _shared;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Enter()
    {
        if (_held != 0) ThrowBusy(_held);
        _held = 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Exit() => _held = 0;

    /// <summary>
    /// A frozen region (a batch of systems, the chunks of a parallel [ForEach], a batch job) starts: structural changes
    /// throw until it ends. Regions nest (a batch job inside a parallel system), hence the atomic add.
    /// </summary>
    public void Freeze() => Interlocked.Add(ref _held, FrozenRegion);

    public void Thaw() => Interlocked.Add(ref _held, -FrozenRegion);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EnterShared()
    {
        if (Interlocked.CompareExchange(ref _shared, 1, 0) != 0) WaitShared();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ExitShared() => Volatile.Write(ref _shared, 0);

    // Contended: spin on a plain read (no cache-line ping-pong), then back off to yields and sleeps.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void WaitShared()
    {
        var spin = new SpinWait();
        do spin.SpinOnce();
        while (Volatile.Read(ref _shared) != 0 || Interlocked.CompareExchange(ref _shared, 1, 0) != 0);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowBusy(int held) => throw new InvalidOperationException(held == 1
        ? "A structural change started while another one was running on this world: structural changes are made by the world's owner thread, one at a time (see doc/guide/threading.md)."
        : "A structural change was made while the world's structure is frozen (systems or a parallel [ForEach] running on several threads, or a batch job holding spans into the tables): record the change as data and apply it in a system or [ForEach] method that runs alone (see doc/guide/threading.md).");
}

/// <summary>Holds the owner's <see cref="StructuralGate"/> for a <c>using</c> block (a ref struct: nothing is allocated).</summary>
internal readonly ref struct StructuralScope
{
    private readonly ref StructuralGate _gate;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public StructuralScope(ref StructuralGate gate)
    {
        _gate = ref gate;
        gate.Enter();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispose() => _gate.Exit();
}

/// <summary>Holds the shared <see cref="StructuralGate"/> for a <c>using</c> block.</summary>
internal readonly ref struct SharedScope
{
    private readonly ref StructuralGate _gate;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SharedScope(ref StructuralGate gate)
    {
        _gate = ref gate;
        gate.EnterShared();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispose() => _gate.ExitShared();
}
