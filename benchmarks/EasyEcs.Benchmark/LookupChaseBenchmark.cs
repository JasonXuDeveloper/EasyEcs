using System;
using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// Random lookups across a large world: every hunter chases another hunter picked at random, so each read lands on a
/// random entity record and a random row (a TLB miss per lookup on small pages once the arrays outgrow the TLB's reach).
/// <see cref="Lookup{T}"/> on one thread and in parallel, and <c>world.TryGetEntity</c> from a <see cref="World"/>
/// parameter. 1,000,000 hunters (16 MiB of records, 8 MiB per column: past the TLB's reach and the last-level cache)
/// against 100,000 (within them). Measures what changes the cost of a random lookup: page sizes (decision D123),
/// software prefetch of the target's record.
/// </summary>
[MemoryDiagnoser]
public partial class LookupChaseBenchmark
{
    [Params(100_000, 1_000_000)] public int Entities;

    private World _world;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Entities });
        var hunters = new Entity[Entities];
        _world.CreateEntities(hunters, new Position(1, 2), new Prey(), new Heading());
        var random = new Random(42);
        for (int i = 0; i < hunters.Length; i++) hunters[i].Prey = new Prey(hunters[random.Next(hunters.Length)].Id);

        for (int i = 0; i < 3; i++) ChaseParallel();
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark(Baseline = true)]
    public void ChaseWorld() => Hunting.ByWorld(_world);

    [Benchmark]
    public void ChaseSerial() => Hunting.ByLookup(_world);

    [Benchmark]
    public void ChaseParallel() => Hunting.ByLookupParallel(_world);

    public record struct Position(float X, float Y) : IComponent;
    public record struct Prey(EntityId Target) : IComponent;
    public record struct Heading(float X, float Y) : IComponent;

    public static class Hunting
    {
        private static Heading Toward(in Position from, in Position to) => new(to.X - from.X, to.Y - from.Y);

        [ForEach]
        public static void ByWorld(World world, in Position position, in Prey prey, ref Heading heading) =>
            heading = world.TryGetEntity(prey.Target, out var target) && target.TryGet<Position>(out var at) ? Toward(in position, in at) : default;

        [ForEach]
        public static void ByLookup(in Position position, in Prey prey, ref Heading heading, Lookup<Position> positions)
        {
            var at = positions[prey.Target];
            heading = at.HasValue ? Toward(in position, in at.Value) : default;
        }

        [ForEach(Parallel = true)]
        public static void ByLookupParallel(in Position position, in Prey prey, ref Heading heading, Lookup<Position> positions)
        {
            var at = positions[prey.Target];
            heading = at.HasValue ? Toward(in position, in at.Value) : default;
        }
    }
}
