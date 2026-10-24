using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// The cost of cold fields in a column a frequent pass walks (EECS090): a movement pass reads and writes 8 bytes of
/// each row (X, VX). <c>Mixed</c> keeps those in one component with <see cref="Size"/> - 8 bytes of fields the pass
/// never touches (spawn data, stats); <c>Split</c> moves the cold fields into a component of their own, which every
/// entity still holds. Both loops run one row at a time (SimdMode.Off), so the difference is the memory the pass walks,
/// not a vector loop that only the split form would get. 0 B either way.
/// </summary>
[MemoryDiagnoser]
public class HotColdBenchmark
{
    [Params(100_000)] public int Entities;
    [Params(16, 32, 64, 128)] public int Size;
    [Params(false, true)] public bool Split;

    private World _world;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Entities });
        var hot = new Mover(0, 1);
        var ids = new Entity[Entities];
        switch (Size, Split)
        {
            case (16, false): _world.CreateEntities(ids, new Body16 { VX = 1 }); _world.AddSystem<Move16>(); break;
            case (32, false): _world.CreateEntities(ids, new Body32 { VX = 1 }); _world.AddSystem<Move32>(); break;
            case (64, false): _world.CreateEntities(ids, new Body64 { VX = 1 }); _world.AddSystem<Move64>(); break;
            case (128, false): _world.CreateEntities(ids, new Body128 { VX = 1 }); _world.AddSystem<Move128>(); break;
            case (16, true): _world.CreateEntities(ids, hot, new Cold8()); break;
            case (32, true): _world.CreateEntities(ids, hot, new Cold24()); break;
            case (64, true): _world.CreateEntities(ids, hot, new Cold56()); break;
            case (128, true): _world.CreateEntities(ids, hot, new Cold120()); break;
        }

        if (Split) _world.AddSystem<MoveSplit>();
        for (int i = 0; i < 10; i++) _world.Update();
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark]
    public void Tick() => _world.Update();

    public record struct Mover(float X, float VX) : IComponent;

#pragma warning disable EECS090 // the cold fields are what this benchmark measures
    public struct Body16 : IComponent { public float X, VX; public long C0; }
    public struct Body32 : IComponent { public float X, VX; public long C0, C1, C2; }
    public struct Body64 : IComponent { public float X, VX; public long C0, C1, C2, C3, C4, C5, C6; }
    public struct Body128 : IComponent
    {
        public float X, VX;
        public long C0, C1, C2, C3, C4, C5, C6, C7, C8, C9, C10, C11, C12, C13, C14;
    }
#pragma warning restore EECS090

    public struct Cold8 : IComponent { public long C0; }
    public struct Cold24 : IComponent { public long C0, C1, C2; }
    public struct Cold56 : IComponent { public long C0, C1, C2, C3, C4, C5, C6; }
    public struct Cold120 : IComponent { public long C0, C1, C2, C3, C4, C5, C6, C7, C8, C9, C10, C11, C12, C13, C14; }

    [EcsSystem] public ref struct Move16 { [ForEach(SimdMode = SimdMode.Off)] static void Move(ref Body16 b) => b.X += b.VX * 0.016f; }
    [EcsSystem] public ref struct Move32 { [ForEach(SimdMode = SimdMode.Off)] static void Move(ref Body32 b) => b.X += b.VX * 0.016f; }
    [EcsSystem] public ref struct Move64 { [ForEach(SimdMode = SimdMode.Off)] static void Move(ref Body64 b) => b.X += b.VX * 0.016f; }
    [EcsSystem] public ref struct Move128 { [ForEach(SimdMode = SimdMode.Off)] static void Move(ref Body128 b) => b.X += b.VX * 0.016f; }
    [EcsSystem] public ref struct MoveSplit { [ForEach(SimdMode = SimdMode.Off)] static void Move(ref Mover m) => m.X += m.VX * 0.016f; }
}
