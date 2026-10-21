using System;
using BenchmarkDotNet.Attributes;
using EasyEcs;
using D = DefaultEcs;

namespace EcsComparison.Features;

// Systems that run at the same time. DefaultEcs is the only peer that schedules whole systems in parallel
// (ParallelSystem); Friflo and Arch run a query's chunks on several threads (see ParallelQueryComparison), not
// systems. The setup checks each library's result before anything is measured.

public struct Body(float x, float y, float vx, float vy) : EasyEcs.IComponent
{
    public float X = x, Y = y, Vx = vx, Vy = vy;
}
public record struct Vigour(int Value, int Max) : EasyEcs.IComponent;
public record struct Mana(int Value, int Max) : EasyEcs.IComponent;
public record struct Recharge(int Ticks) : EasyEcs.IComponent;

public class MoveBodies : EcsSystem
{
    [ForEach] private static void Move(ref Body b) => SystemRules.Move(ref b.X, ref b.Y, ref b.Vx, ref b.Vy);
}

public class RegenerateVigour : EcsSystem
{
    [ForEach] private static void Regenerate(ref Vigour v) => v.Value = SystemRules.Regenerate(v.Value, v.Max);
}

public class RegenerateMana : EcsSystem
{
    [ForEach] private static void Regenerate(ref Mana m) => m.Value = SystemRules.Regenerate(m.Value, m.Max);
}

public class TickRecharge : EcsSystem
{
    [ForEach] private static void Tick(ref Recharge r) => r.Ticks = SystemRules.Recharge(r.Ticks);
}

public struct DBody(float x, float y, float vx, float vy)
{
    public float X = x, Y = y, Vx = vx, Vy = vy;
}
public record struct DVigour(int Value, int Max);
public record struct DMana(int Value, int Max);
public record struct DRecharge(int Ticks);

/// <summary>The rules every library's systems share.</summary>
public static class SystemRules
{
    public static void Move(ref float x, ref float y, ref float vx, ref float vy)
    {
        x += vx;
        y += vy;
        if (x is < 0 or > 10_000) vx = -vx;
        if (y is < 0 or > 10_000) vy = -vy;
    }

    public static int Regenerate(int value, int max) => value < max ? value + 1 : 0;
    public static int Recharge(int ticks) => ticks > 0 ? ticks - 1 : 30;
}

/// <summary>
/// One tick of four independent systems over 100,000 entities (movement, health and mana regeneration, recharges),
/// sequential and in parallel.
/// <list type="bullet">
/// <item>EasyEcs: <c>[ForEach]</c> systems; <see cref="WorldOptions.ParallelSystems"/> runs the four in one stage
/// (their read/write sets are disjoint).</item>
/// <item>DefaultEcs: an <c>AComponentSystem</c> each, in a <c>SequentialSystem</c> or a <c>ParallelSystem</c> with a
/// <c>DefaultParallelRunner</c> (one thread per core).</item>
/// </list>
/// </summary>
[MemoryDiagnoser]
public class ParallelSystemsComparison
{
    private const int Entities = 100_000;

    [Params(false, true)] public bool Parallel;

    private EasyEcs.World _world;
    private D.World _default;
    private D.Threading.DefaultParallelRunner _runner;
    private D.System.ISystem<int> _systems;

    [GlobalSetup(Target = nameof(EasyEcs))]
    public void SetupEasyEcs()
    {
        _world = new EasyEcs.World(new WorldOptions { ParallelSystems = Parallel, InitialEntityCapacity = Entities });
        _world.Reserve<Body, Vigour, Mana, Recharge>(Entities);
        for (int i = 0; i < Entities; i++)
            _world.CreateEntity(new Body(i, i, 1, 1), new Vigour(i % 100, 100), new Mana(i % 50, 50), new Recharge(i % 30));
        _world.AddSystem<MoveBodies>();
        _world.AddSystem<RegenerateVigour>();
        _world.AddSystem<RegenerateMana>();
        _world.AddSystem<TickRecharge>();
        Check(EasyEcs, () =>
        {
            long sum = 0;
            foreach (var row in _world.Query<Body, Vigour, Mana, Recharge>())
                sum += (long)row.Component1.X + row.Component2.Value + row.Component3.Value + row.Component4.Ticks;
            return sum;
        });
    }

    [GlobalSetup(Target = nameof(DefaultEcs))]
    public void SetupDefaultEcs()
    {
        _default = new D.World(Entities);
        for (int i = 0; i < Entities; i++)
        {
            var e = _default.CreateEntity();
            e.Set(new DBody(i, i, 1, 1));
            e.Set(new DVigour(i % 100, 100));
            e.Set(new DMana(i % 50, 50));
            e.Set(new DRecharge(i % 30));
        }

        D.System.ISystem<int>[] systems =
            [new DMoveBodies(_default), new DRegenerateVigour(_default), new DRegenerateMana(_default), new DTickRecharge(_default)];
        _runner = new D.Threading.DefaultParallelRunner(Environment.ProcessorCount);
        _systems = Parallel ? new D.System.ParallelSystem<int>(_runner, systems) : new D.System.SequentialSystem<int>(systems);
        Check(DefaultEcs, () =>
        {
            long sum = 0;
            foreach (var e in _default.GetEntities().AsSet().GetEntities())
                sum += (long)e.Get<DBody>().X + e.Get<DVigour>().Value + e.Get<DMana>().Value + e.Get<DRecharge>().Ticks;
            return sum;
        });
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _world?.Dispose();
        _systems?.Dispose();
        _runner?.Dispose();
        _default?.Dispose();
        (_world, _systems, _runner, _default) = (null, null, null, null);
    }

    [Benchmark(Baseline = true)]
    public void EasyEcs() => _world.Update();

    [Benchmark]
    public void DefaultEcs() => _systems.Update(0);

    /// <summary>The same ticks give the same sum in every library (the rules are the same; checked against a fixed sum).</summary>
    private static void Check(Action tick, Func<long> sum)
    {
        for (int i = 0; i < 100; i++) tick();
        if (sum() != Expected) throw new InvalidOperationException($"the systems went wrong: {sum()}");
    }

    /// <summary>The sum after 100 ticks, computed by the rules alone.</summary>
    private static readonly long Expected = Reference();

    private static long Reference()
    {
        long sum = 0;
        for (int i = 0; i < Entities; i++)
        {
            float x = i, y = i, vx = 1, vy = 1;
            int health = i % 100, mana = i % 50, ticks = i % 30;
            for (int t = 0; t < 100; t++)
            {
                SystemRules.Move(ref x, ref y, ref vx, ref vy);
                health = SystemRules.Regenerate(health, 100);
                mana = SystemRules.Regenerate(mana, 50);
                ticks = SystemRules.Recharge(ticks);
            }

            sum += (long)x + health + mana + ticks;
        }

        return sum;
    }

    private sealed class DMoveBodies(D.World world) : D.System.AComponentSystem<int, DBody>(world)
    {
        protected override void Update(int state, ref DBody b) => SystemRules.Move(ref b.X, ref b.Y, ref b.Vx, ref b.Vy);
    }

    private sealed class DRegenerateVigour(D.World world) : D.System.AComponentSystem<int, DVigour>(world)
    {
        protected override void Update(int state, ref DVigour l) => l = l with { Value = SystemRules.Regenerate(l.Value, l.Max) };
    }

    private sealed class DRegenerateMana(D.World world) : D.System.AComponentSystem<int, DMana>(world)
    {
        protected override void Update(int state, ref DMana m) => m = m with { Value = SystemRules.Regenerate(m.Value, m.Max) };
    }

    private sealed class DTickRecharge(D.World world) : D.System.AComponentSystem<int, DRecharge>(world)
    {
        protected override void Update(int state, ref DRecharge r) => r = new DRecharge(SystemRules.Recharge(r.Ticks));
    }
}
