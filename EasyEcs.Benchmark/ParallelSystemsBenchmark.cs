using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// One tick of four independent per-entity systems of the same priority (movement, health and mana regeneration,
/// cooldowns). Their access is disjoint, so with <see cref="WorldOptions.Parallel"/> they form one batch and run at
/// the same time; sequential is the reference. Steady state must allocate 0 bytes per tick either way.
/// </summary>
[MemoryDiagnoser]
public partial class ParallelSystemsBenchmark
{
    [Params(100_000)] public int Entities;
    [Params(false, true)] public bool Parallel;

    private World _world;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { Parallel = Parallel, InitialEntityCapacity = Entities });
        for (int i = 0; i < Entities; i++)
            _world.CreateEntity(new Body(i, i, 1, 1), new Vitals(i % 100, 100), new Mana(i % 50, 50), new Cooldown(i % 30));
        _world.AddSystem<MoveBodies>();
        _world.AddSystem<RegenerateHealth>();
        _world.AddSystem<RegenerateMana>();
        _world.AddSystem<TickCooldowns>();
        for (int i = 0; i < 100; i++) _world.Update();
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark]
    public void Tick() => _world.Update();

    public record struct Body(float X, float Y, float Vx, float Vy) : IComponent;
    public record struct Vitals(int Health, int Max) : IComponent;
    public record struct Mana(int Value, int Max) : IComponent;
    public record struct Cooldown(int Ticks) : IComponent;

    public partial class MoveBodies : EcsSystem
    {
        [ForEach]
        static void Move(ref Body b)
        {
            b.X += b.Vx;
            b.Y += b.Vy;
            if (b.X is < 0 or > 10_000) b.Vx = -b.Vx;
            if (b.Y is < 0 or > 10_000) b.Vy = -b.Vy;
        }
    }

    public partial class RegenerateHealth : EcsSystem
    {
        [ForEach]
        static void Regenerate(ref Vitals v) => v.Health = v.Health < v.Max ? v.Health + 1 : 0;
    }

    public partial class RegenerateMana : EcsSystem
    {
        [ForEach]
        static void Regenerate(ref Mana m) => m.Value = m.Value < m.Max ? m.Value + 1 : 0;
    }

    public partial class TickCooldowns : EcsSystem
    {
        [ForEach]
        static void Tick(ref Cooldown c) => c.Ticks = c.Ticks > 0 ? c.Ticks - 1 : 30;
    }
}
