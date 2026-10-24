using System;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>
/// <see cref="Scratch{T}"/>: one value per [ForEach] method and thread, created on the thread's first row and kept,
/// never reset, never shared by two threads at once.
/// </summary>
public class ScratchTests
{
    public record struct Number(int Value) : IComponent;
    public record struct DigitSum(int Value) : IComponent;
    public record struct Order(int Value) : IComponent;

    /// <summary>A list of digits, reused by every row the thread runs.</summary>
    public struct Digits
    {
        public List<int> List;
        public int Busy; // 1 while a row uses it: a second row finding 1 means two threads share it
    }

    [EcsSystem]
    public ref struct SumDigits
    {
        [ForEach(Parallel = true, ChunkRows = 64)]
        static void Sum(in Number number, ref DigitSum sum, Scratch<Digits> scratch)
        {
            ref var digits = ref scratch.Value;
            if (Interlocked.Exchange(ref digits.Busy, 1) != 0) sum.Value = -1; // shared: reported as a wrong sum
            var list = digits.List ??= new List<int>(16);
            list.Clear();
            for (int n = number.Value; n > 0; n /= 10) list.Add(n % 10);
            int total = 0;
            for (int i = 0; i < list.Count; i++) total += list[i];
            if (sum.Value != -1) sum.Value = total;
            Volatile.Write(ref digits.Busy, 0);
        }
    }

    public record struct Bonus(int Value) : IComponent;

    /// <summary>
    /// A Scratch method that loops, beside a Maybe: its job walks runs of rows (ForEachBatch) and calls the method out of
    /// line, once per row.
    /// </summary>
    [EcsSystem]
    public ref struct SumDigitsWithBonus
    {
        [ForEach]
        static void Sum(in Number number, ref DigitSum sum, ReadOnlyMaybe<Bonus> bonus, Scratch<List<int>> digits)
        {
            var list = digits.Value ??= new List<int>(16);
            list.Clear();
            for (int n = number.Value; n > 0; n /= 10) list.Add(n % 10);
            int total = bonus.HasValue ? bonus.Value.Value : 0;
            for (int i = 0; i < list.Count; i++) total += list[i];
            sum.Value = total;
        }
    }

    /// <summary>Counts the rows its thread ran: what the last row left is what the next one finds.</summary>
    [EcsSystem]
    public ref struct CountRows
    {
        [ForEach]
        static void Count(ref Order order, Scratch<int> rows) => order.Value = ++rows.Value;
    }

    /// <summary>A second method with the same scratch type: its own value.</summary>
    [EcsSystem]
    public ref struct CountRowsAgain
    {
        [ForEach]
        static void Count(ref Order order, Scratch<int> rows) => order.Value = 1_000_000 + ++rows.Value;
    }

    private static World Numbered(int count, int threads = 4)
    {
        var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        var entities = new Entity[count];
        world.CreateEntities(entities, new Number(0), new DigitSum(0));
        for (int i = 0; i < entities.Length; i++) entities[i].Number = new Number(i * 7919);
        return world;
    }

    private static int SumOfDigits(int n)
    {
        int total = 0;
        for (; n > 0; n /= 10) total += n % 10;
        return total;
    }

    [Test]
    public void ParallelRowsNeverShareAScratchValue()
    {
        using var world = Numbered(20_000);
        world.AddSystem<SumDigits>();
        for (int run = 0; run < 200; run++)
        {
            world.Update();
            int row = 0;
            foreach (var item in world.Read<Number, DigitSum>())
            {
                if (item.DigitSum.Value != SumOfDigits(item.Number.Value))
                    Assert.Fail($"run {run}, row {row}: {item.DigitSum.Value} for {item.Number.Value}");
                row++;
            }
        }
    }

    [Test]
    public void AScratchValueIsKeptAcrossRowsPassesAndWorlds()
    {
        using var first = new World();
        var a = new Entity[10];
        first.CreateEntities(a, new Order(0));
        first.AddSystem<CountRows>();
        first.Update();
        int start = a[0].Order.Value - 1; // what earlier tests on this thread left
        for (int i = 0; i < a.Length; i++) Assert.That(a[i].Order.Value, Is.EqualTo(start + i + 1));
        first.Update(); // never reset between passes
        Assert.That(a[0].Order.Value, Is.EqualTo(start + 11));

        using var second = new World(); // nor between worlds: the value is the thread's
        var b = second.CreateEntity(new Order(0));
        second.AddSystem<CountRows>();
        second.Update();
        Assert.That(b.Order.Value, Is.EqualTo(start + 21));
    }

    [Test]
    public void EachMethodHasItsOwnScratchValue()
    {
        using var world = new World();
        var e = world.CreateEntity(new Order(0));
        world.AddSystem<CountRowsAgain>();
        world.Update();
        int again = e.Order.Value;
        world.Update();
        Assert.That(e.Order.Value, Is.EqualTo(again + 1), "CountRows' value (other tests) is not this method's");
    }

    [Test]
    public void EachThreadHasItsOwnScratchValue()
    {
        int seen = 0;
        var thread = new Thread(() =>
        {
            using var world = new World(); // a fresh thread, with its own world: its scratch starts at default
            var e = world.CreateEntity(new Order(0));
            world.AddSystem<CountRowsAgain>();
            world.Update();
            seen = e.Order.Value;
        });
        thread.Start();
        thread.Join();
        Assert.That(seen, Is.EqualTo(1_000_001));
    }

    [Test]
    public void AParallelPassWithScratchDoesNotAllocateAfterWarmUp()
    {
        using var world = Numbered(World.ParallelChunkRows * 4);
        world.AddSystem<SumDigits>();
        for (int i = 0; i < 20; i++) world.Update(); // every helper thread has created its list
        Assert.That(Allocations.Of(() => { for (int i = 0; i < 200; i++) world.Update(); }), Is.Zero);
    }

    [Test]
    public void AScratchMethodThatLoopsRunsInRunsOfRowsWithoutAllocating()
    {
        using var world = Numbered(1_000);
        var bonused = new Entity[1_000];
        world.CreateEntities(bonused, new Number(0), new DigitSum(0), new Bonus(100));
        for (int i = 0; i < bonused.Length; i++) bonused[i].Number = new Number(i * 31);
        world.AddSystem<SumDigitsWithBonus>();
        world.Update();
        foreach (var item in world.Read<Number, DigitSum>())
        {
            int bonus = item.Entity.TryGetBonus(out var b) ? b.Value : 0;
            Assert.That(item.DigitSum.Value, Is.EqualTo(SumOfDigits(item.Number.Value) + bonus));
        }

        for (int i = 0; i < 10; i++) world.Update();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) world.Update();
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    [Test]
    public void ScratchIsNoAccessOfTheSchedule()
    {
        using var world = new World();
        world.AddSystem<CountRows>();
        world.AddSystem<SumDigits>(); // different components: one stage, scratch adds no conflict
        Assert.That(world.DescribeSchedule().Groups[0].Stages.Length, Is.EqualTo(1), world.DescribeSchedule().ToString());
    }
}
