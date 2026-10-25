using System;
using System.Threading;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>
/// <see cref="WorldOptions.ParallelSystems"/>: systems of one priority run together exactly when their declared access
/// (generated from the [ForEach] parameters) does not conflict.
/// </summary>
public class ParallelTests
{
    /// <summary>Heavy per-row work cut into chunks of rows on several threads.</summary>
    [EcsSystem]
    public ref struct ChunkedIncrement
    {
        [ForEach(Parallel = true), Without<Dead>]
        static void Add(ref Counter counter, in Seen step, Entity entity) => counter.N += step.N + (entity.Id.Index & 0);
    }

    /// <summary>Few heavy rows: chunks of 64 rows, so a few hundred rows reach every thread.</summary>
    [EcsSystem]
    public ref struct HeavyRows
    {
        [ForEach(Parallel = true, ChunkRows = 64)]
        static void Work(ref Counter counter)
        {
            counter.N = Environment.CurrentManagedThreadId;
            Thread.SpinWait(20_000); // microseconds per row
        }
    }

    /// <summary>Writes Armor on the rows whose Seen matches the world's step, in chunks that split blocks of change ticks.</summary>
    [EcsSystem]
    public ref struct SomeArmor
    {
        [ForEach(Parallel = true, ChunkRows = 64)]
        static void Write(in Seen seen, Mut<Armor> armor, in Step step)
        {
            if (seen.N % step.Every == step.Phase) armor.Write().Value++;
        }
    }

    // The same through a Maybe: the optional column's blocks are shared by the chunks too.
    [EcsSystem]
    public ref struct MaybeArmor
    {
        [ForEach(Parallel = true, ChunkRows = 64)]
        static void Write(in Seen seen, Maybe<Armor> armor)
        {
            if (armor.HasValue) armor.Value.Value += seen.N & 1;
        }
    }

    /// <summary>Few heavy rows without ChunkRows: once a run has measured their cost, chunks small enough to spread them.</summary>
    [EcsSystem]
    public ref struct HeavyRowsMeasured
    {
        [ForEach(Parallel = true)]
        static void Work(ref Counter counter)
        {
            counter.N = Environment.CurrentManagedThreadId;
            Thread.SpinWait(20_000); // microseconds per row
        }
    }

    /// <summary>Writes tracked Armor on every row, slowly: chunks cut by the measured cost split blocks of change ticks.</summary>
    [EcsSystem]
    public ref struct SlowArmor
    {
        [ForEach(Parallel = true)]
        static void Write(ref Armor armor)
        {
            armor.Value++;
            Thread.SpinWait(300);
        }
    }

    public record struct Step(int Every, int Phase) : ISingletonComponent;

    // Each system waits at the rendezvous: it reports Met only if another system arrived while it was running.
    public record struct Rendezvous(Barrier Barrier, int TimeoutMs) : ISingletonComponent;
    public record struct Left(bool Met) : IComponent;
    public record struct Right(bool Met) : IComponent;
    public record struct Counter(int N) : IComponent;
    public record struct Seen(int N) : IComponent;
    public record struct Fault(bool Throw) : IComponent;

    [EcsSystem]
    public ref struct WriteLeft
    {
        [ForEach]
        static void Meet(ref Left left, in Rendezvous rendezvous) =>
            left.Met = rendezvous.Barrier.SignalAndWait(rendezvous.TimeoutMs);
    }

    [EcsSystem]
    public ref struct WriteRight
    {
        [ForEach]
        static void Meet(ref Right right, in Rendezvous rendezvous) =>
            right.Met = rendezvous.Barrier.SignalAndWait(rendezvous.TimeoutMs);
    }

    /// <summary>Disjoint data from WriteLeft, but ordered after it: never at the same time.</summary>
    [EcsSystem, UpdateAfter<WriteLeft>]
    public ref struct WriteRightAfterLeft
    {
        [ForEach]
        static void Meet(ref Right right, in Rendezvous rendezvous) =>
            right.Met = rendezvous.Barrier.SignalAndWait(rendezvous.TimeoutMs);
    }

    /// <summary>An Entity parameter can reach any component (and make structural changes): it runs alone.</summary>
    [EcsSystem]
    public ref struct WriteRightThroughEntity
    {
        [ForEach, With<Left>]
        static void Meet(Entity entity, in Rendezvous rendezvous) =>
#pragma warning disable EECS027 // the lookup through the Entity is what this system covers
            entity.Get<Right>().Met = rendezvous.Barrier.SignalAndWait(rendezvous.TimeoutMs);
#pragma warning restore EECS027
    }

    /// <summary>WriteRight's [ForEach] method with an OnUpdate method: it runs alone.</summary>
    [EcsSystem]
    public ref struct WriteRightWithUpdate
    {
        static void OnUpdate() { } // could reach any data: the scheduler cannot know

        [ForEach]
        static void Meet(ref Right right, in Rendezvous rendezvous) =>
            right.Met = rendezvous.Barrier.SignalAndWait(rendezvous.TimeoutMs);
    }

    [EcsSystem]
    public ref struct Count
    {
        [ForEach]
        static void Increment(ref Counter counter) => counter.N++;
    }

    [EcsSystem]
    public ref struct Observe
    {
        [ForEach]
        static void Copy(in Counter counter, ref Seen seen) => seen.N = counter.N;
    }

    [EcsSystem]
    public ref struct Throwing
    {
        [ForEach]
        static void Fail(in Fault fault)
        {
            if (fault.Throw) throw new InvalidOperationException("boom");
        }
    }

    private static World ParallelWorld(int timeoutMs)
    {
        var world = new World(new WorldOptions { ParallelSystems = true, MaxDegreeOfParallelism = 2 });
        world.AddSingleton<Rendezvous>() = new Rendezvous(new Barrier(2), timeoutMs);
        world.CreateEntity(new Left(), new Right());
        return world;
    }

    [Test]
    public void ParallelForEachVisitsEveryMatchingRowOnceOnSeveralThreads()
    {
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4 });
        int rows = World.ParallelChunkRows * 3 + 100;
        var entities = new Entity[rows];
        world.CreateEntities(entities, new Counter(0), new Seen(2));
        for (int i = 0; i < rows; i += 7) entities[i].Set(new Dead());      // a filtered table, two tables in all
        for (int i = 1; i < rows; i += 11) entities[i].Remove<Seen>();     // presence bits: rows to test
        world.AddSystem<ChunkedIncrement>();
        world.Update();
        world.Update();

        for (int i = 0; i < rows; i++)
        {
            bool runs = i % 7 != 0 && (i - 1) % 11 != 0;
            Assert.That(entities[i].Get<Counter>().N, Is.EqualTo(runs ? 4 : 0), $"row {i}");
        }
    }

    [Test]
    public void WorldsRunningParallelPassesAtOnceStayCorrect()
    {
        // Worlds on their own threads run parallel passes at once, their helpers side by side on the thread pool.
        const int rows = 40_000, passes = 200;
        var worlds = new World[3];
        for (int w = 0; w < worlds.Length; w++)
        {
            worlds[w] = new World(new WorldOptions { MaxDegreeOfParallelism = 4 });
            worlds[w].CreateEntities(new Entity[rows], new Counter(0), new Seen(w + 1));
            worlds[w].AddSystem<ChunkedIncrement>();
        }

        var threads = new Thread[worlds.Length];
        for (int w = 0; w < worlds.Length; w++)
        {
            var world = worlds[w];
            threads[w] = new Thread(() =>
            {
                for (int i = 0; i < passes; i++) world.Update();
            });
            threads[w].Start();
        }

        foreach (var thread in threads) thread.Join();
        for (int w = 0; w < worlds.Length; w++)
        {
            int expected = (w + 1) * passes, wrong = 0;
            foreach (var item in worlds[w].Query<Counter>()) wrong += item.Component1.N == expected ? 0 : 1;
            Assert.That(wrong, Is.Zero, $"world {w}");
            worlds[w].Dispose();
        }
    }

    [Test]
    public void ParallelForEachRunsOnTheCallingThreadWhenTheWorldAllowsOneThread()
    {
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 1 });
        var entities = new Entity[World.ParallelChunkRows * 2 + 1];
        world.CreateEntities(entities, new Counter(0), new Seen(1));
        world.AddSystem<ChunkedIncrement>();
        world.Update();
        Assert.That(world.Count<Counter>(), Is.EqualTo(entities.Length));
        foreach (var e in entities) Assert.That(e.Get<Counter>().N, Is.EqualTo(1));
    }

    [Test]
    public void SystemsWithDisjointWritesRunTogether()
    {
        using var world = ParallelWorld(timeoutMs: 10_000);
        world.AddSystem<WriteLeft>();
        world.AddSystem<WriteRight>();
        world.Update();

        foreach (var item in world.Query<Left, Right>())
            Assert.That((item.Component1.Met, item.Component2.Met), Is.EqualTo((true, true)), "both were running at once");
    }

    [Test]
    public void OrderedSystemsRunOneAfterTheOther()
    {
        using var world = ParallelWorld(timeoutMs: 50);
        world.AddSystem<WriteRightAfterLeft>();                               // registered first, ordered after
        world.AddSystem<WriteLeft>();
        world.Update();

        foreach (var item in world.Query<Left, Right>())
            Assert.That((item.Component1.Met, item.Component2.Met), Is.EqualTo((false, false)));
        var order = new System.Collections.Generic.List<System.Type>();
        foreach (var system in world.Systems) order.Add(system.Type);
        Assert.That(order[0], Is.EqualTo(typeof(WriteLeft)));
    }

    [Test]
    public void SystemsThatReachTheWorldRunAlone()
    {
        using var world = ParallelWorld(timeoutMs: 100);
        world.AddSystem<WriteLeft>();
        world.AddSystem<WriteRightThroughEntity>();
        world.Update();

        foreach (var item in world.Query<Left, Right>())
            Assert.That((item.Component1.Met, item.Component2.Met), Is.EqualTo((false, false)), "never at the same time");
    }

    [Test]
    public void SubclassesTheGeneratorDidNotSeeRunAlone()
    {
        using var world = ParallelWorld(timeoutMs: 100);
        world.AddSystem<WriteLeft>();
        world.AddSystem<WriteRightWithUpdate>();
        world.Update();

        foreach (var item in world.Query<Left, Right>())
            Assert.That((item.Component1.Met, item.Component2.Met), Is.EqualTo((false, false)), "never at the same time");
    }

    [Test]
    public void ConflictingSystemsKeepRegistrationOrder()
    {
        using var world = new World(new WorldOptions { ParallelSystems = true });
        for (int i = 0; i < 64; i++) world.CreateEntity(new Counter(), new Seen());
        world.AddSystem<Count>();
        world.AddSystem<Observe>(); // reads what Count writes: runs after it, every update

        for (int update = 1; update <= 200; update++)
        {
            world.Update();
            foreach (var item in world.Query<Counter, Seen>())
                Assert.That(item.Component2.N, Is.EqualTo(update));
        }
    }

    [Test]
    public void OneThreadMovesTheChangeTickPerStageAsParallelStagesDo()
    {
        // Count and Movement share the first stage, Observe (reads Counter) is the second: run on one thread, the three
        // systems of this assembly are one call of its set, and the tick still moves on before each stage, also before a
        // stage whose only system is switched off.
        var ticks = new System.Collections.Generic.List<ulong>[2];
        for (int mode = 0; mode < 2; mode++)
        {
            using var world = new World(new WorldOptions { ParallelSystems = mode == 1 });
            world.CreateEntity(new Counter(), new Seen(), new Position(), new Velocity(1, 1));
            world.AddSystem<Count>();
            world.AddSystem<Movement>();
            var observe = world.AddSystem<Observe>();
            ticks[mode] = [];
            for (int update = 0; update < 6; update++)
            {
                observe.Enabled = update % 2 == 0;
                ulong before = world.ChangeTick;
                world.Update();
                ticks[mode].Add(world.ChangeTick - before);
                foreach (var system in world.Systems) ticks[mode].Add(system.LastRunTick - before);
            }

            Assert.That(world.Query<Seen>().GetEnumerator().MoveNext(), Is.True);
            foreach (var item in world.Query<Seen>()) Assert.That(item.Component1.N, Is.EqualTo(5), "Observe ran on updates 1, 3, 5");
        }

        Assert.That(ticks[0], Is.EqualTo(ticks[1]), "the same ticks on one thread as in parallel stages");
        Assert.That(ticks[0][0], Is.EqualTo(3UL), "two stages and the tick after the update");
    }

    [Test]
    public void ErrorsOnHelperThreadsReachTheCaller()
    {
        using var world = ParallelWorld(timeoutMs: 0);
        world.CreateEntity(new Fault(true));
        world.AddSystem<WriteLeft>();
        world.AddSystem<Throwing>();
        Assert.Throws<InvalidOperationException>(world.Update);
        Assert.DoesNotThrow(() =>
        {
            foreach (var item in world.Query<Fault>()) item.Component1.Throw = false;
            world.Update();
        }, "the next update runs normally");
    }

    [Test]
    public void ParallelUpdatesDoNotAllocate()
    {
        using var world = new World(new WorldOptions { ParallelSystems = true });
        for (int i = 0; i < 1_000; i++) world.CreateEntity(new Position(i, i), new Velocity(1, 1), new Counter());
        world.AddSystem<Count>();
        world.AddSystem<Movement>();

        // Warm-up: helpers, JIT tiers, and the thread pool's own worker threads (started from the queuing thread).
        for (int i = 0; i < 500; i++) world.Update();
        Assert.That(Allocations.Of(() => { for (int i = 0; i < 100; i++) world.Update(); }), Is.Zero);
    }

    private struct StampId : IForEach<Counter>, INoStructuralChanges
    {
        public void Execute(ref Counter counter) => counter.N++;   // from -1 to 0: visited once
    }

    private struct CountUp : IForEach<Counter>, INoStructuralChanges
    {
        public void Execute(ref Counter counter) => counter.N++;
    }

    private struct StampHandle : IForEachEntity<Seen>, INoStructuralChanges
    {
        public void Execute(Entity entity, ref Seen seen) => seen.N = entity.Id.Index + entity.Id.Version;
    }

    [Test]
    public void PassesOfGrowingChunkCountsRunEachChunkOnce()
    {
        // 12 chunks, then 16, in turn (two job types: each keeps its own record of whether helpers pay): a helper still in
        // the last pass must not claim a chunk of the next, larger one (that chunk would run twice and the caller wait
        // forever for it). The passes stop after a time budget too, and the counts are checked against the passes run: a
        // loaded machine (100,000 passes took over a minute under a load of 20 on 4 cores) runs fewer, and what fails is a
        // pass that never ends or a chunk run twice, not a slow machine.
        const int passes = 100_000;
        var budget = TimeSpan.FromSeconds(30);
        int done = 0;
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4 });
        world.CreateEntities(new Entity[12_288], new Counter(0));
        world.CreateEntities(new Entity[65_536], new Counter(0), new Dead());
        var small = QueryFilter.Create().Without<Dead>();
        var large = QueryFilter.Create().With<Dead>();
        var thread = new Thread(() =>
        {
            var ids = new StampId();
            var counts = new CountUp();
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int i = 0; i < passes && System.Diagnostics.Stopwatch.GetElapsedTime(start) < budget; i++)
            {
                world.ForEachParallel<Counter, StampId>(ref ids, small);
                world.ForEachParallel<Counter, CountUp>(ref counts, large);
                done = i + 1;
            }
        }) { IsBackground = true };
        thread.Start();

        Assert.That(thread.Join(budget + TimeSpan.FromSeconds(60)), Is.True, "a pass never ended");
        Assert.That(done, Is.GreaterThan(1_000), "too few passes to cover the race");
        int wrong = 0;
        foreach (var item in world.Query<Counter>()) wrong += item.Component1.N == done ? 0 : 1;
        Assert.That(wrong, Is.Zero);
    }

    [Test]
    public void ChunkRowsSpreadAFewHeavyRowsOverThreads()
    {
        if (Environment.ProcessorCount < 2) Assert.Ignore("one processor: nothing to spread over");
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4 });
        world.CreateEntities(new Entity[300], new Counter(0)); // under the 1,024 rows of a default chunk
        world.AddSystem<HeavyRows>();
        var threads = new System.Collections.Generic.HashSet<int>();
        // Helpers start on the thread pool: on a loaded machine they may start too late to pay in the first pass, which then
        // runs alone until its next probe of helpers (after 16 passes): give it the passes to get there.
        for (int i = 0; i < 100 && threads.Count < 2; i++)
        {
            world.Update();
            foreach (var item in world.Query<Counter>()) threads.Add(item.Component1.N);
        }

        Assert.That(threads, Has.Count.GreaterThan(1));
    }

    [Test]
    public void MeasuredCostSpreadsAFewHeavyRowsOverThreadsWithoutChunkRows()
    {
        if (Environment.ProcessorCount < 2) Assert.Ignore("one processor: nothing to spread over");
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4 });
        world.CreateEntities(new Entity[300], new Counter(0)); // one chunk of the default 1,024-row floor before measuring
        world.AddSystem<HeavyRowsMeasured>();
        var threads = new System.Collections.Generic.HashSet<int>();
        for (int i = 0; i < 100 && threads.Count < 2; i++) // the first pass measures; helpers may start late (see above)
        {
            world.Update();
            foreach (var item in world.Query<Counter>()) threads.Add(item.Component1.N);
        }

        Assert.That(threads, Has.Count.GreaterThan(1));
    }

    [Test]
    public void ChunkSizeFollowsTheMeasuredCost()
    {
        long second = System.Diagnostics.Stopwatch.Frequency;
        var heavy = new CountingWork(1);
        Assert.That(heavy.Rows(100_000, 4), Is.Zero, "nothing measured yet: the count decides");
        heavy.Items = 100_000;
        heavy.Cost(second / 10); // 1 µs per row: 100 ms would make 4,000 chunks of 25 µs; 64 per thread at most
        Assert.That(heavy.Rows(100_000, 4), Is.EqualTo(448));
        Assert.That(heavy.Rows(2_000, 4), Is.EqualTo(64), "2 ms: chunks of 25 µs, the 64-row least");

        var light = new CountingWork(1) { Items = 1_000_000 };
        light.Cost(second / 1_000); // 1 ns per row
        Assert.That(light.Rows(1_000_000, 4), Is.EqualTo(25_024), "1 ms: 40 chunks of 25 µs, not 16 of 62 µs");
        Assert.That(light.Rows(100_000, 4), Is.EqualTo(6_272), "100 µs: four chunks per thread");
        Assert.That(light.Rows(12_000, 4), Is.EqualTo(5_056), "12 µs: chunks of 5 µs at least");
        Assert.That(light.Rows(2_000, 4), Is.EqualTo(2_048), "2 µs: one chunk, on the caller");

        var merging = new CountingWork(1);
        merging.Cost(second); // a pass cut by count (Items 0) learns nothing
        Assert.That(merging.Measured, Is.False);
    }

    [Test]
    public void ChunksCutByMeasuredCostKeepChangeTicksExact()
    {
        // Heavy rows writing a tracked component: once measured, chunks of a few dozen rows split every 4,096-row block
        // of change ticks (opened first), and a Changed query still finds every row each pass.
        const int rows = 10_000;
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4 });
        world.CreateEntities(new Entity[rows], new Armor(0));
        world.AddSystem<SlowArmor>();
        for (int i = 0; i < 40; i++)
        {
            ulong mark = world.ChangeTick;
            world.Update();
            Assert.That(world.Count<Armor>(QueryFilter.Create().Changed<Armor>(mark)), Is.EqualTo(rows), $"pass {i}");
        }

        foreach (var item in world.Read<Armor>()) Assert.That(item.Component1.Value, Is.EqualTo(40));
    }

    [Test]
    public void ChunksSplittingBlocksKeepChangeTicksExact()
    {
        // 10,000 rows in chunks of 64: about 64 chunks share each 4,096-row block of change ticks. Each step writes
        // another seventh of the rows; a Changed query since the step finds exactly those, one since two steps both.
        const int rows = 10_000, every = 7;
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4 });
        var entities = new Entity[rows];
        world.CreateEntities(entities, new Seen(0), new Armor(0));
        for (int i = 0; i < rows; i++) entities[i].Get<Seen>().N = i;
        world.AddSystem<SomeArmor>();
        world.AddSingleton<Step>() = new Step(every, -1);
        world.Update(); // past the creation's tick: no row written yet
        var marks = new ulong[every + 1];
        for (int phase = 0; phase < every; phase++)
        {
            world.GetSingleton<Step>() = new Step(every, phase);
            marks[phase] = world.ChangeTick;
            world.Update();
            marks[phase + 1] = world.ChangeTick;
            Assert.That(world.Count<Armor>(QueryFilter.Create().Changed<Armor>(marks[phase])), Is.EqualTo(Phases(phase, phase)),
                $"step {phase}");
            if (phase > 0)
                Assert.That(world.Count<Armor>(QueryFilter.Create().Changed<Armor>(marks[phase - 1])), Is.EqualTo(Phases(phase - 1, phase)),
                    $"steps {phase - 1} and {phase}");
        }

        // Every row each step: the chunks start together, so threads stamp the first words of a shared block at once.
        world.GetSingleton<Step>() = new Step(1, 0);
        for (int i = 0; i < 1_000; i++)
        {
            ulong mark = world.ChangeTick;
            world.Update();
            Assert.That(world.Count<Armor>(QueryFilter.Create().Changed<Armor>(mark)), Is.EqualTo(rows), $"pass {i}");
        }

        static int Phases(int from, int to)
        {
            int count = 0;
            for (int i = 0; i < rows; i++)
                if (i % every >= from && i % every <= to) count++;
            return count;
        }
    }

    [Test]
    public void ChunksSplittingBlocksKeepTheChangeTicksOfAMaybeExact()
    {
        // A Maybe's column is not queried: its blocks are opened as the queried columns' are, or two chunks of one
        // block race on its summary and a Changed query misses the words one of them stamped.
        if (Environment.ProcessorCount < 2) Assert.Ignore("one processor: nothing to race");
        const int rows = 10_000;
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4 });
        var entities = new Entity[rows];
        world.CreateEntities(entities, new Seen(0), new Armor(0));
        for (int i = 0; i < rows; i += 3) entities[i].Remove<Armor>(); // a table with the column and one without
        int armored = world.Count<Armor>();
        world.AddSystem<MaybeArmor>();
        for (int i = 0; i < 1_000; i++)
        {
            ulong mark = world.ChangeTick;
            world.Update();
            Assert.That(world.Count<Armor>(QueryFilter.Create().Changed<Armor>(mark)), Is.EqualTo(armored), $"pass {i}");
        }
    }

    [Test]
    public void AdHocParallelJobsVisitEveryMatchingRowOnce()
    {
        using var world = new World();
        var entities = new Entity[World.ParallelChunkRows * 3 + 17];                // several chunks and a partial one
        world.CreateEntities(entities, new Counter(-1), new Seen(-1));
        for (int i = 0; i < entities.Length; i += 10) entities[i].Set(new Dead());
        var filter = QueryFilter.Create().Without<Dead>();

        var ids = new StampId();
        world.ForEachParallel<Counter, StampId>(ref ids, filter);
        var handles = new StampHandle();
        world.ForEachEntityParallel<Seen, StampHandle>(ref handles, filter);

        for (int i = 0; i < entities.Length; i++)
        {
            var e = entities[i];
            bool dead = i % 10 == 0;
            Assert.That(e.Get<Counter>().N, Is.EqualTo(dead ? -1 : 0));
            Assert.That(e.Get<Seen>().N, Is.EqualTo(dead ? -1 : e.Id.Index + e.Id.Version));
        }
    }
    /// <summary>Items that count their runs, and throw on a chosen run of a chosen item.</summary>
    private sealed class CountingWork(int items) : World.ParallelWork
    {
        public readonly int[] Runs = new int[items];
        public int ThrowAt = -1;

        public override void Run(int index)
        {
            if (Interlocked.Increment(ref Runs[index]) == ThrowAt) throw new InvalidOperationException("item " + index);
        }
    }

    [Test]
    public void WorkThatSettlesOnRunningAloneStillRunsEveryItemOnce()
    {
        // Items too small for helpers: the runner runs them alone after its first runs, and probes helpers now and then.
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4 });
        var work = new CountingWork(8);
        bool alone = false;
        int runs = 0;
        for (; runs < 20_000 && !(alone && runs >= 2_000); runs++)
        {
            world.RunChunks(work, 8);
            alone |= work.Alone;
        }

        Assert.That(alone, Is.True, "tiny items run alone");
        Assert.That(work.Runs, Is.All.EqualTo(runs), "every item once per run, alone or with helpers");
        Assert.That(Allocations.Of(() => { for (int i = 0; i < 100; i++) world.RunChunks(work, 8); }), Is.Zero);
    }

    [Test]
    public void AnItemThatThrowsWhileRunningAloneLetsTheOthersRun()
    {
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4 });
        var work = new CountingWork(8);
        int runs = 0;
        while (!work.Alone && runs < 20_000)
        {
            world.RunChunks(work, 8);
            runs++;
        }

        Assume.That(work.Alone, Is.True, "the runner chose to run the items alone");
        work.ThrowAt = runs + 1; // every item's next run throws: the first one's exception reaches the caller
        var error = Assert.Throws<InvalidOperationException>(() => world.RunChunks(work, 8));
        Assert.That(error.Message, Is.EqualTo("item 0"));
        Assert.That(work.Runs, Is.All.EqualTo(runs + 1), "the items after the one that threw ran too");
        Assert.DoesNotThrow(() => world.RunChunks(work, 8), "the next run is normal");
        Assert.That(world.CreateEntity(new Counter()).IsAlive, Is.True, "the gate is open again");
    }

    /// <summary>One item that tries a structural change, and records whether the world was in a parallel region.</summary>
    private sealed class StructuralWork(World world) : World.ParallelWork
    {
        public bool Refused, InParallel;

        public override void Run(int index)
        {
            InParallel = world.Gate.InParallel;
            try
            {
                world.CreateEntity(new Counter());
            }
            catch (InvalidOperationException)
            {
                Refused = true;
            }
        }
    }

    [Test]
    public void OneChunkRunsAsASerialPassOnTheCallingThread()
    {
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4 });
        var work = new StructuralWork(world);
        world.RunChunks(work, 1);
        Assert.That((work.Refused, work.InParallel), Is.EqualTo((true, false)), "frozen as a serial pass, no parallel region");
        Assert.That(world.Count<Counter>(), Is.Zero);
        Assert.That(world.CreateEntity(new Counter()).IsAlive, Is.True, "the gate is open again");

        world.RunChunks(work, 0); // no chunk: nothing runs, nothing stays frozen
        Assert.That(world.CreateEntity(new Counter()).IsAlive, Is.True);
    }

    private struct OneChunkInside : IForEachBatch<Counter>
    {
        public World World;
        public StructuralWork Work;

        public void Execute(EntitySpan entities, Span<Counter> c1) => World.RunChunks(Work, 1);
    }

    [Test]
    public void OneChunkInsideAFrozenRegionLeavesItFrozen()
    {
        using var world = new World();
        world.CreateEntity(new Counter());
        var work = new StructuralWork(world);
        var job = new OneChunkInside { World = world, Work = work };
        world.ForEachBatch<Counter, OneChunkInside>(ref job);
        Assert.That(work.Refused, Is.True, "the batch job's freeze holds inside the chunk");
        Assert.That(world.CreateEntity(new Counter()).IsAlive, Is.True, "and ends with the batch job");
    }
}
