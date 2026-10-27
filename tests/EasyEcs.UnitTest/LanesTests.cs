using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public class LanesTests
{
    public record struct Triple(int A, int B, int C);

    // Every element size the helper has a path for (1, 2: one at a time; 4, 8: vectors with AVX-512; 12: one at a
    // time), every source length up to 64, and masks from empty to full, sparse and dense.
    [Test]
    public void CompactWritesTheSelectedElementsInOrder()
    {
        Check(i => (byte)i);
        Check(i => (short)(i * 3));
        Check(i => i * 7 + 1);
        Check(i => (float)i / 3);
        Check(i => (long)i << 40 | (uint)i);
        Check(i => new EntityId(i, i + 1));
        Check(i => new Triple(i, -i, i * i));
    }

    private static void Check<T>(Func<int, T> make) where T : unmanaged, IEquatable<T>
    {
        var random = new Random(7);
        var source = new T[64];
        for (int i = 0; i < 64; i++) source[i] = make(i);
        for (int length = 0; length <= 64; length++)
        for (int round = 0; round < 40; round++)
        {
            ulong valid = length == 64 ? ulong.MaxValue : (1UL << length) - 1;
            ulong mask = round switch
            {
                0 => 0,
                1 => valid,
                2 => valid & 0x8000_0000_0000_0001UL,
                _ => (ulong)random.NextInt64() & (ulong)random.NextInt64() & (round % 2 == 0 ? ulong.MaxValue : (ulong)random.NextInt64()) & valid,
            };
            if (round % 3 == 0 && round > 3) mask = (ulong)random.NextInt64() & valid; // about half
            var expected = new List<T>();
            for (int i = 0; i < length; i++)
                if ((mask >> i & 1) != 0) expected.Add(source[i]);
            var destination = new T[expected.Count + 3];
            var sentinel = make(1000);
            destination.AsSpan().Fill(sentinel);
            int written = Lanes.Compact<T>(source.AsSpan(0, length), mask, destination.AsSpan(0, expected.Count));
            Assert.That(written, Is.EqualTo(expected.Count), $"{typeof(T).Name}, {length} elements, mask {mask:X}");
            Assert.That(destination[..written], Is.EqualTo(expected.ToArray()));
            Assert.That(destination[written..], Is.All.EqualTo(sentinel)); // nothing written past the selected ones
        }
    }

    [Test]
    public void RowsWritesTheNumbersOfTheSelectedRows([Values(0, 64, 1_000_000)] int first)
    {
        var random = new Random(11);
        for (int round = 0; round < 400; round++)
        {
            ulong mask = round switch { 0 => 0, 1 => ulong.MaxValue, 2 => 1UL << 63, _ => (ulong)random.NextInt64() & (round % 2 == 0 ? (ulong)random.NextInt64() : ulong.MaxValue) };
            var expected = new List<int>();
            for (int i = 0; i < 64; i++)
                if ((mask >> i & 1) != 0) expected.Add(first + i);
            var destination = new int[expected.Count + 2];
            destination.AsSpan().Fill(-1);
            Assert.That(Lanes.Selection(mask, first, destination.AsSpan(0, expected.Count)), Is.EqualTo(expected.Count));
            Assert.That(destination[..expected.Count], Is.EqualTo(expected.ToArray()), $"mask {mask:X}");
            Assert.That(destination[expected.Count..], Is.All.EqualTo(-1));
        }
    }

    [Test]
    public void CompactRefusesABitPastTheSourceOrTooShortADestination()
    {
        var source = new int[10];
        Assert.Throws<ArgumentException>(() => Lanes.Compact<int>(source, 1UL << 10, new int[4]));
        Assert.Throws<ArgumentException>(() => Lanes.Compact<int>(source, 0b111, new int[2]));
        Assert.That(Lanes.Compact<int>(source, 0, Span<int>.Empty), Is.Zero);
    }

    // The use it is for: a batch method's vector compare picks rows (the ballot), Compact writes their ids.
    public struct Picked : ISingletonComponent
    {
        public EntityId[] Ids;
        public int Count;
    }

    public static class Pick
    {
        [ForEach]
        public static void Hurt(EntitySpan entities, ReadOnlySpan<Health> hp, ref Picked picked)
        {
            var values = MemoryMarshal.Cast<Health, int>(hp);
            int i = 0;
            for (; i + Vector256<int>.Count <= values.Length; i += Vector256<int>.Count)
            {
                ulong mask = Vector256.LessThan(Vector256.Create(values.Slice(i, Vector256<int>.Count)), Vector256.Create(50)).ExtractMostSignificantBits();
                picked.Count += Lanes.Compact(entities.Ids.Slice(i, Vector256<int>.Count), mask, picked.Ids.AsSpan(picked.Count));
            }
            for (; i < values.Length; i++)
                if (values[i] < 50) picked.Ids[picked.Count++] = entities.Ids[i];
        }
    }

    [Test]
    public void ABatchMethodCollectsTheIdsOfTheRowsItPicks()
    {
        using var world = new World();
        var random = new Random(3);
        var expected = new List<EntityId>();
        var all = new Entity[1000];
        world.CreateEntities(all, new Health(0));
        foreach (var item in world.Query<Health>())
        {
            item.Component1 = new Health(random.Next(100));
            if (item.Component1.Value < 50) expected.Add(item.Entity);
        }

        world.AddSingleton<Picked>() = new Picked { Ids = new EntityId[all.Length] };
        Pick.Hurt(world);
        Assert.That(world.Picked.Ids.AsSpan(0, world.Picked.Count).ToArray(), Is.EqualTo(expected.ToArray()));
    }
}
