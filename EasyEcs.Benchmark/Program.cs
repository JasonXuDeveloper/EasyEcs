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

/// <summary>
/// 典型服务器负载：实体频繁创建/销毁（玩家上下线、子弹、掉落物），
/// 以及每帧按组件组合遍历。
/// </summary>
[MemoryDiagnoser]
public class EcsBenchmarks
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

    /// <summary>创建 N 个实体并挂 2 个组件，然后全部销毁（单轮生命周期）。</summary>
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

    /// <summary>长期运行场景：同一个 Context 里反复 创建→加组件→销毁，10 轮。</summary>
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

    /// <summary>添加/移除组件（状态切换，如 Buff、战斗状态）。</summary>
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

    /// <summary>每帧遍历：Position += Velocity。</summary>
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

    /// <summary>同 QueryIterate，但走 struct job 的 ForEach。</summary>
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

    /// <summary>纯写入（无跨实体依赖链），衡量遍历本身的开销：GroupOf 版本。</summary>
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

    /// <summary>理论下限：直接遍历两个数组。</summary>
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

    /// <summary>纯写入：ForEach 版本。</summary>
    [Benchmark]
    public void ForEachWrite()
    {
        var job = new IntegrateJob();
        _queryCtx.ForEach<Position, Velocity, IntegrateJob>(ref job);
    }

    /// <summary>三组件查询（命中一半实体）。</summary>
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

    /// <summary>单例组件读写。</summary>
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

public static class Program
{
    public static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
