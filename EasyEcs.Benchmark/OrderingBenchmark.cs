using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// What ordering costs per update: four systems over 10,000 entities, registered in run order, against the same
/// four registered backwards with <c>[UpdateAfter&lt;T&gt;]</c> putting them in that order. The order is resolved
/// when systems are added, so the updates should cost the same. Allocates 0 bytes.
/// </summary>
[MemoryDiagnoser]
public class OrderingBenchmark
{
    [Params(10_000)] public int Entities;

    private World _registered, _ordered;

    [GlobalSetup]
    public void Setup()
    {
        _registered = new World(new WorldOptions { InitialEntityCapacity = Entities });
        _registered.AddSystem<Gather>();
        _registered.AddSystem<Craft>();
        _registered.AddSystem<Trade>();
        _registered.AddSystem<Spend>();
        _ordered = new World(new WorldOptions { InitialEntityCapacity = Entities });
        _ordered.AddSystem<SpendAfter>();                                       // backwards: the attributes reorder
        _ordered.AddSystem<TradeAfter>();
        _ordered.AddSystem<CraftAfter>();
        _ordered.AddSystem<GatherFirst>();
        for (int i = 0; i < Entities; i++)
        {
            _registered.CreateEntity(new Stock(0, 0));
            _ordered.CreateEntity(new Stock(0, 0));
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _registered.Dispose();
        _ordered.Dispose();
    }

    [Benchmark(Baseline = true)]
    public void Registered() => _registered.Update();

    [Benchmark]
    public void Ordered() => _ordered.Update();

    public record struct Stock(int Ore, int Gold) : IComponent;

    public sealed class Gather : EcsSystem { [ForEach] static void Run(ref Stock s) => s.Ore += 3; }
    public sealed class Craft : EcsSystem { [ForEach] static void Run(ref Stock s) => s.Ore -= s.Ore > 2 ? 2 : 0; }
    public sealed class Trade : EcsSystem { [ForEach] static void Run(ref Stock s) => s.Gold += s.Ore & 1; }
    public sealed class Spend : EcsSystem { [ForEach] static void Run(ref Stock s) => s.Gold -= s.Gold > 10 ? 10 : 0; }

    public sealed class GatherFirst : EcsSystem { [ForEach] static void Run(ref Stock s) => s.Ore += 3; }
    [UpdateAfter<GatherFirst>] public sealed class CraftAfter : EcsSystem { [ForEach] static void Run(ref Stock s) => s.Ore -= s.Ore > 2 ? 2 : 0; }
    [UpdateAfter<CraftAfter>] public sealed class TradeAfter : EcsSystem { [ForEach] static void Run(ref Stock s) => s.Gold += s.Ore & 1; }
    [UpdateAfter<TradeAfter>] public sealed class SpendAfter : EcsSystem { [ForEach] static void Run(ref Stock s) => s.Gold -= s.Gold > 10 ? 10 : 0; }
}
