namespace EasyEcs.Core.Enumerators;

/// <summary>
/// Enumerates every alive entity in id order. Entities created during enumeration beyond the captured
/// capacity are not visited.
/// </summary>
public struct ActiveEntityEnumerator
{
    private readonly World _world;
    private readonly EntityMeta[] _meta; // captured: a resize swaps the array, ids beyond it are not visited
    private int _index;

    public EntityRef Current { get; private set; }

    public ActiveEntityEnumerator(World world)
    {
        _world = world;
        _meta = world.Meta;
        _index = 0;
        Current = default;
    }

    // ReSharper disable once UnusedMember.Global
    public ActiveEntityEnumerator GetEnumerator() => new(_world);

    public bool MoveNext()
    {
        var meta = _meta;
        while (_index < meta.Length)
        {
            ref var m = ref meta[_index];
            if (m.Archetype != null)
            {
                // Version might be stale if the entity is destroyed concurrently; EntityRef.Value validates it.
                Current = new EntityRef(_index, m.Version, _world);
                _index++;
                return true;
            }

            _index++;
        }

        Current = default;
        return false;
    }
}
