namespace EasyEcs.Core.Enumerators;

public struct ActiveEntityEnumerator
{
    private readonly bool[] _activeEntityIds;
    private readonly int[] _entityVersions;
    private readonly Context _context;
    private readonly int _maxLength; // Capture length to avoid resizing issues
    private int _index;
    public EntityRef Current { get; private set; }

    public ActiveEntityEnumerator(bool[] activeEntityIds, int[] entityVersions, Context context)
    {
        _activeEntityIds = activeEntityIds;
        _entityVersions = entityVersions;
        _context = context;
        // Capture min length to ensure we don't go out of bounds if arrays are resized
        _maxLength = System.Math.Min(activeEntityIds.Length, entityVersions.Length);
        _index = 0;
        Current = default;
    }

    // ReSharper disable once UnusedMember.Global
    public ActiveEntityEnumerator GetEnumerator() => new(_activeEntityIds, _entityVersions, _context);

    public bool MoveNext()
    {
        // Use captured length to avoid ArrayIndexOutOfBoundsException during concurrent resize
        while (_index < _maxLength)
        {
            // Read active flag first
            bool isActive = _activeEntityIds[_index];

            if (isActive)
            {
                // Then read version - might be stale if entity was just destroyed, but EntityRef.Value will validate
                int version = _entityVersions[_index];
                Current = new EntityRef(_index, version, _context);
                _index++;
                return true;
            }

            _index++;
        }

        Current = default;
        return false;
    }
}