using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// What a system costs per update apart from its rows: 16 systems whose [ForEach] method visits one entity, and the
/// same 16 with an OnUpdate override that does nothing. The update calls each system's generated code and runs one
/// pass per system, so the numbers divided by 16 are the cost of a system on an update. Allocates 0 bytes.
/// </summary>
[MemoryDiagnoser]
public class SystemDispatchBenchmark
{
    private World _plain, _withOnUpdate;

    [GlobalSetup]
    public void Setup()
    {
        _plain = new World();
        _plain.AddSystem<S0>(); _plain.AddSystem<S1>(); _plain.AddSystem<S2>(); _plain.AddSystem<S3>();
        _plain.AddSystem<S4>(); _plain.AddSystem<S5>(); _plain.AddSystem<S6>(); _plain.AddSystem<S7>();
        _plain.AddSystem<S8>(); _plain.AddSystem<S9>(); _plain.AddSystem<S10>(); _plain.AddSystem<S11>();
        _plain.AddSystem<S12>(); _plain.AddSystem<S13>(); _plain.AddSystem<S14>(); _plain.AddSystem<S15>();
        _plain.CreateEntity(new Counter(0));
        _withOnUpdate = new World();
        _withOnUpdate.AddSystem<U0>(); _withOnUpdate.AddSystem<U1>(); _withOnUpdate.AddSystem<U2>(); _withOnUpdate.AddSystem<U3>();
        _withOnUpdate.AddSystem<U4>(); _withOnUpdate.AddSystem<U5>(); _withOnUpdate.AddSystem<U6>(); _withOnUpdate.AddSystem<U7>();
        _withOnUpdate.AddSystem<U8>(); _withOnUpdate.AddSystem<U9>(); _withOnUpdate.AddSystem<U10>(); _withOnUpdate.AddSystem<U11>();
        _withOnUpdate.AddSystem<U12>(); _withOnUpdate.AddSystem<U13>(); _withOnUpdate.AddSystem<U14>(); _withOnUpdate.AddSystem<U15>();
        _withOnUpdate.CreateEntity(new Counter(0));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _plain.Dispose();
        _withOnUpdate.Dispose();
    }

    [Benchmark(Baseline = true)]
    public void Plain() => _plain.Update();

    [Benchmark]
    public void WithOnUpdate() => _withOnUpdate.Update();

    public record struct Counter(int Value) : IComponent;

    public sealed class S0 : EcsSystem { [ForEach] static void Run(ref Counter c) => c.Value++; }
    public sealed class S1 : EcsSystem { [ForEach] static void Run(ref Counter c) => c.Value++; }
    public sealed class S2 : EcsSystem { [ForEach] static void Run(ref Counter c) => c.Value++; }
    public sealed class S3 : EcsSystem { [ForEach] static void Run(ref Counter c) => c.Value++; }
    public sealed class S4 : EcsSystem { [ForEach] static void Run(ref Counter c) => c.Value++; }
    public sealed class S5 : EcsSystem { [ForEach] static void Run(ref Counter c) => c.Value++; }
    public sealed class S6 : EcsSystem { [ForEach] static void Run(ref Counter c) => c.Value++; }
    public sealed class S7 : EcsSystem { [ForEach] static void Run(ref Counter c) => c.Value++; }
    public sealed class S8 : EcsSystem { [ForEach] static void Run(ref Counter c) => c.Value++; }
    public sealed class S9 : EcsSystem { [ForEach] static void Run(ref Counter c) => c.Value++; }
    public sealed class S10 : EcsSystem { [ForEach] static void Run(ref Counter c) => c.Value++; }
    public sealed class S11 : EcsSystem { [ForEach] static void Run(ref Counter c) => c.Value++; }
    public sealed class S12 : EcsSystem { [ForEach] static void Run(ref Counter c) => c.Value++; }
    public sealed class S13 : EcsSystem { [ForEach] static void Run(ref Counter c) => c.Value++; }
    public sealed class S14 : EcsSystem { [ForEach] static void Run(ref Counter c) => c.Value++; }
    public sealed class S15 : EcsSystem { [ForEach] static void Run(ref Counter c) => c.Value++; }

    // An OnUpdate that does nothing: the system runs alone, and the runtime calls OnUpdate before the pass.
    public abstract class WithHook : EcsSystem { protected override void OnUpdate() { } }
    public sealed class U0 : WithHook { [ForEach] static void Run(ref Counter c) => c.Value++; }
    public sealed class U1 : WithHook { [ForEach] static void Run(ref Counter c) => c.Value++; }
    public sealed class U2 : WithHook { [ForEach] static void Run(ref Counter c) => c.Value++; }
    public sealed class U3 : WithHook { [ForEach] static void Run(ref Counter c) => c.Value++; }
    public sealed class U4 : WithHook { [ForEach] static void Run(ref Counter c) => c.Value++; }
    public sealed class U5 : WithHook { [ForEach] static void Run(ref Counter c) => c.Value++; }
    public sealed class U6 : WithHook { [ForEach] static void Run(ref Counter c) => c.Value++; }
    public sealed class U7 : WithHook { [ForEach] static void Run(ref Counter c) => c.Value++; }
    public sealed class U8 : WithHook { [ForEach] static void Run(ref Counter c) => c.Value++; }
    public sealed class U9 : WithHook { [ForEach] static void Run(ref Counter c) => c.Value++; }
    public sealed class U10 : WithHook { [ForEach] static void Run(ref Counter c) => c.Value++; }
    public sealed class U11 : WithHook { [ForEach] static void Run(ref Counter c) => c.Value++; }
    public sealed class U12 : WithHook { [ForEach] static void Run(ref Counter c) => c.Value++; }
    public sealed class U13 : WithHook { [ForEach] static void Run(ref Counter c) => c.Value++; }
    public sealed class U14 : WithHook { [ForEach] static void Run(ref Counter c) => c.Value++; }
    public sealed class U15 : WithHook { [ForEach] static void Run(ref Counter c) => c.Value++; }
}
