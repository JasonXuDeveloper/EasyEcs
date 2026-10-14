using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using EasyEcs.Core;
using EasyEcs.Core.Components;

namespace EasyEcs.Benchmark;

public struct Position : IComponent
{
    public float X, Y;
}

public struct Velocity : IComponent
{
    public float X, Y;
}

public struct Health : IComponent
{
    public int Value;
}

public struct Clock : ISingletonComponent
{
    public long Tick;
}

/// <summary>[Query]-generated system doing the same work as ForEachWrite.</summary>
public partial class IntegrateSystem : EasyEcs.Core.Systems.SystemBase
{
    [EasyEcs.Core.Systems.Query]
    private static void Integrate(ref Position p, in Velocity v)
    {
        p.X += v.X;
        p.Y += v.Y;
    }

    public void Run(Context ctx) => RunIntegrate(ctx);
}

/// <summary>
/// Micro benchmarks isolating single operations (diagnostics). The headline numbers come from
/// <see cref="WorldSimulationBenchmark"/>.
/// </summary>
[MemoryDiagnoser]
public class MicroBenchmarks
{
    [Params(10_000)] public int N;

    private Context _queryCtx;

    [GlobalSetup]
    public void Setup()
    {
        _queryCtx = new Context(new Context.Options(parallel: false));
        for (int i = 0; i < N; i++)
        {
            var e = _queryCtx.CreateEntity();
            e.Value.AddComponents<Position, Velocity>();
            if ((i & 1) == 0) e.Value.AddComponent<Health>();
        }

        _queryCtx.AddSingletonComponent<Clock>();
    }

    /// <summary>Create N entities with 2 components, then destroy them all (one lifecycle round).</summary>
    [Benchmark]
    public int CreateAddDestroy()
    {
        var ctx = new Context(new Context.Options(parallel: false));
        var refs = new EntityRef[N];
        for (int i = 0; i < N; i++)
        {
            var e = ctx.CreateEntity();
            e.Value.AddComponent<Position>();
            e.Value.AddComponent<Velocity>();
            refs[i] = e;
        }

        for (int i = 0; i < N; i++) ctx.DestroyEntity(refs[i]);
        return ctx.EntityCount;
    }

    /// <summary>Long-running process: 10 rounds of create -> add -> destroy in the same context.</summary>
    [Benchmark]
    public int Churn()
    {
        var ctx = new Context(new Context.Options(parallel: false, initialEntityCapacity: 1024));
        var refs = new EntityRef[N / 10];
        for (int round = 0; round < 10; round++)
        {
            for (int i = 0; i < refs.Length; i++)
            {
                var e = ctx.CreateEntity();
                e.Value.AddComponent<Position>();
                e.Value.AddComponent<Health>();
                refs[i] = e;
            }

            for (int i = 0; i < refs.Length; i++) ctx.DestroyEntity(refs[i]);
        }

        return ctx.EntityCount;
    }

    /// <summary>Remove + add a component (state toggles such as buffs or combat state).</summary>
    [Benchmark]
    public int ToggleComponent()
    {
        int c = 0;
        foreach (var r in _queryCtx.GroupOf<Position, Velocity>())
        {
            ref var e = ref r.Entity;
            if (e.HasComponent<Health>())
            {
                e.RemoveComponent<Health>();
                e.AddComponent<Health>();
                c++;
            }
        }

        return c;
    }

    /// <summary>Per-frame iteration: Position += Velocity, with a reduction.</summary>
    [Benchmark]
    public float QueryIterate()
    {
        float sum = 0;
        foreach (var r in _queryCtx.GroupOf<Position, Velocity>())
        {
            ref var p = ref r.Component1;
            ref readonly var v = ref r.Component2;
            p.X += v.X + 1;
            p.Y += v.Y;
            sum += p.X;
        }

        return sum;
    }

    private struct MoveJob : IForEach<Position, Velocity>
    {
        public float Sum;

        public void Execute(int entityId, ref Position p, ref Velocity v)
        {
            p.X += v.X + 1;
            p.Y += v.Y;
            Sum += p.X;
        }
    }

    /// <summary>Same work as QueryIterate through a struct-job ForEach.</summary>
    [Benchmark]
    public float ForEachIterate()
    {
        var job = new MoveJob();
        _queryCtx.ForEach<Position, Velocity, MoveJob>(ref job);
        return job.Sum;
    }

    private struct IntegrateJob : IForEach<Position, Velocity>
    {
        public void Execute(int entityId, ref Position p, ref Velocity v)
        {
            p.X += v.X;
            p.Y += v.Y;
        }
    }

    /// <summary>Pure write (no cross-entity dependency chain) measuring iteration overhead: GroupOf.</summary>
    [Benchmark]
    public void QueryWrite()
    {
        foreach (var r in _queryCtx.GroupOf<Position, Velocity>())
        {
            ref var p = ref r.Component1;
            ref readonly var v = ref r.Component2;
            p.X += v.X;
            p.Y += v.Y;
        }
    }

    private Position[] _rawP;
    private Velocity[] _rawV;

    /// <summary>Theoretical floor: two plain arrays.</summary>
    [Benchmark(Baseline = true)]
    public void RawArrayWrite()
    {
        _rawP ??= new Position[N];
        _rawV ??= new Velocity[N];
        var p = _rawP;
        var v = _rawV;
        for (int i = 0; i < p.Length && i < v.Length; i++)
        {
            p[i].X += v[i].X;
            p[i].Y += v[i].Y;
        }
    }

    private readonly IntegrateSystem _integrate = new();

    /// <summary>Pure write via a [Query] system (generated job).</summary>
    [Benchmark]
    public void QuerySystemWrite() => _integrate.Run(_queryCtx);

    /// <summary>Pure write: ForEach.</summary>
    [Benchmark]
    public void ForEachWrite()
    {
        var job = new IntegrateJob();
        _queryCtx.ForEach<Position, Velocity, IntegrateJob>(ref job);
    }

    /// <summary>Three-component query (matches half of the entities).</summary>
    [Benchmark]
    public int QueryIterate3()
    {
        int sum = 0;
        foreach (var r in _queryCtx.GroupOf<Position, Velocity, Health>())
        {
            sum += ++r.Component3.Value;
        }

        return sum;
    }

    /// <summary>Singleton component read/write.</summary>
    [Benchmark]
    public long SingletonAccess()
    {
        long s = 0;
        for (int i = 0; i < 1000; i++)
        {
            ref var c = ref _queryCtx.GetSingletonComponent<Clock>().Value;
            s += ++c.Tick;
        }

        return s;
    }
}

