using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// What a system costs per update apart from its rows: 16 systems whose [ForEach] method visits one entity, and the
/// same 16 with an [OnUpdate] method that does nothing. The update calls each system's generated code and runs one
/// pass per system, so the numbers divided by 16 are the cost of a system on an update. <see cref="ParallelStage"/>: 16
/// systems of disjoint components in a world with <see cref="WorldOptions.ParallelSystems"/>, one stage handed to the
/// runner, whose work is too small for helpers: what a stage costs when it settles on running alone. Allocates 0 bytes.
/// </summary>
[MemoryDiagnoser]
public class SystemDispatchBenchmark
{
    private World _plain, _withOnUpdate, _parallel;

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
        _parallel = new World(new WorldOptions { ParallelSystems = true });
        _parallel.AddSystem<P0>(); _parallel.AddSystem<P1>(); _parallel.AddSystem<P2>(); _parallel.AddSystem<P3>();
        _parallel.AddSystem<P4>(); _parallel.AddSystem<P5>(); _parallel.AddSystem<P6>(); _parallel.AddSystem<P7>();
        _parallel.AddSystem<P8>(); _parallel.AddSystem<P9>(); _parallel.AddSystem<P10>(); _parallel.AddSystem<P11>();
        _parallel.AddSystem<P12>(); _parallel.AddSystem<P13>(); _parallel.AddSystem<P14>(); _parallel.AddSystem<P15>();
        _parallel.CreateEntity(new C0(), new C1(), new C2(), new C3(), new C4(), new C5(), new C6(), new C7());
        _parallel.CreateEntity(new C8(), new C9(), new C10(), new C11(), new C12(), new C13(), new C14(), new C15());
        for (int i = 0; i < 2_000; i++) _parallel.Update(); // the runner settles on running the stage alone
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _plain.Dispose();
        _withOnUpdate.Dispose();
        _parallel.Dispose();
    }

    [Benchmark(Baseline = true)]
    public void Plain() => _plain.Update();

    [Benchmark]
    public void WithOnUpdate() => _withOnUpdate.Update();

    [Benchmark]
    public void ParallelStage() => _parallel.Update();

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

    // One component each: no two conflict, so the 16 systems form one parallel stage.
    public record struct C0(int Value) : IComponent;
    public record struct C1(int Value) : IComponent;
    public record struct C2(int Value) : IComponent;
    public record struct C3(int Value) : IComponent;
    public record struct C4(int Value) : IComponent;
    public record struct C5(int Value) : IComponent;
    public record struct C6(int Value) : IComponent;
    public record struct C7(int Value) : IComponent;
    public record struct C8(int Value) : IComponent;
    public record struct C9(int Value) : IComponent;
    public record struct C10(int Value) : IComponent;
    public record struct C11(int Value) : IComponent;
    public record struct C12(int Value) : IComponent;
    public record struct C13(int Value) : IComponent;
    public record struct C14(int Value) : IComponent;
    public record struct C15(int Value) : IComponent;

    [EcsSystem] public ref struct P0 { [ForEach] static void Run(ref C0 c) => c.Value++; }
    [EcsSystem] public ref struct P1 { [ForEach] static void Run(ref C1 c) => c.Value++; }
    [EcsSystem] public ref struct P2 { [ForEach] static void Run(ref C2 c) => c.Value++; }
    [EcsSystem] public ref struct P3 { [ForEach] static void Run(ref C3 c) => c.Value++; }
    [EcsSystem] public ref struct P4 { [ForEach] static void Run(ref C4 c) => c.Value++; }
    [EcsSystem] public ref struct P5 { [ForEach] static void Run(ref C5 c) => c.Value++; }
    [EcsSystem] public ref struct P6 { [ForEach] static void Run(ref C6 c) => c.Value++; }
    [EcsSystem] public ref struct P7 { [ForEach] static void Run(ref C7 c) => c.Value++; }
    [EcsSystem] public ref struct P8 { [ForEach] static void Run(ref C8 c) => c.Value++; }
    [EcsSystem] public ref struct P9 { [ForEach] static void Run(ref C9 c) => c.Value++; }
    [EcsSystem] public ref struct P10 { [ForEach] static void Run(ref C10 c) => c.Value++; }
    [EcsSystem] public ref struct P11 { [ForEach] static void Run(ref C11 c) => c.Value++; }
    [EcsSystem] public ref struct P12 { [ForEach] static void Run(ref C12 c) => c.Value++; }
    [EcsSystem] public ref struct P13 { [ForEach] static void Run(ref C13 c) => c.Value++; }
    [EcsSystem] public ref struct P14 { [ForEach] static void Run(ref C14 c) => c.Value++; }
    [EcsSystem] public ref struct P15 { [ForEach] static void Run(ref C15 c) => c.Value++; }
}
