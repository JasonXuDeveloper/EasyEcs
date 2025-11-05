namespace EasyEcs.Core.Enumerators;

public struct ActiveEntityEnumerator
{
    private readonly bool[] _activeEntityIds;
    private readonly Context _context;
    private int _index;
    public EntityRef Current { get; private set; }

    public ActiveEntityEnumerator(bool[] activeEntityIds, Context context)
    {
        _activeEntityIds = activeEntityIds;
        _context = context;
        _index = 0;
        Current = default;
    }

    // ReSharper disable once UnusedMember.Global
    public ActiveEntityEnumerator GetEnumerator() => new(_activeEntityIds, _context);

    public bool MoveNext()
    {
        while (_index < _activeEntityIds.Length)
        {
            if (_activeEntityIds[_index])
            {
                Current = new EntityRef(_index, _context.EntityVersions[_index], _context);
                _index++;
                return true;
            }

            _index++;
        }

        Current = default;
        return false;
    }
}