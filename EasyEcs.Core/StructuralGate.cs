using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
namespace EasyEcs;

/// <summary>
/// The world's structural gate: one compare-and-swap to enter, one release store to leave. It is not reentrant, and
/// nothing taken under it calls back into user code (systems' OnCreate runs outside it), so it never needs to be; debug
/// builds assert that. The flag sits alone on its own cache line, so threads spinning on it never share a line with the
/// world's hot fields.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 2 * Archetype.CacheLine)]
internal struct StructuralGate
{
    [FieldOffset(Archetype.CacheLine)] private int _held;
#if DEBUG
    [FieldOffset(Archetype.CacheLine + sizeof(int))] private int _owner;
#endif

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Enter()
    {
#if DEBUG
        Debug.Assert(Volatile.Read(ref _owner) != Environment.CurrentManagedThreadId, "StructuralGate is not reentrant");
#endif
        if (Interlocked.CompareExchange(ref _held, 1, 0) != 0) Wait();
#if DEBUG
        _owner = Environment.CurrentManagedThreadId;
#endif
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Exit()
    {
#if DEBUG
        _owner = 0;
#endif
        Volatile.Write(ref _held, 0);
    }

    // Contended: spin on a plain read (no cache-line ping-pong), then back off to yields and sleeps.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Wait()
    {
        var spin = new SpinWait();
        do spin.SpinOnce();
        while (Volatile.Read(ref _held) != 0 || Interlocked.CompareExchange(ref _held, 1, 0) != 0);
    }
}

/// <summary>Holds the <see cref="StructuralGate"/> for a <c>using</c> block (a ref struct: nothing is allocated).</summary>
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
