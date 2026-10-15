using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

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

/// <summary>[ForEach] system doing the same work as ForEachWrite (the only system of its world).</summary>
public partial class IntegrateSystem : EcsSystem
{
    [ForEach]
    private static void Integrate(ref Position p, in Velocity v)
    {
        p.X += v.X;
        p.Y += v.Y;
    }
}

/// <summary>
/// Micro benchmarks isolating single operations (diagnostics). The headline numbers come from
/// <see cref="WorldSimulationBenchmark"/>.
/// </summary>
[MemoryDiagnoser]
public class MicroBenchmarks
{
    [Params(10_000)] public int N;

    private World _queryCtx;

    [GlobalSetup]
    public void Setup()
    {
        _queryCtx = new World();
        for (int i = 0; i < N; i++)
        {
            var e = _queryCtx.CreateEntity(new Position(), new Velocity());
            if ((i & 1) == 0) e.Add<Health>();
        }

        _queryCtx.AddSingleton<Clock>();
        _systemWorld = new World();
        for (int i = 0; i < N; i++) _systemWorld.CreateEntity(new Position(), new Velocity());
        _systemWorld.AddSystem<IntegrateSystem>();
    }

    /// <summary>Create N entities with 2 components, then destroy them all (one lifecycle round).</summary>
    [Benchmark]
    public int CreateAddDestroy()
    {
        var world = new World();
        var entities = new Entity[N];
        for (int i = 0; i < N; i++)
        {
            var e = world.CreateEntity();
            e.Add<Position>();
            e.Add<Velocity>();
            entities[i] = e;
        }

        for (int i = 0; i < N; i++) entities[i].Destroy();
        return world.EntityCount;
    }

    /// <summary>Long-running process: 10 rounds of create -> add -> destroy in the same world.</summary>
    [Benchmark]
    public int Churn()
    {
        var world = new World(new WorldOptions { InitialEntityCapacity = 1024 });
        var entities = new Entity[N / 10];
        for (int round = 0; round < 10; round++)
        {
            for (int i = 0; i < entities.Length; i++)
                entities[i] = world.CreateEntity(new Position(), new Health());

            for (int i = 0; i < entities.Length; i++) entities[i].Destroy();
        }

        return world.EntityCount;
    }

    /// <summary>Remove + add a component (state toggles such as buffs or combat state).</summary>
    [Benchmark]
    public int ToggleComponent()
    {
        int c = 0;
        foreach (var r in _queryCtx.Query<Position, Velocity>())
        {
            var e = r.Entity;
            if (e.Has<Health>())
            {
                e.Remove<Health>();
                e.Add<Health>();
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
        foreach (var r in _queryCtx.Query<Position, Velocity>())
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

    /// <summary>Pure write (no cross-entity dependency chain) measuring Query iteration overhead.</summary>
    [Benchmark]
    public void QueryWrite()
    {
        foreach (var r in _queryCtx.Query<Position, Velocity>())
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

    private World _systemWorld;

    /// <summary>Pure write via a [ForEach] system: one World.Update of a world holding only that system.</summary>
    [Benchmark]
    public void ForEachSystemWrite() => _systemWorld.Update();

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
        foreach (var r in _queryCtx.Query<Position, Velocity, Health>())
        {
            sum += ++r.Component3.Value;
        }

        return sum;
    }

    /// <summary>1000 component reads/writes through entity handles (version-checked).</summary>
    [Benchmark]
    public int HandleAccess()
    {
        _handles ??= CollectHandles();
        int sum = 0;
        var handles = _handles;
        for (int i = 0; i < handles.Length; i++)
            sum += (int)(handles[i].Get<Position>().X += 1);
        return sum;
    }

    private Entity[] _handles;

    private Entity[] CollectHandles()
    {
        var list = new System.Collections.Generic.List<Entity>();
        foreach (var r in _queryCtx.Query<Position>())
            if (list.Count < 1000) list.Add(r.Entity);
        return list.ToArray();
    }

    /// <summary>Singleton component read/write.</summary>
    [Benchmark]
    public long SingletonAccess()
    {
        long s = 0;
        for (int i = 0; i < 1000; i++)
        {
            ref var c = ref _queryCtx.GetSingleton<Clock>();
            s += ++c.Tick;
        }

        return s;
    }
}

