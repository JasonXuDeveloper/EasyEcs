using System.Collections.Generic;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Nino.Core;

namespace EasyEcs.Benchmark;

/// <summary>
/// A halo exchange between two worlds side by side (samples/MultiWorld's Border): every tick the ghosts of the last
/// exchange are destroyed (<c>DestroyAll&lt;Ghost&gt;</c>) and the border rows of the neighbor (the units standing in
/// a band along its edge, <see cref="Border"/> of its 20,000) are copied in again as new entities with a
/// <see cref="Ghost"/> tag and <see cref="Components"/> of their components (the position; or position, heading and
/// health), never their velocity. Three ways: the <c>CreateEntity</c> loop over the band as the sample writes it
/// (baseline), the band's ids through <c>SaveEntitiesWith</c> + <c>LoadEntities</c> (then the tag given to the loaded
/// rows in bulk, <c>SetAll</c>), and the band's ids through <c>CopyEntities&lt;Ghost&gt;</c>. <see cref="Sorted"/>: the
/// neighbor's table in space order (the band is one run of rows, as after a pair method's sort) or in creation order
/// (the band scattered over the table: runs of one row).
/// </summary>
[MemoryDiagnoser]
public class GhostCopyBenchmark
{
    public record struct HaloPosition(float X, float Y) : IComponent;
    public record struct HaloVelocity(float X, float Y) : IComponent;
    public record struct HaloHeading(float Angle) : IComponent;
    public record struct HaloHealth(int Value) : IComponent;
    public struct Ghost : IComponent;

    private const int Units = 20_000;
    private const float Width = 1_000;

    [Params(500, 1_000, 5_000)] public int Border;
    [Params(1, 3)] public int Components;
    [Params(false, true)] public bool Sorted;

    private World _neighbor, _zone;
    private float _band; // units with X below it are border rows of the neighbor
    private readonly List<EntityId> _ids = [];
    private readonly NinoArrayBufferWriter _bytes = new();
    private ComponentSet _set;
    private static readonly ComponentSet Position = ComponentSet.Create().With<HaloPosition>();
    private static readonly ComponentSet Full = ComponentSet.Create().With<HaloPosition>().With<HaloHeading>().With<HaloHealth>();
    private static readonly QueryFilter Loaded = QueryFilter.Create().Without<HaloVelocity>().Without<Ghost>();

    [GlobalSetup]
    public void Setup()
    {
        _neighbor = Populate();
        _zone = Populate();
        _band = Width * Border / Units;
        _set = Components == 1 ? Position : Full;
        int copied = CreateLoop();
        if (copied != Border || SaveAndLoad() != Border || Copy() != Border || _zone.Count<Ghost>() != Border)
            throw new System.InvalidOperationException($"{copied} ghosts, expected {Border}");
    }

    // Units spread evenly over the width: unit i stands at x = i * Width / Units, created in space order or in a
    // scattered order (a stride coprime with the count).
    private World Populate()
    {
        var world = new World();
        world.Reserve<HaloPosition, HaloVelocity, HaloHeading, HaloHealth>(Units);
        for (int n = 0; n < Units; n++)
        {
            int i = Sorted ? n : (int)((long)n * 7_919 % Units);
            world.CreateEntity(new HaloPosition(i * Width / Units, i % 97), new HaloVelocity(1, 0), new HaloHeading(i), new HaloHealth(i));
        }

        return world;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _neighbor.Dispose();
        _zone.Dispose();
    }

    [Benchmark(Baseline = true)]
    public int CreateLoop()
    {
        _zone.DestroyAll<Ghost>();
        int count = 0;
        if (Components == 1)
            foreach (var unit in _neighbor.Read<HaloPosition>())
            {
                if (unit.Component1.X >= _band) continue;
                _zone.CreateEntity(unit.Component1, new Ghost());
                count++;
            }
        else
            foreach (var unit in _neighbor.Read<HaloPosition, HaloHeading, HaloHealth>())
            {
                if (unit.Component1.X >= _band) continue;
                _zone.CreateEntity(unit.Component1, unit.Component2, unit.Component3, new Ghost());
                count++;
            }

        return count;
    }

    [Benchmark]
    public int SaveAndLoad()
    {
        _zone.DestroyAll<Ghost>();
        CollectBand();
        _bytes.ResetWrittenCount();
        _neighbor.SaveEntitiesWith(_bytes, CollectionsMarshal.AsSpan(_ids), in _set);
        int count = _zone.LoadEntities(_bytes.WrittenSpan);
        _zone.SetAll(new Ghost(), in Loaded); // the loaded rows are the zone's only rows without a velocity
        return count;
    }

    [Benchmark]
    public int Copy()
    {
        _zone.DestroyAll<Ghost>();
        CollectBand();
        return _neighbor.CopyEntities<Ghost>(_zone, CollectionsMarshal.AsSpan(_ids), in _set);
    }

    private void CollectBand()
    {
        _ids.Clear();
        foreach (var unit in _neighbor.Read<HaloPosition>())
            if (unit.Component1.X < _band) _ids.Add(unit.Entity.Id);
    }
}
