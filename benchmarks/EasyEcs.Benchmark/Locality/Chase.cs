using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace EasyEcs.Benchmark.Locality;

/// <summary>
/// borrowed-ideas.md item 1: hiding the latency of lookup chases (group prefetching, Chen et al. 2004; AMAC, Kocberber
/// et al. VLDB 2015). Every hunter reads the position of a random other hunter through a <see cref="Lookup{T}"/>: a miss
/// on its record, then one on its row. The hand-written jobs here are what the generator would write for the plain
/// method: <see cref="RowsJob"/> runs it row by row over each run of rows and, with a distance k, prefetches the record
/// of row i + 2k and the row of row i + k (two stages: the record must be in cache before the row's address is known);
/// <see cref="GatherJob"/> is D131's vector loop (Resolve, then a gather per field) prefetching the rows k ahead of each vector of 8.
/// </summary>
public static partial class Chase
{
    public record struct Position(float X, float Y) : IComponent;
    public record struct Prey(EntityId Target) : IComponent;
    public record struct Heading(float X, float Y) : IComponent;

    /// <summary><paramref name="count"/> hunters, each chasing one picked at random (seed 42).</summary>
    public static World Create(int count)
    {
        var world = new World(new WorldOptions { InitialEntityCapacity = count });
        var hunters = new Entity[count];
        world.CreateEntities(hunters, new Position(1, 2), new Prey(), new Heading());
        var random = new Random(42);
        for (int i = 0; i < hunters.Length; i++)
        {
            hunters[i].Get<Position>() = new Position(i * 0.5f, i * 0.25f);
            hunters[i].Prey = new Prey(hunters[random.Next(hunters.Length)].Id);
        }

        return world;
    }

    /// <summary>The plain method: what the user writes.</summary>
    [ForEach]
    public static void Hunt(in Position position, in Prey prey, ref Heading heading, Lookup<Position> positions)
    {
        var at = positions[prey.Target];
        heading = at.HasValue ? new Heading(at.Value.X - position.X, at.Value.Y - position.Y) : default;
    }

    /// <summary>The plain method as D131's vector loop (gathers).</summary>
    [ForEach(SimdMode = SimdMode.Require)]
    public static void HuntGathered(in Position position, in Prey prey, ref Heading heading, Lookup<Position> positions)
    {
        var at = positions[prey.Target];
        heading = at.HasValue ? new Heading(at.Value.X - position.X, at.Value.Y - position.Y) : default;
    }

    /// <summary>The method's job over each run of rows, prefetching <paramref name="distance"/> rows ahead (0: none).</summary>
    public static void Rows(World world, int distance, bool rows = true)
    {
        var job = new RowsJob { Positions = new Lookup<Position>(world, null), Distance = distance, Rows = rows };
        world.ForEachBatch<Position, Prey, Heading, RowsJob>(ref job);
    }

    /// <summary>D131's vector loop over each run of rows, prefetching <paramref name="distance"/> rows ahead (0: none).</summary>
    public static void Gathered(World world, int distance)
    {
        var job = new GatherJob { Positions = new Lookup<Position>(world, null), Distance = distance };
        world.ForEachBatch<Position, Prey, Heading, GatherJob>(ref job);
    }

    public ref struct RowsJob : IForEachBatch<Position, Prey, Heading>
    {
        public Lookup<Position> Positions;
        public int Distance;
        public bool Rows;

        public void Execute(EntitySpan entities, Span<Position> position, Span<Prey> prey, Span<Heading> heading)
        {
            int n = prey.Length, k = Distance;
            if (k == 0)
            {
                for (int i = 0; i < n; i++) Hunt(in position[i], in prey[i], ref heading[i], Positions);
                return;
            }

            // Prologue: the records of the first rows; their rows are prefetched as the loop reaches i + k.
            int ahead = Math.Min(2 * k, n);
            for (int j = 0; j < ahead; j++) Positions.Prefetch(prey[j].Target);
            for (int i = 0; i < n; i++)
            {
                if (i + 2 * k < n) Positions.Prefetch(prey[i + 2 * k].Target);
                if (Rows && i + k < n) Positions.PrefetchRow(prey[i + k].Target);
                Hunt(in position[i], in prey[i], ref heading[i], Positions);
            }
        }
    }

    public ref struct GatherJob : IForEachBatch<Position, Prey, Heading>
    {
        public Lookup<Position> Positions;
        public int Distance;

        public void Execute(EntitySpan entities, Span<Position> position, Span<Prey> prey, Span<Heading> heading)
        {
            int n = prey.Length, i = 0, k = Distance;
            if (Avx2.IsSupported && LookupLanes.IsAccelerated)
            {
                if (k > 0)
                    for (int j = 0, ahead = Math.Min(2 * k, n); j < ahead; j++) Positions.Prefetch(prey[j].Target);

                ref int ids = ref Unsafe.As<Prey, int>(ref MemoryMarshal.GetReference(prey));
                ref float from = ref Unsafe.As<Position, float>(ref MemoryMarshal.GetReference(position));
                ref float to = ref Unsafe.As<Heading, float>(ref MemoryMarshal.GetReference(heading));
                var pairs = Vector256.Create(0, 2, 4, 6, 1, 3, 5, 7);
                int backoff = 0;
                for (; i + 8 <= n; i += 8)
                {
                    if (k > 0)
                    {
                        for (int j = i + 2 * k, end = Math.Min(j + 8, n); j < end; j++) Positions.Prefetch(prey[j].Target);
                        for (int j = i + k, end = Math.Min(j + 8, n); j < end; j++) Positions.PrefetchRow(prey[j].Target);
                    }

                    // EntityId is (Version, Index): 8 ids are 16 ints, deinterleaved into versions and indexes.
                    var a = Avx2.PermuteVar8x32(Vector256.LoadUnsafe(ref ids, (nuint)(2 * i)), pairs);
                    var b = Avx2.PermuteVar8x32(Vector256.LoadUnsafe(ref ids, (nuint)(2 * i + 8)), pairs);
                    var version = Avx2.Permute2x128(a, b, 0x20);
                    var index = Avx2.Permute2x128(a, b, 0x31);
                    if (!Positions.Resolve(version, index, ref backoff, out var has, out var rows, out var column))
                    {
                        for (int r = i; r < i + 8; r++) Hunt(in position[r], in prey[r], ref heading[r], Positions);
                        continue;
                    }

                    var tx = LookupLanes.Gather<float>(column, rows, has, 0);
                    var ty = LookupLanes.Gather<float>(column, rows, has, 4);
                    var p = Avx2.PermuteVar8x32(Vector256.LoadUnsafe(ref from, (nuint)(2 * i)), pairs);
                    var q = Avx2.PermuteVar8x32(Vector256.LoadUnsafe(ref from, (nuint)(2 * i + 8)), pairs);
                    var hx = (tx - Avx.Permute2x128(p, q, 0x20)) & has.AsSingle();
                    var hy = (ty - Avx.Permute2x128(p, q, 0x31)) & has.AsSingle();
                    var low = Avx.UnpackLow(hx, hy);
                    var high = Avx.UnpackHigh(hx, hy);
                    Avx.Permute2x128(low, high, 0x20).StoreUnsafe(ref to, (nuint)(2 * i));
                    Avx.Permute2x128(low, high, 0x31).StoreUnsafe(ref to, (nuint)(2 * i + 8));
                }
            }

            for (; i < n; i++) Hunt(in position[i], in prey[i], ref heading[i], Positions);
        }
    }

    /// <summary>Every heading, for comparing versions.</summary>
    public static Heading[] Headings(World world)
    {
        var all = new Heading[world.Count<Heading>()];
        var job = new Collect { Into = all };
        world.ForEachBatch<Heading, Collect>(ref job);
        return all;
    }

    private struct Collect : IForEachBatch<Heading>
    {
        public Heading[] Into;
        private int _at;

        public void Execute(EntitySpan entities, Span<Heading> heading)
        {
            heading.CopyTo(Into.AsSpan(_at));
            _at += heading.Length;
        }
    }
}
