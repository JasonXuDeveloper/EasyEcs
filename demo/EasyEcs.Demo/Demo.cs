using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Intrinsics;
using System.Runtime.Versioning;
using EasyEcs;

namespace EasyEcs.Demo;

// The host: owns the world on the page's thread and answers the page. Every call is a few numbers;
// the sparks reach the page through the frame array, which lives on the pinned object heap so its
// address never changes.
[SupportedOSPlatform("browser")]
public static partial class Demo
{
    private static World? _world;
    private static float[] _frame = [];
    private static double _updateMs;

    // The last motion time of each group: [0] per entity, [1] batched.
    private static readonly double[] _motionMs = new double[2];

    // A new world of `count` sparks in an arena of `width` x `height` pixels.
    [JSExport]
    public static void Reset(int count, float width, float height)
    {
        _world?.Dispose();
        _world = new World(new WorldOptions { InitialEntityCapacity = count + Units.Emitters });
        _world.SetArena(width, height);
        _world.SetClock(0, 0);
        _world.SetPopulation(count);
        _world.SetPointer(0, 0, false);

        // Motion, in either group: Update<Batched>() or Update<PerEntity>() runs it
        _world.AddSystem<DragBatchSystem>();
        _world.AddSystem<MovementBatchSystem>();
        _world.AddSystem<DragSystem>();
        _world.AddSystem<MovementSystem>();

        // The default group, run by Update() after the motion, in this order
        _world.AddSystem<LifetimeSystem>();
        _world.AddSystem<OrbitSystem>();
        _world.AddSystem<BurstSystem>();
        _world.AddSystem<AttractSystem>();
        _world.AddSystem<BounceSystem>();
        _world.AddSystem<FadeSystem>();

        // The emitters, evenly spread along their curve; the first update moves them onto it
        for (int i = 0; i < Units.Emitters; i++)
        {
            float phase = i * MathF.Tau / Units.Emitters;
            _world.CreateEntity(new Position(), new Emitter(phase, i * 0.1f));
        }

        // The population never exceeds its target: the array holds every spark of any frame
        if (_frame.Length < count * Frame.Stride)
        {
            _frame = GC.AllocateUninitializedArray<float>(count * Frame.Stride, pinned: true);
        }
    }

    // The canvas changed size: sparks outside the new walls bounce back in on the next update.
    [JSExport]
    public static void Resize(float width, float height) => World.SetArena(width, height);

    // The pointer moved over the canvas (active) or left it.
    [JSExport]
    public static void Point(float x, float y, bool active) => World.SetPointer(x, y, active);

    // Milliseconds the last update of the batch (true) or per-entity (false) motion group took.
    [JSExport]
    public static double MotionMs(bool batched) => _motionMs[batched ? 1 : 0];

    // Which vector widths run as hardware SIMD here, as bits: 1 = Vector128, 2 = Vector256,
    // 4 = Vector512, 8 = Vector<T>.
    [JSExport]
    public static int Simd() =>
        (Vector128.IsHardwareAccelerated ? 1 : 0) | (Vector256.IsHardwareAccelerated ? 2 : 0)
        | (Vector512.IsHardwareAccelerated ? 4 : 0) | (Vector.IsHardwareAccelerated ? 8 : 0);

    // Advances the world by `dt` seconds, then writes the frame. Returns the number of sparks in
    // it.
    [JSExport]
    public static int Step(float dt)
    {
        var world = World;
        ref var clock = ref world.Clock;
        clock.Dt = dt;
        clock.Tick++;

        // The two motion groups take turns, one per step. They do the same work, so the picture
        // does not change, and each is timed on the same sparks
        bool batched = (clock.Tick & 1) == 0;
        long start = Stopwatch.GetTimestamp();
        if (batched)
        {
            world.Update<Batched>();
        }
        else
        {
            world.Update<PerEntity>();
        }

        long moved = Stopwatch.GetTimestamp();
        world.Update();
        long end = Stopwatch.GetTimestamp();

        _motionMs[batched ? 1 : 0] = Stopwatch.GetElapsedTime(start, moved).TotalMilliseconds;
        _updateMs = Stopwatch.GetElapsedTime(start, end).TotalMilliseconds;

        int count = 0;
        Frame.Write(world, _frame, ref count);
        return count;
    }

    // Where the frame array starts in the WebAssembly memory, for a Float32Array over it.
    [JSExport]
    public static unsafe int FrameAddress() =>
        (int)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_frame));

    // Milliseconds the last update took (the motion group and the default group).
    [JSExport]
    public static double UpdateMs() => _updateMs;

    // Live entities (the sparks and the emitters), after the last update.
    [JSExport]
    public static int EntityCount() => World.EntityCount;

    private static World World =>
        _world ?? throw new InvalidOperationException("Call Reset first.");
}
