using System;
using System.Runtime.CompilerServices;

namespace EasyEcs;

/// <summary>
/// A system added to a world (<see cref="World.AddSystem{T}"/>, <see cref="World.System{T}"/>,
/// <see cref="World.Systems"/>): a small handle over the world's record of it, as an <see cref="Entity"/> is over its
/// row. The record is data the world owns (enabled, error, ticks, counts, stats); no system object exists. Once the
/// system is removed (or the world disposed) the handle is stale: <see cref="IsAlive"/> is false and its other members
/// throw <see cref="InvalidOperationException"/>, even after the world reuses the record for another system.
/// Hooks (<see cref="OnUpdateAttribute">[OnUpdate]</see> and the others) may take it to read their own system's record.
/// </summary>
public readonly struct SystemHandle : IEquatable<SystemHandle>
{
    private readonly World _world;
    private readonly int _index;
    private readonly int _version;

    internal SystemHandle(World world, int index, int version)
    {
        _world = world;
        _index = index;
        _version = version;
    }

    /// <summary>Index of the system's record in its world (valid while the handle is alive).</summary>
    internal int Index => _index;

    /// <summary>Version of the record the handle was made for.</summary>
    internal int Version => _version;

    /// <summary>Whether the system is still in its world (not removed, the world not disposed). O(1).</summary>
    public bool IsAlive
    {
        get
        {
            var rows = _world?.SystemRows;
            return rows != null && (uint)_index < (uint)rows.Length && rows[_index].Version == _version && rows[_index].Code != null;
        }
    }

    /// <summary>The system's record; throws when the handle is stale or default.</summary>
    internal ref SystemRow Row
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var rows = _world?.SystemRows;
            if (rows == null || (uint)_index >= (uint)rows.Length) ThrowStale();
            ref var row = ref rows[_index];
            if (row.Version != _version || row.Code == null) ThrowStale();
            return ref row;
        }
    }

    /// <summary>The world the system belongs to.</summary>
    public World World
    {
        get
        {
            _ = ref Row;
            return _world;
        }
    }

    /// <summary>The system's type (its <see cref="EcsSystemAttribute">[EcsSystem]</see> ref struct), for tools and logs.</summary>
    public Type Type => Row.Code.Type;

    /// <summary>The system type's name as reports show it (<c>Regen&lt;Health&gt;</c>).</summary>
    public string Name => ScheduleInfo.NameOf(Row.Code.Type);

    /// <summary>
    /// Disabled systems are skipped by <see cref="World.Update()"/> and <see cref="World.Update{TGroup}()"/>. Enabling
    /// a system clears its <see cref="Error"/>.
    /// </summary>
    public bool Enabled
    {
        get => Row.Enabled;
        set
        {
            ref var row = ref Row;
            row.Enabled = value;
            if (value) row.Error = null;
        }
    }

    /// <summary>
    /// The exception that switched this system off under <see cref="SystemErrorPolicy.Disable"/>, until it is enabled
    /// again; null otherwise.
    /// </summary>
    public Exception Error => Row.Error;

    /// <summary>
    /// The world's <see cref="World.ChangeTick"/> when this system last ran (0 before its first run): its
    /// <c>[Changed&lt;T&gt;]</c> methods select rows written after that tick, so not their own writes of that run.
    /// </summary>
    public ulong LastRunTick => Row.LastRunTick;

    /// <summary>
    /// How many times the system has run before the current run (0 during its first run; runs skipped by
    /// <see cref="UpdateIntervalAttribute">[UpdateInterval]</see> or while disabled do not count, a run that throws does). A
    /// <c>[ForEach(Slices = N)]</c> method visits slice <c>RunCount % N</c> of its rows, so an
    /// <see cref="OnUpdateAttribute">[OnUpdate]</see> method can tell which slice this run visits.
    /// </summary>
    public long RunCount => Row.RunCount;

    /// <summary>
    /// Run count, time, allocated bytes and visited entities of this system; recorded only with
    /// <see cref="WorldOptions.MeasureSystems"/>.
    /// </summary>
    public SystemStats Stats => Row.Stats;

    /// <summary>The same system of the same world (both stale, or both default, compare equal too).</summary>
    public bool Equals(SystemHandle other) => _world == other._world && _index == other._index && _version == other._version;

    /// <inheritdoc/>
    public override bool Equals(object obj) => obj is SystemHandle other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(_world, _index, _version);

    /// <summary>Whether two handles are the same system.</summary>
    public static bool operator ==(SystemHandle left, SystemHandle right) => left.Equals(right);

    /// <summary>Whether two handles are different systems.</summary>
    public static bool operator !=(SystemHandle left, SystemHandle right) => !left.Equals(right);

    /// <summary>The system's name, or "stale system".</summary>
    public override string ToString() => IsAlive ? Name : "stale system";

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowStale() =>
        throw new InvalidOperationException("The system was removed from its world (or the world was disposed): its handle is stale");
}

/// <summary>
/// The world's record of one added system: what <see cref="SystemHandle"/> reads, what the run loop
/// (<see cref="World.RunSystems{TSet}"/>) reads and writes, and what the scheduler needs. The fields a run touches
/// come first. A free record has no <see cref="Code"/> and a version moved past every handle of its last system.
/// </summary>
internal struct SystemRow
{
    public int Version;
    public int Id;          // the system's id in its set: the case of the set's switch
    public bool Enabled;
    public int Interval, Phase;
    public ulong LastRunTick;
    public long RunCount;
    public SystemCode.Entry Code; // null: a free record
    public int Group;
    public int TypeSlot;    // TypeIndex<SystemCode.Entry, T>: the world's lookup by type
    public Exception Error;
    public SystemStats Stats;
    public long Visited;    // entities the [ForEach] methods of the current measured run visited (SystemCode.Visit)
}
