using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// A combat pass that produces hit records (target, amount) for a later serial system to apply, the output of a
/// parallel pass a server produces each tick: <see cref="Emit{T}"/> in parallel and on one thread, against what a
/// pass writes without it: the hand-written "write a component, then scan it" (a parallel pass writes a pending hit
/// into each attacker's row, a serial query collects them in row order), a shared <see cref="ConcurrentQueue{T}"/>
/// (records in the order threads happen to add them), and an <see cref="Interlocked"/> cursor into a preallocated
/// array (the same, without the queue's segments). Every variant ends with the records in one list and clears it, as
/// the applying system would. Half the attackers hit (the others are out of range).
/// </summary>
[MemoryDiagnoser]
public class EmitBenchmark
{
    [Params(10_000, 100_000)] public int Attackers;

    private World _world;
    private readonly List<Hit> _collected = new();
    private readonly StrongBox<ConcurrentQueue<Hit>> _queue = new(new ConcurrentQueue<Hit>());
    private readonly StrongBox<Hit[]> _array = new();
    private readonly StrongBox<int> _cursor = new();

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Attackers + 64 });
        var targets = new Entity[64];
        _world.CreateEntities(targets, new Life(100));
        var attackers = new Entity[Attackers];
        _world.CreateEntities(attackers, new Attack(0, 0), new Aim(default), new PendingHit());
        for (int i = 0; i < attackers.Length; i++)
            attackers[i].Set(new Attack(i % 50, (i * 7) % 20), new Aim(targets[i % targets.Length].Id));
        _array.Value = new Hit[Attackers];
        _collected.Capacity = Attackers;
        _world.GetOrAddSingleton<Emitted<Hit>>().Items.EnsureCapacity(Attackers);
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark(Baseline = true)]
    public int EmitParallel()
    {
        Strikes.Emitting(_world);
        ref var hits = ref _world.GetSingleton<Emitted<Hit>>().Items;
        int count = hits.Count;
        hits.Clear();
        return count;
    }

    [Benchmark]
    public int EmitSerial()
    {
        Strikes.EmittingSerial(_world);
        ref var hits = ref _world.GetSingleton<Emitted<Hit>>().Items;
        int count = hits.Count;
        hits.Clear();
        return count;
    }

    [Benchmark]
    public int ComponentThenScan()
    {
        Strikes.Pending(_world);
        Strikes.Collect(_world, _collected);
        int count = _collected.Count;
        _collected.Clear();
        return count;
    }

    [Benchmark]
    public int ConcurrentQueue()
    {
        var job = new Enqueue { Queue = _queue };
        _world.ForEachParallel<Attack, Aim, Enqueue>(ref job);
        int count = 0;
        while (_queue.Value.TryDequeue(out var hit)) _collected.Add(hit);
        count = _collected.Count;
        _collected.Clear();
        return count;
    }

    [Benchmark]
    public int InterlockedCursor()
    {
        _cursor.Value = 0;
        var job = new Claim { Hits = _array, Cursor = _cursor };
        _world.ForEachParallel<Attack, Aim, Claim>(ref job);
        return _cursor.Value;
    }

    public record struct Life(int Value) : IComponent;
    public record struct Attack(int Range, int Power) : IComponent;
    public record struct Aim(EntityId Target) : IComponent;
    public record struct PendingHit(EntityId Target, int Amount) : IComponent;
    public record struct Hit(EntityId Target, int Amount);

    public static class Strikes
    {
        [ForEach(Parallel = true)]
        public static void Emitting(in Attack attack, in Aim aim, Emit<Hit> hits)
        {
            if (attack.Range < 25) hits.Add(new Hit(aim.Target, attack.Power));
        }

        [ForEach]
        public static void EmittingSerial(in Attack attack, in Aim aim, Emit<Hit> hits)
        {
            if (attack.Range < 25) hits.Add(new Hit(aim.Target, attack.Power));
        }

        [ForEach(Parallel = true)]
        public static void Pending(in Attack attack, in Aim aim, ref PendingHit pending) =>
            pending = attack.Range < 25 ? new PendingHit(aim.Target, attack.Power) : default;

        [ForEach]
        public static void Collect(in PendingHit pending, List<Hit> into)
        {
            if (pending.Amount != 0 || !pending.Target.IsNull) into.Add(new Hit(pending.Target, pending.Amount));
        }
    }

    private struct Enqueue : IForEach<Attack, Aim>, INoStructuralChanges
    {
        public StrongBox<ConcurrentQueue<Hit>> Queue;

        public void Execute(ref Attack attack, ref Aim aim)
        {
            if (attack.Range < 25) Queue.Value.Enqueue(new Hit(aim.Target, attack.Power));
        }

        static int IForEach<Attack, Aim>.WriteMask => 0;
    }

    private struct Claim : IForEach<Attack, Aim>, INoStructuralChanges
    {
        public StrongBox<Hit[]> Hits;
        public StrongBox<int> Cursor;

        public void Execute(ref Attack attack, ref Aim aim)
        {
            if (attack.Range < 25) Hits.Value[Interlocked.Increment(ref Cursor.Value) - 1] = new Hit(aim.Target, attack.Power);
        }

        static int IForEach<Attack, Aim>.WriteMask => 0;
    }
}
