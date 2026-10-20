using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// Work on one entity at a time, as a request handler or an actor's message does it: 1,000 calls, each on another of
/// 10,000 entities, reading and writing 1, 3 or 5 components (the first one tracked), with or without a filter
/// (<c>Without&lt;Dead&gt;</c>, a tenth of the entities dead), with or without a structural change in the middle (a tag
/// toggled, then one more write). <c>Handwritten</c> does the same work through the handle: <c>Has&lt;Dead&gt;()</c>, a
/// <c>TryGetRef</c> per component (which stamps the tracked one), and after the structural change the ref taken again.
/// <c>Run</c> is <c>entity.Run</c> with a job (one implementing <c>INoStructuralChanges</c> when it changes nothing).
/// 0 bytes either way.
/// </summary>
[MemoryDiagnoser]
public class SingleEntityBenchmark
{
    private const int Count = 10_000, Calls = 1_000;
    private static readonly QueryFilter Alive = QueryFilter.Create().Without<Dead>();

    [Params(1, 3, 5)] public int Components;
    [Params(false, true)] public bool Filtered;
    [Params(false, true)] public bool Structural;

    private World _world;
    private Entity[] _entities;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World();
        _entities = new Entity[Count];
        _world.CreateEntities(_entities, new S1(1), new S2(2), new S3(3), new S4(4), new S5(5));
        for (int i = 0; i < Count; i += 10) _entities[i].Set(new Dead());
        for (int i = 0; i < 20; i++) { Handwritten(); Run(); } // tables of the toggled tag exist
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark(Baseline = true)]
    public int Handwritten()
    {
        int done = 0;
        for (int i = 0; i < Calls; i++)
            if (By(_entities[i * 7 % Count])) done++;
        return done;
    }

    [Benchmark]
    public int Run()
    {
        int done = 0;
        for (int i = 0; i < Calls; i++)
            if (With(_entities[i * 7 % Count])) done++;
        return done;
    }

    private bool By(Entity e)
    {
        if (Filtered && e.Has<Dead>()) return false;
        switch (Components)
        {
            case 1:
            {
                ref var s1 = ref e.TryGetRef<S1>(out bool f1);
                if (!f1) return false;
                s1.V++;
                if (Structural)
                {
                    Toggle(e);
                    e.Get<S1>().V++;   // the row moved: the ref is taken again
                }

                return true;
            }
            case 3:
            {
                ref var s1 = ref e.TryGetRef<S1>(out bool f1);
                if (!f1) return false;
                ref var s2 = ref e.TryGetRef<S2>(out bool f2);
                if (!f2) return false;
                ref var s3 = ref e.TryGetRef<S3>(out bool f3);
                if (!f3) return false;
                s1.V += s2.V;
                s2.V += s3.V;
                s3.V++;
                if (Structural)
                {
                    Toggle(e);
                    e.Get<S3>().V++;
                }

                return true;
            }
            default:
            {
                ref var s1 = ref e.TryGetRef<S1>(out bool f1);
                if (!f1) return false;
                ref var s2 = ref e.TryGetRef<S2>(out bool f2);
                if (!f2) return false;
                ref var s3 = ref e.TryGetRef<S3>(out bool f3);
                if (!f3) return false;
                ref var s4 = ref e.TryGetRef<S4>(out bool f4);
                if (!f4) return false;
                ref var s5 = ref e.TryGetRef<S5>(out bool f5);
                if (!f5) return false;
                s1.V += s2.V;
                s2.V += s3.V;
                s3.V += s4.V;
                s4.V += s5.V;
                s5.V++;
                if (Structural)
                {
                    Toggle(e);
                    e.Get<S5>().V++;
                }

                return true;
            }
        }
    }

    private bool With(Entity e)
    {
        switch (Components, Structural)
        {
            case (1, false):
            {
                var job = new One();
                return Filtered ? e.Run<S1, One>(ref job, Alive) : e.Run<S1, One>(ref job);
            }
            case (1, true):
            {
                var job = new OneToggling();
                return Filtered ? e.Run<S1, OneToggling>(ref job, Alive) : e.Run<S1, OneToggling>(ref job);
            }
            case (3, false):
            {
                var job = new Three();
                return Filtered ? e.Run<S1, S2, S3, Three>(ref job, Alive) : e.Run<S1, S2, S3, Three>(ref job);
            }
            case (3, true):
            {
                var job = new ThreeToggling();
                return Filtered ? e.Run<S1, S2, S3, ThreeToggling>(ref job, Alive) : e.Run<S1, S2, S3, ThreeToggling>(ref job);
            }
            case (_, false):
            {
                var job = new Five();
                return Filtered ? e.Run<S1, S2, S3, S4, S5, Five>(ref job, Alive) : e.Run<S1, S2, S3, S4, S5, Five>(ref job);
            }
            default:
            {
                var job = new FiveToggling();
                return Filtered ? e.Run<S1, S2, S3, S4, S5, FiveToggling>(ref job, Alive) : e.Run<S1, S2, S3, S4, S5, FiveToggling>(ref job);
            }
        }
    }

    private static void Toggle(Entity e)
    {
        if (e.Has<Marked>()) e.Remove<Marked>();
        else e.Set(new Marked());
    }

    public record struct S1(int V) : IComponent, ITrackChanges;
    public record struct S2(int V) : IComponent;
    public record struct S3(int V) : IComponent;
    public record struct S4(int V) : IComponent;
    public record struct S5(int V) : IComponent;
    public struct Dead : IComponent;
    public struct Marked : IComponent;

    private struct One : IForEachEntity<S1>, INoStructuralChanges
    {
        public void Execute(Entity e, ref S1 s1) => s1.V++;
    }

    private struct OneToggling : IForEachEntity<S1>
    {
        public void Execute(Entity e, ref S1 s1)
        {
            s1.V++;
            Toggle(e);
            s1.V++; // through the same ref: carried over to the new row
        }
    }

    private struct Three : IForEachEntity<S1, S2, S3>, INoStructuralChanges
    {
        public void Execute(Entity e, ref S1 s1, ref S2 s2, ref S3 s3)
        {
            s1.V += s2.V;
            s2.V += s3.V;
            s3.V++;
        }
    }

    private struct ThreeToggling : IForEachEntity<S1, S2, S3>
    {
        public void Execute(Entity e, ref S1 s1, ref S2 s2, ref S3 s3)
        {
            s1.V += s2.V;
            s2.V += s3.V;
            s3.V++;
            Toggle(e);
            s3.V++;
        }
    }

    private struct Five : IForEachEntity<S1, S2, S3, S4, S5>, INoStructuralChanges
    {
        public void Execute(Entity e, ref S1 s1, ref S2 s2, ref S3 s3, ref S4 s4, ref S5 s5)
        {
            s1.V += s2.V;
            s2.V += s3.V;
            s3.V += s4.V;
            s4.V += s5.V;
            s5.V++;
        }
    }

    private struct FiveToggling : IForEachEntity<S1, S2, S3, S4, S5>
    {
        public void Execute(Entity e, ref S1 s1, ref S2 s2, ref S3 s3, ref S4 s4, ref S5 s5)
        {
            s1.V += s2.V;
            s2.V += s3.V;
            s3.V += s4.V;
            s4.V += s5.V;
            s5.V++;
            Toggle(e);
            s5.V++;
        }
    }
}
