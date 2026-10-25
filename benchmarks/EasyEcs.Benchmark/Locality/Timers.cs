using System;

namespace EasyEcs.Benchmark.Locality;

/// <summary>
/// Item 2 of docs/plans/borrowed-ideas.md: entities that wait (cooldowns, respawns, buffs that end) and do some work when
/// their wait is over. Each entity has a period of 50 to 150 ticks (1 % of them wake per tick on average) and wakes at
/// <see cref="Cooldown.Until"/>, then waits one period more. Four ways per tick: a [ForEach] over every entity comparing
/// its wake tick with the clock (the generator's vector loop, and the method row by row); a [ForEach] counting each
/// countdown down; and a hashed timer wheel (Varghese and Lauck 1987) of 256 buckets of ids by wake tick, of which
/// only this tick's is visited, the work run on the woken through their ids, or a tag set on them for a [ForEach]
/// over the tag (a presence flip: the tag's column exists, the rows never move). Periods are below the wheel's 256
/// slots, so no entry waits for a later round of the wheel. Every version does the same work on the same ticks.
/// </summary>
public static partial class Timers
{
    public const int Slots = 256;           // a power of two above the longest period

    public record struct Cooldown(int Until) : IComponent;
    public record struct Countdown(int Left) : IComponent;
    public record struct Period(int Ticks) : IComponent;
    public record struct Work(int Count) : IComponent;
    public record struct Awake : IComponent;

    public record struct Clock(int Tick) : ISingletonComponent;

    /// <summary>The wheel: the ids waking at tick t are in bucket t % 256.</summary>
    [NotSaved]
    public struct Wheel : ISingletonComponent
    {
        public EntityId[][] Buckets;
        public int[] Counts;

        public void Add(int tick, EntityId id)
        {
            int slot = tick & (Slots - 1);
            ref var bucket = ref Buckets[slot];
            if (Counts[slot] == bucket.Length) Array.Resize(ref bucket, bucket.Length * 2);   // past the peak only
            bucket[Counts[slot]++] = id;
        }
    }

    /// <summary><paramref name="entities"/> waiting entities; each wakes first in 1 .. its period ticks.</summary>
    public static World Create(int entities, int seed = 42)
    {
        var world = new World(new WorldOptions { InitialEntityCapacity = entities + 16 });
        world.AddSingleton<Clock>();
        ref var wheel = ref world.AddSingleton<Wheel>();
        wheel.Buckets = new EntityId[Slots][];
        wheel.Counts = new int[Slots];
        for (int s = 0; s < Slots; s++) wheel.Buckets[s] = new EntityId[Math.Max(16, entities / 100 * 4)];
        var all = new Entity[entities];
        world.CreateEntities(all, new Cooldown(), new Countdown(), new Period(), new Work(), new Awake());
        world.RemoveAll<Awake>();                    // the tag's column stays: setting it later flips a presence bit
        var random = new Random(seed);
        foreach (var entity in all)
        {
            int period = random.Next(50, 151), first = random.Next(1, period + 1);
            entity.Get<Period>() = new Period(period);
            entity.Get<Cooldown>() = new Cooldown(first);
            entity.Get<Countdown>() = new Countdown(first);
            wheel.Add(first, entity.Id);
        }

        return world;
    }

    // #region scan
    /// <summary>A tick of the scan: every entity compares its wake tick with the clock.</summary>
    public static void ScanTick(World world)
    {
        world.GetSingleton<Clock>().Tick++;
        Timers.Wake(world);
    }

    /// <summary>The scan, row by row.</summary>
    public static void ScanScalarTick(World world)
    {
        world.GetSingleton<Clock>().Tick++;
        Timers.WakeScalar(world);
    }

    /// <summary>A tick of the countdowns: every entity counts down and wakes at 0.</summary>
    public static void CountdownTick(World world)
    {
        world.GetSingleton<Clock>().Tick++;
        Timers.CountDown(world);
    }

    [ForEach]
    public static void Wake(ref Cooldown cooldown, in Period period, ref Work work, in Clock clock)
    {
        if (clock.Tick >= cooldown.Until)
        {
            work = new Work(work.Count + 1);
            cooldown = new Cooldown(cooldown.Until + period.Ticks);
        }
    }

    [ForEach(SimdMode = SimdMode.Off)]
    public static void WakeScalar(ref Cooldown cooldown, in Period period, ref Work work, in Clock clock)
    {
        if (clock.Tick >= cooldown.Until)
        {
            work = new Work(work.Count + 1);
            cooldown = new Cooldown(cooldown.Until + period.Ticks);
        }
    }

    [ForEach]
    public static void CountDown(ref Countdown countdown, in Period period, ref Work work)
    {
        int left = countdown.Left - 1;
        if (left == 0)
        {
            work = new Work(work.Count + 1);
            left = period.Ticks;
        }

        countdown = new Countdown(left);
    }
    // #endregion scan

    // #region wheel
    /// <summary>A tick of the wheel: this tick's bucket, the work run on each woken entity through its id, which then
    /// goes into the bucket of its next wake.</summary>
    public static void WheelTick(World world)
    {
        int tick = ++world.GetSingleton<Clock>().Tick;
        ref var wheel = ref world.GetSingleton<Wheel>();
        int slot = tick & (Slots - 1), count = wheel.Counts[slot];
        var bucket = wheel.Buckets[slot];
        wheel.Counts[slot] = 0;
        for (int i = 0; i < count; i++)
        {
            var entity = world.GetEntity(bucket[i]);
            entity.Get<Work>().Count++;
            ref var cooldown = ref entity.Get<Cooldown>();
            cooldown.Until += entity.Read<Period>().Ticks;
            wheel.Add(cooldown.Until, entity.Id);
        }
    }

    /// <summary>A tick of the wheel that tags the woken: the tag set through each id (a presence flip), a [ForEach] over
    /// the tag doing the work, and the tag cleared in bulk.</summary>
    public static void WheelTagTick(World world)
    {
        int tick = ++world.GetSingleton<Clock>().Tick;
        ref var wheel = ref world.GetSingleton<Wheel>();
        int slot = tick & (Slots - 1), count = wheel.Counts[slot];
        var bucket = wheel.Buckets[slot];
        wheel.Counts[slot] = 0;
        for (int i = 0; i < count; i++) world.GetEntity(bucket[i]).Set(new Awake());
        Timers.Woken(world);
        world.RemoveAll<Awake>();
    }

    [ForEach(SimdMode = SimdMode.Off), With<Awake>]
    public static void Woken(Entity entity, ref Cooldown cooldown, in Period period, ref Work work, ref Wheel wheel)
    {
        work = new Work(work.Count + 1);
        cooldown = new Cooldown(cooldown.Until + period.Ticks);
        wheel.Add(cooldown.Until, entity.Id);
    }
    // #endregion wheel

    /// <summary>Each entity's work count, in table order.</summary>
    public static int[] Counts(World world)
    {
        var all = new int[world.Count<Work>()];
        int k = 0;
        foreach (var item in world.Query<Work>()) all[k++] = item.Component1.Count;
        return all;
    }
}
