using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// A view against the refs it stands for: 100,000 movers step their position by their velocity. <c>Refs</c> is the
/// [ForEach] method taking <c>ref Position, in Velocity</c>; <c>View</c> takes a <c>[View] Mover</c> over the same two
/// components and calls its <c>Step</c>; both get the vector loop, and their <c>Scalar</c> twins are the same methods
/// with <c>SimdMode.Off</c>. <c>EntityRefs</c> / <c>EntityView</c> step 1,000 single entities through
/// <c>Get</c> / <c>Read</c> against <c>entity.AsMover()</c>. 0 bytes every way.
/// </summary>
[MemoryDiagnoser]
public class ViewBenchmark
{
    [Params(100_000)] public int Movers;

    private World _world;
    private Entity[] _entities;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Movers });
        _world.AddSingleton<Clock>() = new Clock(0.016f);
        _entities = new Entity[Movers];
        _world.CreateEntities(_entities, new Position(), new Velocity(1f, 0.5f));
        Refs(); View(); RefsScalar(); ViewScalar(); EntityRefs(); EntityView();
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark(Baseline = true)]
    public void Refs() => Motion.ByRefs(_world);

    [Benchmark]
    public void View() => Motion.ByView(_world);

    [Benchmark]
    public void RefsScalar() => Motion.ByRefsScalar(_world);

    [Benchmark]
    public void ViewScalar() => Motion.ByViewScalar(_world);

    [Benchmark]
    public float EntityRefs()
    {
        float sum = 0;
        for (int i = 0; i < 1_000; i++)
        {
            var e = _entities[i * 97 % Movers];
            ref var position = ref e.Get<Position>();
            ref readonly var velocity = ref e.Read<Velocity>();
            position.X += velocity.X * 0.016f;
            position.Y += velocity.Y * 0.016f;
            sum += position.X;
        }

        return sum;
    }

    [Benchmark]
    public float EntityView()
    {
        float sum = 0;
        for (int i = 0; i < 1_000; i++)
        {
            var mover = _entities[i * 97 % Movers].AsMover();
            mover.Step(0.016f);
            sum += mover.Position.X;
        }

        return sum;
    }

    public record struct Position(float X, float Y) : IComponent;
    public record struct Velocity(float X, float Y) : IComponent;
    public record struct Clock(float Dt) : ISingletonComponent;

    /// <summary>A mover: its position (written) and velocity (read).</summary>
    [View]
    public ref struct Mover
    {
        public ref Position Position;
        public ref readonly Velocity Velocity;

        public void Step(float dt)
        {
            Position.X += Velocity.X * dt;
            Position.Y += Velocity.Y * dt;
        }
    }
}

public static class Motion
{
    [ForEach]
    static void ByRefs(ref ViewBenchmark.Position position, in ViewBenchmark.Velocity velocity, in ViewBenchmark.Clock clock)
    {
        position.X += velocity.X * clock.Dt;
        position.Y += velocity.Y * clock.Dt;
    }

    [ForEach]
    static void ByView(ViewBenchmark.Mover mover, in ViewBenchmark.Clock clock) => mover.Step(clock.Dt);

    [ForEach(SimdMode = SimdMode.Off)]
    static void ByRefsScalar(ref ViewBenchmark.Position position, in ViewBenchmark.Velocity velocity, in ViewBenchmark.Clock clock)
    {
        position.X += velocity.X * clock.Dt;
        position.Y += velocity.Y * clock.Dt;
    }

    [ForEach(SimdMode = SimdMode.Off)]
    static void ByViewScalar(ViewBenchmark.Mover mover, in ViewBenchmark.Clock clock) => mover.Step(clock.Dt);
}
