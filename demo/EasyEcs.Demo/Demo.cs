using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using EasyEcs;

namespace EasyEcs.Demo;

// The host: owns the world on the page's thread and answers the page. Every call is a few numbers; the sparks reach
// the page through the frame array, which lives on the pinned object heap so its address never changes.
[SupportedOSPlatform("browser")]
public static partial class Demo
{
    private static World? _world;
    private static float[] _frame = [];
    private static int _frameCount;
    private static double _updateMs;

    // A new world of `count` sparks in an arena of `width` x `height` pixels.
    [JSExport]
    public static void Reset(int count, float width, float height)
    {
        _world?.Dispose();
        _world = new World(new WorldOptions { InitialEntityCapacity = count });
        _world.SetArena(width, height);
        _world.SetClock(0, 0);
        _world.SetPopulation(count);
        _world.AddSystem<LifetimeSystem>();
        _world.AddSystem<SpawnSystem>();
        _world.AddSystem<MovementSystem>();
        _world.AddSystem<BounceSystem>();
        _world.AddSystem<FadeSystem>();

        // The population never exceeds its target: the array holds every spark of any frame
        if (_frame.Length < count * Frame.Stride)
        {
            _frame = GC.AllocateUninitializedArray<float>(count * Frame.Stride, pinned: true);
        }

        _frameCount = 0;
    }

    // The canvas changed size: sparks outside the new walls bounce back in on the next update.
    [JSExport]
    public static void Resize(float width, float height) => World.SetArena(width, height);

    // Advances the world by `dt` seconds, then writes the frame. Returns the number of sparks in it.
    [JSExport]
    public static int Step(float dt)
    {
        var world = World;
        ref var clock = ref world.Clock;
        clock.Dt = dt;
        clock.Tick++;

        long start = Stopwatch.GetTimestamp();
        world.Update();
        _updateMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        int count = 0;
        Frame.Write(world, _frame, ref count);
        _frameCount = count;
        return count;
    }

    // Where the frame array starts in the WebAssembly memory, for a Float32Array over it.
    [JSExport]
    public static unsafe int FrameAddress() =>
        (int)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_frame));

    // Floats per spark in the frame.
    [JSExport]
    public static int FrameStride() => Frame.Stride;

    // Milliseconds the last world.Update() took.
    [JSExport]
    public static double UpdateMs() => _updateMs;

    // Live entities, including the ones created and destroyed by the last update.
    [JSExport]
    public static int EntityCount() => World.EntityCount;

    private static World World => _world ?? throw new InvalidOperationException("Call Reset first.");
}
