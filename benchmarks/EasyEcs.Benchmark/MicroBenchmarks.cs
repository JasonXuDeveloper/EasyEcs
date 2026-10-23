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

public struct Energy : IComponent, ITracked
{
    public int Value;
}

public struct Clock : ISingletonComponent
{
    public long Tick;
}

/// <summary>[ForEach] system doing the same work as ForEachWrite (the only system of its world).</summary>
[EcsSystem]
public ref struct IntegrateSystem
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
    private World _churn;
    private Entity[] _churned;

    [GlobalSetup]
    public void Setup()
    {
        _queryCtx = new World();
        _queryCtx.CreateEntities(new Entity[N / 2], new Position(), new Velocity());
        _queryCtx.CreateEntities(new Entity[N - N / 2], new Position(), new Velocity(), new Health()); // every other entity

        _queryCtx.AddSingleton<Clock>();
        _systemWorld = new World();
        _systemWorld.CreateEntities(new Entity[N], new Position(), new Velocity());
        _systemWorld.AddSystem<IntegrateSystem>();
        _tracked = new World();
        _trackedHandles = new Entity[1000];
        _tracked.Reserve<Position, Energy>(N);
        for (int i = 0; i < N; i++)
        {
            var e = _tracked.CreateEntity(new Position(), new Energy());
            if (i % (N / 1000) == 0 && i / (N / 1000) < 1000) _trackedHandles[i / (N / 1000)] = e; // scattered rows
        }

        _churn = new World(new WorldOptions { InitialEntityCapacity = 1024 });
        _churned = new Entity[N / 10];
        _states = new World();
        _stateHandles = new Entity[N / 10];
        _states.CreateEntities(_stateHandles, new Position(), new Velocity(), new Health(), new Energy());
    }

    private World _states;
    private Entity[] _stateHandles;

    /// <summary>
    /// Create N entities with 2 components, then destroy them all, in a new world (cold start: the world, its tables
    /// and their growth are allocated by the call; the churn benchmarks measure a warm world, which allocates nothing).
    /// </summary>
    [Benchmark]
    public int CreateAddDestroy()
    {
        var world = new World();
        var entities = new Entity[N];
        for (int i = 0; i < N; i++)
        {
#pragma warning disable EECS052 // the add after the creation is what this measures
            var e = world.CreateEntity();
#pragma warning restore EECS052
            e.Set(new Position());
            e.Set(new Velocity());
            entities[i] = e;
        }

        for (int i = 0; i < N; i++) entities[i].Destroy();
        return world.EntityCount;
    }

    /// <summary>
    /// Long-running process: 10 rounds of create -> destroy in one warm world (recycled ids and rows, no allocation).
    /// </summary>
    [Benchmark]
    public int Churn()
    {
        var entities = _churned;
        for (int round = 0; round < 10; round++)
        {
#pragma warning disable EECS031 // the per-entity side of Churn / ChurnBatch: what this case measures
            for (int i = 0; i < entities.Length; i++)
                entities[i] = _churn.CreateEntity(new Position(), new Health());
#pragma warning restore EECS031

            for (int i = 0; i < entities.Length; i++) entities[i].Destroy();
        }

        return _churn.EntityCount;
    }

    /// <summary>Churn through the batch API: CreateEntities and DestroyAll, one gate pass each per round.</summary>
    [Benchmark]
    public int ChurnBatch()
    {
        for (int round = 0; round < 10; round++)
        {
            _churn.CreateEntities(_churned, new Position(), new Health());
            _churn.DestroyAll<Position>();
        }

        return _churn.EntityCount;
    }

    /// <summary>Remove three components in one call and give them back in one call (a status with several parts).</summary>
    [Benchmark]
    public int RemoveMany()
    {
        var entities = _stateHandles;
        for (int i = 0; i < entities.Length; i++)
        {
            entities[i].Remove<Velocity, Health, Energy>();
            entities[i].Set(new Velocity(), new Health(), new Energy());
        }

        return entities.Length;
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
                e.Set(new Health());
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

        public void Execute(ref Position p, ref Velocity v)
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
        public void Execute(ref Position p, ref Velocity v)
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

    private World _tracked;
    private Entity[] _trackedHandles;

    /// <summary>Look 1,000 entities up by id (TryGetEntity: the id from a client message), then read through the handle.</summary>
    [Benchmark]
    public int TryGetEntity()
    {
        _handles ??= CollectHandles();
        int sum = 0;
        var handles = _handles;
        for (int i = 0; i < handles.Length; i++)
            if (_queryCtx.TryGetEntity(handles[i].Id, out var entity))
                sum += (int)entity.Get<Position>().X;
        return sum;
    }

    /// <summary>
    /// 1,000 writes through handles to a tracked component, every 10th row: each write stamps its row's change tick
    /// and the summary of its 64-row word (compare with <see cref="HandleAccess"/>, an untracked write).
    /// </summary>
    [Benchmark]
    public int TrackedHandleWrite()
    {
        int sum = 0;
        var handles = _trackedHandles;
        for (int i = 0; i < handles.Length; i++) sum += ++handles[i].Get<Energy>().Value;
        return sum;
    }

    private static readonly QueryFilter NoHealth = QueryFilter.Create().Without<Health>();

    /// <summary>Count and Any over the two tables (sums of table counts; Any stops at the first table), ×1000.</summary>
    [Benchmark]
    public int CountAndAny()
    {
        int s = 0;
        for (int i = 0; i < 1000; i++)
            s += _queryCtx.Count<Position, Velocity>() + _queryCtx.Count<Position>(NoHealth) + (_queryCtx.Any<Health>() ? 1 : 0);
        return s;
    }
}
