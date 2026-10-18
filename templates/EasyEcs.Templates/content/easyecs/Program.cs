using System.Diagnostics;
using EasyEcs;
using EasyEcsApp;

// The host: it builds the world, then updates it on a fixed tick until Ctrl+C
// (or `--ticks N`). The game itself is in Components/ and Systems/.
const int TicksPerSecond = 20;

long ticksToRun = long.MaxValue;
if (args.Length == 2 && args[0] == "--ticks")
{
    ticksToRun = long.Parse(args[1]);
}

// Ctrl+C stops the loop cleanly instead of killing the process.
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

using var world = new World();

// Singletons first: the systems read them.
world.SetClock(Tick: 0, DeltaTime: 1f / TicksPerSecond);
world.SetSpawner(
    Bullet: new Prefab().SetPosition(0, 0).SetVelocity(10, 0).SetLifetime(3 * TicksPerSecond),
    Wave: 100,
    Interval: TicksPerSecond);

// Systems run in the order they are added, once per world.Update().
world.AddSystem<SpawnSystem>();
world.AddSystem<MovementSystem>();
world.AddSystem<LifetimeSystem>();
world.AddSystem<ClockSystem>();

var tickLength = TimeSpan.FromSeconds(1.0 / TicksPerSecond);
var clock = Stopwatch.StartNew();
var nextTick = TimeSpan.Zero;
while (world.Clock.Tick < ticksToRun && !stop.IsCancellationRequested)
{
    world.Update();

    long tick = world.Clock.Tick;
    if (tick % TicksPerSecond == 0)
    {
        Console.WriteLine($"tick {tick}: {world.EntityCount} entities");
    }

    // Sleep until the next tick is due.
    nextTick += tickLength;
    TimeSpan wait = nextTick - clock.Elapsed;
    if (wait > TimeSpan.Zero)
    {
        stop.Token.WaitHandle.WaitOne(wait);
    }
}

Console.WriteLine($"stopped after {world.Clock.Tick} ticks");
