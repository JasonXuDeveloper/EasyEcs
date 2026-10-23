using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// What a system costs per update apart from its rows: 16 systems whose [ForEach] method visits one entity, and the
/// same 16 with an [OnUpdate] method that does nothing. The update calls each system's generated code and runs one
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

    [EcsSystem] public ref struct S0 { [ForEach] static void Run(ref Counter c) => c.Value++; }
    [EcsSystem] public ref struct S1 { [ForEach] static void Run(ref Counter c) => c.Value++; }
    [EcsSystem] public ref struct S2 { [ForEach] static void Run(ref Counter c) => c.Value++; }
    [EcsSystem] public ref struct S3 { [ForEach] static void Run(ref Counter c) => c.Value++; }
    [EcsSystem] public ref struct S4 { [ForEach] static void Run(ref Counter c) => c.Value++; }
    [EcsSystem] public ref struct S5 { [ForEach] static void Run(ref Counter c) => c.Value++; }
    [EcsSystem] public ref struct S6 { [ForEach] static void Run(ref Counter c) => c.Value++; }
    [EcsSystem] public ref struct S7 { [ForEach] static void Run(ref Counter c) => c.Value++; }
    [EcsSystem] public ref struct S8 { [ForEach] static void Run(ref Counter c) => c.Value++; }
    [EcsSystem] public ref struct S9 { [ForEach] static void Run(ref Counter c) => c.Value++; }
    [EcsSystem] public ref struct S10 { [ForEach] static void Run(ref Counter c) => c.Value++; }
    [EcsSystem] public ref struct S11 { [ForEach] static void Run(ref Counter c) => c.Value++; }
    [EcsSystem] public ref struct S12 { [ForEach] static void Run(ref Counter c) => c.Value++; }
    [EcsSystem] public ref struct S13 { [ForEach] static void Run(ref Counter c) => c.Value++; }
    [EcsSystem] public ref struct S14 { [ForEach] static void Run(ref Counter c) => c.Value++; }
    [EcsSystem] public ref struct S15 { [ForEach] static void Run(ref Counter c) => c.Value++; }

    // An [OnUpdate] method that does nothing: the system runs alone, and the runtime calls it before the pass.
    [EcsSystem] public ref struct U0 { [OnUpdate] static void Tick() { } [ForEach] static void Run(ref Counter c) => c.Value++; }
    [EcsSystem] public ref struct U1 { [OnUpdate] static void Tick() { } [ForEach] static void Run(ref Counter c) => c.Value++; }
    [EcsSystem] public ref struct U2 { [OnUpdate] static void Tick() { } [ForEach] static void Run(ref Counter c) => c.Value++; }
    [EcsSystem] public ref struct U3 { [OnUpdate] static void Tick() { } [ForEach] static void Run(ref Counter c) => c.Value++; }
    [EcsSystem] public ref struct U4 { [OnUpdate] static void Tick() { } [ForEach] static void Run(ref Counter c) => c.Value++; }
    [EcsSystem] public ref struct U5 { [OnUpdate] static void Tick() { } [ForEach] static void Run(ref Counter c) => c.Value++; }
    [EcsSystem] public ref struct U6 { [OnUpdate] static void Tick() { } [ForEach] static void Run(ref Counter c) => c.Value++; }
    [EcsSystem] public ref struct U7 { [OnUpdate] static void Tick() { } [ForEach] static void Run(ref Counter c) => c.Value++; }
    [EcsSystem] public ref struct U8 { [OnUpdate] static void Tick() { } [ForEach] static void Run(ref Counter c) => c.Value++; }
    [EcsSystem] public ref struct U9 { [OnUpdate] static void Tick() { } [ForEach] static void Run(ref Counter c) => c.Value++; }
    [EcsSystem] public ref struct U10 { [OnUpdate] static void Tick() { } [ForEach] static void Run(ref Counter c) => c.Value++; }
    [EcsSystem] public ref struct U11 { [OnUpdate] static void Tick() { } [ForEach] static void Run(ref Counter c) => c.Value++; }
    [EcsSystem] public ref struct U12 { [OnUpdate] static void Tick() { } [ForEach] static void Run(ref Counter c) => c.Value++; }
    [EcsSystem] public ref struct U13 { [OnUpdate] static void Tick() { } [ForEach] static void Run(ref Counter c) => c.Value++; }
    [EcsSystem] public ref struct U14 { [OnUpdate] static void Tick() { } [ForEach] static void Run(ref Counter c) => c.Value++; }
    [EcsSystem] public ref struct U15 { [OnUpdate] static void Tick() { } [ForEach] static void Run(ref Counter c) => c.Value++; }
}
