using System.Runtime.CompilerServices;
using System.Threading;

namespace EasyEcs;

/// <summary>
/// A lock for a few instructions that parallel work takes from several threads at once (the helper threads' queue, the
/// lists of a pool): one compare-and-swap to enter, a plain write to leave, and a contended thread spins on a plain read,
/// then yields, and never blocks. A <see cref="Lock"/> blocks a thread that waits past its spin on an event it creates
/// the first time one does, on that thread: 56 bytes in a parallel pass long after warm-up, whenever a preempted holder
/// first makes a thread wait (D128). Not reentrant; the holder runs no user code.
/// </summary>
internal struct SpinGate
{
    private int _held;

    /// <summary>Enter, spinning (then yielding) while another thread holds the gate.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Enter()
    {
        if (Interlocked.CompareExchange(ref _held, 1, 0) != 0) Wait();
    }

    /// <summary>Leave (the holder only).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Exit() => Volatile.Write(ref _held, 0);

    // Contended: spin on a plain read (no cache-line ping-pong), then yield; never sleep a millisecond (a section holds
    // the gate for a few instructions, and a preempted holder runs again on a yield).
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Wait()
    {
        var spin = new SpinWait();
        do spin.SpinOnce(sleep1Threshold: -1);
        while (Volatile.Read(ref _held) != 0 || Interlocked.CompareExchange(ref _held, 1, 0) != 0);
    }
}

/// <summary>Holds a <see cref="SpinGate"/> for a <c>using</c> block (a ref struct: nothing is allocated).</summary>
internal readonly ref struct SpinScope
{
    private readonly ref SpinGate _gate;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SpinScope(ref SpinGate gate)
    {
        _gate = ref gate;
        gate.Enter();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispose() => _gate.Exit();
}
