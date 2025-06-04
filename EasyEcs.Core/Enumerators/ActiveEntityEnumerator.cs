namespace EasyEcs.Core.Enumerators;

public readonly struct ActiveEntityEnumerator
{
    private readonly bool[] _activeEntityIds;
    private readonly Context _context;

    public ActiveEntityEnumerator(bool[] activeEntityIds, Context context)
    {
        _activeEntityIds = activeEntityIds;
        _context = context;
    }

    // ReSharper disable once UnusedMember.Global
    public Enumerator GetEnumerator() => new(_activeEntityIds, _context);

    public struct Enumerator
    {
        private readonly bool[] _activeEntityIds;
        private readonly Context _context;
        private bool _initialized;
        // ReSharper disable once UnusedAutoPropertyAccessor.Global
        public EntityRef Current { get; private set; }
        private int _index;

        public Enumerator(bool[] activeEntityIds, Context context)
        {
            _activeEntityIds = activeEntityIds;
            _context = context;
            _initialized = false;
            Current = default;
            _index = 0;
        }

        public bool MoveNext()
        {
            if (!_initialized)
            {
                _index = 0;
                _initialized = true;
            }

            while (_index < _activeEntityIds.Length)
            {
                if (_activeEntityIds[_index])
                {
                    Current = new EntityRef(_index, _context);
                    _index++;
                    return true;
                }

                _index++;
            }

            Current = default;
            return false;
        }
    }
}